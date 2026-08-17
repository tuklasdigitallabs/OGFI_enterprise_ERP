using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Multitenancy;
using Ogfi.BuildingBlocks.Time;
using Ogfi.Modules.Catalog.Persistence;

namespace Ogfi.Modules.Catalog;

public sealed class CatalogReferenceService(
    CatalogDbContext dbContext,
    ITenantExecutionContextAccessor executionContext)
{
    public async Task<CatalogItemReference?> GetItemAsync(Guid catalogItemId, CancellationToken cancellationToken)
    {
        if (!executionContext.IsResolved || executionContext.TenantId is not Guid tenantId)
        {
            return null;
        }

        return await (
            from item in dbContext.CatalogItems.AsNoTracking()
            join baseUom in dbContext.Uoms.AsNoTracking() on item.BaseUomId equals baseUom.Id
            where item.TenantId == tenantId && item.Id == catalogItemId && item.Status == CatalogStatuses.Active
            select new CatalogItemReference(item.Id, item.Code, item.Name, baseUom.Id, baseUom.Code, item.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PurchaseConversionReference?> ResolvePurchaseConversionAsync(
        Guid catalogItemId,
        Guid purchaseUomId,
        BusinessDate businessDate,
        CancellationToken cancellationToken)
    {
        var item = await GetItemAsync(catalogItemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var purchaseUom = await dbContext.Uoms.AsNoTracking()
            .Where(x => x.Id == purchaseUomId && x.Status == CatalogStatuses.Active)
            .SingleOrDefaultAsync(cancellationToken);
        if (purchaseUom is null)
        {
            return null;
        }

        if (purchaseUomId == item.BaseUomId)
        {
            return new PurchaseConversionReference(
                item.CatalogItemId, purchaseUom.Id, purchaseUom.Code,
                item.BaseUomId, item.BaseUomCode, 1, 1);
        }

        var conversion = await dbContext.PackagingConversions.AsNoTracking()
            .Where(x => x.TenantId == executionContext.TenantId
                && x.CatalogItemId == catalogItemId
                && x.PurchaseUomId == purchaseUomId
                && x.BaseUomId == item.BaseUomId
                && x.EffectiveFromBusinessDate <= businessDate.Value
                && (x.EffectiveToBusinessDate == null || x.EffectiveToBusinessDate >= businessDate.Value))
            .OrderByDescending(x => x.EffectiveFromBusinessDate)
            .FirstOrDefaultAsync(cancellationToken);

        return conversion is null
            ? null
            : new PurchaseConversionReference(
                item.CatalogItemId, purchaseUom.Id, purchaseUom.Code,
                item.BaseUomId, item.BaseUomCode, conversion.Numerator, conversion.Denominator);
    }
}

public sealed class StandardUomConversionService(CatalogDbContext dbContext)
{
    public async Task<decimal?> ConvertAsync(
        decimal quantity,
        Guid fromUomId,
        Guid toUomId,
        CancellationToken cancellationToken)
    {
        var uoms = await dbContext.Uoms.AsNoTracking()
            .Where(x => (x.Id == fromUomId || x.Id == toUomId) && x.Status == CatalogStatuses.Active)
            .ToListAsync(cancellationToken);
        var from = uoms.SingleOrDefault(x => x.Id == fromUomId);
        var to = uoms.SingleOrDefault(x => x.Id == toUomId);
        if (from is null || to is null || !string.Equals(from.DimensionCode, to.DimensionCode, StringComparison.Ordinal))
        {
            return null;
        }

        var dimensionBase = quantity * from.StandardNumerator / from.StandardDenominator;
        return dimensionBase * to.StandardDenominator / to.StandardNumerator;
    }
}
