using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Inventory.Persistence;

namespace Ogfi.Api.Endpoints;

public static class InventoryOperationsEndpoints
{
    public static IEndpointRouteBuilder MapInventoryOperationsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventory/stock-positions", ListStockPositionsAsync).RequireAuthorization().Produces<IReadOnlyList<StockPositionResponse>>();
        endpoints.MapGet("/api/inventory/movements", ListInventoryMovementsAsync).RequireAuthorization().Produces<IReadOnlyList<InventoryMovementResponse>>();
        endpoints.MapPost("/api/inventory/stock-positions/rebuild", RebuildStockPositionsAsync).RequireAuthorization().Produces<StockPositionRebuildResponse>();
        return endpoints;
    }

    private static async Task<IResult> ListStockPositionsAsync(Guid? outletId, Guid? stockLocationId, Guid? catalogItemId, int? offset, int? limit, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, InventoryDbContext db, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.StockRead, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory stock read permission is required.");
        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var query = db.StockPositions.AsNoTracking().Where(x => x.TenantId == tenantId && scopedOutletIds.Contains(x.OutletId));
        if (outletId is Guid outlet) query = query.Where(x => x.OutletId == outlet);
        if (stockLocationId is Guid location) query = query.Where(x => x.StockLocationId == location);
        if (catalogItemId is Guid item) query = query.Where(x => x.CatalogItemId == item);
        var rows = await query.OrderBy(x => x.CatalogItemCodeSnapshot).ThenBy(x => x.StockLocationCodeSnapshot).Skip(page.Offset).Take(page.Limit)
            .Select(x => new StockPositionResponse(x.Id, x.CatalogItemId, x.CatalogItemCodeSnapshot, x.CatalogItemNameSnapshot, x.StockLocationId, x.StockLocationCodeSnapshot, x.OutletId, x.BaseUomId, x.BaseUomCodeSnapshot, x.QuantityOnHand, x.LastMovementOccurredAtUtc)).ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ListInventoryMovementsAsync(Guid? outletId, Guid? stockLocationId, Guid? catalogItemId, Guid? goodsReceiptId, int? offset, int? limit, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, InventoryDbContext db, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.MovementRead, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory movement read permission is required.");
        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var query = db.InventoryMovements.AsNoTracking().Where(x => x.TenantId == tenantId && scopedOutletIds.Contains(x.OutletId));
        if (outletId is Guid outlet) query = query.Where(x => x.OutletId == outlet);
        if (stockLocationId is Guid location) query = query.Where(x => x.StockLocationId == location);
        if (catalogItemId is Guid item) query = query.Where(x => x.CatalogItemId == item);
        if (goodsReceiptId is Guid receipt) query = query.Where(x => x.SourceDocumentId == receipt);
        var rows = await query.OrderByDescending(x => x.OccurredAtUtc).ThenBy(x => x.Id).Skip(page.Offset).Take(page.Limit)
            .Select(x => new InventoryMovementResponse(x.Id, x.MovementType, x.SourceEventId, x.SourceDocumentId, x.SourceLineId, x.PurchaseOrderId, x.PurchaseOrderLineId, x.CatalogItemId, x.CatalogItemCodeSnapshot, x.CatalogItemNameSnapshot, x.StockLocationId, x.StockLocationCodeSnapshot, x.OutletId, x.BaseUomId, x.BaseUomCodeSnapshot, x.QuantityBaseUom, x.BusinessDate, x.OccurredAtUtc, x.CorrelationId)).ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> RebuildStockPositionsAsync(RebuildStockPositionsRequest request, HttpContext httpContext, ITenantExecutionContextAccessor executionContext, FoundationAuthorizationEvaluator authorization, StockPositionRebuildService rebuild, CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _)) return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.StockRebuild, cancellationToken)) return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory Stock Position rebuild permission is required.");
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        if (request.OutletId is Guid outletId && !scopedOutletIds.Contains(outletId)) return Results.NotFound();
        var count = await rebuild.RebuildAsync(tenantId, scopedOutletIds, request.OutletId, request.CatalogItemId, cancellationToken);
        return Results.Ok(new StockPositionRebuildResponse(count));
    }
}

public sealed record RebuildStockPositionsRequest(Guid? OutletId, Guid? CatalogItemId);
