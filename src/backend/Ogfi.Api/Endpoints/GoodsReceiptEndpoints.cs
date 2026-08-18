using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Inventory.Persistence;
using Ogfi.Modules.Procurement;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Api.Endpoints;

public static class GoodsReceiptEndpoints
{
    public static IEndpointRouteBuilder MapGoodsReceiptEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/procurement/goods-receipts", ListGoodsReceiptsAsync).RequireAuthorization().Produces<IReadOnlyList<GoodsReceiptSummaryResponse>>();
        endpoints.MapPost("/api/procurement/goods-receipts", CreateGoodsReceiptAsync).RequireAuthorization().Produces<GoodsReceiptResponse>(StatusCodes.Status201Created);
        endpoints.MapGet("/api/procurement/goods-receipts/{goodsReceiptId:guid}", GetGoodsReceiptAsync).RequireAuthorization().Produces<GoodsReceiptResponse>().Produces(StatusCodes.Status404NotFound);
        endpoints.MapPost("/api/procurement/goods-receipts/{goodsReceiptId:guid}/post", PostGoodsReceiptAsync).RequireAuthorization().Produces<GoodsReceiptResponse>();
        return endpoints;
    }

    private static async Task<IResult> ListGoodsReceiptsAsync(string? q, string? status, Guid? purchaseOrderId, Guid? outletId, int? offset, int? limit, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, ProcurementDbContext db, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.GoodsReceiptRead, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Goods Receipt read permission is required.");
        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var query = db.GoodsReceipts.AsNoTracking().Where(x => x.TenantId == tenantId && scopedOutletIds.Contains(x.OutletId));
        if (purchaseOrderId is Guid poId) query = query.Where(x => x.PurchaseOrderId == poId);
        if (outletId is Guid outlet) query = query.Where(x => x.OutletId == outlet);
        if (!string.IsNullOrWhiteSpace(status)) { var normalizedStatus = status.Trim().ToUpperInvariant(); query = query.Where(x => x.Status == normalizedStatus); }
        if (!string.IsNullOrWhiteSpace(q)) { var term = q.Trim().ToUpperInvariant(); query = query.Where(x => x.Number.Contains(term) || x.PurchaseOrderNumberSnapshot.Contains(term) || x.SupplierCodeSnapshot.Contains(term) || x.SupplierNameSnapshot.ToUpper().Contains(term)); }
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc).Skip(page.Offset).Take(page.Limit)
            .Select(x => new GoodsReceiptSummaryResponse(x.Id, x.Number, x.PurchaseOrderId, x.PurchaseOrderNumberSnapshot, x.SupplierId, x.SupplierCodeSnapshot, x.SupplierNameSnapshot, x.OutletId, x.StockLocationId, x.StockLocationCodeSnapshot, x.Currency, x.Status, x.BusinessDate, x.TotalNetAmount, x.CreatedAtUtc, x.PostedAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateGoodsReceiptAsync(CreateGoodsReceiptRequest request, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, BusinessTimeResolver businessTime, ProcurementDbContext procurementDb, InventoryDbContext inventoryDb, GoodsReceiptService service, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.GoodsReceiptWrite, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Goods Receipt write permission is required.");
        var po = await procurementDb.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.PurchaseOrderId, cancellationToken);
        if (po is null || !await authorization.HasOutletScopeAsync(po.OutletId, cancellationToken)) return Results.NotFound();
        var location = await inventoryDb.StockLocations.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == request.StockLocationId, cancellationToken);
        if (location is null) return Results.NotFound();
        if (!location.IsActive || location.OutletId != po.OutletId) return EndpointSupport.Problem(httpContext, 422, "PROCUREMENT.GR.STOCK_LOCATION_INVALID", "Receiving Stock Location must be active and belong to the Purchase Order Outlet.");
        var businessContext = await businessTime.ResolveAsync(po.OutletId, cancellationToken);
        if (businessContext is null) return Results.NotFound();
        try
        {
            var receipt = await service.CreateDraftAsync(tenantId, userId, po.Id, new ReceivingStockLocationReference(location.Id, location.OutletId, location.Code), businessContext.BusinessDate, request.Lines.Select(x => new GoodsReceiptLineInput(x.PurchaseOrderLineId, x.Quantity)).ToArray(), timeProvider.GetUtcNow(), cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(receipt.Version);
            return Results.Created($"/api/procurement/goods-receipts/{receipt.Id}", Map(receipt));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static async Task<IResult> GetGoodsReceiptAsync(Guid goodsReceiptId, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, ProcurementDbContext db, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.GoodsReceiptRead, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Goods Receipt read permission is required.");
        var receipt = await db.GoodsReceipts.AsNoTracking().Include(x => x.Lines).SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == goodsReceiptId, cancellationToken);
        if (receipt is null || !await authorization.HasOutletScopeAsync(receipt.OutletId, cancellationToken)) return Results.NotFound();
        httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(receipt.Version);
        return Results.Ok(Map(receipt));
    }

    private static async Task<IResult> PostGoodsReceiptAsync(Guid goodsReceiptId, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, ProcurementDbContext procurementDb, InventoryDbContext inventoryDb, GoodsReceiptService service, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out var userId)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(ProcurementPermissionCodes.GoodsReceiptPost, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Goods Receipt posting permission is required.");
        if (!EndpointSupport.TryReadIfMatch(httpContext, out var expectedVersion)) return EndpointSupport.Problem(httpContext, 428, "CONCURRENCY.IF_MATCH_REQUIRED", "A valid If-Match ETag is required.");
        if (!httpContext.Request.Headers.TryGetValue("Idempotency-Key", out var rawKey) || string.IsNullOrWhiteSpace(rawKey)) return EndpointSupport.Problem(httpContext, 400, "IDEMPOTENCY.KEY_REQUIRED", "Idempotency-Key is required.");
        var receiptContext = await procurementDb.GoodsReceipts.AsNoTracking().Where(x => x.TenantId == tenantId && x.Id == goodsReceiptId).Select(x => new { x.OutletId, x.StockLocationId, x.StockLocationCodeSnapshot }).SingleOrDefaultAsync(cancellationToken);
        if (receiptContext is null || !await authorization.HasOutletScopeAsync(receiptContext.OutletId, cancellationToken)) return Results.NotFound();
        var location = await inventoryDb.StockLocations.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == receiptContext.StockLocationId, cancellationToken);
        if (location is null) return Results.NotFound();
        if (!location.IsActive || location.OutletId != receiptContext.OutletId || location.Code != receiptContext.StockLocationCodeSnapshot) return EndpointSupport.Problem(httpContext, 422, "PROCUREMENT.GR.STOCK_LOCATION_INVALID", "Receiving Stock Location is inactive or no longer matches the Goods Receipt context.");
        try
        {
            var result = await service.PostAsync(tenantId, goodsReceiptId, expectedVersion, userId, rawKey.ToString(), new ReceivingStockLocationReference(location.Id, location.OutletId, location.Code), EndpointSupport.CorrelationId(httpContext), timeProvider.GetUtcNow(), cancellationToken);
            httpContext.Response.Headers.ETag = EndpointSupport.ToEtag(result.Receipt.Version);
            if (result.IsReplay) httpContext.Response.Headers["X-OGFI-Idempotent-Replay"] = "true";
            return Results.Ok(Map(result.Receipt));
        }
        catch (ProcurementRuleException ex) { return ProcurementProblem(httpContext, ex); }
    }

    private static GoodsReceiptResponse Map(GoodsReceipt receipt) => new(receipt.Id, receipt.Number, receipt.PurchaseOrderId, receipt.PurchaseOrderNumberSnapshot, receipt.SupplierId, receipt.SupplierCodeSnapshot, receipt.SupplierNameSnapshot, receipt.LegalEntityId, receipt.OutletId, receipt.StockLocationId, receipt.StockLocationCodeSnapshot, receipt.Currency, receipt.BusinessDate, receipt.Status, receipt.TotalNetAmount, receipt.CreatedByUserId, receipt.CreatedAtUtc, receipt.PostedByUserId, receipt.PostedAtUtc, receipt.Lines.OrderBy(x => x.LineNumber).Select(x => new GoodsReceiptLineResponse(x.Id, x.LineNumber, x.PurchaseOrderLineId, x.CatalogItemId, x.CatalogItemCodeSnapshot, x.CatalogItemNameSnapshot, x.ReceivedQuantity, x.PurchaseUomId, x.PurchaseUomCodeSnapshot, x.BaseUomId, x.BaseUomCodeSnapshot, x.ConversionNumerator, x.ConversionDenominator, x.NormalizedBaseQuantity, x.UnitPrice, x.LineNetAmount)).ToArray());

    private static IResult ProcurementProblem(HttpContext context, ProcurementRuleException ex)
    {
        var status = ex.Code switch
        {
            "PROCUREMENT.GR.NOT_FOUND" or "PROCUREMENT.PO.NOT_FOUND" => StatusCodes.Status404NotFound,
            "CONCURRENCY.CONFLICT" or "IDEMPOTENCY.CONFLICT" => StatusCodes.Status409Conflict,
            "IDEMPOTENCY.KEY_REQUIRED" or "IDEMPOTENCY.KEY_INVALID" or "VALIDATION.REQUIRED" => StatusCodes.Status400BadRequest,
            _ => StatusCodes.Status422UnprocessableEntity
        };
        return EndpointSupport.Problem(context, status, ex.Code, ex.Message);
    }
}

public sealed record CreateGoodsReceiptRequest(Guid PurchaseOrderId, Guid StockLocationId, IReadOnlyList<CreateGoodsReceiptLineRequest> Lines);
public sealed record CreateGoodsReceiptLineRequest(Guid PurchaseOrderLineId, decimal Quantity);
