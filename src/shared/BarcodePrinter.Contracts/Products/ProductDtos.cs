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

public sealed record SaveProductRequest(
    string Code,
    string Description,
    string? BarcodeValue,
    long? UomId,
    string? Size,
    string? Color,
    long? CategoryId,
    string? DefaultBatch,
    DateOnly? DefaultProductionDate,
    DateOnly? DefaultExpiryDate,
    decimal? DefaultQuantity,
    string? DefaultQuantityText,
    decimal? CartonQuantity,
    int? CartonsPerPallet,
    // Required on update, ignored on create (optimistic concurrency, §11.1 Rev A)
    string? ConcurrencyStamp);

/// <summary>Keyset pagination envelope (B-12): no total count by default —
/// totals are opt-in because COUNT(*) at depth costs more than the page.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, string? NextCursor, bool HasMore);

public sealed record UomDto(long Id, string Code, string Name);
public sealed record CategoryDto(long Id, string Code, string Name);
