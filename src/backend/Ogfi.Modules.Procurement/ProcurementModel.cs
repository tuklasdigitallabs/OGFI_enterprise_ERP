namespace Ogfi.Modules.Procurement;

public static class ProcurementPermissionCodes
{
    public const string SupplierRead = "procurement.supplier.read";
    public const string SupplierWrite = "procurement.supplier.write";
    public const string PurchaseOrderRead = "procurement.purchase_order.read";
    public const string PurchaseOrderWrite = "procurement.purchase_order.write";
    public const string PurchaseOrderSubmit = "procurement.purchase_order.submit";
}

public static class ProcurementStatuses
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
}

public sealed class Supplier
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
}

public sealed class SupplierOffer
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SupplierId { get; set; }
    public Guid CatalogItemId { get; set; }
    public required string CatalogItemCodeSnapshot { get; set; }
    public required string CatalogItemNameSnapshot { get; set; }
    public string? SupplierItemCode { get; set; }
    public Guid PurchaseUomId { get; set; }
    public required string PurchaseUomCodeSnapshot { get; set; }
    public Guid BaseUomId { get; set; }
    public required string BaseUomCodeSnapshot { get; set; }
    public long ConversionNumerator { get; set; }
    public long ConversionDenominator { get; set; }
    public decimal UnitPrice { get; set; }
    public required string Currency { get; set; }
    public DateOnly EffectiveFromBusinessDate { get; set; }
    public DateOnly? EffectiveToBusinessDate { get; set; }
}

public sealed class PurchaseOrder
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Number { get; set; }
    public Guid SupplierId { get; set; }
    public required string SupplierCodeSnapshot { get; set; }
    public required string SupplierNameSnapshot { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid OutletId { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public DateOnly BusinessDate { get; set; }
    public decimal TotalNetAmount { get; set; }
    public long Version { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? SubmittedByUserId { get; set; }
    public DateTimeOffset? SubmittedAtUtc { get; set; }
    public List<PurchaseOrderLine> Lines { get; set; } = [];
}

public sealed class PurchaseOrderLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public int LineNumber { get; set; }
    public Guid SupplierOfferId { get; set; }
    public Guid CatalogItemId { get; set; }
    public required string CatalogItemCodeSnapshot { get; set; }
    public required string CatalogItemNameSnapshot { get; set; }
    public decimal OrderQuantity { get; set; }
    public Guid PurchaseUomId { get; set; }
    public required string PurchaseUomCodeSnapshot { get; set; }
    public Guid BaseUomId { get; set; }
    public required string BaseUomCodeSnapshot { get; set; }
    public long ConversionNumerator { get; set; }
    public long ConversionDenominator { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineNetAmount { get; set; }
}

public sealed record SupplierOfferReferenceInput(
    Guid CatalogItemId,
    string CatalogItemCode,
    string CatalogItemName,
    Guid PurchaseUomId,
    string PurchaseUomCode,
    Guid BaseUomId,
    string BaseUomCode,
    long ConversionNumerator,
    long ConversionDenominator);

public sealed record PurchaseOrderLineInput(Guid SupplierOfferId, decimal Quantity);

public sealed record PurchaseOrderApprovalRequestedV1(
    Guid EventId,
    Guid TenantId,
    Guid PurchaseOrderId,
    int ApprovalRound,
    long SubjectVersion,
    Guid RequestedByUserId,
    Guid LegalEntityId,
    Guid OutletId,
    DateOnly BusinessDate,
    decimal PurchaseOrderTotal,
    string Currency,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed class ProcurementRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
