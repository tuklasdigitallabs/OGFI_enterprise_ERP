namespace Ogfi.Modules.Catalog;

public static class CatalogPermissionCodes
{
    public const string Read = "catalog.read";
    public const string Write = "catalog.write";
}

public static class CatalogStatuses
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
}

public static class UomIds
{
    public static readonly Guid Each = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid Kilogram = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Gram = Guid.Parse("10000000-0000-0000-0000-000000000003");
    public static readonly Guid Case = Guid.Parse("10000000-0000-0000-0000-000000000004");
}

public sealed class Uom
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string DimensionCode { get; set; }
    public long StandardNumerator { get; set; }
    public long StandardDenominator { get; set; }
    public int PrecisionScale { get; set; }
    public required string Status { get; set; }
}

public sealed class CatalogItem
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid BaseUomId { get; set; }
    public required string Status { get; set; }
}

public sealed class ItemPackagingConversion
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid CatalogItemId { get; set; }
    public Guid PurchaseUomId { get; set; }
    public Guid BaseUomId { get; set; }
    public long Numerator { get; set; }
    public long Denominator { get; set; }
    public DateOnly EffectiveFromBusinessDate { get; set; }
    public DateOnly? EffectiveToBusinessDate { get; set; }
}

public sealed record CatalogItemReference(
    Guid CatalogItemId,
    string ItemCode,
    string ItemName,
    Guid BaseUomId,
    string BaseUomCode);

public sealed record PurchaseConversionReference(
    Guid CatalogItemId,
    Guid PurchaseUomId,
    string PurchaseUomCode,
    Guid BaseUomId,
    string BaseUomCode,
    long Numerator,
    long Denominator);
