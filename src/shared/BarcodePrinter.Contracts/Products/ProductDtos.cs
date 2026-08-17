namespace BarcodePrinter.Contracts.Products;

/// <summary>Grid row: intentionally narrow (blueprint §11.2 — no SELECT *,
/// thumbnails only in grids).</summary>
public sealed record ProductSummary(
    long Id,
    string Code,
    string Description,
    string? Uom,
    string? Size,
    string? Color,
    string? DefaultBatch,
    bool IsActive,
    bool HasImage,
    string? ImageHash);

public sealed record ProductDetail(
    long Id,
    string Code,
    string Description,
    string? BarcodeValue,
    long? UomId,
    string? Uom,
    string? Size,
    string? Color,
    long? CategoryId,
    string? Category,
    string? DefaultBatch,
    DateOnly? DefaultProductionDate,
    DateOnly? DefaultExpiryDate,
    decimal? DefaultQuantity,
    string? DefaultQuantityText,
    decimal? CartonQuantity,
    int? CartonsPerPallet,
    bool IsActive,
    bool HasImage,
    string? ImageHash,
    string ConcurrencyStamp,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc);

/// <summary>
/// Product master. Deliberately does NOT carry a barcode value, a production
/// date, an expiry date or a category:
///
///   * the product CODE is the barcode — one value that cannot drift out of
///     step with itself (the label resolves it, see ProductValues.BarcodeValue);
///   * production and expiry dates describe a print RUN, not a product, and are
///     entered on the Print Labels screen where they default to today and
///     today + 1 year;
///   * category was unreachable master data (nothing could create one), so it
///     only ever blocked Excel imports.
///
/// The underlying columns still exist and are left untouched by a save, so no
/// existing value is destroyed by this contract change.
/// </summary>
public sealed record SaveProductRequest(
    string Code,
    string Description,
    long? UomId,
    string? Size,
    string? Color,
    string? DefaultBatch,
    decimal? DefaultQuantity,
    string? DefaultQuantityText,
    decimal? CartonQuantity,
    int? CartonsPerPallet,
    // Required on update, ignored on create (optimistic concurrency, §11.1 Rev A)
    string? ConcurrencyStamp,
    // Free-text UOM entry (§10): when UomId is null and this is set, the server
    // finds or creates the UOM row — arbitrary units without breaking the FK
    // that Excel import validation relies on.
    string? UomCode = null);

/// <summary>Keyset pagination envelope (B-12): no total count by default —
/// totals are opt-in because COUNT(*) at depth costs more than the page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

public sealed record UomDto(long Id, string Code, string Name);
public sealed record CategoryDto(long Id, string Code, string Name);
