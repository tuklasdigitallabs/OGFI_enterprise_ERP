namespace Ogfi.Modules.Inventory;

public static class InventoryMovementTypes
{
    public const string PurchaseReceipt = "PURCHASE_RECEIPT";
}

public sealed class InventorySourceEffect
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid SourceEventId { get; set; }
    public required string SourceType { get; set; }
    public Guid SourceDocumentId { get; set; }
    public required string CorrelationId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}

public sealed class InventoryMovement
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string MovementType { get; set; }
    public Guid SourceEventId { get; set; }
    public Guid SourceDocumentId { get; set; }
    public Guid SourceLineId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public Guid CatalogItemId { get; set; }
    public required string CatalogItemCodeSnapshot { get; set; }
    public required string CatalogItemNameSnapshot { get; set; }
    public Guid StockLocationId { get; set; }
    public required string StockLocationCodeSnapshot { get; set; }
    public Guid OutletId { get; set; }
    public Guid BaseUomId { get; set; }
    public required string BaseUomCodeSnapshot { get; set; }
    public decimal QuantityBaseUom { get; set; }
    public DateOnly BusinessDate { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public required string CorrelationId { get; set; }
}

public sealed class StockPosition
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CatalogItemId { get; set; }
    public required string CatalogItemCodeSnapshot { get; set; }
    public required string CatalogItemNameSnapshot { get; set; }
    public Guid StockLocationId { get; set; }
    public required string StockLocationCodeSnapshot { get; set; }
    public Guid OutletId { get; set; }
    public Guid BaseUomId { get; set; }
    public required string BaseUomCodeSnapshot { get; set; }
    public decimal QuantityOnHand { get; set; }
    public DateTimeOffset? LastMovementOccurredAtUtc { get; set; }
    public long Version { get; set; } = 1;
}

public sealed class InventoryRuleException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
