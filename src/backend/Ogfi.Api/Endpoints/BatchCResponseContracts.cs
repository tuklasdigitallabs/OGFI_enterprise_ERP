namespace Ogfi.Api.Endpoints;

public sealed record UomResponse(Guid Id, string Code, string Name, string DimensionCode, int PrecisionScale);
public sealed record UomConversionResponse(decimal Quantity, Guid FromUomId, Guid ToUomId, decimal ConvertedQuantity);

public sealed record CatalogItemResponse(Guid Id, string Code, string Name, Guid BaseUomId, string BaseUomCode, string Status);
public sealed record PackagingConversionResponse(
    Guid Id, Guid CatalogItemId, Guid PurchaseUomId, Guid BaseUomId,
    long Numerator, long Denominator, DateOnly EffectiveFromBusinessDate, DateOnly? EffectiveToBusinessDate);

public sealed record InventoryProfileResponse(Guid Id, Guid CatalogItemId, Guid BaseUomId, bool IsStocked, bool NegativeStockAllowed);
public sealed record StockLocationResponse(Guid Id, Guid OutletId, string Code, string Name, string LocationType, bool IsActive);

public sealed record SupplierResponse(Guid Id, string Code, string Name, string Status);
public sealed record SupplierOfferResponse(
    Guid Id, Guid SupplierId, Guid CatalogItemId, string CatalogItemCodeSnapshot, string CatalogItemNameSnapshot,
    string? SupplierItemCode, Guid PurchaseUomId, string PurchaseUomCodeSnapshot,
    Guid BaseUomId, string BaseUomCodeSnapshot, long ConversionNumerator, long ConversionDenominator,
    decimal UnitPrice, string Currency, DateOnly EffectiveFromBusinessDate, DateOnly? EffectiveToBusinessDate);

public sealed record PurchaseOrderSummaryResponse(
    Guid Id, string Number, Guid SupplierId, string SupplierCodeSnapshot, string SupplierNameSnapshot,
    Guid LegalEntityId, Guid OutletId, string Currency, string Status, DateOnly BusinessDate,
    decimal TotalNetAmount, DateTimeOffset CreatedAtUtc);

public sealed record PurchaseOrderLineResponse(
    Guid Id, int LineNumber, Guid SupplierOfferId, Guid CatalogItemId,
    string CatalogItemCodeSnapshot, string CatalogItemNameSnapshot, decimal OrderQuantity,
    Guid PurchaseUomId, string PurchaseUomCodeSnapshot, Guid BaseUomId, string BaseUomCodeSnapshot,
    long ConversionNumerator, long ConversionDenominator, decimal UnitPrice, decimal LineNetAmount);

public sealed record PurchaseOrderResponse(
    Guid Id, string Number, Guid SupplierId, string SupplierCodeSnapshot, string SupplierNameSnapshot,
    Guid LegalEntityId, Guid OutletId, string Currency, string Status, string BusinessDate,
    decimal TotalNetAmount, Guid CreatedByUserId, DateTimeOffset CreatedAtUtc,
    Guid? SubmittedByUserId, DateTimeOffset? SubmittedAtUtc,
    IReadOnlyList<PurchaseOrderLineResponse> Lines);
