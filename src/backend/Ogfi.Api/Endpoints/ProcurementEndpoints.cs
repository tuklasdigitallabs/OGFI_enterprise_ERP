using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.BuildingBlocks.Time;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Api.Endpoints;

public static class ProcurementEndpoints
{
    public static IEndpointRouteBuilder MapProcurementEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/procurement/suppliers", ListSuppliersAsync)
            .RequireAuthorization().Produces<IReadOnlyList<SupplierResponse>>();
        endpoints.MapGet("/api/procurement/suppliers/{supplierId:guid}", GetSupplierAsync)
            .RequireAuthorization().Produces<SupplierResponse>().Produces(StatusCodes.Status404NotFound);
        endpoints.MapPost("/api/procurement/suppliers", CreateSupplierAsync)
            .RequireAuthorization().Produces<SupplierResponse>(StatusCodes.Status201Created);
        endpoints.MapPut("/api/procurement/suppliers/{supplierId:guid}", UpdateSupplierAsync)
            .RequireAuthorization().Produces<SupplierResponse>();
        endpoints.MapGet("/api/procurement/supplier-offers", ListSupplierOffersAsync)
            .RequireAuthorization().Produces<IReadOnlyList<SupplierOfferResponse>>();
        endpoints.MapPost("/api/procurement/supplier-offers", CreateSupplierOfferAsync)
            .RequireAuthorization().Produces<SupplierOfferResponse>(StatusCodes.Status201Created);
        endpoints.MapGet("/api/procurement/purchase-orders", ListPurchaseOrdersAsync)
            .RequireAuthorization().Produces<IReadOnlyList<PurchaseOrderSummaryResponse>>();
        endpoints.MapPost("/api/procurement/purchase-orders", CreatePurchaseOrderAsync)
            .RequireAuthorization().Produces<PurchaseOrderResponse>(StatusCodes.Status201Created);
        endpoints.MapGet("/api/procurement/purchase-orders/{purchaseOrderId:guid}", GetPurchaseOrderAsync)
            .RequireAuthorization().Produces<PurchaseOrderResponse>().Produces(StatusCodes.Status404NotFound);
        endpoints.MapPut("/api/procurement/purchase-orders/{purchaseOrderId:guid}", UpdatePurchaseOrderAsync)
            .RequireAuthorization().Produces<PurchaseOrderResponse>();
        endpoints.MapPost("/api/procurement/purchase-orders/{purchaseOrderId:guid}/submit", SubmitPurchaseOrderAsync)
            .RequireAuthorization().Produces<PurchaseOrderResponse>();
        return endpoints;
    }

    private static async Task<IResult> ListSuppliersAsync(
        string? q, int? offset, int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        ProcurementDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.SupplierRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Supplier read permission is required.");

        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var query = db.Suppliers.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToUpperInvariant();
            query = query.Where(x => x.Code.Contains(term) || x.Name.ToUpper().Contains(term));
        }

        var rows = await query.OrderBy(x => x.Code)
            .Skip(page.Offset).Take(page.Limit)
            .Select(x => new SupplierResponse(x.Id, x.Code, x.Name, x.Status))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetSupplierAsync(
        Guid supplierId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        ProcurementDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.SupplierRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Supplier read permission is required.");

        var supplier = await db.Suppliers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == supplierId, cancellationToken);
        if (supplier is null) return Results.NotFound();
        httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(supplier.Version);
        return Results.Ok(MapSupplier(supplier));
    }

    private static async Task<IResult> CreateSupplierAsync(
        CreateSupplierRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        PurchaseOrderService service,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.SupplierWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Supplier write permission is required.");

        try
        {
            var supplier = await service.CreateSupplierAsync(tenantId, request.Code, request.Name, cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(supplier.Version);
            return Results.Created($"/api/procurement/suppliers/{supplier.Id}", MapSupplier(supplier));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static async Task<IResult> UpdateSupplierAsync(
        Guid supplierId,
        UpdateSupplierRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        PurchaseOrderService service,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.SupplierWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Supplier write permission is required.");
        if (!EndpointSupport.TryReadIfMatch(httpContext, out var expectedVersion))
            return EndpointSupport.Problem(httpContext, 428, "CONCURRENCY.IF_MATCH_REQUIRED", "A valid If-Match ETag is required.");

        try
        {
            var supplier = await service.UpdateSupplierAsync(tenantId, supplierId, expectedVersion, request.Name, cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(supplier.Version);
            return Results.Ok(MapSupplier(supplier));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static async Task<IResult> ListSupplierOffersAsync(
        Guid? supplierId, Guid? catalogItemId, int? offset, int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        ProcurementDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.SupplierRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Supplier read permission is required.");

        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var query = db.SupplierOffers.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (supplierId is Guid supplier) query = query.Where(x => x.SupplierId == supplier);
        if (catalogItemId is Guid item) query = query.Where(x => x.CatalogItemId == item);

        var rows = await query.OrderBy(x => x.SupplierId).ThenBy(x => x.CatalogItemCodeSnapshot)
            .Skip(page.Offset).Take(page.Limit)
            .Select(x => new SupplierOfferResponse(
                x.Id, x.SupplierId, x.CatalogItemId, x.CatalogItemCodeSnapshot, x.CatalogItemNameSnapshot,
                x.SupplierItemCode, x.PurchaseUomId, x.PurchaseUomCodeSnapshot, x.BaseUomId, x.BaseUomCodeSnapshot,
                x.ConversionNumerator, x.ConversionDenominator, x.UnitPrice, x.Currency,
                x.EffectiveFromBusinessDate, x.EffectiveToBusinessDate))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateSupplierOfferAsync(
        CreateSupplierOfferRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogReferenceService catalog,
        PurchaseOrderService service,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.SupplierWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Supplier write permission is required.");

        var item = await catalog.GetItemAsync(request.CatalogItemId, cancellationToken);
        if (item is null) return Results.NotFound();
        var conversion = await catalog.ResolvePurchaseConversionAsync(
            request.CatalogItemId, request.PurchaseUomId,
            new BusinessDate(request.EffectiveFromBusinessDate), cancellationToken);
        if (conversion is null)
            return EndpointSupport.Problem(httpContext, 422, "CATALOG.UOM.CONVERSION_MISSING", "No effective item-specific purchase-to-base conversion exists.");

        var reference = new SupplierOfferReferenceInput(
            item.CatalogItemId, item.ItemCode, item.ItemName,
            conversion.PurchaseUomId, conversion.PurchaseUomCode,
            conversion.BaseUomId, conversion.BaseUomCode,
            conversion.Numerator, conversion.Denominator);
        try
        {
            var offer = await service.CreateSupplierOfferAsync(
                tenantId, request.SupplierId, reference, request.SupplierItemCode, request.UnitPrice,
                request.Currency, request.EffectiveFromBusinessDate, request.EffectiveToBusinessDate, cancellationToken);
            return Results.Created($"/api/procurement/supplier-offers/{offer.Id}", MapSupplierOffer(offer));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static async Task<IResult> ListPurchaseOrdersAsync(
        string? q, string? status, Guid? outletId, int? offset, int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        ProcurementDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Purchase Order read permission is required.");

        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var query = db.PurchaseOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && scopedOutletIds.Contains(x.OutletId));
        if (outletId is Guid outlet) query = query.Where(x => x.OutletId == outlet);
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalizedStatus = status.Trim().ToUpperInvariant();
            query = query.Where(x => x.Status == normalizedStatus);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToUpperInvariant();
            query = query.Where(x => x.Number.Contains(term) || x.SupplierCodeSnapshot.Contains(term) || x.SupplierNameSnapshot.ToUpper().Contains(term));
        }

        var rows = await query.OrderByDescending(x => x.CreatedAtUtc)
            .Skip(page.Offset).Take(page.Limit)
            .Select(x => new PurchaseOrderSummaryResponse(
                x.Id, x.Number, x.SupplierId, x.SupplierCodeSnapshot, x.SupplierNameSnapshot,
                x.LegalEntityId, x.OutletId, x.Currency, x.Status, x.BusinessDate,
                x.TotalNetAmount, x.CreatedAtUtc))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreatePurchaseOrderAsync(
        CreatePurchaseOrderRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FoundationOrganizationReferenceService organization,
        BusinessTimeResolver businessTime,
        PurchaseOrderService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Purchase Order write permission is required.");
        if (!await authorization.HasOutletScopeAsync(request.OutletId, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.SCOPE_DENIED", "Outlet is outside the user's organization scope.");

        var org = await organization.GetOutletAsync(request.OutletId, cancellationToken);
        var businessContext = await businessTime.ResolveAsync(request.OutletId, cancellationToken);
        if (org is null || businessContext is null || org.LegalEntityId != request.LegalEntityId)
            return EndpointSupport.Problem(httpContext, 422, "PROCUREMENT.PO.ORG_INVALID", "Legal Entity and Outlet context is invalid.");

        try
        {
            var po = await service.CreateDraftAsync(
                tenantId, userId, request.SupplierId, request.LegalEntityId, request.OutletId, request.Currency,
                businessContext.BusinessDate,
                request.Lines.Select(x => new PurchaseOrderLineInput(x.SupplierOfferId, x.Quantity)).ToArray(),
                timeProvider.GetUtcNow(), cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(po.Version);
            return Results.Created($"/api/procurement/purchase-orders/{po.Id}", MapPurchaseOrder(po));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static async Task<IResult> GetPurchaseOrderAsync(
        Guid purchaseOrderId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        ProcurementDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Purchase Order read permission is required.");

        var po = await db.PurchaseOrders.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == purchaseOrderId, cancellationToken);
        if (po is null) return Results.NotFound();
        if (!await authorization.HasOutletScopeAsync(po.OutletId, cancellationToken)) return Results.NotFound();
        httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(po.Version);
        return Results.Ok(MapPurchaseOrder(po));
    }

    private static async Task<IResult> UpdatePurchaseOrderAsync(
        Guid purchaseOrderId,
        UpdatePurchaseOrderRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        ProcurementDbContext db,
        PurchaseOrderService service,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Purchase Order write permission is required.");

        var outletId = await db.PurchaseOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == purchaseOrderId)
            .Select(x => (Guid?)x.OutletId).SingleOrDefaultAsync(cancellationToken);
        if (outletId is null) return Results.NotFound();
        if (!await authorization.HasOutletScopeAsync(outletId.Value, cancellationToken)) return Results.NotFound();
        if (!EndpointSupport.TryReadIfMatch(httpContext, out var expectedVersion))
            return EndpointSupport.Problem(httpContext, 428, "CONCURRENCY.IF_MATCH_REQUIRED", "A valid If-Match ETag is required.");

        try
        {
            var po = await service.UpdateDraftLinesAsync(
                tenantId, purchaseOrderId, expectedVersion,
                request.Lines.Select(x => new PurchaseOrderLineInput(x.SupplierOfferId, x.Quantity)).ToArray(), cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(po.Version);
            return Results.Ok(MapPurchaseOrder(po));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static async Task<IResult> SubmitPurchaseOrderAsync(
        Guid purchaseOrderId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        BusinessTimeResolver businessTime,
        ProcurementDbContext db,
        PurchaseOrderService service,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.PurchaseOrderSubmit, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Purchase Order submit permission is required.");

        var outletId = await db.PurchaseOrders.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.Id == purchaseOrderId)
            .Select(x => (Guid?)x.OutletId).SingleOrDefaultAsync(cancellationToken);
        if (outletId is null) return Results.NotFound();
        if (!await authorization.HasOutletScopeAsync(outletId.Value, cancellationToken)) return Results.NotFound();
        var businessContext = await businessTime.ResolveAsync(outletId.Value, cancellationToken);
        if (businessContext is null) return Results.NotFound();
        if (!EndpointSupport.TryReadIfMatch(httpContext, out var expectedVersion))
            return EndpointSupport.Problem(httpContext, 428, "CONCURRENCY.IF_MATCH_REQUIRED", "A valid If-Match ETag is required.");

        try
        {
            var po = await service.SubmitAsync(
                tenantId, purchaseOrderId, expectedVersion, userId, businessContext.BusinessDate,
                EndpointSupport.CorrelationId(httpContext), timeProvider.GetUtcNow(), cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(po.Version);
            return Results.Ok(MapPurchaseOrder(po));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static SupplierResponse MapSupplier(Supplier supplier)
        => new(supplier.Id, supplier.Code, supplier.Name, supplier.Status);

    private static SupplierOfferResponse MapSupplierOffer(SupplierOffer offer)
        => new(
            offer.Id, offer.SupplierId, offer.CatalogItemId, offer.CatalogItemCodeSnapshot, offer.CatalogItemNameSnapshot,
            offer.SupplierItemCode, offer.PurchaseUomId, offer.PurchaseUomCodeSnapshot,
            offer.BaseUomId, offer.BaseUomCodeSnapshot, offer.ConversionNumerator, offer.ConversionDenominator,
            offer.UnitPrice, offer.Currency, offer.EffectiveFromBusinessDate, offer.EffectiveToBusinessDate);

    private static PurchaseOrderResponse MapPurchaseOrder(PurchaseOrder po)
        => new(
            po.Id, po.Number, po.SupplierId, po.SupplierCodeSnapshot, po.SupplierNameSnapshot,
            po.LegalEntityId, po.OutletId, po.Currency, po.Status, po.BusinessDate.ToString("yyyy-MM-dd"),
            po.TotalNetAmount, po.CreatedByUserId, po.CreatedAtUtc, po.SubmittedByUserId, po.SubmittedAtUtc,
            po.Lines.OrderBy(x => x.LineNumber)
                .Select(x => new PurchaseOrderLineResponse(
                    x.Id, x.LineNumber, x.SupplierOfferId, x.CatalogItemId,
                    x.CatalogItemCodeSnapshot, x.CatalogItemNameSnapshot, x.OrderQuantity,
                    x.PurchaseUomId, x.PurchaseUomCodeSnapshot, x.BaseUomId, x.BaseUomCodeSnapshot,
                    x.ConversionNumerator, x.ConversionDenominator, x.UnitPrice, x.LineNetAmount))
                .ToArray());

    private static IResult ProcurementProblem(HttpContext httpContext, ProcurementRuleException ex)
    {
        var status = ex.Code switch
        {
            "CONCURRENCY.CONFLICT" => 409,
            "VALIDATION.REQUIRED" => 400,
            "PROCUREMENT.PO.NOT_FOUND" or "PROCUREMENT.SUPPLIER.NOT_FOUND" => 404,
            _ when ex.Code.EndsWith(".EXISTS", StringComparison.Ordinal) || ex.Code.EndsWith(".OVERLAP", StringComparison.Ordinal) => 409,
            _ => 422
        };
        return EndpointSupport.Problem(httpContext, status, ex.Code, ex.Message);
    }
}

public sealed record CreateSupplierRequest(string Code, string Name);
public sealed record UpdateSupplierRequest(string Name);
public sealed record CreateSupplierOfferRequest(Guid SupplierId, Guid CatalogItemId, Guid PurchaseUomId, string? SupplierItemCode, decimal UnitPrice, string Currency, DateOnly EffectiveFromBusinessDate, DateOnly? EffectiveToBusinessDate);
public sealed record PurchaseOrderLineRequest(Guid SupplierOfferId, decimal Quantity);
public sealed record CreatePurchaseOrderRequest(Guid SupplierId, Guid LegalEntityId, Guid OutletId, string Currency, IReadOnlyCollection<PurchaseOrderLineRequest> Lines);
public sealed record UpdatePurchaseOrderRequest(IReadOnlyCollection<PurchaseOrderLineRequest> Lines);
