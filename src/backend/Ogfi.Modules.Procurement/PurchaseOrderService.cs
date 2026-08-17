using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ogfi.BuildingBlocks.Messaging;
using Ogfi.BuildingBlocks.Time;
using Ogfi.Modules.Procurement.Persistence;

namespace Ogfi.Modules.Procurement;

public sealed class PurchaseOrderService(ProcurementDbContext dbContext)
{
    public async Task<Supplier> CreateSupplierAsync(Guid tenantId, string code, string name, CancellationToken cancellationToken)
    {
        var normalizedCode = NormalizeCode(code);
        if (await dbContext.Suppliers.AnyAsync(x => x.TenantId == tenantId && x.Code == normalizedCode, cancellationToken))
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER.CODE_EXISTS", "Supplier code already exists for this tenant.");
        }

        var supplier = new Supplier
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Code = normalizedCode,
            Name = RequiredText(name, "Supplier name"),
            Status = ProcurementStatuses.Active,
            Version = 1
        };
        dbContext.Suppliers.Add(supplier);
        await dbContext.SaveChangesAsync(cancellationToken);
        return supplier;
    }

    public async Task<Supplier> UpdateSupplierAsync(
        Guid tenantId,
        Guid supplierId,
        long expectedVersion,
        string name,
        CancellationToken cancellationToken)
    {
        var supplier = await dbContext.Suppliers.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == supplierId,
            cancellationToken);
        if (supplier is null)
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER.NOT_FOUND", "Supplier does not exist.");
        }
        if (supplier.Version != expectedVersion)
        {
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Supplier version does not match If-Match.");
        }

        supplier.Name = RequiredText(name, "Supplier name");
        supplier.Version++;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Supplier was modified by another request.");
        }
        return supplier;
    }

    public async Task<SupplierOffer> CreateSupplierOfferAsync(
        Guid tenantId,
        Guid supplierId,
        SupplierOfferReferenceInput reference,
        string? supplierItemCode,
        decimal unitPrice,
        string currency,
        DateOnly effectiveFrom,
        DateOnly? effectiveTo,
        CancellationToken cancellationToken)
    {
        if (unitPrice < 0 || reference.ConversionNumerator <= 0 || reference.ConversionDenominator <= 0)
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER_OFFER.INVALID", "Supplier Offer price and conversion must be valid.");
        }
        if (effectiveTo is DateOnly to && to < effectiveFrom)
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER_OFFER.INVALID", "Supplier Offer effective date range is invalid.");
        }

        var supplier = await dbContext.Suppliers.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == supplierId && x.Status == ProcurementStatuses.Active,
            cancellationToken);
        if (supplier is null)
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER.INVALID", "Supplier is not active or does not exist.");
        }

        var normalizedCurrency = NormalizeCurrency(currency);
        var overlap = await dbContext.SupplierOffers.AnyAsync(x =>
            x.TenantId == tenantId && x.SupplierId == supplierId && x.CatalogItemId == reference.CatalogItemId
            && x.PurchaseUomId == reference.PurchaseUomId
            && (x.EffectiveToBusinessDate == null || x.EffectiveToBusinessDate >= effectiveFrom)
            && (effectiveTo == null || x.EffectiveFromBusinessDate <= effectiveTo), cancellationToken);
        if (overlap)
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER_OFFER.OVERLAP", "An overlapping Supplier Offer already exists.");
        }

        var offer = new SupplierOffer
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            SupplierId = supplierId,
            CatalogItemId = reference.CatalogItemId,
            CatalogItemCodeSnapshot = reference.CatalogItemCode,
            CatalogItemNameSnapshot = reference.CatalogItemName,
            SupplierItemCode = string.IsNullOrWhiteSpace(supplierItemCode) ? null : supplierItemCode.Trim(),
            PurchaseUomId = reference.PurchaseUomId,
            PurchaseUomCodeSnapshot = reference.PurchaseUomCode,
            BaseUomId = reference.BaseUomId,
            BaseUomCodeSnapshot = reference.BaseUomCode,
            ConversionNumerator = reference.ConversionNumerator,
            ConversionDenominator = reference.ConversionDenominator,
            UnitPrice = decimal.Round(unitPrice, 4, MidpointRounding.ToEven),
            Currency = normalizedCurrency,
            EffectiveFromBusinessDate = effectiveFrom,
            EffectiveToBusinessDate = effectiveTo
        };
        dbContext.SupplierOffers.Add(offer);
        await dbContext.SaveChangesAsync(cancellationToken);
        return offer;
    }

    public async Task<PurchaseOrder> CreateDraftAsync(
        Guid tenantId,
        Guid userId,
        Guid supplierId,
        Guid legalEntityId,
        Guid outletId,
        string currency,
        BusinessDate businessDate,
        IReadOnlyCollection<PurchaseOrderLineInput> requestedLines,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        if (requestedLines.Count == 0)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "Purchase Order must contain at least one line.");
        }

        var supplier = await dbContext.Suppliers.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Id == supplierId && x.Status == ProcurementStatuses.Active,
            cancellationToken);
        if (supplier is null)
        {
            throw new ProcurementRuleException("PROCUREMENT.SUPPLIER.INVALID", "Supplier is not active or does not exist.");
        }

        var normalizedCurrency = NormalizeCurrency(currency);
        var offerIds = requestedLines.Select(x => x.SupplierOfferId).Distinct().ToArray();
        var offers = await dbContext.SupplierOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && offerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (offers.Count != offerIds.Length)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "One or more Supplier Offers are missing.");
        }

        var po = new PurchaseOrder
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Number = $"PO-{businessDate.Value:yyyyMMdd}-{Guid.NewGuid():N}"[..19].ToUpperInvariant(),
            SupplierId = supplier.Id,
            SupplierCodeSnapshot = supplier.Code,
            SupplierNameSnapshot = supplier.Name,
            LegalEntityId = legalEntityId,
            OutletId = outletId,
            Currency = normalizedCurrency,
            Status = ProcurementStatuses.Draft,
            BusinessDate = businessDate.Value,
            Version = 1,
            CreatedByUserId = userId,
            CreatedAtUtc = occurredAtUtc
        };

        po.Lines = BuildLines(po, supplierId, normalizedCurrency, businessDate.Value, requestedLines, offers);
        po.TotalNetAmount = po.Lines.Sum(x => x.LineNetAmount);
        dbContext.PurchaseOrders.Add(po);
        await dbContext.SaveChangesAsync(cancellationToken);
        return po;
    }

    public async Task<PurchaseOrder> UpdateDraftLinesAsync(
        Guid tenantId,
        Guid purchaseOrderId,
        long expectedVersion,
        IReadOnlyCollection<PurchaseOrderLineInput> requestedLines,
        CancellationToken cancellationToken)
    {
        if (requestedLines.Count == 0)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "Purchase Order must contain at least one line.");
        }

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var po = await dbContext.PurchaseOrders
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == purchaseOrderId, cancellationToken);
        EnsureEditable(po, expectedVersion);

        var offerIds = requestedLines.Select(x => x.SupplierOfferId).Distinct().ToArray();
        var offers = await dbContext.SupplierOffers.AsNoTracking()
            .Where(x => x.TenantId == tenantId && offerIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (offers.Count != offerIds.Length)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "One or more Supplier Offers are missing.");
        }

        var replacementLines = BuildLines(po!, po!.SupplierId, po.Currency, po.BusinessDate, requestedLines, offers);

        await dbContext.PurchaseOrderLines
            .Where(x => x.TenantId == tenantId && x.PurchaseOrderId == purchaseOrderId)
            .ExecuteDeleteAsync(cancellationToken);

        dbContext.PurchaseOrderLines.AddRange(replacementLines);
        po.Lines = replacementLines;
        po.TotalNetAmount = replacementLines.Sum(x => x.LineNetAmount);
        po.Version++;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Purchase Order was modified by another request.");
        }

        return po;
    }

    public async Task<PurchaseOrder> SubmitAsync(
        Guid tenantId,
        Guid purchaseOrderId,
        long expectedVersion,
        Guid userId,
        BusinessDate businessDate,
        string correlationId,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var po = await dbContext.PurchaseOrders.Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == purchaseOrderId, cancellationToken);
        EnsureEditable(po, expectedVersion);
        if (po!.Lines.Count == 0 || po.TotalNetAmount < 0)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "Purchase Order does not satisfy submission validation.");
        }

        po.Status = ProcurementStatuses.Submitted;
        po.SubmittedByUserId = userId;
        po.SubmittedAtUtc = occurredAtUtc;
        po.BusinessDate = businessDate.Value;
        po.Version++;

        var eventId = Guid.NewGuid();
        var payload = new PurchaseOrderApprovalRequestedV1(
            eventId,
            tenantId,
            po.Id,
            1,
            po.Version,
            userId,
            po.LegalEntityId,
            po.OutletId,
            businessDate.Value,
            new PurchaseOrderApprovalContext(po.TotalNetAmount, po.Currency, po.OutletId, userId),
            correlationId,
            occurredAtUtc);

        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            TenantId = tenantId,
            Type = "Procurement.PurchaseOrderApprovalRequested",
            SchemaVersion = 1,
            OccurredAtUtc = occurredAtUtc,
            CorrelationId = correlationId,
            CausationId = $"PO:{po.Id}:APPROVAL:1",
            Payload = JsonSerializer.Serialize(payload)
        });

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Purchase Order was modified by another request.");
        }
        return po;
    }

    private static List<PurchaseOrderLine> BuildLines(
        PurchaseOrder po,
        Guid supplierId,
        string currency,
        DateOnly businessDate,
        IReadOnlyCollection<PurchaseOrderLineInput> requestedLines,
        IReadOnlyDictionary<Guid, SupplierOffer> offers)
    {
        var result = new List<PurchaseOrderLine>();
        var lineNumber = 1;
        foreach (var request in requestedLines)
        {
            if (request.Quantity <= 0 || !offers.TryGetValue(request.SupplierOfferId, out var offer)
                || offer.SupplierId != supplierId || !string.Equals(offer.Currency, currency, StringComparison.Ordinal)
                || offer.EffectiveFromBusinessDate > businessDate
                || (offer.EffectiveToBusinessDate is DateOnly to && to < businessDate))
            {
                throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "Supplier Offer or ordered quantity is invalid for the Purchase Order context.");
            }

            var quantity = decimal.Round(request.Quantity, 6, MidpointRounding.ToEven);
            result.Add(new PurchaseOrderLine
            {
                Id = Guid.NewGuid(),
                TenantId = po.TenantId,
                PurchaseOrderId = po.Id,
                LineNumber = lineNumber++,
                SupplierOfferId = offer.Id,
                CatalogItemId = offer.CatalogItemId,
                CatalogItemCodeSnapshot = offer.CatalogItemCodeSnapshot,
                CatalogItemNameSnapshot = offer.CatalogItemNameSnapshot,
                OrderQuantity = quantity,
                PurchaseUomId = offer.PurchaseUomId,
                PurchaseUomCodeSnapshot = offer.PurchaseUomCodeSnapshot,
                BaseUomId = offer.BaseUomId,
                BaseUomCodeSnapshot = offer.BaseUomCodeSnapshot,
                ConversionNumerator = offer.ConversionNumerator,
                ConversionDenominator = offer.ConversionDenominator,
                UnitPrice = offer.UnitPrice,
                LineNetAmount = decimal.Round(quantity * offer.UnitPrice, 4, MidpointRounding.ToEven)
            });
        }
        return result;
    }

    private static void EnsureEditable(PurchaseOrder? po, long expectedVersion)
    {
        if (po is null)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_FOUND", "Purchase Order does not exist.");
        }
        if (po.Status != ProcurementStatuses.Draft)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "Only a DRAFT Purchase Order can be changed or submitted.");
        }
        if (po.Version != expectedVersion)
        {
            throw new ProcurementRuleException("CONCURRENCY.CONFLICT", "Purchase Order version does not match If-Match.");
        }
    }

    private static string NormalizeCode(string value)
        => RequiredText(value, "Code").ToUpperInvariant();

    private static string NormalizeCurrency(string value)
    {
        var currency = RequiredText(value, "Currency").ToUpperInvariant();
        if (currency.Length != 3)
        {
            throw new ProcurementRuleException("PROCUREMENT.PO.NOT_APPROVABLE", "Currency must be an ISO-style three-character code.");
        }
        return currency;
    }

    private static string RequiredText(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ProcurementRuleException("VALIDATION.REQUIRED", $"{field} is required.");
        }
        return value.Trim();
    }
}
