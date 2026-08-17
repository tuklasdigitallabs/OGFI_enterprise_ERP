using Microsoft.EntityFrameworkCore;
using Ogfi.Api.Infrastructure;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.BuildingBlocks.Time;
using Ogfi.Modules.Catalog;
using Ogfi.Modules.Catalog.Persistence;
using Ogfi.Modules.Foundation.Security;

namespace Ogfi.Api.Endpoints;

public static class CatalogEndpoints
{
    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/catalog/uoms", ListUomsAsync).RequireAuthorization();
        endpoints.MapPost("/api/catalog/uom-conversions/preview", ConvertUomAsync).RequireAuthorization();
        endpoints.MapGet("/api/catalog/items", ListItemsAsync).RequireAuthorization();
        endpoints.MapGet("/api/catalog/items/{itemId:guid}", GetItemAsync).RequireAuthorization();
        endpoints.MapPost("/api/catalog/items", CreateItemAsync).RequireAuthorization();
        endpoints.MapPost("/api/catalog/items/{itemId:guid}/packaging-conversions", CreatePackagingConversionAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ListUomsAsync(
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out _, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(CatalogPermissionCodes.Read, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Catalog read permission is required.");

        var rows = await db.Uoms.AsNoTracking().Where(x => x.Status == CatalogStatuses.Active)
            .OrderBy(x => x.Code)
            .Select(x => new { x.Id, x.Code, x.Name, x.DimensionCode, x.PrecisionScale })
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> ConvertUomAsync(
        UomConversionPreviewRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        StandardUomConversionService conversion,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out _, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(CatalogPermissionCodes.Read, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Catalog read permission is required.");
        if (request.Quantity < 0)
            return EndpointSupport.Problem(httpContext, 400, "CATALOG.UOM.QUANTITY_INVALID", "Quantity cannot be negative.");

        var converted = await conversion.ConvertAsync(request.Quantity, request.FromUomId, request.ToUomId, cancellationToken);
        return converted is null
            ? EndpointSupport.Problem(httpContext, 422, "CATALOG.UOM.CONVERSION_MISSING", "No standard same-dimension conversion exists.")
            : Results.Ok(new { request.Quantity, request.FromUomId, request.ToUomId, convertedQuantity = converted.Value });
    }

    private static async Task<IResult> ListItemsAsync(
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(CatalogPermissionCodes.Read, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Catalog read permission is required.");

        var rows = await (
            from item in db.CatalogItems.AsNoTracking()
            join uom in db.Uoms.AsNoTracking() on item.BaseUomId equals uom.Id
            where item.TenantId == tenantId
            orderby item.Code
            select new { item.Id, item.Code, item.Name, item.BaseUomId, baseUomCode = uom.Code, item.Status })
            .ToListAsync(cancellationToken);
        return Results.Ok(rows);
    }

    private static async Task<IResult> GetItemAsync(
        Guid itemId,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogReferenceService catalog,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out _, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(CatalogPermissionCodes.Read, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Catalog read permission is required.");
        var item = await catalog.GetItemAsync(itemId, cancellationToken);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> CreateItemAsync(
        CreateCatalogItemRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(CatalogPermissionCodes.Write, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Catalog write permission is required.");
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.Name))
            return EndpointSupport.Problem(httpContext, 400, "VALIDATION.REQUIRED", "Item code and name are required.");

        var baseUom = await db.Uoms.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == request.BaseUomId && x.Status == CatalogStatuses.Active, cancellationToken);
        if (baseUom is null)
            return EndpointSupport.Problem(httpContext, 422, "CATALOG.UOM.CONVERSION_MISSING", "Base UOM is invalid or inactive.");

        var code = request.Code.Trim().ToUpperInvariant();
        if (await db.CatalogItems.AnyAsync(x => x.TenantId == tenantId && x.Code == code, cancellationToken))
            return EndpointSupport.Problem(httpContext, 409, "CATALOG.ITEM.CODE_EXISTS", "Catalog Item code already exists.");

        var item = new CatalogItem
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Code = code, Name = request.Name.Trim(),
            BaseUomId = baseUom.Id, Status = CatalogStatuses.Active
        };
        db.CatalogItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/catalog/items/{item.Id}", new { item.Id, item.Code, item.Name, item.BaseUomId, baseUomCode = baseUom.Code });
    }

    private static async Task<IResult> CreatePackagingConversionAsync(
        Guid itemId,
        CreatePackagingConversionRequest request,
        HttpContext httpContext,
        ITenantExecutionContextAccessor executionContext,
        FoundationAuthorizationEvaluator authorization,
        CatalogDbContext db,
        CancellationToken cancellationToken)
    {
        if (!EndpointSupport.TryResolveActor(executionContext, out var tenantId, out _))
            return EndpointSupport.Problem(httpContext, 401, "AUTH.TENANT_DENIED", "Tenant execution context is not resolved.");
        if (!await authorization.HasPermissionAsync(CatalogPermissionCodes.Write, cancellationToken))
            return EndpointSupport.Problem(httpContext, 403, "AUTH.PERMISSION_DENIED", "Catalog write permission is required.");
        if (request.Numerator <= 0 || request.Denominator <= 0 || (request.EffectiveToBusinessDate is DateOnly to && to < request.EffectiveFromBusinessDate))
            return EndpointSupport.Problem(httpContext, 422, "CATALOG.UOM.CONVERSION_INVALID", "Packaging conversion is invalid.");

        var item = await db.CatalogItems.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == itemId, cancellationToken);
        var purchaseUom = await db.Uoms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.PurchaseUomId && x.Status == CatalogStatuses.Active, cancellationToken);
        if (item is null || purchaseUom is null)
            return Results.NotFound();
        if (request.PurchaseUomId == item.BaseUomId)
            return EndpointSupport.Problem(httpContext, 422, "CATALOG.UOM.CONVERSION_INVALID", "A packaging conversion is unnecessary when purchase and base UOM are identical.");

        var overlap = await db.PackagingConversions.AnyAsync(x => x.TenantId == tenantId && x.CatalogItemId == itemId
            && x.PurchaseUomId == request.PurchaseUomId
            && (x.EffectiveToBusinessDate == null || x.EffectiveToBusinessDate >= request.EffectiveFromBusinessDate)
            && (request.EffectiveToBusinessDate == null || x.EffectiveFromBusinessDate <= request.EffectiveToBusinessDate), cancellationToken);
        if (overlap)
            return EndpointSupport.Problem(httpContext, 409, "CATALOG.UOM.CONVERSION_OVERLAP", "An overlapping packaging conversion already exists.");

        var row = new ItemPackagingConversion
        {
            Id = Guid.NewGuid(), TenantId = tenantId, CatalogItemId = item.Id,
            PurchaseUomId = purchaseUom.Id, BaseUomId = item.BaseUomId,
            Numerator = request.Numerator, Denominator = request.Denominator,
            EffectiveFromBusinessDate = request.EffectiveFromBusinessDate,
            EffectiveToBusinessDate = request.EffectiveToBusinessDate
        };
        db.PackagingConversions.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Results.Created($"/api/catalog/items/{item.Id}/packaging-conversions/{row.Id}", row);
    }
}

public sealed record CreateCatalogItemRequest(string Code, string Name, Guid BaseUomId);
public sealed record CreatePackagingConversionRequest(Guid PurchaseUomId, long Numerator, long Denominator, DateOnly EffectiveFromBusinessDate, DateOnly? EffectiveToBusinessDate);
public sealed record UomConversionPreviewRequest(decimal Quantity, Guid FromUomId, Guid ToUomId);
