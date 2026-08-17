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

/// <summary>Styled template with a UOM dropdown plus a plain-English
/// "Instructions" sheet (ClosedXML — DOM cost is fine for a 2-sheet template;
/// the READ side never uses ClosedXML).
/// Sheet 1 MUST stay the data sheet: the reader takes the first worksheet.</summary>
public static class ExcelTemplate
{
    private static readonly XLColor Navy = XLColor.FromHtml("#1F4E79");
    private const int UomColumn = 3;        // TemplateHeaders[2]
    private const int ValidationRows = 10_000;

    /// <summary>One row per import column: what the sheet says, whether it is
    /// required, the DB limit it must respect, and a worked example. Kept next to
    /// <see cref="ImportPipeline.TemplateHeaders"/> — the guard below fails loudly
    /// if the two ever drift.</summary>
    private static readonly (string Column, string Required, string Limit, string Format, string Example)[] Guide =
    [
        ("Code",               "Required", "64 characters",      "Text. Must be unique in the file. THIS IS THE BARCODE VALUE.", "IMP000001"),
        ("Description",        "Required", "255 characters",     "Text.",                                          "Cotton yarn cone 40/2"),
        ("UOM",                "Optional", "Must already exist", "Pick from the dropdown. Blank = no unit.",       "PCS"),
        ("Size",               "Optional", "64 characters",      "Text.",                                          "M2"),
        ("Color",              "Optional", "64 characters",      "Text.",                                          "NATURAL"),
        ("Batch",              "Optional", "64 characters",      "Text. The default batch, overridable at print time.", "CONE"),
        ("Quantity",           "Optional", "18 digits, 3 decimals", "Number, not negative. No thousands separators.", "750"),
        ("Carton Quantity",    "Optional", "18 digits, 3 decimals", "Number, not negative.",                       "750"),
        ("Cartons per Pallet", "Optional", "Whole number",       "Whole number, not negative.",                    "40"),
    ];

    private static readonly string[] Notes =
    [
        "The Product Code IS the barcode value — there is no separate barcode column.",
        "Production Date and Expiry Date are NOT imported. They belong to a print run and are entered on the Print Labels screen.",
        "Category is NOT imported.",
        "Any extra column in your file (Category, Production Date, Expiry Date, Barcode, your own notes) is ignored, not rejected — old files still import.",
        "Rows are matched on Code: an existing code is UPDATED, a new one is INSERTED. A code that appears twice in one file rejects every occurrence.",
        "Blank optional cells are imported as empty and OVERWRITE what the product holds today — leave a column out entirely to keep the current values.",
        "Delete the grey sample row before uploading. Completely empty rows are skipped.",
    ];

    public static byte[] Build(IReadOnlyList<string> uomCodes)
    {
        var headers = ImportPipeline.TemplateHeaders;
        if (!headers.SequenceEqual(Guide.Select(g => g.Column)))
        {
            throw new InvalidOperationException(
                "ExcelTemplate.Guide is out of sync with ImportPipeline.TemplateHeaders.");
        }

        using var wb = new XLWorkbook();
        var sheet = wb.AddWorksheet("Products");

        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = Navy;
            cell.Style.Font.FontColor = XLColor.White;
        }

        // ONE sample row, unmistakably marked so it is deleted rather than imported.
        sheet.Cell(2, 1).Value = "SAMPLE-001";
        sheet.Cell(2, 2).Value = "DELETE THIS SAMPLE ROW — it is an example, not your data";
        sheet.Cell(2, UomColumn).Value = uomCodes.FirstOrDefault() ?? "";
        sheet.Cell(2, 4).Value = "M2";
        sheet.Cell(2, 5).Value = "NATURAL";
        sheet.Cell(2, 6).Value = "CONE";
        sheet.Cell(2, 7).Value = 750;
        sheet.Cell(2, 8).Value = 750;
        sheet.Cell(2, 9).Value = 40;
        var sample = sheet.Range(2, 1, 2, headers.Length);
        sample.Style.Fill.BackgroundColor = XLColor.FromHtml("#F2F2F2");
        sample.Style.Font.Italic = true;
        sample.Style.Font.FontColor = XLColor.FromHtml("#7F7F7F");

        sheet.SheetView.FreezeRows(1);
        sheet.Columns(1, headers.Length).AdjustToContents();
        sheet.Column(2).Width = 45;

        // UOM dropdown backed by a hidden sheet.
        if (uomCodes.Count > 0)
        {
            var lookupSheet = wb.AddWorksheet("Lookups");
            for (var i = 0; i < uomCodes.Count; i++)
            {
                lookupSheet.Cell(i + 1, 1).Value = uomCodes[i];
            }
            lookupSheet.Hide();
            sheet.Range(2, UomColumn, ValidationRows, UomColumn).CreateDataValidation()
                .List(lookupSheet.Range(1, 1, uomCodes.Count, 1));
        }

        // Code as text: product codes like 0012340 must not lose their leading
        // zeros, and a code is a barcode, never a number.
        sheet.Range(2, 1, ValidationRows, 1).Style.NumberFormat.Format = "@";

        BuildInstructions(wb.AddWorksheet("Instructions"), uomCodes);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void BuildInstructions(IXLWorksheet sheet, IReadOnlyList<string> uomCodes)
    {
        sheet.Cell(1, 1).Value = "How to fill in the Products sheet";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(1, 1).Style.Font.FontColor = Navy;

        string[] tableHeaders = ["Column", "Required?", "Maximum / limit", "Accepted format", "Example"];
        const int HeaderRow = 3;
        for (var i = 0; i < tableHeaders.Length; i++)
        {
            var cell = sheet.Cell(HeaderRow, i + 1);
            cell.Value = tableHeaders[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = Navy;
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = HeaderRow + 1;
        foreach (var (column, required, limit, format, example) in Guide)
        {
            sheet.Cell(row, 1).Value = column;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 2).Value = required;
            sheet.Cell(row, 2).Style.Font.FontColor =
                required == "Required" ? XLColor.FromHtml("#C00000") : XLColor.FromHtml("#7F7F7F");
            sheet.Cell(row, 3).Value = limit;
            sheet.Cell(row, 4).Value = format;
            sheet.Cell(row, 5).SetValue(example);   // SetValue: "750" stays text
            row++;
        }
        sheet.Range(HeaderRow, 1, row - 1, tableHeaders.Length).Style.Border.InsideBorder =
            XLBorderStyleValues.Hair;

        row += 1;
        sheet.Cell(row, 1).Value = "Valid UOM codes";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 2).Value = uomCodes.Count > 0
            ? string.Join(", ", uomCodes)
            : "(none configured yet — leave the UOM column blank)";
        row += 2;

        sheet.Cell(row, 1).Value = "Read this before you upload";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontColor = Navy;
        row++;
        foreach (var note in Notes)
        {
            sheet.Cell(row, 1).Value = "•";
            sheet.Cell(row, 2).Value = note;
            row++;
        }

        sheet.Columns(1, 4).AdjustToContents();
        sheet.Column(1).Width = Math.Max(sheet.Column(1).Width, 20);
        sheet.Column(4).Width = 55;
        sheet.Column(5).Width = 24;
        sheet.Column(2).Width = Math.Max(sheet.Column(2).Width, 14);
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

/// <summary>Product master export (§15 guard rails). ClosedXML replaces the raw
/// MiniExcel dump so the file matches the house report style (see ReportExport)
/// — title block, navy header row, banded rows, typed date/number cells.
/// Dapper still reads unbuffered and each row is written straight into the sheet
/// and dropped, so the products never exist as a second full list alongside the
/// workbook; only the ClosedXML DOM is held, which is unavoidable for styling.</summary>
public sealed class ProductExport(IDbConnectionFactory connections)
{
    /// <summary>A SUPERSET of <see cref="ImportPipeline.TemplateHeaders"/>, in the
    /// same order, so an export is still re-importable without re-arranging
    /// columns. Production Date, Expiry Date and Category are reported here
    /// because they exist on the product, but they are outside the import
    /// contract and the importer ignores them on the way back in.</summary>
    private static readonly string[] Headers =
        ["Code", "Description", "UOM", "Size", "Color", "Batch",
         "Quantity", "Carton Quantity", "Cartons per Pallet",
         "Production Date", "Expiry Date", "Category"];

    private const int HeaderRow = 4;
    private const int FirstDataRow = HeaderRow + 1;

    // 1-based column indexes used by the formatting passes below.
    private const int ColDescription = 2;
    private const int ColQuantity = 7;
    private const int ColCartonQuantity = 8;
    private const int ColCartonsPerPallet = 9;
    private const int ColProductionDate = 10;
    private const int ColExpiryDate = 11;
    private const int ColCategory = 12;

    private static readonly XLColor Navy = XLColor.FromHtml("#1F4E79");
    private static readonly XLColor NavyDark = XLColor.FromHtml("#14375A");
    private static readonly XLColor Band = XLColor.FromHtml("#F2F6FA");
    private static readonly XLColor Grid = XLColor.FromHtml("#D9E2EC");

    /// <summary>Auto-fit samples only the first page of data. Measuring every one
    /// of 100k rows costs far more than the extra accuracy is worth, and product
    /// codes are fixed-shape anyway.</summary>
    private const int WidthSampleRows = 200;

    public async Task<byte[]> BuildAsync(CancellationToken ct)
    {
        await using var conn = await connections.OpenAsync(ct);

        // Dates and quantities come back RAW (no DATE_FORMAT) so they can be
        // written as real Excel dates/numbers rather than text that will not
        // sort or total in a pivot.
        var rows = conn.Query<Row>(
            """
            SELECT p.code             AS Code,
                   p.description      AS Description,
                   u.code             AS Uom,
                   p.size             AS Size,
                   p.color            AS Color,
                   p.default_batch    AS Batch,
                   p.default_production_date AS ProductionDate,
                   p.default_expiry_date     AS ExpiryDate,
                   p.default_quantity  AS Quantity,
                   p.carton_quantity   AS CartonQuantity,
                   p.cartons_per_pallet AS CartonsPerPallet,
                   c.name              AS Category
            FROM products p
            LEFT JOIN uoms u ON u.id = p.uom_id
            LEFT JOIN product_categories c ON c.id = p.category_id
            WHERE p.is_active = 1
            ORDER BY p.code
            """, buffered: false);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Products");

        sheet.Cell(1, 1).Value = "Products";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;
        sheet.Cell(1, 1).Style.Font.FontColor = Navy;

        // No user identity reaches this class (the endpoint injects only the
        // export service), so the sub-header states the source, not the operator.
        // Invariant on purpose: the server's culture uses ' - ' as its date
        // separator, which turns "dd/MM/yyyy" into "16 - 08 - 2026".
        sheet.Cell(2, 1).Value = FormattableString.Invariant(
            $"Generated: {DateTime.Now:dd/MM/yyyy HH:mm} — active products only");
        sheet.Cell(2, 1).Style.Font.FontColor = XLColor.Gray;

        for (var i = 0; i < Headers.Length; i++)
        {
            var cell = sheet.Cell(HeaderRow, i + 1);
            cell.Value = Headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = Navy;
            cell.Style.Font.FontColor = XLColor.White;
            cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            cell.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
            cell.Style.Border.BottomBorderColor = NavyDark;
        }
        // Numeric headers sit over right-aligned data.
        sheet.Range(HeaderRow, ColQuantity, HeaderRow, ColCartonsPerPallet)
            .Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        sheet.Row(HeaderRow).Height = 20;

        var row = FirstDataRow;
        var count = 0;
        foreach (var item in rows)
        {
            ct.ThrowIfCancellationRequested();

            // SetIfPresent everywhere: a NULL column must leave a genuinely empty
            // cell, otherwise Excel shows "0" / "01/01/0001" for missing data.
            SetText(sheet.Cell(row, 1), item.Code);
            SetText(sheet.Cell(row, ColDescription), item.Description);
            SetText(sheet.Cell(row, 3), item.Uom);
            SetText(sheet.Cell(row, 4), item.Size);
            SetText(sheet.Cell(row, 5), item.Color);
            SetText(sheet.Cell(row, 6), item.Batch);
            SetNumber(sheet.Cell(row, ColQuantity), item.Quantity);
            SetNumber(sheet.Cell(row, ColCartonQuantity), item.CartonQuantity);
            SetNumber(sheet.Cell(row, ColCartonsPerPallet), item.CartonsPerPallet);
            SetDate(sheet.Cell(row, ColProductionDate), item.ProductionDate);
            SetDate(sheet.Cell(row, ColExpiryDate), item.ExpiryDate);
            SetText(sheet.Cell(row, ColCategory), item.Category);

            if (count % 2 == 1)
            {
                sheet.Range(row, 1, row, Headers.Length).Style.Fill.BackgroundColor = Band;
            }
            row++;
            count++;
        }

        var lastRow = Math.Max(row - 1, FirstDataRow);
        var data = sheet.Range(HeaderRow, 1, lastRow, Headers.Length);

        if (count > 0)
        {
            var body = sheet.Range(FirstDataRow, 1, lastRow, Headers.Length);
            body.Style.Border.InsideBorder = XLBorderStyleValues.Hair;
            body.Style.Border.InsideBorderColor = Grid;
            body.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            body.Style.Border.OutsideBorderColor = Grid;

            sheet.Range(FirstDataRow, ColProductionDate, lastRow, ColExpiryDate)
                .Style.NumberFormat.Format = "dd/MM/yyyy";
            sheet.Range(FirstDataRow, ColQuantity, lastRow, ColCartonQuantity)
                .Style.NumberFormat.Format = "#,##0.###";
            sheet.Range(FirstDataRow, ColCartonsPerPallet, lastRow, ColCartonsPerPallet)
                .Style.NumberFormat.Format = "#,##0";
        }

        // Filter + freeze so the header stays put on a long product list.
        data.SetAutoFilter();
        sheet.SheetView.FreezeRows(HeaderRow);

        var sampleTo = Math.Min(lastRow, HeaderRow + WidthSampleRows);
        for (var col = 1; col <= Headers.Length; col++)
        {
            var column = sheet.Column(col);
            column.AdjustToContents(HeaderRow, sampleTo);
            // AdjustToContents() has no max-width overload here, so long free
            // text gets a fixed width and wraps instead of stretching off-screen.
            column.Width = col == ColDescription
                ? 45
                : Math.Clamp(column.Width + 2, 10, 24);
        }
        sheet.Column(ColDescription).Style.Alignment.WrapText = true;

        var footerRow = lastRow + 2;
        sheet.Cell(footerRow, 1).Value = FormattableString.Invariant(
            $"{count:N0} {(count == 1 ? "product" : "products")} exported");
        sheet.Cell(footerRow, 1).Style.Font.Italic = true;
        sheet.Cell(footerRow, 1).Style.Font.FontColor = XLColor.Gray;

        // Printing a product list is a real workflow here; repeat the header.
        sheet.PageSetup.PageOrientation = XLPageOrientation.Landscape;
        sheet.PageSetup.SetRowsToRepeatAtTop(HeaderRow, HeaderRow);
        sheet.PageSetup.FitToPages(1, 0);

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    private static void SetText(IXLCell cell, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            cell.SetValue(value);
        }
    }

    private static void SetDate(IXLCell cell, DateTime? value)
    {
        if (value is { } date)
        {
            cell.SetValue(date);
        }
    }

    private static void SetNumber(IXLCell cell, decimal? value)
    {
        if (value is { } number)
        {
            cell.SetValue((double)number);
        }
    }

    private static void SetNumber(IXLCell cell, int? value)
    {
        if (value is { } number)
        {
            cell.SetValue(number);
        }
    }

    private sealed class Row
    {
        public string? Code { get; init; }
        public string? Description { get; init; }
        public string? Uom { get; init; }
        public string? Size { get; init; }
        public string? Color { get; init; }
        public string? Batch { get; init; }
        public DateTime? ProductionDate { get; init; }
        public DateTime? ExpiryDate { get; init; }
        public decimal? Quantity { get; init; }
        public decimal? CartonQuantity { get; init; }
        public int? CartonsPerPallet { get; init; }
        public string? Category { get; init; }
    }
}
