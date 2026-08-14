using BarcodePrinter.Contracts.Reports;
using ClosedXML.Excel;

namespace BarcodePrinter.Infrastructure.Reports;

/// <summary>
/// Excel export of any report (A-24). The report is already bounded by the
/// query layer, so the export never materialises an unbounded history; it
/// writes the same rows the operator is looking at, plus a header block that
/// makes the file self-describing when it is emailed on.
/// </summary>
public sealed class ReportExport(ReportQueries queries)
{
    public async Task<byte[]> BuildAsync(ReportFilter filter, string generatedBy, CancellationToken ct)
    {
        // Export the full report, not just the visible page.
        var result = await queries.RunAsync(filter with { PageSize = 500, Cursor = null }, ct);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Report");

        sheet.Cell(1, 1).Value = result.Title;
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;

        var from = filter.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        var to = filter.ToUtc ?? DateTime.UtcNow;
        sheet.Cell(2, 1).Value = $"Period: {from:dd/MM/yyyy} to {to:dd/MM/yyyy}";
        sheet.Cell(3, 1).Value = $"Generated: {DateTime.Now:dd/MM/yyyy HH:mm} by {generatedBy}";
        sheet.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;

        var headerRow = 5;
        for (var i = 0; i < result.Columns.Count; i++)
        {
            var cell = sheet.Cell(headerRow, i + 1);
            cell.Value = result.Columns[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var isDetail = result.Type is nameof(ReportType.PrintLog) or nameof(ReportType.Reprints);
        var row = headerRow + 1;
        foreach (var item in result.Rows)
        {
            var column = 1;
            if (isDetail)
            {
                sheet.Cell(row, column++).Value = item.JobNo;
                sheet.Cell(row, column++).Value = item.RequestedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
                sheet.Cell(row, column++).Value = item.Key;
                sheet.Cell(row, column++).Value = item.Secondary;
                sheet.Cell(row, column++).Value = item.Batch;
                sheet.Cell(row, column++).Value = item.User;
                sheet.Cell(row, column++).Value = item.Printer;
                sheet.Cell(row, column++).Value = item.Labels;
                sheet.Cell(row, column).Value = item.Status;
            }
            else
            {
                sheet.Cell(row, column++).Value = item.Key;
                sheet.Cell(row, column++).Value = item.Secondary;
                sheet.Cell(row, column++).Value = item.Jobs;
                sheet.Cell(row, column++).Value = item.Labels;
                sheet.Cell(row, column++).Value = item.Cartons;
                sheet.Cell(row, column++).Value = item.Failed;
                sheet.Cell(row, column++).Value = item.Reprints;
                sheet.Cell(row, column).Value = item.LastPrintedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm");
            }
            row++;
        }

        // Totals from the query layer — the full filtered set, not this page.
        row++;
        sheet.Cell(row, 1).Value = "TOTAL";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        if (isDetail)
        {
            sheet.Cell(row, 8).Value = result.Totals.Labels;
            sheet.Cell(row, 8).Style.Font.Bold = true;
        }
        else
        {
            sheet.Cell(row, 3).Value = result.Totals.Jobs;
            sheet.Cell(row, 4).Value = result.Totals.Labels;
            sheet.Cell(row, 5).Value = result.Totals.Cartons;
            sheet.Cell(row, 6).Value = result.Totals.Failed;
            sheet.Cell(row, 7).Value = result.Totals.Reprints;
            sheet.Range(row, 3, row, 7).Style.Font.Bold = true;
        }

        if (result.HasMore)
        {
            row += 2;
            sheet.Cell(row, 1).Value =
                "Note: this export shows the first 500 rows. Narrow the filters for a complete list.";
            sheet.Cell(row, 1).Style.Font.FontColor = XLColor.DarkOrange;
        }

        sheet.Columns(1, Math.Max(result.Columns.Count, 9)).AdjustToContents();
        sheet.SheetView.FreezeRows(headerRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
