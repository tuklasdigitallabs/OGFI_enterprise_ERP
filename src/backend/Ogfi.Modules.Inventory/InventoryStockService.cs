using System.Data;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.Modules.Inventory.Persistence;

namespace Ogfi.Modules.Inventory;

public sealed class GoodsReceiptPostedConsumer(InventoryDbContext dbContext, TimeProvider timeProvider)
{
    public async Task<bool> ApplyAsync(GoodsReceiptPostedV1 message, CancellationToken cancellationToken)
    {
        if (message.EventId == Guid.Empty || message.TenantId == Guid.Empty || message.Lines.Count == 0)
        {
            throw new InventoryRuleException("INVENTORY.EVENT.INVALID", "GoodsReceiptPosted event envelope is invalid.");
        }

        if (await dbContext.SourceEffects.AsNoTracking()
            .AnyAsync(x => x.TenantId == message.TenantId && x.SourceEventId == message.EventId, cancellationToken))
        {
            return false;
        }

        var location = await dbContext.StockLocations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == message.TenantId && x.Id == message.StockLocationId, cancellationToken);
        if (location is null || !location.IsActive || location.OutletId != message.OutletId)
        {
            throw new InventoryRuleException("INVENTORY.STOCK_LOCATION.INVALID", "Receiving Stock Location is missing, inactive or outside the event Outlet.");
        }

        var itemIds = message.Lines.Select(x => x.CatalogItemId).Distinct().ToArray();
        var profiles = await dbContext.InventoryProfiles.AsNoTracking()
            .Where(x => x.TenantId == message.TenantId && itemIds.Contains(x.CatalogItemId))
            .ToDictionaryAsync(x => x.CatalogItemId, cancellationToken);
        if (profiles.Count != itemIds.Length || profiles.Values.Any(x => !x.IsStocked))
        {
            throw new InventoryRuleException("INVENTORY.PROFILE.INVALID", "One or more received Catalog Items do not have an active stocked Inventory Profile.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            if (await dbContext.SourceEffects.AnyAsync(
                x => x.TenantId == message.TenantId && x.SourceEventId == message.EventId,
                cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            var positions = await dbContext.StockPositions
                .Where(x => x.TenantId == message.TenantId && x.StockLocationId == message.StockLocationId && itemIds.Contains(x.CatalogItemId))
                .ToListAsync(cancellationToken);
            var positionMap = positions.ToDictionary(x => new PositionKey(x.CatalogItemId, x.StockLocationId, x.BaseUomId));

            foreach (var line in message.Lines.OrderBy(x => x.LineNumber))
            {
                if (!profiles.TryGetValue(line.CatalogItemId, out var profile)
                    || profile.BaseUomId != line.BaseUomId
                    || line.ConversionNumerator <= 0
                    || line.ConversionDenominator <= 0
                    || line.ReceivedQuantity <= 0)
                {
                    throw new InventoryRuleException("INVENTORY.UOM.INVALID", "Goods Receipt line is incompatible with the Inventory Profile or conversion context.");
                }

                var normalized = Normalize(line.ReceivedQuantity, line.ConversionNumerator, line.ConversionDenominator);
                if (normalized != line.NormalizedBaseQuantity)
                {
                    throw new InventoryRuleException("INVENTORY.UOM.INVALID", "Published normalized quantity does not match the immutable conversion snapshot.");
                }

                dbContext.InventoryMovements.Add(new InventoryMovement
                {
                    Id = Guid.NewGuid(),
                    TenantId = message.TenantId,
                    MovementType = InventoryMovementTypes.PurchaseReceipt,
                    SourceEventId = message.EventId,
                    SourceDocumentId = message.GoodsReceiptId,
                    SourceLineId = line.GoodsReceiptLineId,
                    PurchaseOrderId = message.PurchaseOrderId,
                    PurchaseOrderLineId = line.PurchaseOrderLineId,
                    CatalogItemId = line.CatalogItemId,
                    CatalogItemCodeSnapshot = line.CatalogItemCodeSnapshot,
                    CatalogItemNameSnapshot = line.CatalogItemNameSnapshot,
                    StockLocationId = message.StockLocationId,
                    StockLocationCodeSnapshot = message.StockLocationCodeSnapshot,
                    OutletId = message.OutletId,
                    BaseUomId = line.BaseUomId,
                    BaseUomCodeSnapshot = line.BaseUomCodeSnapshot,
                    QuantityBaseUom = normalized,
                    BusinessDate = message.BusinessDate,
                    OccurredAtUtc = message.OccurredAtUtc,
                    CorrelationId = message.CorrelationId
                });

                var key = new PositionKey(line.CatalogItemId, message.StockLocationId, line.BaseUomId);
                if (!positionMap.TryGetValue(key, out var position))
                {
                    position = new StockPosition
                    {
                        Id = Guid.NewGuid(),
                        TenantId = message.TenantId,
                        CatalogItemId = line.CatalogItemId,
                        CatalogItemCodeSnapshot = line.CatalogItemCodeSnapshot,
                        CatalogItemNameSnapshot = line.CatalogItemNameSnapshot,
                        StockLocationId = message.StockLocationId,
                        StockLocationCodeSnapshot = message.StockLocationCodeSnapshot,
                        OutletId = message.OutletId,
                        BaseUomId = line.BaseUomId,
                        BaseUomCodeSnapshot = line.BaseUomCodeSnapshot,
                        QuantityOnHand = 0,
                        Version = 1
                    };
                    positionMap.Add(key, position);
                    dbContext.StockPositions.Add(position);
                }

                position.CatalogItemCodeSnapshot = line.CatalogItemCodeSnapshot;
                position.CatalogItemNameSnapshot = line.CatalogItemNameSnapshot;
                position.StockLocationCodeSnapshot = message.StockLocationCodeSnapshot;
                position.BaseUomCodeSnapshot = line.BaseUomCodeSnapshot;
                position.QuantityOnHand = decimal.Round(position.QuantityOnHand + normalized, 6, MidpointRounding.ToEven);
                position.LastMovementOccurredAtUtc = message.OccurredAtUtc;
                if (dbContext.Entry(position).State != EntityState.Added)
                {
                    position.Version++;
                }
            }

            dbContext.SourceEffects.Add(new InventorySourceEffect
            {
                Id = Guid.NewGuid(),
                TenantId = message.TenantId,
                SourceEventId = message.EventId,
                SourceType = "Procurement.GoodsReceiptPosted",
                SourceDocumentId = message.GoodsReceiptId,
                CorrelationId = message.CorrelationId,
                OccurredAtUtc = message.OccurredAtUtc,
                ProcessedAtUtc = timeProvider.GetUtcNow()
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            if (await dbContext.SourceEffects.AsNoTracking()
                .AnyAsync(x => x.TenantId == message.TenantId && x.SourceEventId == message.EventId, cancellationToken))
            {
                return false;
            }
            throw;
        }
    }

    private static decimal Normalize(decimal quantity, long numerator, long denominator)
        => decimal.Round(quantity * numerator / denominator, 6, MidpointRounding.ToEven);

    private readonly record struct PositionKey(Guid CatalogItemId, Guid StockLocationId, Guid BaseUomId);
}

public sealed class StockPositionRebuildService(InventoryDbContext dbContext)
{
    public async Task<int> RebuildAsync(
        Guid tenantId,
        IReadOnlyCollection<Guid> allowedOutletIds,
        Guid? outletId,
        Guid? catalogItemId,
        CancellationToken cancellationToken)
    {
        var allowed = allowedOutletIds.Distinct().ToArray();
        if (allowed.Length == 0)
        {
            return 0;
        }

        var movementQuery = dbContext.InventoryMovements.AsNoTracking()
            .Where(x => x.TenantId == tenantId && allowed.Contains(x.OutletId));
        var positionQuery = dbContext.StockPositions
            .Where(x => x.TenantId == tenantId && allowed.Contains(x.OutletId));
        if (outletId is Guid outlet)
        {
            movementQuery = movementQuery.Where(x => x.OutletId == outlet);
            positionQuery = positionQuery.Where(x => x.OutletId == outlet);
        }
        if (catalogItemId is Guid item)
        {
            movementQuery = movementQuery.Where(x => x.CatalogItemId == item);
            positionQuery = positionQuery.Where(x => x.CatalogItemId == item);
        }

        var movements = await movementQuery
            .OrderBy(x => x.OccurredAtUtc)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var aggregates = movements
            .GroupBy(x => new { x.CatalogItemId, x.StockLocationId, x.OutletId, x.BaseUomId })
            .Select(group => new
            {
                group.Key.CatalogItemId,
                group.Key.StockLocationId,
                group.Key.OutletId,
                group.Key.BaseUomId,
                Quantity = group.Sum(x => x.QuantityBaseUom),
                Last = group.Last()
            })
            .ToArray();

        var existing = await positionQuery.ToListAsync(cancellationToken);
        var map = existing.ToDictionary(x => new PositionKey(x.CatalogItemId, x.StockLocationId, x.BaseUomId));
        foreach (var position in existing)
        {
            position.QuantityOnHand = 0;
            position.LastMovementOccurredAtUtc = null;
            position.Version++;
        }

        foreach (var aggregate in aggregates)
        {
            var key = new PositionKey(aggregate.CatalogItemId, aggregate.StockLocationId, aggregate.BaseUomId);
            if (!map.TryGetValue(key, out var position))
            {
                position = new StockPosition
                {
                    Id = Guid.NewGuid(),
                    TenantId = tenantId,
                    CatalogItemId = aggregate.CatalogItemId,
                    CatalogItemCodeSnapshot = aggregate.Last.CatalogItemCodeSnapshot,
                    CatalogItemNameSnapshot = aggregate.Last.CatalogItemNameSnapshot,
                    StockLocationId = aggregate.StockLocationId,
                    StockLocationCodeSnapshot = aggregate.Last.StockLocationCodeSnapshot,
                    OutletId = aggregate.OutletId,
                    BaseUomId = aggregate.BaseUomId,
                    BaseUomCodeSnapshot = aggregate.Last.BaseUomCodeSnapshot,
                    Version = 1
                };
                dbContext.StockPositions.Add(position);
                map.Add(key, position);
            }
            position.CatalogItemCodeSnapshot = aggregate.Last.CatalogItemCodeSnapshot;
            position.CatalogItemNameSnapshot = aggregate.Last.CatalogItemNameSnapshot;
            position.StockLocationCodeSnapshot = aggregate.Last.StockLocationCodeSnapshot;
            position.BaseUomCodeSnapshot = aggregate.Last.BaseUomCodeSnapshot;
            position.QuantityOnHand = decimal.Round(aggregate.Quantity, 6, MidpointRounding.ToEven);
            position.LastMovementOccurredAtUtc = aggregate.Last.OccurredAtUtc;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return aggregates.Length;
    }

    private readonly record struct PositionKey(Guid CatalogItemId, Guid StockLocationId, Guid BaseUomId);
}
