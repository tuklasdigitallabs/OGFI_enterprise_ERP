using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Ogfi.BuildingBlocks.Messaging;
using Ogfi.BuildingBlocks.Messaging.Contracts;
using Ogfi.BuildingBlocks.Time;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Modules.Procurement;

public sealed class GoodsReceiptService(ProcurementDbContext dbContext)
{
    public async Task<GoodsReceipt> CreateDraftAsync(
        Guid tenantId,
        Guid userId,
        Guid purchaseOrderId,
        ReceivingStockLocationReference stockLocation,
        BusinessDate businessDate,
        IReadOnlyCollection<GoodsReceiptLineInput> requestedLines,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (requestedLines.Count == 0 || requestedLines.Select(x => x.PurchaseOrderLineId).Distinct().Count() != requestedLines.Count)
        {
            throw new ProcurementRuleException("PROCUREMENT.GR.INVALID", "Goods Receipt must contain unique Purchase Order lines.");
        }

        var po = await dbContext.PurchaseOrders.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == purchaseOrderId, cancellationToken);
        if (po is null)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_FOUND", "Purchase Order does not exist.");
        }
        if (po.Status != ProcurementStatuses.Approved)
        {
            throw new ProcurementRuleException("PROCUREMENT.GR.PO_NOT_APPROVED", "Goods Receipt requires an APPROVED Purchase Order.");
        }
        if (stockLocation.OutletId != po.OutletId)
        {
            throw new ProcurementRuleException("PROCUREMENT.GR.STOCK_LOCATION_INVALID", "Receiving Stock Location does not belong to the Purchase Order Outlet.");
        }

        var poLines = po.Lines.ToDictionary(x => x.Id);
        var receipt = new GoodsReceipt
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Number = $"GR-{businessDate.Value:yyyyMMdd}-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
            PurchaseOrderId = po.Id,
            PurchaseOrderNumberSnapshot = po.Number,
            SupplierId = po.SupplierId,
            SupplierCodeSnapshot = po.SupplierCodeSnapshot,
            SupplierNameSnapshot = po.SupplierNameSnapshot,
            LegalEntityId = po.LegalEntityId,
            OutletId = po.OutletId,
            StockLocationId = stockLocation.StockLocationId,
            StockLocationCodeSnapshot = stockLocation.Code,
            Currency = po.Currency,
            BusinessDate = businessDate.Value,
            Status = ProcurementStatuses.Draft,
            Version = 1,
            CreatedByUserId = userId,
            CreatedAtUtc = occurredAtUtc
        };

        var lineNumber = 1;
        foreach (var request in requestedLines)
        {
            if (request.Quantity <= 0 || !poLines.TryGetValue(request.PurchaseOrderLineId, out var poLine))
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.LINE_INVALID", "Goods Receipt line does not reference a valid Purchase Order line.");
            }

            var quantity = decimal.Round(request.Quantity, 6, MidpointRounding.ToEven);
            var remaining = decimal.Round(poLine.OrderQuantity - poLine.ReceivedQuantity, 6, MidpointRounding.ToEven);
            if (quantity > remaining)
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.OVER_RECEIPT", "Received quantity exceeds the remaining ordered quantity. RI-01 over-receipt tolerance is zero.");
            }
            if (poLine.ConversionNumerator <= 0 || poLine.ConversionDenominator <= 0)
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.UOM_INVALID", "Purchase Order line conversion snapshot is invalid.");
            }

            var normalized = Normalize(quantity, poLine.ConversionNumerator, poLine.ConversionDenominator);
            receipt.Lines.Add(new GoodsReceiptLine
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                GoodsReceiptId = receipt.Id,
                LineNumber = lineNumber++,
                PurchaseOrderLineId = poLine.Id,
                CatalogItemId = poLine.CatalogItemId,
                CatalogItemCodeSnapshot = poLine.CatalogItemCodeSnapshot,
                CatalogItemNameSnapshot = poLine.CatalogItemNameSnapshot,
                ReceivedQuantity = quantity,
                PurchaseUomId = poLine.PurchaseUomId,
                PurchaseUomCodeSnapshot = poLine.PurchaseUomCodeSnapshot,
                BaseUomId = poLine.BaseUomId,
                BaseUomCodeSnapshot = poLine.BaseUomCodeSnapshot,
                ConversionNumerator = poLine.ConversionNumerator,
                ConversionDenominator = poLine.ConversionDenominator,
                NormalizedBaseQuantity = normalized,
                UnitPrice = poLine.UnitPrice,
                LineNetAmount = decimal.Round(quantity * poLine.UnitPrice, 4, MidpointRounding.ToEven)
            });
        }

        receipt.TotalNetAmount = receipt.Lines.Sum(x => x.LineNetAmount);
        dbContext.GoodsReceipts.Add(receipt);
        await dbContext.SaveChangesAsync(cancellationToken);
        return receipt;
    }

    public async Task<GoodsReceiptPostResult> PostAsync(
        Guid tenantId,
        Guid goodsReceiptId,
        long expectedVersion,
        Guid userId,
        string idempotencyKey,
        ReceivingStockLocationReference stockLocation,
        string correlationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        var normalizedKey = NormalizeIdempotencyKey(idempotencyKey);
        var requestHash = HashRequest(goodsReceiptId, expectedVersion);

        var prior = await dbContext.GoodsReceiptPostingCommands.AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
        if (prior is not null)
        {
            if (prior.GoodsReceiptId != goodsReceiptId || prior.RequestHash != requestHash)
            {
                throw new ProcurementRuleException("IDEMPOTENCY.CONFLICT", "Idempotency key was already used for a different Goods Receipt posting request.");
            }

            var replay = await dbContext.GoodsReceipts.AsNoTracking().Include(x => x.Lines)
                .SingleAsync(x => x.TenantId == tenantId && x.Id == prior.GoodsReceiptId, cancellationToken);
            return new GoodsReceiptPostResult(replay, true);
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var receipt = await dbContext.GoodsReceipts.Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == goodsReceiptId, cancellationToken);
            if (receipt is null)
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.NOT_FOUND", "Goods Receipt does not exist.");
            }
            if (receipt.Status != ProcurementStatuses.Draft)
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.NOT_POSTABLE", "Only a DRAFT Goods Receipt can be posted.");
            }
            if (receipt.Version != expectedVersion)
            {
                throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Goods Receipt version does not match If-Match.");
            }
            if (receipt.Lines.Count == 0)
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.NOT_POSTABLE", "Goods Receipt has no lines.");
            }
            if (receipt.StockLocationId != stockLocation.StockLocationId
                || receipt.OutletId != stockLocation.OutletId
                || !string.Equals(receipt.StockLocationCodeSnapshot, stockLocation.Code, StringComparison.Ordinal))
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.STOCK_LOCATION_INVALID", "Receiving Stock Location no longer matches the Goods Receipt context.");
            }

            var po = await dbContext.PurchaseOrders.Include(x => x.Lines)
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == receipt.PurchaseOrderId, cancellationToken);
            if (po is null)
            {
                throw new ProcurementRuleException("PROCUREMENT.PO.NOT_FOUND", "Purchase Order does not exist.");
            }
            if (po.Status != ProcurementStatuses.Approved)
            {
                throw new ProcurementRuleException("PROCUREMENT.GR.PO_NOT_APPROVED", "Purchase Order is no longer APPROVED.");
            }

            var poLines = po.Lines.ToDictionary(x => x.Id);
            foreach (var line in receipt.Lines)
            {
                if (!poLines.TryGetValue(line.PurchaseOrderLineId, out var poLine)
                    || poLine.CatalogItemId != line.CatalogItemId
                    || poLine.PurchaseUomId != line.PurchaseUomId
                    || poLine.BaseUomId != line.BaseUomId
                    || poLine.ConversionNumerator != line.ConversionNumerator
                    || poLine.ConversionDenominator != line.ConversionDenominator)
                {
                    throw new ProcurementRuleException("PROCUREMENT.GR.UOM_INVALID", "Goods Receipt line no longer matches its immutable Purchase Order line context.");
                }

                var remaining = decimal.Round(poLine.OrderQuantity - poLine.ReceivedQuantity, 6, MidpointRounding.ToEven);
                if (line.ReceivedQuantity <= 0 || line.ReceivedQuantity > remaining)
                {
                    throw new ProcurementRuleException("PROCUREMENT.GR.OVER_RECEIPT", "Posting would exceed the remaining ordered quantity. RI-01 over-receipt tolerance is zero.");
                }
                if (Normalize(line.ReceivedQuantity, line.ConversionNumerator, line.ConversionDenominator) != line.NormalizedBaseQuantity)
                {
                    throw new ProcurementRuleException("PROCUREMENT.GR.UOM_INVALID", "Goods Receipt normalized quantity does not match its conversion snapshot.");
                }

                poLine.ReceivedQuantity = decimal.Round(poLine.ReceivedQuantity + line.ReceivedQuantity, 6, MidpointRounding.ToEven);
            }

            po.Version++;
            receipt.Status = ProcurementStatuses.Posted;
            receipt.PostedByUserId = userId;
            receipt.PostedAtUtc = occurredAtUtc;
            receipt.Version++;

            var eventId = Guid.NewGuid();
            var payload = new GoodsReceiptPostedV1(
                eventId,
                tenantId,
                receipt.Id,
                receipt.Number,
                po.Id,
                po.Number,
                receipt.SupplierId,
                receipt.SupplierCodeSnapshot,
                receipt.SupplierNameSnapshot,
                receipt.LegalEntityId,
                receipt.OutletId,
                receipt.StockLocationId,
                receipt.StockLocationCodeSnapshot,
                receipt.Currency,
                receipt.BusinessDate,
                userId,
                correlationId,
                occurredAtUtc,
                receipt.Lines.OrderBy(x => x.LineNumber).Select(line => new GoodsReceiptPostedLineV1(
                    line.Id,
                    line.LineNumber,
                    line.PurchaseOrderLineId,
                    line.CatalogItemId,
                    line.CatalogItemCodeSnapshot,
                    line.CatalogItemNameSnapshot,
                    line.ReceivedQuantity,
                    line.PurchaseUomId,
                    line.PurchaseUomCodeSnapshot,
                    line.BaseUomId,
                    line.BaseUomCodeSnapshot,
                    line.ConversionNumerator,
                    line.ConversionDenominator,
                    line.NormalizedBaseQuantity,
                    line.UnitPrice,
                    line.LineNetAmount)).ToArray());

            dbContext.OutboxMessages.Add(new OutboxMessage
            {
                Id = eventId,
                TenantId = tenantId,
                Type = "Procurement.GoodsReceiptPosted",
                SchemaVersion = 1,
                OccurredAtUtc = occurredAtUtc,
                CorrelationId = correlationId,
                CausationId = $"GR:{receipt.Id}:POST",
                Payload = JsonSerializer.Serialize(payload)
            });
            dbContext.GoodsReceiptPostingCommands.Add(new GoodsReceiptPostingCommand
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                IdempotencyKey = normalizedKey,
                RequestHash = requestHash,
                GoodsReceiptId = receipt.Id,
                ResultVersion = receipt.Version,
                CreatedAtUtc = occurredAtUtc
            });

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GoodsReceiptPostResult(receipt, false);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Goods Receipt or Purchase Order was modified by another request.");
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Concurrent Goods Receipt posting must be retried with the current state.");
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            var existing = await dbContext.GoodsReceiptPostingCommands.AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IdempotencyKey == normalizedKey, cancellationToken);
            if (existing is not null)
            {
                if (existing.GoodsReceiptId != goodsReceiptId || existing.RequestHash != requestHash)
                {
                    throw new ProcurementRuleException("IDEMPOTENCY.CONFLICT", "Idempotency key was already used for a different Goods Receipt posting request.");
                }
                var replay = await dbContext.GoodsReceipts.AsNoTracking().Include(x => x.Lines)
                    .SingleAsync(x => x.TenantId == tenantId && x.Id == existing.GoodsReceiptId, cancellationToken);
                return new GoodsReceiptPostResult(replay, true);
            }
            throw;
        }
    }

    private static decimal Normalize(decimal quantity, long numerator, long denominator)
        => decimal.Round(quantity * numerator / denominator, 6, MidpointRounding.ToEven);

    private static string NormalizeIdempotencyKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProcurementRuleException("IDEMPOTENCY.KEY_REQUIRED", "Idempotency-Key is required.");
        }
        var key = value.Trim();
        if (key.Length > 128)
        {
            throw new ProcurementRuleException("IDEMPOTENCY.KEY_INVALID", "Idempotency-Key cannot exceed 128 characters.");
        }
        return key;
    }

    private static string HashRequest(Guid receiptId, long expectedVersion)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{receiptId:N}|{expectedVersion}"));
        return Convert.ToHexString(bytes);
    }
}
