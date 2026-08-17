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

    // Lengths mirror `products` in 0002_product.sql — a value that would not fit
    // must be reported as a row error, never blow up the whole bulk upsert.
    private const int CodeMax = 64;          // products.code            VARCHAR(64)
    private const int DescriptionMax = 255;  // products.description     VARCHAR(255)
    private const int SizeMax = 64;          // products.size            VARCHAR(64)
    private const int ColorMax = 64;         // products.color           VARCHAR(64)
    private const int BatchMax = 64;         // products.default_batch   VARCHAR(64)

    /// <summary>
    /// THE import contract. Only product-master fields live here:
    ///  • Category was removed — product_categories is never populated (no seed,
    ///    no create endpoint, no UI), so every non-blank value rejected its row.
    ///    products.category_id is left untouched by the importer, not nulled.
    ///  • Production/Expiry Date were removed — they are print-run values entered
    ///    on the Print Labels screen, not product master.
    ///  • There is no Barcode column: the product code IS the barcode value
    ///    (the label resolves COALESCE(NULLIF(barcode_value,''), code)).
    /// Unknown columns in an uploaded file are IGNORED, never rejected: customers
    /// already hold files carrying Category / date / barcode columns.
    /// Column order must match BuildChunkTable and the template (§ExcelTemplate).
    /// </summary>
    public static readonly string[] TemplateHeaders =
        ["Code", "Description", "UOM", "Size", "Color", "Batch",
         "Quantity", "Carton Quantity", "Cartons per Pallet"];

    /// <summary>Staging column for the parsed Cartons per Pallet value, added by
    /// migration 0012 so the value travels in a column that means what it says.</summary>
    private const string CartonsPerPalletColumn = "n_cartons_per_pallet";

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
                    cmd.Parameters.AddWithValue($"@m{i}", Truncate(chunk[i].Message, 512));
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

        // Only the contract columns are read. Anything else in the sheet
        // (Category, Production Date, Expiry Date, Barcode, the customer's own
        // notes) is simply not looked at, and therefore cannot fail a row.
        var code = Text(row, "code");
        var description = Text(row, "description");
        var uom = Text(row, "uom");
        var size = Text(row, "size");
        var color = Text(row, "color");
        var batchText = Text(row, "batch");

        if (string.IsNullOrEmpty(code))
        {
            errors.Add(Error(rowNo, code, "Code", "REQUIRED", "Product code is required.", code));
        }
        else if (code.Length > CodeMax)
        {
            errors.Add(Error(rowNo, code, "Code", "TOO_LONG",
                $"Product code exceeds {CodeMax} characters.", code));
        }

        if (string.IsNullOrEmpty(description))
        {
            errors.Add(Error(rowNo, code, "Description", "REQUIRED", "Description is required.", description));
        }
        else if (description.Length > DescriptionMax)
        {
            errors.Add(Error(rowNo, code, "Description", "TOO_LONG",
                $"Description exceeds {DescriptionMax} characters.", description));
        }

        CheckLength(rowNo, code, "Size", size, SizeMax, errors);
        CheckLength(rowNo, code, "Color", color, ColorMax, errors);
        CheckLength(rowNo, code, "Batch", batchText, BatchMax, errors);

        long? uomId = null;
        if (!string.IsNullOrEmpty(uom))
        {
            if (!lookups.Uoms.TryGetValue(uom, out var resolved))
            {
                errors.Add(Error(rowNo, code, "UOM", "UNKNOWN",
                    $"UOM '{uom}' does not exist. Valid values: {lookups.UomList}.", uom));
            }
            else
            {
                uomId = resolved;
            }
        }

        var quantity = ParseDecimal(row, "quantity", rowNo, code, "Quantity", errors);
        var cartonQty = ParseDecimal(row, "carton quantity", rowNo, code, "Carton Quantity", errors);
        var cartonsPerPallet = ParseInt(row, "cartons per pallet", rowNo, code, "Cartons per Pallet", errors);

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
        r["c_quantity"] = (object?)Text(row, "quantity") ?? DBNull.Value;
        r["c_carton_qty"] = (object?)Text(row, "carton quantity") ?? DBNull.Value;
        r["is_valid"] = isValid;
        r["n_quantity"] = (object?)quantity ?? DBNull.Value;
        r["n_carton_qty"] = (object?)cartonQty ?? DBNull.Value;
        r["n_uom_id"] = (object?)uomId ?? DBNull.Value;
        r[CartonsPerPalletColumn] = (object?)cartonsPerPallet ?? DBNull.Value;
        table.Rows.Add(r);
        return isValid;
    }

    /// <summary>Row errors name the sheet row AND the product code, because the
    /// user is looking at a 20 000-row spreadsheet:
    /// <c>Row 2 (IMP000001): UOM 'XX' does not exist.</c>
    /// rowNo counts DATA rows (import_errors.row_no keeps that meaning, the error
    /// report matches on it); the message shows rowNo + 1 — the row number Excel
    /// puts in the margin, header included.</summary>
    private static ErrorRow Error(int rowNo, string? code, string column,
        string errorCode, string message, string? rawValue) =>
        new(rowNo, column, errorCode,
            $"Row {rowNo + 1}{(string.IsNullOrEmpty(code) ? "" : $" ({code})")}: {message}",
            rawValue);

    private static void CheckLength(int rowNo, string? code, string column,
        string? value, int max, List<ErrorRow> errors)
    {
        if (value is not null && value.Length > max)
        {
            errors.Add(Error(rowNo, code, column, "TOO_LONG",
                $"{column} exceeds {max} characters.", value));
        }
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
                   CONCAT('Row ', s.row_no + 1, ' (', COALESCE(s.c_code, ''), '): ',
                          'This code appears more than once in the file.'), s.c_code
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
        // category_id, default_production_date and default_expiry_date are NOT in
        // the column list: they are outside the import contract, so an import must
        // leave whatever the product already holds alone rather than null it.
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await conn.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO products
                    (code, description, uom_id, size, color,
                     default_batch, default_quantity, carton_quantity, cartons_per_pallet,
                     is_active, concurrency_stamp, created_at, created_by)
                SELECT c_code, c_description, n_uom_id, c_size, c_color,
                       c_batch, n_quantity, n_carton_qty, n_cartons_per_pallet,
                       1, UUID(), UTC_TIMESTAMP(3), @UploadedBy
                FROM product_import_staging
                WHERE batch_id = @Id AND is_valid = 1
                ON DUPLICATE KEY UPDATE
                    description             = VALUES(description),
                    uom_id                  = VALUES(uom_id),
                    size                    = VALUES(size),
                    color                   = VALUES(color),
                    default_batch           = VALUES(default_batch),
                    default_quantity        = VALUES(default_quantity),
                    carton_quantity         = VALUES(carton_quantity),
                    cartons_per_pallet      = VALUES(cartons_per_pallet),
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
        return new Lookups(uoms, string.Join(", ", uoms.Keys.Order()));
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

    /// <summary>Only the contract columns are supplied. The dropped staging
    /// columns (c_category, c_production_date, c_expiry_date, n_production_date,
    /// n_expiry_date) are all NULL-able in 0005_import.sql, so the bulk copy's
    /// explicit column list simply leaves them at NULL — nothing to write.</summary>
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
        t.Columns.Add("c_quantity", typeof(string));
        t.Columns.Add("c_carton_qty", typeof(string));
        t.Columns.Add("is_valid", typeof(bool));
        t.Columns.Add("n_quantity", typeof(decimal));
        t.Columns.Add("n_carton_qty", typeof(decimal));
        t.Columns.Add("n_uom_id", typeof(long));
        t.Columns.Add(CartonsPerPalletColumn, typeof(int));   // Cartons per Pallet
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

    private static decimal? ParseDecimal(IReadOnlyDictionary<string, object?> row, string key,
        int rowNo, string? code, string column, List<ErrorRow> errors)
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
                errors.Add(Error(rowNo, code, column, "BAD_NUMBER",
                    $"'{value}' is not a valid number.", value.ToString()));
                return null;
            case < 0:
                errors.Add(Error(rowNo, code, column, "NEGATIVE",
                    "Value cannot be negative.", value.ToString()));
                return null;
            default:
                return parsed;
        }
    }

    /// <summary>Cartons per Pallet is products.cartons_per_pallet INT NULL:
    /// optional, whole, non-negative. "40.0" from a spreadsheet is a 40.</summary>
    private static int? ParseInt(IReadOnlyDictionary<string, object?> row, string key,
        int rowNo, string? code, string column, List<ErrorRow> errors)
    {
        var before = errors.Count;
        var parsed = ParseDecimal(row, key, rowNo, code, column, errors);
        if (parsed is not { } value)
        {
            return null;
        }
        if (decimal.Truncate(value) != value)
        {
            errors.Add(Error(rowNo, code, column, "NOT_WHOLE",
                $"'{value}' must be a whole number.", value.ToString(CultureInfo.InvariantCulture)));
            return null;
        }
        if (value > int.MaxValue)
        {
            errors.Add(Error(rowNo, code, column, "OUT_OF_RANGE",
                $"'{value}' is too large.", value.ToString(CultureInfo.InvariantCulture)));
            return null;
        }
        return errors.Count == before ? (int)value : null;
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : s.Length <= max ? s : s[..max];

    private sealed record BatchRow(long Id, string StoredPath, long UploadedBy, string CommitPolicy, string UploadedByUsername);
    private sealed record ErrorRow(int RowNo, string Column, string Code, string Message, string? RawValue);
    private sealed record Lookups(Dictionary<string, long> Uoms, string UomList);
}
