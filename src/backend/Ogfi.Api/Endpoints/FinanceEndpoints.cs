using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Finance;
using Ogfi.Modules.Finance.Persistence;
using Ogfi.Modules.Foundation.Persistence;
using Ogfi.Modules.Foundation.Security;

namespace Ogfi.Api.Endpoints;

public static class FinanceEndpoints
{
    public static IEndpointRouteBuilder MapFinanceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/finance/books", CreateBookAsync)
            .RequireAuthorization().Produces<AccountingBookResponse>(StatusCodes.Status201Created);
        endpoints.MapPost("/api/finance/accounts", CreateAccountAsync)
            .RequireAuthorization().Produces<AccountResponse>(StatusCodes.Status201Created);
        endpoints.MapPost("/api/finance/periods", CreatePeriodAsync)
            .RequireAuthorization().Produces<AccountingPeriodResponse>(StatusCodes.Status201Created);
        endpoints.MapPost("/api/finance/periods/{periodId:guid}/open", OpenPeriodAsync)
            .RequireAuthorization().Produces<AccountingPeriodResponse>();
        endpoints.MapPost("/api/finance/posting-rules/goods-receipt/versions", CreateGoodsReceiptPostingRuleAsync)
            .RequireAuthorization().Produces<PostingRuleVersionResponse>(StatusCodes.Status201Created);

        endpoints.MapGet("/api/finance/source-postings", ListSourcePostingsAsync)
            .RequireAuthorization().Produces<IReadOnlyList<FinanceSourcePostingSummaryResponse>>();
        endpoints.MapGet("/api/finance/source-postings/{sourcePostingId:guid}", GetSourcePostingAsync)
            .RequireAuthorization().Produces<FinanceSourcePostingDetailResponse>().Produces(StatusCodes.Status404NotFound);
        endpoints.MapGet("/api/finance/source-postings/{sourcePostingId:guid}/eligibility", GetEligibilityAsync)
            .RequireAuthorization().Produces<FinanceEligibilityResponse>();
        endpoints.MapPost("/api/finance/source-postings/{sourcePostingId:guid}/replay", ReplaySourcePostingAsync)
            .RequireAuthorization().Produces<FinanceSourcePostingDetailResponse>();

        endpoints.MapGet("/api/finance/journals", ListJournalsAsync)
            .RequireAuthorization().Produces<IReadOnlyList<JournalSummaryResponse>>();
        endpoints.MapGet("/api/finance/journals/{journalId:guid}", GetJournalAsync)
            .RequireAuthorization().Produces<JournalDetailResponse>().Produces(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> CreateBookAsync(
        CreateAccountingBookRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FoundationDbContext foundationDb,
        FinanceSetupService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SetupManage, cancellationToken)) return PermissionDenied(httpContext, "Finance setup management permission is required.");
        if (!await foundationDb.LegalEntities.AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == request.LegalEntityId, cancellationToken)) return Results.NotFound();
        try
        {
            var book = await service.CreatePrimaryBookAsync(tenantId, request.LegalEntityId, request.Code, request.Name, request.FunctionalCurrency, timeProvider.GetUtcNow(), cancellationToken);
            return Results.Created($"/api/finance/books/{book.Id}", Map(book));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> CreateAccountAsync(
        CreateAccountRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceSetupService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SetupManage, cancellationToken)) return PermissionDenied(httpContext, "Finance setup management permission is required.");
        try
        {
            var account = await service.CreateAccountAsync(tenantId, request.AccountingBookId, request.Code, request.Name, request.AccountType, request.NormalBalance, timeProvider.GetUtcNow(), cancellationToken);
            return Results.Created($"/api/finance/accounts/{account.Id}", Map(account));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> CreatePeriodAsync(
        CreateAccountingPeriodRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceSetupService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SetupManage, cancellationToken)) return PermissionDenied(httpContext, "Finance setup management permission is required.");
        try
        {
            var period = await service.CreatePeriodAsync(tenantId, request.AccountingBookId, request.Name, request.StartBusinessDate, request.EndBusinessDate, timeProvider.GetUtcNow(), cancellationToken);
            return Results.Created($"/api/finance/periods/{period.Id}", Map(period));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> OpenPeriodAsync(
        Guid periodId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceSetupService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SetupManage, cancellationToken)) return PermissionDenied(httpContext, "Finance setup management permission is required.");
        try
        {
            var period = await service.SetPeriodStatusAsync(tenantId, periodId, FinanceStatuses.Open, userId, timeProvider.GetUtcNow(), cancellationToken);
            return Results.Ok(Map(period));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> CreateGoodsReceiptPostingRuleAsync(
        CreateGoodsReceiptPostingRuleRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceSetupService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SetupManage, cancellationToken)) return PermissionDenied(httpContext, "Finance setup management permission is required.");
        try
        {
            var rule = await service.CreateGoodsReceiptPostingRuleAsync(
                tenantId, request.AccountingBookId, request.Version, request.Name,
                request.EffectiveFromBusinessDate, request.EffectiveToBusinessDate,
                request.DebitAccountId, request.CreditAccountId,
                timeProvider.GetUtcNow(), cancellationToken);
            return Results.Created($"/api/finance/posting-rules/goods-receipt/versions/{rule.Id}", Map(rule));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> ListSourcePostingsAsync(
        string? status,
        Guid? goodsReceiptId,
        int? offset,
        int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SourcePostingRead, cancellationToken)) return PermissionDenied(httpContext, "Finance source-posting read permission is required.");
        var scopedOutlets = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var query = db.SourcePostings.AsNoTracking().Where(x => x.TenantId == tenantId && scopedOutlets.Contains(x.OutletId));
        if (!string.IsNullOrWhiteSpace(status)) { var normalized = status.Trim().ToUpperInvariant(); query = query.Where(x => x.Status == normalized); }
        if (goodsReceiptId is Guid receiptId) query = query.Where(x => x.GoodsReceiptId == receiptId);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip(page.Offset).Take(page.Limit)
            .Select(x => new FinanceSourcePostingSummaryResponse(x.Id, x.SourceEventId, x.GoodsReceiptId, x.GoodsReceiptNumber, x.LegalEntityId, x.OutletId, x.BusinessDate, x.Currency, x.Status, x.ErrorCode, x.JournalId, x.AttemptCount, x.ReplayCount, x.CreatedAtUtc, x.LastAttemptAtUtc, x.PostedAtUtc))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetSourcePostingAsync(
        Guid sourcePostingId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SourcePostingRead, cancellationToken)) return PermissionDenied(httpContext, "Finance source-posting read permission is required.");
        var source = await db.SourcePostings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sourcePostingId, cancellationToken);
        if (source is null || !await authorization.HasOutletScopeAsync(source.OutletId, cancellationToken)) return Results.NotFound();
        return Results.Ok(Map(source));
    }

    private static async Task<IResult> GetEligibilityAsync(
        Guid sourcePostingId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceDbContext db,
        FinancePostingService service,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SourcePostingRead, cancellationToken)) return PermissionDenied(httpContext, "Finance source-posting read permission is required.");
        var source = await db.SourcePostings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sourcePostingId, cancellationToken);
        if (source is null || !await authorization.HasOutletScopeAsync(source.OutletId, cancellationToken)) return Results.NotFound();
        try
        {
            var eligibility = await service.EvaluateStoredAsync(tenantId, sourcePostingId, cancellationToken);
            return Results.Ok(new FinanceEligibilityResponse(eligibility.Eligible, eligibility.ErrorCode, eligibility.ErrorDetail, eligibility.AccountingBookId, eligibility.AccountingPeriodId, eligibility.PostingRuleVersionId, eligibility.DebitAccountId, eligibility.CreditAccountId));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> ReplaySourcePostingAsync(
        Guid sourcePostingId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceDbContext db,
        FinancePostingService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.SourcePostingReplay, cancellationToken)) return PermissionDenied(httpContext, "Finance source-posting replay permission is required.");
        var source = await db.SourcePostings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == sourcePostingId, cancellationToken);
        if (source is null || !await authorization.HasOutletScopeAsync(source.OutletId, cancellationToken)) return Results.NotFound();
        try
        {
            var replayed = await service.ReplayAsync(tenantId, sourcePostingId, userId, timeProvider.GetUtcNow(), cancellationToken);
            return Results.Ok(Map(replayed));
        }
        catch (FinanceRuleException ex) { return FinanceProblem(httpContext, ex); }
    }

    private static async Task<IResult> ListJournalsAsync(
        Guid? goodsReceiptId,
        int? offset,
        int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.JournalRead, cancellationToken)) return PermissionDenied(httpContext, "Finance Journal read permission is required.");
        var scopedOutlets = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var sourceOutlet = db.SourcePostings.AsNoTracking().Where(x => x.TenantId == tenantId && scopedOutlets.Contains(x.OutletId));
        var query = from journal in db.Journals.AsNoTracking()
                    join source in sourceOutlet on journal.SourcePostingId equals source.Id
                    select journal;
        if (goodsReceiptId is Guid receiptId) query = query.Where(x => x.GoodsReceiptId == receiptId);
        var rows = await query.OrderByDescending(x => x.PostedAtUtc).Skip(page.Offset).Take(page.Limit)
            .Select(x => new JournalSummaryResponse(x.Id, x.Number, x.GoodsReceiptId, x.GoodsReceiptNumber, x.LegalEntityId, x.BusinessDate, x.Currency, x.Status, x.TotalDebit, x.TotalCredit, x.PostedAtUtc))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetJournalAsync(
        Guid journalId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FinanceDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return UnauthorizedTenant(httpContext);
        if (!await authorization.HasPermissionAsync(FinancePermissionCodes.JournalRead, cancellationToken)) return PermissionDenied(httpContext, "Finance Journal read permission is required.");
        var journal = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == journalId, cancellationToken);
        if (journal is null) return Results.NotFound();
        var source = await db.SourcePostings.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == journal.SourcePostingId, cancellationToken);
        if (source is null || !await authorization.HasOutletScopeAsync(source.OutletId, cancellationToken)) return Results.NotFound();
        var lines = await db.JournalLines.AsNoTracking().Where(x => x.TenantId == tenantId && x.JournalId == journalId).OrderBy(x => x.LineNumber)
            .Select(x => new JournalLineResponse(x.Id, x.LineNumber, x.AccountId, x.AccountCodeSnapshot, x.AccountNameSnapshot, x.DebitAmount, x.CreditAmount, x.GoodsReceiptLineId, x.PurchaseOrderId, x.PurchaseOrderLineId, x.SupplierId, x.OutletId, x.StockLocationId, x.CatalogItemId, x.SourceLineAmount, x.Description))
            .ToListAsync(cancellationToken);
        return Results.Ok(new JournalDetailResponse(journal.Id, journal.Number, journal.AccountingBookId, journal.LegalEntityId, journal.BusinessDate, journal.PostingDate, journal.Currency, journal.Status, journal.SourcePostingId, journal.SourceEventId, journal.GoodsReceiptId, journal.GoodsReceiptNumber, journal.PostingRuleVersionId, journal.PostingRuleCodeSnapshot, journal.PostingRuleVersionNumber, journal.TotalDebit, journal.TotalCredit, journal.CorrelationId, journal.PostedAtUtc, lines));
    }

    private static AccountingBookResponse Map(AccountingBook x) => new(x.Id, x.LegalEntityId, x.Code, x.Name, x.FunctionalCurrency, x.IsPrimary, x.Status, x.CreatedAtUtc);
    private static AccountResponse Map(Account x) => new(x.Id, x.AccountingBookId, x.Code, x.Name, x.AccountType, x.NormalBalance, x.PostingEnabled, x.Status, x.CreatedAtUtc);
    private static AccountingPeriodResponse Map(AccountingPeriod x) => new(x.Id, x.AccountingBookId, x.Name, x.StartBusinessDate, x.EndBusinessDate, x.Status, x.CreatedAtUtc, x.OpenedAtUtc, x.OpenedByUserId);
    private static PostingRuleVersionResponse Map(PostingRuleVersion x) => new(x.Id, x.AccountingBookId, x.Code, x.SourceType, x.Version, x.Name, x.EffectiveFromBusinessDate, x.EffectiveToBusinessDate, x.DebitAccountId, x.CreditAccountId, x.Status, x.CreatedAtUtc);
    private static FinanceSourcePostingDetailResponse Map(FinanceSourcePosting x) => new(x.Id, x.SourceEventId, x.SourceType, x.SourceSchemaVersion, x.GoodsReceiptId, x.GoodsReceiptNumber, x.PurchaseOrderId, x.SupplierId, x.LegalEntityId, x.OutletId, x.BusinessDate, x.Currency, x.CorrelationId, x.Status, x.ErrorCode, x.ErrorDetail, x.JournalId, x.AttemptCount, x.ReplayCount, x.CreatedAtUtc, x.LastAttemptAtUtc, x.LastReplayAtUtc, x.LastReplayByUserId, x.PostedAtUtc);

    private static IResult UnauthorizedTenant(HttpContext context) => EndpointSupport.Problem(context, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
    private static IResult PermissionDenied(HttpContext context, string detail) => EndpointSupport.Problem(context, 403, "AUTH.PERMISSION_DENIED", detail);
    private static IResult FinanceProblem(HttpContext context, FinanceRuleException ex)
    {
        var status = ex.Code switch
        {
            "FINANCE.SOURCE_POSTING.NOT_FOUND" or "FINANCE.PERIOD.MISSING" or "FINANCE.BOOK.MISSING" => StatusCodes.Status404NotFound,
            "FINANCE.BOOK.PRIMARY_EXISTS" or "FINANCE.PERIOD.OVERLAP" or "FINANCE.POSTING_RULE.OVERLAP" or "FINANCE.EVENT.CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status422UnprocessableEntity
        };
        return EndpointSupport.Problem(context, status, ex.Code, ex.Message);
    }
}

public sealed record CreateAccountingBookRequest(Guid LegalEntityId, string Code, string Name, string FunctionalCurrency);
public sealed record CreateAccountRequest(Guid AccountingBookId, string Code, string Name, string AccountType, string NormalBalance);
public sealed record CreateAccountingPeriodRequest(Guid AccountingBookId, string Name, DateOnly StartBusinessDate, DateOnly EndBusinessDate);
public sealed record CreateGoodsReceiptPostingRuleRequest(Guid AccountingBookId, int Version, string Name, DateOnly EffectiveFromBusinessDate, DateOnly? EffectiveToBusinessDate, Guid DebitAccountId, Guid CreditAccountId);
public sealed record AccountingBookResponse(Guid Id, Guid LegalEntityId, string Code, string Name, string FunctionalCurrency, bool IsPrimary, string Status, DateTimeOffset CreatedAtUtc);
public sealed record AccountResponse(Guid Id, Guid AccountingBookId, string Code, string Name, string AccountType, string NormalBalance, bool PostingEnabled, string Status, DateTimeOffset CreatedAtUtc);
public sealed record AccountingPeriodResponse(Guid Id, Guid AccountingBookId, string Name, DateOnly StartBusinessDate, DateOnly EndBusinessDate, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? OpenedAtUtc, Guid? OpenedByUserId);
public sealed record PostingRuleVersionResponse(Guid Id, Guid AccountingBookId, string Code, string SourceType, int Version, string Name, DateOnly EffectiveFromBusinessDate, DateOnly? EffectiveToBusinessDate, Guid DebitAccountId, Guid CreditAccountId, string Status, DateTimeOffset CreatedAtUtc);
public sealed record FinanceSourcePostingSummaryResponse(Guid Id, Guid SourceEventId, Guid GoodsReceiptId, string GoodsReceiptNumber, Guid LegalEntityId, Guid OutletId, DateOnly BusinessDate, string Currency, string Status, string? ErrorCode, Guid? JournalId, int AttemptCount, int ReplayCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastAttemptAtUtc, DateTimeOffset? PostedAtUtc);
public sealed record FinanceSourcePostingDetailResponse(Guid Id, Guid SourceEventId, string SourceType, int SourceSchemaVersion, Guid GoodsReceiptId, string GoodsReceiptNumber, Guid PurchaseOrderId, Guid SupplierId, Guid LegalEntityId, Guid OutletId, DateOnly BusinessDate, string Currency, string CorrelationId, string Status, string? ErrorCode, string? ErrorDetail, Guid? JournalId, int AttemptCount, int ReplayCount, DateTimeOffset CreatedAtUtc, DateTimeOffset? LastAttemptAtUtc, DateTimeOffset? LastReplayAtUtc, Guid? LastReplayByUserId, DateTimeOffset? PostedAtUtc);
public sealed record FinanceEligibilityResponse(bool Eligible, string? ErrorCode, string? ErrorDetail, Guid? AccountingBookId, Guid? AccountingPeriodId, Guid? PostingRuleVersionId, Guid? DebitAccountId, Guid? CreditAccountId);
public sealed record JournalSummaryResponse(Guid Id, string Number, Guid GoodsReceiptId, string GoodsReceiptNumber, Guid LegalEntityId, DateOnly BusinessDate, string Currency, string Status, decimal TotalDebit, decimal TotalCredit, DateTimeOffset PostedAtUtc);
public sealed record JournalLineResponse(Guid Id, int LineNumber, Guid AccountId, string AccountCodeSnapshot, string AccountNameSnapshot, decimal DebitAmount, decimal CreditAmount, Guid GoodsReceiptLineId, Guid PurchaseOrderId, Guid PurchaseOrderLineId, Guid SupplierId, Guid OutletId, Guid StockLocationId, Guid CatalogItemId, decimal SourceLineAmount, string Description);
public sealed record JournalDetailResponse(Guid Id, string Number, Guid AccountingBookId, Guid LegalEntityId, DateOnly BusinessDate, DateOnly PostingDate, string Currency, string Status, Guid SourcePostingId, Guid SourceEventId, Guid GoodsReceiptId, string GoodsReceiptNumber, Guid PostingRuleVersionId, string PostingRuleCodeSnapshot, int PostingRuleVersionNumber, decimal TotalDebit, decimal TotalCredit, string CorrelationId, DateTimeOffset PostedAtUtc, IReadOnlyList<JournalLineResponse> Lines);
