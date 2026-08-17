using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Foundation.Security;
using Ogfi.Modules.Inventory;
using Ogfi.Modules.Inventory.Persistence;

namespace Ogfi.Api.Endpoints;

public static class InventorySetupEndpoints
{
    public static IEndpointRouteBuilder MapInventorySetupEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/inventory/profiles", ListProfilesAsync)
            .RequireAuthorization().Produces<IReadOnlyList<InventoryProfileResponse>>();
        endpoints.MapPost("/api/inventory/profiles", CreateProfileAsync)
            .RequireAuthorization().Produces<InventoryProfileResponse>(StatusCodes.Status201Created);
        endpoints.MapGet("/api/inventory/stock-locations", ListStockLocationsAsync)
            .RequireAuthorization().Produces<IReadOnlyList<StockLocationResponse>>();
        endpoints.MapPost("/api/inventory/stock-locations", CreateStockLocationAsync)
            .RequireAuthorization().Produces<StockLocationResponse>(StatusCodes.Status201Created);
        return endpoints;
    }

    private static async Task<IResult> ListProfilesAsync(
        Guid? catalogItemId, int? offset, int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        InventoryDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.SetupRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory setup read permission is required.");

        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var query = db.InventoryProfiles.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (catalogItemId is Guid itemId) query = query.Where(x => x.CatalogItemId == itemId);
        var rows = await query.OrderBy(x => x.CatalogItemId)
            .Skip(page.Offset).Take(page.Limit)
            .Select(x => new InventoryProfileResponse(x.Id, x.CatalogItemId, x.BaseUomId, x.IsStocked, x.NegativeStockAllowed))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateProfileAsync(
        CreateInventoryProfileRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogReferenceService catalog,
        InventoryDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.SetupWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory setup write permission is required.");
        var item = await catalog.GetItemAsync(request.CatalogItemId, cancellationToken);
        if (item is null) return Results.NotFound();
        if (await db.InventoryProfiles.AnyAsync(x => x.TenantId == tenantId && x.CatalogItemId == item.CatalogItemId, cancellationToken))
            return EndpointSupport.Problem(httpContext, 409, "INVENTORY.PROFILE.EXISTS", "Inventory Profile already exists for this Catalog Item.");

        var profile = new InventoryProfile
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CatalogItemId = item.CatalogItemId,
            BaseUomId = item.BaseUomId, IsStocked = true, NegativeStockAllowed = false
        };
        db.InventoryProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/inventory/profiles/{profile.Id}",
            new InventoryProfileResponse(profile.Id, profile.CatalogItemId, profile.BaseUomId, profile.IsStocked, profile.NegativeStockAllowed));
    }

    private static async Task<IResult> ListStockLocationsAsync(
        Guid? outletId, string? q, int? offset, int? limit,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        InventoryDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.SetupRead, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory setup read permission is required.");

        var page = EndpointPage.Normalize(httpContext, offset, limit);
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var query = db.StockLocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && scopedOutletIds.Contains(x.OutletId));
        if (outletId is Guid outlet) query = query.Where(x => x.OutletId == outlet);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToUpperInvariant();
            query = query.Where(x => x.Code.Contains(term) || x.Name.ToUpper().Contains(term));
        }
        var rows = await query.OrderBy(x => x.Code)
            .Skip(page.Offset).Take(page.Limit)
            .Select(x => new StockLocationResponse(x.Id, x.OutletId, x.Code, x.Name, x.LocationType, x.IsActive))
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> CreateStockLocationAsync(
        CreateStockLocationRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        FoundationOrganizationReferenceService organization,
        InventoryDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(InventoryPermissionCodes.SetupWrite, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Inventory setup write permission is required.");
        if (!await authorization.HasOutletScopeAsync(request.OutletId, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.SCOPE_DENIED", "Outlet is outside the user's organization scope.");
        if (await organization.GetOutletAsync(request.OutletId, cancellationToken) is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return EndpointSupport.Problem(httpContext, 400, "VALIDATION.REQUIRED", "Stock Location code and name are required.");

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.StockLocations.AnyAsync(x => x.TenantId == tenantId && x.OutletId == request.OutletId && x.Code == code, cancellationToken))
            return EndpointSupport.Problem(httpContext, 409, "INVENTORY.STOCK_LOCATION.CODE_EXISTS", "Stock Location code already exists for this Outlet.");

        var location = new StockLocation
        {
            Id = Guid.NewGuid(), TenantId = tenantId, OutletId = request.OutletId,
            Code = code, Name = request.Name.Trim(), LocationType = "STOCK", IsActive = true
        };
        db.StockLocations.Add(location);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/inventory/stock-locations/{location.Id}",
            new StockLocationResponse(location.Id, location.OutletId, location.Code, location.Name, location.LocationType, location.IsActive));
    }
}

public sealed record CreateInventoryProfileRequest(Guid CatalogItemId);
public sealed record CreateStockLocationRequest(Guid OutletId, string Code, string Name);
