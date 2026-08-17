using BarcodePrinter.Contracts.Admin;
using ClosedXML.Excel;

namespace BarcodePrinter.Infrastructure.Admin;

/// <summary>
/// Excel export of the audit trail. The route and the Audit.Export permission
/// already existed but nothing was mapped to them, so any client that offered
/// the download got a 404. Bounded by the same cursor query the screen uses,
/// so the export can never materialise an unbounded history.
/// </summary>
public sealed class AuditExport(AdminQueries queries)
{
    private const int MaxRows = 5_000;

    public async Task<byte[]> BuildAsync(AuditFilter filter, string generatedBy, CancellationToken ct)
    {
        var rows = new List<AuditEntryDto>();
        string? cursor = null;
        do
        {
            var page = await queries.QueryAuditAsync(
                filter with { Cursor = cursor, PageSize = 200 }, ct);
            rows.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        while (cursor is not null && rows.Count < MaxRows);

        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Audit");

        sheet.Cell(1, 1).Value = "Audit log";
        sheet.Cell(1, 1).Style.Font.Bold = true;
        sheet.Cell(1, 1).Style.Font.FontSize = 14;

        var from = filter.FromUtc ?? DateTime.UtcNow.AddDays(-7);
        var to = filter.ToUtc ?? DateTime.UtcNow;
        sheet.Cell(2, 1).Value = FormattableString.Invariant($"Period: {from:dd/MM/yyyy} to {to:dd/MM/yyyy}");
        sheet.Cell(3, 1).Value = FormattableString.Invariant($"Generated: {DateTime.Now:dd/MM/yyyy HH:mm} by {generatedBy}");
        sheet.Cell(3, 1).Style.Font.FontColor = XLColor.Gray;
        if (rows.Count >= MaxRows)
        {
            sheet.Cell(4, 1).Value =
                $"Truncated at {MaxRows:N0} entries — narrow the period to export the rest.";
            sheet.Cell(4, 1).Style.Font.FontColor = XLColor.FromHtml("#B26B00");
        }

        string[] headers =
        [
            "When", "User", "Action", "Entity type", "Reference",
            "Severity", "Workstation", "IP", "Correlation", "Before", "After",
        ];
        const int headerRow = 6;
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(headerRow, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F4E79");
            cell.Style.Font.FontColor = XLColor.White;
        }

        var row = headerRow + 1;
        foreach (var entry in rows)
        {
            var column = 1;
            sheet.Cell(row, column++).Value = entry.OccurredAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            sheet.Cell(row, column++).Value = entry.Username;
            sheet.Cell(row, column++).Value = entry.Action;
            sheet.Cell(row, column++).Value = entry.EntityType;
            sheet.Cell(row, column++).Value = entry.EntityId;
            sheet.Cell(row, column++).Value = entry.Severity;
            sheet.Cell(row, column++).Value = entry.Workstation;
            sheet.Cell(row, column++).Value = entry.Ip;
            sheet.Cell(row, column++).Value = entry.CorrelationId;
            sheet.Cell(row, column++).Value = entry.BeforeJson;
            sheet.Cell(row, column).Value = entry.AfterJson;
            row++;
        }

        // Before/After hold JSON; cap the width so one long payload cannot
        // stretch a column across the screen.
        sheet.Columns(1, headers.Length - 2).AdjustToContents();
        sheet.Column(headers.Length - 1).Width = 60;
        sheet.Column(headers.Length).Width = 60;
        sheet.SheetView.FreezeRows(headerRow);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
