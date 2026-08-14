using System.Threading.Channels;
using BarcodePrinter.Contracts.Imports;
using BarcodePrinter.Infrastructure.Services;
using ClosedXML.Excel;
using Dapper;
using MiniExcelLibs;

namespace BarcodePrinter.Infrastructure.Imports;

/// <summary>In-process work queue feeding the ImportWorker. Unbounded is safe:
/// submissions are capped upstream (one per user, two running globally).</summary>
public sealed class ImportQueue
{
    private readonly Channel<long> _channel = Channel.CreateUnbounded<long>();
    public ChannelWriter<long> Writer => _channel.Writer;
    public ChannelReader<long> Reader => _channel.Reader;
}

/// <summary>Batch status reads shared by the REST endpoint and the SignalR push.</summary>
public sealed class ImportsQuery(IDbConnectionFactory connections)
{
    public async Task<ImportBatchDto?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return (await QueryAsync(conn, "WHERE b.id = @id", new { id }, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<ImportBatchDto>> RecentAsync(long userId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await QueryAsync(conn,
            "WHERE b.uploaded_by = @userId ORDER BY b.id DESC LIMIT 20", new { userId }, ct);
    }

    public async Task<bool> HasRunningAsync(long userId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT COUNT(*) FROM import_batches
            WHERE uploaded_by = @userId AND status IN ('Uploaded','Validating','Committing')
            """, new { userId }, cancellationToken: ct)) > 0;
    }

    private static async Task<IReadOnlyList<ImportBatchDto>> QueryAsync(
        MySqlConnector.MySqlConnection conn, string where, object args, CancellationToken ct)
    {
        var rows = await conn.QueryAsync<Row>(new CommandDefinition(
            $"""
            SELECT CAST(b.id AS SIGNED) AS Id, b.file_name AS FileName, b.status AS Status,
                   b.commit_policy AS CommitPolicy, b.total_rows AS TotalRows,
                   b.processed_rows AS ProcessedRows, b.valid_rows AS ValidRows,
                   b.invalid_rows AS InvalidRows, b.inserted_rows AS InsertedRows,
                   b.updated_rows AS UpdatedRows, b.uploaded_at AS UploadedAtUtc,
                   b.started_at AS StartedAtUtc, b.finished_at AS FinishedAtUtc,
                   b.error_message AS ErrorMessage,
                   EXISTS(SELECT 1 FROM import_errors e WHERE e.batch_id = b.id) AS HasErrorReport
            FROM import_batches b {where}
            """, args, cancellationToken: ct));
        return rows.Select(r => new ImportBatchDto(
            r.Id, r.FileName, r.Status, r.CommitPolicy, r.TotalRows, r.ProcessedRows,
            r.ValidRows, r.InvalidRows, r.InsertedRows, r.UpdatedRows,
            r.UploadedAtUtc, r.StartedAtUtc, r.FinishedAtUtc, r.ErrorMessage, r.HasErrorReport)).ToList();
    }

    private sealed class Row
    {
        public long Id { get; set; }
        public string FileName { get; set; } = "";
        public string Status { get; set; } = "";
        public string CommitPolicy { get; set; } = "";
        public int TotalRows { get; set; }
        public int ProcessedRows { get; set; }
        public int ValidRows { get; set; }
        public int InvalidRows { get; set; }
        public int InsertedRows { get; set; }
        public int UpdatedRows { get; set; }
        public DateTime UploadedAtUtc { get; set; }
        public DateTime? StartedAtUtc { get; set; }
        public DateTime? FinishedAtUtc { get; set; }
        public string? ErrorMessage { get; set; }
        public bool HasErrorReport { get; set; }
    }
}

/// <summary>Styled template with a UOM dropdown (ClosedXML — DOM cost is fine
/// for a 3-row template; the READ side never uses ClosedXML).</summary>
public static class ExcelTemplate
{
    public static byte[] Build(IReadOnlyList<string> uomCodes)
    {
        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Products");

        for (var i = 0; i < ImportPipeline.TemplateHeaders.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = ImportPipeline.TemplateHeaders[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            cell.Style.Font.FontColor = XLColor.White;
        }
        sheet.Cell(2, 1).Value = "SAMPLE-001";
        sheet.Cell(2, 2).Value = "Sample product — delete this row";
        sheet.Cell(2, 3).Value = uomCodes.FirstOrDefault() ?? "";
        sheet.Cell(2, 7).Value = "21/07/2026";
        sheet.Cell(2, 8).Value = "21/07/2027";
        sheet.Columns(1, ImportPipeline.TemplateHeaders.Length).AdjustToContents();

        // UOM dropdown backed by a hidden sheet.
        if (uomCodes.Count > 0)
        {
            var lookupSheet = wb.AddWorksheet("Lookups");
            for (var i = 0; i < uomCodes.Count; i++)
            {
                lookupSheet.Cell(i + 1, 1).Value = uomCodes[i];
            }
            lookupSheet.Hide();
            sheet.Range(2, 3, 10_000, 3).CreateDataValidation()
                .List(lookupSheet.Range(1, 1, uomCodes.Count, 1));
        }

        // Date columns as text so Excel does not silently re-format them.
        sheet.Range(2, 7, 10_000, 8).Style.NumberFormat.Format = "@";

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }
}

/// <summary>Error workbook: the ORIGINAL failed rows plus an Error column, so
/// the user fixes and re-uploads the same-shaped file (§15 pipeline step 8).
/// Generated lazily — most imports are clean.</summary>
public sealed class ErrorReportBuilder(IDbConnectionFactory connections)
{
    public async Task<byte[]?> BuildAsync(long batchId, CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        var stored = await conn.QuerySingleOrDefaultAsync<string?>(new CommandDefinition(
            "SELECT stored_path FROM import_batches WHERE id = @batchId",
            new { batchId }, cancellationToken: ct));
        if (stored is null || !File.Exists(stored))
        {
            return null;
        }

        var errors = (await conn.QueryAsync<(int RowNo, string? Column, string Message)>(new CommandDefinition(
                "SELECT row_no, column_name, message FROM import_errors WHERE batch_id = @batchId ORDER BY row_no",
                new { batchId }, cancellationToken: ct)))
            .GroupBy(e => e.RowNo)
            .ToDictionary(g => g.Key,
                g => string.Join("; ", g.Select(e => e.Column is null ? e.Message : $"{e.Column}: {e.Message}")));
        if (errors.Count == 0)
        {
            return null;
        }

        var output = new List<Dictionary<string, object?>>();
        var rowNo = 0;
        foreach (var raw in MiniExcel.Query(stored, useHeaderRow: true))
        {
            rowNo++;
            if (!errors.TryGetValue(rowNo, out var message))
            {
                continue;
            }
            var row = new Dictionary<string, object?>((IDictionary<string, object?>)raw)
            {
                ["Error"] = message,
            };
            output.Add(row);
        }

        using var ms = new MemoryStream();
        await MiniExcel.SaveAsAsync(ms, output, cancellationToken: ct);
        return ms.ToArray();
    }
}

/// <summary>Streaming product export (§15 guard rails): Dapper unbuffered →
/// MiniExcel row-by-row — 100k products never materialise as objects at once.</summary>
public sealed class ProductExport(IDbConnectionFactory connections)
{
    public async Task<byte[]> BuildAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);
        var rows = conn.Query(
            """
            SELECT p.code            AS `Code`,
                   p.description     AS `Description`,
                   u.code            AS `UOM`,
                   p.size            AS `Size`,
                   p.color           AS `Color`,
                   p.default_batch   AS `Batch`,
                   DATE_FORMAT(p.default_production_date, '%d/%m/%Y') AS `Production Date`,
                   DATE_FORMAT(p.default_expiry_date, '%d/%m/%Y')     AS `Expiry Date`,
                   p.default_quantity AS `Quantity`,
                   p.carton_quantity  AS `Carton Quantity`,
                   c.name             AS `Category`
            FROM products p
            LEFT JOIN uoms u ON u.id = p.uom_id
            LEFT JOIN product_categories c ON c.id = p.category_id
            WHERE p.is_active = 1
            ORDER BY p.code
            """, buffered: false);

        using var ms = new MemoryStream();
        await MiniExcel.SaveAsAsync(ms, rows, cancellationToken: ct);
        return ms.ToArray();
    }
}
