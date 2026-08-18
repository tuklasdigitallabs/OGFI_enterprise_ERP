namespace Ogfi.Modules.Procurement;

public static class ProcurementPermissionCodes
{
    public const string SupplierRead = "procurement.supplier.read";
    public const string SupplierWrite = "procurement.supplier.write";
    public const string PurchaseOrderRead = "procurement.purchase_order.read";
    public const string PurchaseOrderWrite = "procurement.purchase_order.write";
    public const string PurchaseOrderSubmit = "procurement.purchase_order.submit";
    public const string PurchaseOrderApprove = "procurement.purchase_order.approve";
    public const string GoodsReceiptRead = "procurement.goods_receipt.read";
    public const string GoodsReceiptWrite = "procurement.goods_receipt.write";
    public const string GoodsReceiptPost = "procurement.goods_receipt.post";
}

public static class ProcurementStatuses
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
    public const string Draft = "DRAFT";
    public const string Submitted = "SUBMITTED";
    public const string Approved = "APPROVED";
    public const string Posted = "POSTED";
}

public sealed class Supplier
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Status { get; set; }
    public long Version { get; set; } = 1;
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
    public decimal ReceivedQuantity { get; set; }
    public Guid PurchaseUomId { get; set; }
    public required string PurchaseUomCodeSnapshot { get; set; }
    public Guid BaseUomId { get; set; }
    public required string BaseUomCodeSnapshot { get; set; }
    public long ConversionNumerator { get; set; }
    public long ConversionDenominator { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineNetAmount { get; set; }
}

public sealed class GoodsReceipt
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Number { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public required string PurchaseOrderNumberSnapshot { get; set; }
    public Guid SupplierId { get; set; }
    public required string SupplierCodeSnapshot { get; set; }
    public required string SupplierNameSnapshot { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid OutletId { get; set; }
    public Guid StockLocationId { get; set; }
    public required string StockLocationCodeSnapshot { get; set; }
    public required string Currency { get; set; }
    public DateOnly BusinessDate { get; set; }
    public required string Status { get; set; }
    public decimal TotalNetAmount { get; set; }
    public long Version { get; set; } = 1;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public Guid? PostedByUserId { get; set; }
    public DateTimeOffset? PostedAtUtc { get; set; }
    public List<GoodsReceiptLine> Lines { get; set; } = [];
}

public sealed class GoodsReceiptLine
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid GoodsReceiptId { get; set; }
    public int LineNumber { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public Guid CatalogItemId { get; set; }
    public required string CatalogItemCodeSnapshot { get; set; }
    public required string CatalogItemNameSnapshot { get; set; }
    public decimal ReceivedQuantity { get; set; }
    public Guid PurchaseUomId { get; set; }
    public required string PurchaseUomCodeSnapshot { get; set; }
    public Guid BaseUomId { get; set; }
    public required string BaseUomCodeSnapshot { get; set; }
    public long ConversionNumerator { get; set; }
    public long ConversionDenominator { get; set; }
    public decimal NormalizedBaseQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal LineNetAmount { get; set; }
}

public sealed class GoodsReceiptPostingCommand
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required string RequestHash { get; set; }
    public Guid GoodsReceiptId { get; set; }
    public long ResultVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
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
public sealed record GoodsReceiptLineInput(Guid PurchaseOrderLineId, decimal Quantity);
public sealed record ReceivingStockLocationReference(Guid StockLocationId, Guid OutletId, string Code);
public sealed record GoodsReceiptPostResult(GoodsReceipt Receipt, bool IsReplay);

public sealed record PurchaseOrderApprovalContext(
    decimal PurchaseOrderTotal,
    string Currency,
    Guid OutletId,
    Guid RequesterUserId);

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
    PurchaseOrderApprovalContext ApprovalContext,
    string CorrelationId,
    DateTimeOffset OccurredAtUtc);

public sealed record PurchaseOrderApprovalOutcome(
    Guid WorkflowInstanceId,
    Guid WorkflowTaskId,
    Guid PurchaseOrderId,
    int ApprovalRound,
    long SubjectVersion,
    string Decision,
    Guid ActorUserId,
    DateTimeOffset DecidedAtUtc,
    string CorrelationId);

public sealed class ProcurementRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
