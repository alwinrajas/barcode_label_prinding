using System.Data;
using System.Globalization;
using BarcodePrinter.Application.Abstractions;
using BarcodePrinter.Contracts;
using BarcodePrinter.Infrastructure.Services;
using Dapper;
using Microsoft.Extensions.Logging;
using MiniExcelLibs;
using MySqlConnector;

namespace BarcodePrinter.Infrastructure.Imports;

/// <summary>Port implemented in the Api layer over SignalR; the pipeline
/// stays transport-agnostic.</summary>
public interface IImportProgressBroadcaster
{
    Task BatchChangedAsync(long batchId, CancellationToken ct);
}

/// <summary>
/// The 20k+ import pipeline (blueprint §15). The client uploads a file and
/// watches; everything below happens server-side:
///
///   [3] stream (MiniExcel, forward-only) + per-row validate, chunks of 5 000
///   [4] MySqlBulkCopy → product_import_staging   (LOAD DATA LOCAL INFILE)
///   [5] cross-row validation in set-based SQL    (duplicates in file)
///   [6] ONE INSERT … SELECT … ON DUPLICATE KEY UPDATE, per commit policy
///   [7] finalise: counts, staging cleanup, ONE audit row
///
/// No row-by-row inserts, no per-row round trips, no EF (A-11/A-25).
/// </summary>
public sealed class ImportPipeline(
    IDbConnectionFactory connections,
    ISettingsProvider settings,
    IAuditWriter audit,
    IImportProgressBroadcaster progress,
    ILogger<ImportPipeline> logger)
{
    private const int ChunkSize = 5_000;

    private static readonly string[] DateFormats =
        ["dd/MM/yyyy", "d/M/yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd/MMM/yyyy", "d-MMM-yyyy"];

    // Column order must match BuildChunkTable and the template (§ExcelTemplate).
    public static readonly string[] TemplateHeaders =
        ["Code", "Description", "UOM", "Size", "Color", "Batch",
         "Production Date", "Expiry Date", "Quantity", "Carton Quantity", "Category"];

    public async Task ProcessAsync(long batchId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        var batch = await conn.QuerySingleOrDefaultAsync<BatchRow>(new CommandDefinition(
            """
            SELECT CAST(b.id AS SIGNED) AS Id, b.stored_path AS StoredPath,
                   CAST(b.uploaded_by AS SIGNED) AS UploadedBy, b.commit_policy AS CommitPolicy,
                   COALESCE(u.username, '') AS UploadedByUsername
            FROM import_batches b
            LEFT JOIN users u ON u.id = b.uploaded_by
            WHERE b.id = @batchId AND b.status = 'Uploaded'
            """, new { batchId }, cancellationToken: ct));
        if (batch is null)
        {
            return;   // already processed / cancelled — claim failed, not an error
        }

        var maxRows = await settings.GetIntAsync("Import:MaxRows", 200_000, ct);
        await SetStatusAsync(conn, batchId, "Validating", "started_at = UTC_TIMESTAMP(3)", ct);
        await progress.BatchChangedAsync(batchId, ct);

        try
        {
            var lookups = await LoadLookupsAsync(conn, ct);   // ONCE — not 20k round trips
            var stats = await StreamValidateAndStageAsync(conn, batch, lookups, maxRows, ct);

            if (await IsCancelledAsync(conn, batchId, ct))
            {
                await CleanupAsync(conn, batchId, ct);
                return;
            }

            await CrossRowValidateAsync(conn, batchId, ct);
            await CommitAsync(conn, batch, ct);
            await CleanupStagingAsync(conn, batchId, ct);

            await audit.WriteAsync(new AuditEntry("ProductsImported",
                UserId: batch.UploadedBy, UsernameSnapshot: batch.UploadedByUsername,
                EntityType: "ImportBatch", EntityId: batchId.ToString(),
                AfterJson: await SummaryJsonAsync(conn, batchId, ct)), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import batch {BatchId} failed", batchId);
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE import_batches
                SET status = 'Failed', error_message = @message, finished_at = UTC_TIMESTAMP(3)
                WHERE id = @batchId
                """,
                new { batchId, message = Truncate(ex.Message, 500) },
                cancellationToken: CancellationToken.None));
            await CleanupStagingAsync(conn, batchId, CancellationToken.None);
        }
        finally
        {
            await progress.BatchChangedAsync(batchId, CancellationToken.None);
        }
    }

    // ---- [3]+[4] stream, validate, bulk-load --------------------------------

    private async Task<(int Total, int Invalid)> StreamValidateAndStageAsync(
        MySqlConnection conn, BatchRow batch, Lookups lookups, int maxRows, CancellationToken ct)
    {
        var table = BuildChunkTable();
        var errors = new List<ErrorRow>();
        int sheetRow = 0, staged = 0, invalid = 0;

        foreach (var raw in MiniExcel.Query(batch.StoredPath, useHeaderRow: true))
        {
            ct.ThrowIfCancellationRequested();
            sheetRow++;

            var row = NormalizeKeys((IDictionary<string, object?>)raw);

            // Skip rows with no data at all. Templates carry data-validation
            // ranges (rows 2–10 000) that spreadsheet readers surface as
            // thousands of phantom empty rows — those are not user errors.
            if (IsCompletelyEmpty(row))
            {
                continue;
            }

            staged++;
            if (staged > maxRows)
            {
                throw new Domain.DomainException(ErrorCodes.ImportRowLimitExceeded,
                    $"The file exceeds the {maxRows:N0}-row limit.");
            }

            if (!ValidateRow(batch.Id, sheetRow, row, lookups, table, errors))
            {
                invalid++;
            }

            if (table.Rows.Count >= ChunkSize)
            {
                await FlushChunkAsync(conn, batch.Id, table, errors, staged, ct);
                if (await IsCancelledAsync(conn, batch.Id, ct))
                {
                    return (staged, invalid);
                }
            }
        }

        await FlushChunkAsync(conn, batch.Id, table, errors, staged, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE import_batches SET total_rows = @staged, processed_rows = @staged WHERE id = @id",
            new { staged, id = batch.Id }, cancellationToken: ct));
        return (staged, invalid);
    }

    private async Task FlushChunkAsync(
        MySqlConnection conn, long batchId, DataTable table,
        List<ErrorRow> errors, int processedSoFar, CancellationToken ct)
    {
        if (table.Rows.Count > 0)
        {
            var bulk = new MySqlBulkCopy(conn) { DestinationTableName = "product_import_staging" };
            for (var i = 0; i < table.Columns.Count; i++)
            {
                bulk.ColumnMappings.Add(new MySqlBulkCopyColumnMapping(i, table.Columns[i].ColumnName));
            }
            await bulk.WriteToServerAsync(table, ct);
            table.Rows.Clear();
        }

        if (errors.Count > 0)
        {
            foreach (var chunk in errors.Chunk(500))
            {
                var values = string.Join(",", chunk.Select((_, i) =>
                    $"(@b{i}, @r{i}, @c{i}, @e{i}, @m{i}, @v{i})"));
                var cmd = new MySqlCommand(
                    $"INSERT INTO import_errors (batch_id, row_no, column_name, error_code, message, raw_value) VALUES {values}",
                    conn);
                for (var i = 0; i < chunk.Length; i++)
                {
                    cmd.Parameters.AddWithValue($"@b{i}", batchId);
                    cmd.Parameters.AddWithValue($"@r{i}", chunk[i].RowNo);
                    cmd.Parameters.AddWithValue($"@c{i}", chunk[i].Column);
                    cmd.Parameters.AddWithValue($"@e{i}", chunk[i].Code);
                    cmd.Parameters.AddWithValue($"@m{i}", chunk[i].Message);
                    cmd.Parameters.AddWithValue($"@v{i}", Truncate(chunk[i].RawValue, 500));
                }
                await cmd.ExecuteNonQueryAsync(ct);
            }
            errors.Clear();
        }

        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE import_batches SET processed_rows = @processedSoFar WHERE id = @batchId",
            new { processedSoFar, batchId }, cancellationToken: ct));
        await progress.BatchChangedAsync(batchId, ct);
    }

    private bool ValidateRow(
        long batchId, int rowNo, IReadOnlyDictionary<string, object?> row,
        Lookups lookups, DataTable table, List<ErrorRow> errors)
    {
        var before = errors.Count;

        var code = Text(row, "code");
        var description = Text(row, "description");
        var uom = Text(row, "uom");
        var size = Text(row, "size");
        var color = Text(row, "color");
        var batchText = Text(row, "batch");
        var category = Text(row, "category");

        if (string.IsNullOrEmpty(code))
        {
            errors.Add(new(rowNo, "Code", "REQUIRED", "Product code is required.", code));
        }
        else if (code.Length > 64)
        {
            errors.Add(new(rowNo, "Code", "TOO_LONG", "Product code exceeds 64 characters.", code));
        }

        if (string.IsNullOrEmpty(description))
        {
            errors.Add(new(rowNo, "Description", "REQUIRED", "Description is required.", description));
        }
        else if (description.Length > 255)
        {
            errors.Add(new(rowNo, "Description", "TOO_LONG", "Description exceeds 255 characters.", description));
        }

        long? uomId = null;
        if (!string.IsNullOrEmpty(uom))
        {
            if (!lookups.Uoms.TryGetValue(uom, out var resolved))
            {
                errors.Add(new(rowNo, "UOM", "UNKNOWN",
                    $"UOM '{uom}' does not exist. Valid values: {lookups.UomList}.", uom));
            }
            else
            {
                uomId = resolved;
            }
        }

        long? categoryId = null;
        if (!string.IsNullOrEmpty(category))
        {
            if (!lookups.Categories.TryGetValue(category, out var resolved))
            {
                errors.Add(new(rowNo, "Category", "UNKNOWN", $"Category '{category}' does not exist.", category));
            }
            else
            {
                categoryId = resolved;
            }
        }

        var prodDate = ParseDate(row, "production date", rowNo, "Production Date", errors);
        var expDate = ParseDate(row, "expiry date", rowNo, "Expiry Date", errors);
        if (prodDate is { } p && expDate is { } e && e < p)
        {
            errors.Add(new(rowNo, "Expiry Date", "BEFORE_PRODUCTION",
                "Expiry date is before the production date.", expDate?.ToString("yyyy-MM-dd")));
        }

        var quantity = ParseDecimal(row, "quantity", rowNo, "Quantity", errors);
        var cartonQty = ParseDecimal(row, "carton quantity", rowNo, "Carton Quantity", errors);

        var isValid = errors.Count == before;

        var r = table.NewRow();
        r["batch_id"] = batchId;
        r["row_no"] = rowNo;
        r["c_code"] = (object?)code ?? DBNull.Value;
        r["c_description"] = (object?)description ?? DBNull.Value;
        r["c_uom"] = (object?)uom ?? DBNull.Value;
        r["c_size"] = (object?)size ?? DBNull.Value;
        r["c_color"] = (object?)color ?? DBNull.Value;
        r["c_batch"] = (object?)batchText ?? DBNull.Value;
        r["c_production_date"] = (object?)Text(row, "production date") ?? DBNull.Value;
        r["c_expiry_date"] = (object?)Text(row, "expiry date") ?? DBNull.Value;
        r["c_quantity"] = (object?)Text(row, "quantity") ?? DBNull.Value;
        r["c_carton_qty"] = (object?)Text(row, "carton quantity") ?? DBNull.Value;
        r["c_category"] = (object?)category ?? DBNull.Value;
        r["is_valid"] = isValid;
        r["n_production_date"] = (object?)prodDate ?? DBNull.Value;
        r["n_expiry_date"] = (object?)expDate ?? DBNull.Value;
        r["n_quantity"] = (object?)quantity ?? DBNull.Value;
        r["n_carton_qty"] = (object?)cartonQty ?? DBNull.Value;
        r["n_uom_id"] = (object?)uomId ?? DBNull.Value;
        r["n_category_id"] = (object?)categoryId ?? DBNull.Value;
        table.Rows.Add(r);
        return isValid;
    }

    // ---- [5] cross-row validation, set-based ---------------------------------

    private static async Task CrossRowValidateAsync(MySqlConnection conn, long batchId, CancellationToken ct)
    {
        // Duplicates INSIDE the file: every occurrence is invalid — the user
        // must decide which row is right, the system must not guess.
        await conn.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO import_errors (batch_id, row_no, column_name, error_code, message, raw_value)
            SELECT s.batch_id, s.row_no, 'Code', 'DUPLICATE_IN_FILE',
                   'This code appears more than once in the file.', s.c_code
            FROM product_import_staging s
            JOIN (SELECT c_code FROM product_import_staging
                  WHERE batch_id = @batchId AND is_valid = 1
                  GROUP BY c_code HAVING COUNT(*) > 1) d ON d.c_code = s.c_code
            WHERE s.batch_id = @batchId AND s.is_valid = 1;

            UPDATE product_import_staging s
            JOIN (SELECT c_code FROM product_import_staging
                  WHERE batch_id = @batchId AND is_valid = 1
                  GROUP BY c_code HAVING COUNT(*) > 1) d ON d.c_code = s.c_code
            SET s.is_valid = 0
            WHERE s.batch_id = @batchId;
            """, new { batchId }, cancellationToken: ct));

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE import_batches b
            SET b.valid_rows   = (SELECT COUNT(*) FROM product_import_staging WHERE batch_id = @batchId AND is_valid = 1),
                b.invalid_rows = (SELECT COUNT(*) FROM product_import_staging WHERE batch_id = @batchId AND is_valid = 0)
            WHERE b.id = @batchId
            """, new { batchId }, cancellationToken: ct));
    }

    // ---- [6] commit per policy (C-13: both implemented) ------------------------

    private async Task CommitAsync(MySqlConnection conn, BatchRow batch, CancellationToken ct)
    {
        var counts = await conn.QuerySingleAsync<(int Valid, int Invalid)>(new CommandDefinition(
            "SELECT valid_rows, invalid_rows FROM import_batches WHERE id = @Id",
            new { batch.Id }, cancellationToken: ct));

        if (batch.CommitPolicy == "AllOrNothing" && counts.Invalid > 0)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                UPDATE import_batches
                SET status = 'Failed', finished_at = UTC_TIMESTAMP(3),
                    error_message = @message
                WHERE id = @Id
                """,
                new
                {
                    batch.Id,
                    message = $"{counts.Invalid} row(s) failed validation. Nothing was imported (all-or-nothing policy).",
                }, cancellationToken: ct));
            return;
        }

        await SetStatusAsync(conn, batch.Id, "Committing", null, ct);
        await progress.BatchChangedAsync(batch.Id, ct);

        // Inserted-vs-updated split, computed BEFORE the upsert: robust against
        // affected-rows semantics (1 per insert, 2 per update, 0 per no-change).
        var inserted = await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM product_import_staging s
            WHERE s.batch_id = @Id AND s.is_valid = 1
              AND NOT EXISTS (SELECT 1 FROM products p WHERE p.code = s.c_code)
            """, new { batch.Id }, cancellationToken: ct));

        // THE set-based upsert — the literal answer to "no row-by-row inserts".
        // F-3 (documented): import wins over a concurrent manual edit;
        // updated_by records the importer and the audit row makes it traceable.
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO products
                    (code, description, uom_id, size, color, category_id,
                     default_batch, default_production_date, default_expiry_date,
                     default_quantity, carton_quantity,
                     is_active, concurrency_stamp, created_at, created_by)
                SELECT c_code, c_description, n_uom_id, c_size, c_color, n_category_id,
                       c_batch, n_production_date, n_expiry_date,
                       n_quantity, n_carton_qty,
                       1, UUID(), UTC_TIMESTAMP(3), @UploadedBy
                FROM product_import_staging
                WHERE batch_id = @Id AND is_valid = 1
                ON DUPLICATE KEY UPDATE
                    description             = VALUES(description),
                    uom_id                  = VALUES(uom_id),
                    size                    = VALUES(size),
                    color                   = VALUES(color),
                    category_id             = VALUES(category_id),
                    default_batch           = VALUES(default_batch),
                    default_production_date = VALUES(default_production_date),
                    default_expiry_date     = VALUES(default_expiry_date),
                    default_quantity        = VALUES(default_quantity),
                    carton_quantity         = VALUES(carton_quantity),
                    concurrency_stamp       = UUID(),
                    updated_at              = UTC_TIMESTAMP(3),
                    updated_by              = @UploadedBy
                """,
                new { batch.Id, batch.UploadedBy },
                transaction: tx, commandTimeout: 300, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }

        await conn.ExecuteAsync(new CommandDefinition(
            """
            UPDATE import_batches
            SET status = 'Completed', finished_at = UTC_TIMESTAMP(3),
                inserted_rows = @inserted, updated_rows = valid_rows - @inserted
            WHERE id = @Id
            """, new { batch.Id, inserted }, cancellationToken: ct));
    }

    // ---- helpers ----------------------------------------------------------------

    private static async Task<Lookups> LoadLookupsAsync(MySqlConnection conn, CancellationToken ct)
    {
        var uoms = (await conn.QueryAsync<(string Code, long Id)>(new CommandDefinition(
                "SELECT code, CAST(id AS SIGNED) FROM uoms WHERE is_active = 1", cancellationToken: ct)))
            .ToDictionary(x => x.Code, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var cats = (await conn.QueryAsync<(string Name, long Id)>(new CommandDefinition(
                "SELECT name, CAST(id AS SIGNED) FROM product_categories WHERE is_active = 1", cancellationToken: ct)))
            .ToDictionary(x => x.Name, x => x.Id, StringComparer.OrdinalIgnoreCase);
        return new Lookups(uoms, cats, string.Join(", ", uoms.Keys.Order()));
    }

    private static async Task<bool> IsCancelledAsync(MySqlConnection conn, long batchId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT status FROM import_batches WHERE id = @batchId",
            new { batchId }, cancellationToken: ct)) == "Cancelled";

    private static Task CleanupAsync(MySqlConnection conn, long batchId, CancellationToken ct) =>
        CleanupStagingAsync(conn, batchId, ct);

    private static Task<int> CleanupStagingAsync(MySqlConnection conn, long batchId, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            "DELETE FROM product_import_staging WHERE batch_id = @batchId",
            new { batchId }, cancellationToken: ct));

    private static Task SetStatusAsync(MySqlConnection conn, long batchId, string status,
        string? extraSet, CancellationToken ct) =>
        conn.ExecuteAsync(new CommandDefinition(
            $"UPDATE import_batches SET status = @status{(extraSet is null ? "" : ", " + extraSet)} WHERE id = @batchId",
            new { batchId, status }, cancellationToken: ct));

    private static async Task<string> SummaryJsonAsync(MySqlConnection conn, long batchId, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<string>(new CommandDefinition(
            """
            SELECT JSON_OBJECT('status', status, 'total', total_rows, 'valid', valid_rows,
                               'invalid', invalid_rows, 'inserted', inserted_rows, 'updated', updated_rows)
            FROM import_batches WHERE id = @batchId
            """, new { batchId }, cancellationToken: ct)) ?? "{}";

    private static DataTable BuildChunkTable()
    {
        var t = new DataTable();
        t.Columns.Add("batch_id", typeof(long));
        t.Columns.Add("row_no", typeof(int));
        t.Columns.Add("c_code", typeof(string));
        t.Columns.Add("c_description", typeof(string));
        t.Columns.Add("c_uom", typeof(string));
        t.Columns.Add("c_size", typeof(string));
        t.Columns.Add("c_color", typeof(string));
        t.Columns.Add("c_batch", typeof(string));
        t.Columns.Add("c_production_date", typeof(string));
        t.Columns.Add("c_expiry_date", typeof(string));
        t.Columns.Add("c_quantity", typeof(string));
        t.Columns.Add("c_carton_qty", typeof(string));
        t.Columns.Add("c_category", typeof(string));
        t.Columns.Add("is_valid", typeof(bool));
        t.Columns.Add("n_production_date", typeof(DateTime));
        t.Columns.Add("n_expiry_date", typeof(DateTime));
        t.Columns.Add("n_quantity", typeof(decimal));
        t.Columns.Add("n_carton_qty", typeof(decimal));
        t.Columns.Add("n_uom_id", typeof(long));
        t.Columns.Add("n_category_id", typeof(long));
        return t;
    }

    private static bool IsCompletelyEmpty(IReadOnlyDictionary<string, object?> row) =>
        row.Values.All(v => v is null || (v is string s && string.IsNullOrWhiteSpace(s)));

    private static Dictionary<string, object?> NormalizeKeys(IDictionary<string, object?> raw) =>
        raw.GroupBy(kv => kv.Key.Trim().ToLowerInvariant())
           .ToDictionary(g => g.Key, g => g.First().Value);

    private static string? Text(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }
        var s = value switch
        {
            DateTime dt => dt.ToString("dd/MM/yyyy"),
            double d => d.ToString("0.######", CultureInfo.InvariantCulture),
            _ => value.ToString(),
        };
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static DateTime? ParseDate(IReadOnlyDictionary<string, object?> row, string key,
        int rowNo, string column, List<ErrorRow> errors)
    {
        if (!row.TryGetValue(key, out var value) || value is null ||
            (value is string blank && string.IsNullOrWhiteSpace(blank)))
        {
            return null;
        }
        if (value is DateTime dt)
        {
            return dt.Date;
        }
        var s = value.ToString()!.Trim();
        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed))
        {
            return parsed.Date;
        }
        errors.Add(new(rowNo, column, "BAD_DATE",
            $"'{s}' is not a valid date. Use dd/MM/yyyy.", s));
        return null;
    }

    private static decimal? ParseDecimal(IReadOnlyDictionary<string, object?> row, string key,
        int rowNo, string column, List<ErrorRow> errors)
    {
        if (!row.TryGetValue(key, out var value) || value is null ||
            (value is string blank && string.IsNullOrWhiteSpace(blank)))
        {
            return null;
        }
        var parsed = value switch
        {
            double d => (decimal?)Convert.ToDecimal(d),
            decimal m => m,
            _ => decimal.TryParse(value.ToString()!.Trim(), NumberStyles.Number,
                CultureInfo.InvariantCulture, out var v) ? v : null,
        };
        switch (parsed)
        {
            case null:
                errors.Add(new(rowNo, column, "BAD_NUMBER",
                    $"'{value}' is not a valid number.", value.ToString()));
                return null;
            case < 0:
                errors.Add(new(rowNo, column, "NEGATIVE", "Value cannot be negative.", value.ToString()));
                return null;
            default:
                return parsed;
        }
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];

    private sealed record BatchRow(long Id, string StoredPath, long UploadedBy, string CommitPolicy, string UploadedByUsername);
    private sealed record ErrorRow(int RowNo, string Column, string Code, string Message, string? RawValue);
    private sealed record Lookups(
        Dictionary<string, long> Uoms, Dictionary<string, long> Categories, string UomList);
}
