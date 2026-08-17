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
        endpoints.MapGet("/api/inventory/profiles", ListProfilesAsync).RequireAuthorization();
        endpoints.MapPost("/api/inventory/profiles", CreateProfileAsync).RequireAuthorization();
        endpoints.MapGet("/api/inventory/stock-locations", ListStockLocationsAsync).RequireAuthorization();
        endpoints.MapPost("/api/inventory/stock-locations", CreateStockLocationAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListProfilesAsync(
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
        return Results.Ok(await db.InventoryProfiles.AsNoTracking().Where(x => x.TenantId == tenantId).OrderBy(x => x.CatalogItemId).ToListAsync(cancellationToken));
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
        if (item is null)
            return Results.NotFound();
        if (await db.InventoryProfiles.AnyAsync(x => x.TenantId == tenantId && x.CatalogItemId == item.CatalogItemId, cancellationToken))
            return EndpointSupport.Problem(httpContext, 409, "INVENTORY.PROFILE.EXISTS", "Inventory Profile already exists for this Catalog Item.");

        var profile = new InventoryProfile
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CatalogItemId = item.CatalogItemId,
            BaseUomId = item.BaseUomId, IsStocked = true, NegativeStockAllowed = false
        };
        db.InventoryProfiles.Add(profile);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/inventory/profiles/{profile.Id}", profile);
    }

    private static async Task<IResult> ListStockLocationsAsync(
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
        var scopedOutletIds = await authorization.GetOutletScopeIdsAsync(cancellationToken);
        var rows = await db.StockLocations.AsNoTracking()
            .Where(x => x.TenantId == tenantId && scopedOutletIds.Contains(x.OutletId))
            .OrderBy(x => x.Code)
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
        if (await organization.GetOutletAsync(request.OutletId, cancellationToken) is null)
            return Results.NotFound();
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
        return Results.Created($"/api/inventory/stock-locations/{location.Id}", location);
    }
}

public sealed record CreateInventoryProfileRequest(Guid CatalogItemId);
public sealed record CreateStockLocationRequest(Guid OutletId, string Code, string Name);
