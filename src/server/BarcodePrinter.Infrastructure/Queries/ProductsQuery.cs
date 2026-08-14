using System.Text;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Infrastructure.Services;
using Dapper;

namespace BarcodePrinter.Infrastructure.Queries;

/// <summary>
/// Read-side product queries (Dapper, explicit columns, §9.3 search strategy):
///   * no term       → keyset pagination by (code, id) — O(page) at any depth
///   * term &lt; 3 chars → index-backed prefix match, top N
///   * term ≥ 3 chars → ngram FULLTEXT ranked, exact code hit unioned first
/// </summary>
public sealed class ProductsQuery(IDbConnectionFactory connections)
{
    private const int SearchLimit = 50;

    public async Task<PagedResult<ProductSummary>> ListAsync(
        string? term, bool includeInactive, string? cursor, int pageSize, CancellationToken ct)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);   // hard server-side cap (§11.2)
        await using var conn = await connections.OpenAsync(ct);

        var activeFilter = includeInactive ? "" : "AND p.is_active = 1";
        term = term?.Trim();

        if (string.IsNullOrEmpty(term))
        {
            var (afterCode, afterId) = DecodeCursor(cursor);
            var rows = (await conn.QueryAsync<Row>(new CommandDefinition($"""
                SELECT {Columns}
                FROM products p
                LEFT JOIN uoms u ON u.id = p.uom_id
                LEFT JOIN product_images pi ON pi.id = p.primary_image_id
                WHERE (@afterCode IS NULL OR (p.code, p.id) > (@afterCode, @afterId))
                  {activeFilter}
                ORDER BY p.code, p.id
                LIMIT @limit
                """,
                new { afterCode, afterId, limit = pageSize + 1 },
                cancellationToken: ct))).ToList();

            var hasMore = rows.Count > pageSize;
            if (hasMore)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            var next = hasMore ? EncodeCursor(rows[^1].Code, rows[^1].Id) : null;
            return new PagedResult<ProductSummary>(rows.Select(Map).ToList(), next, hasMore);
        }

        if (term.Length < 3)
        {
            var rows = await conn.QueryAsync<Row>(new CommandDefinition($"""
                SELECT {Columns}
                FROM products p
                LEFT JOIN uoms u ON u.id = p.uom_id
                LEFT JOIN product_images pi ON pi.id = p.primary_image_id
                WHERE (p.code LIKE CONCAT(@term, '%') OR p.description LIKE CONCAT(@term, '%'))
                  {activeFilter}
                ORDER BY p.code
                LIMIT @limit
                """,
                new { term, limit = SearchLimit }, cancellationToken: ct));
            return new PagedResult<ProductSummary>(rows.Select(Map).ToList(), null, false);
        }

        // ≥3 chars: exact code hit ranked first, then ngram relevance.
        var searchRows = await conn.QueryAsync<Row>(new CommandDefinition($"""
            SELECT {Columns},
                   (p.code = @term) AS exact_hit,
                   MATCH(p.search_text) AGAINST(@term IN BOOLEAN MODE) AS score
            FROM products p
            LEFT JOIN uoms u ON u.id = p.uom_id
            LEFT JOIN product_images pi ON pi.id = p.primary_image_id
            WHERE (p.code = @term
                   OR MATCH(p.search_text) AGAINST(@term IN BOOLEAN MODE))
              {activeFilter}
            ORDER BY exact_hit DESC, score DESC, p.code
            LIMIT @limit
            """,
            new { term, limit = SearchLimit }, cancellationToken: ct));
        return new PagedResult<ProductSummary>(searchRows.Select(Map).ToList(), null, false);
    }

    public async Task<ProductDetail?> GetDetailAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var r = await conn.QuerySingleOrDefaultAsync<DetailRow>(new CommandDefinition("""
            SELECT CAST(p.id AS SIGNED)            AS Id,
                   p.code                          AS Code,
                   p.description                   AS Description,
                   p.barcode_value                 AS BarcodeValue,
                   CAST(p.uom_id AS SIGNED)        AS UomId,
                   u.code                          AS Uom,
                   p.size                          AS Size,
                   p.color                         AS Color,
                   CAST(p.category_id AS SIGNED)   AS CategoryId,
                   c.name                          AS Category,
                   p.default_batch                 AS DefaultBatch,
                   p.default_production_date       AS DefaultProductionDate,
                   p.default_expiry_date           AS DefaultExpiryDate,
                   p.default_quantity              AS DefaultQuantity,
                   p.default_quantity_text         AS DefaultQuantityText,
                   p.carton_quantity               AS CartonQuantity,
                   p.cartons_per_pallet            AS CartonsPerPallet,
                   p.is_active                     AS IsActive,
                   (p.primary_image_id IS NOT NULL) AS HasImage,
                   pi.content_hash                 AS ImageHash,
                   p.concurrency_stamp             AS ConcurrencyStamp,
                   p.created_at                    AS CreatedAtUtc,
                   p.updated_at                    AS UpdatedAtUtc
            FROM products p
            LEFT JOIN uoms u ON u.id = p.uom_id
            LEFT JOIN product_categories c ON c.id = p.category_id
            LEFT JOIN product_images pi ON pi.id = p.primary_image_id
            WHERE p.id = @id
            """, new { id }, cancellationToken: ct));

        return r is null ? null : new ProductDetail(
            r.Id, r.Code, r.Description, r.BarcodeValue,
            r.UomId, r.Uom, r.Size, r.Color, r.CategoryId, r.Category,
            r.DefaultBatch,
            ToDateOnly(r.DefaultProductionDate), ToDateOnly(r.DefaultExpiryDate),
            r.DefaultQuantity, r.DefaultQuantityText,
            r.CartonQuantity, r.CartonsPerPallet,
            r.IsActive, r.HasImage, r.ImageHash,
            r.ConcurrencyStamp, r.CreatedAtUtc, r.UpdatedAtUtc);
    }

    private static DateOnly? ToDateOnly(DateTime? d) =>
        d is null ? null : DateOnly.FromDateTime(d.Value);

    // Mutable row (not the DTO record): MySQL DATE arrives as DateTime and
    // boolean SQL expressions as long — property mapping converts, positional
    // record constructors do not.
    private sealed class DetailRow
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string? BarcodeValue { get; set; }
        public long? UomId { get; set; }
        public string? Uom { get; set; }
        public string? Size { get; set; }
        public string? Color { get; set; }
        public long? CategoryId { get; set; }
        public string? Category { get; set; }
        public string? DefaultBatch { get; set; }
        public DateTime? DefaultProductionDate { get; set; }
        public DateTime? DefaultExpiryDate { get; set; }
        public decimal? DefaultQuantity { get; set; }
        public string? DefaultQuantityText { get; set; }
        public decimal? CartonQuantity { get; set; }
        public int? CartonsPerPallet { get; set; }
        public bool IsActive { get; set; }
        public bool HasImage { get; set; }
        public string? ImageHash { get; set; }
        public string ConcurrencyStamp { get; set; } = "";
        public DateTime CreatedAtUtc { get; set; }
        public DateTime? UpdatedAtUtc { get; set; }
    }

    public async Task<string?> GetImageHashAsync(long productId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            SELECT pi.content_hash
            FROM products p JOIN product_images pi ON pi.id = p.primary_image_id
            WHERE p.id = @productId
            """, new { productId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<UomDto>> UomsAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return (await conn.QueryAsync<UomDto>(new CommandDefinition(
            "SELECT CAST(id AS SIGNED) AS Id, code AS Code, name AS Name FROM uoms WHERE is_active = 1 ORDER BY code",
            cancellationToken: ct))).ToList();
    }

    public async Task<IReadOnlyList<CategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return (await conn.QueryAsync<CategoryDto>(new CommandDefinition(
            "SELECT CAST(id AS SIGNED) AS Id, code AS Code, name AS Name FROM product_categories WHERE is_active = 1 ORDER BY name",
            cancellationToken: ct))).ToList();
    }

    private const string Columns = """
        CAST(p.id AS SIGNED)             AS Id,
        p.code                           AS Code,
        p.description                    AS Description,
        u.code                           AS Uom,
        p.size                           AS Size,
        p.color                          AS Color,
        p.default_batch                  AS DefaultBatch,
        p.is_active                      AS IsActive,
        (p.primary_image_id IS NOT NULL) AS HasImage,
        pi.content_hash                  AS ImageHash
        """;

    private static ProductSummary Map(Row r) => new(
        r.Id, r.Code, r.Description, r.Uom, r.Size, r.Color,
        r.DefaultBatch, r.IsActive, r.HasImage, r.ImageHash);

    // Mutable class, not a positional record: Dapper property mapping converts
    // MySQL's BIGINT/TINYINT types flexibly where constructor mapping will not.
    private sealed class Row
    {
        public long Id { get; set; }
        public string Code { get; set; } = "";
        public string Description { get; set; } = "";
        public string? Uom { get; set; }
        public string? Size { get; set; }
        public string? Color { get; set; }
        public string? DefaultBatch { get; set; }
        public bool IsActive { get; set; }
        public bool HasImage { get; set; }
        public string? ImageHash { get; set; }
    }

    private static string EncodeCursor(string code, long id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{id}|{code}"));

    private static (string? Code, long Id) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor))
        {
            return (null, 0);
        }
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|', 2);
            return (parts[1], long.Parse(parts[0]));
        }
        catch (FormatException)
        {
            return (null, 0);   // malformed cursor → first page, not an error
        }
    }
}
