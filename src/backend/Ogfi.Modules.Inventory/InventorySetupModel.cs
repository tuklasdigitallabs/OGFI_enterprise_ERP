namespace Ogfi.Modules.Inventory;

public static class InventoryPermissionCodes
{
    public const string SetupRead = "inventory.setup.read";
    public const string SetupWrite = "inventory.setup.write";
    public const string StockRead = "inventory.stock.read";
    public const string MovementRead = "inventory.movement.read";
    public const string StockRebuild = "inventory.stock.rebuild";
}

public sealed class InventoryProfile
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CatalogItemId { get; set; }
    public Guid BaseUomId { get; set; }
    public bool IsStocked { get; set; } = true;
    public bool NegativeStockAllowed { get; set; }
}

public sealed class StockLocation
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OutletId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string LocationType { get; set; }
    public bool IsActive { get; set; } = true;
}
