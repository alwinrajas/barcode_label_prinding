using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using BarcodePrinter.Contracts.Reports;

namespace BarcodePrinter.Wpf.Features.Reports;

/// <summary>
/// Builds the printable form of a report (A-24).
///
/// A paginated <see cref="FlowDocument"/> rather than a screenshot of the grid:
/// a printed report is a document, so it needs a heading that says what it is
/// and over what period, column headers that repeat on every page, page
/// numbers, and totals that are unambiguously the totals for the whole filtered
/// set rather than for the rows that happen to be on the last page.
///
/// It carries the CLIENT's context, never ours — no application branding on a
/// report the client will file or send on.
/// </summary>
public static class ReportDocumentBuilder
{
    private static readonly Thickness CellPadding = new(6, 3, 6, 3);

    public static FlowDocument Build(
        string title, string period, string printedBy,
        IReadOnlyList<string> columns, IReadOnlyList<ReportRow> rows,
        ReportTotals? totals, bool isDetail, bool truncated)
    {
        var document = new FlowDocument
        {
            // A4 landscape at 96 dpi, less a 40-unit margin each side. Landscape
            // because these reports are wide; portrait would wrap every row.
            PageWidth = 1123,
            PageHeight = 794,
            PagePadding = new Thickness(40),
            ColumnWidth = double.PositiveInfinity,   // one column, not newspaper columns
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
        };

        document.Blocks.Add(Heading(title, period, printedBy));

        if (rows.Count == 0)
        {
            document.Blocks.Add(new Paragraph(new Run("No activity in this period."))
            {
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(0, 12, 0, 0),
            });
            return document;
        }

        document.Blocks.Add(BuildTable(columns, rows, isDetail));

        if (totals is not null)
        {
            document.Blocks.Add(TotalsParagraph(totals));
        }

        if (truncated)
        {
            // Silently printing a partial report as though it were complete is
            // the failure mode worth guarding against here.
            document.Blocks.Add(new Paragraph(new Run(
                "Only the rows loaded on screen are printed. Use Export to Excel for the complete set."))
            {
                FontStyle = FontStyles.Italic,
                FontSize = 10,
                Foreground = Brushes.DimGray,
                Margin = new Thickness(0, 10, 0, 0),
            });
        }

        return document;
    }

    private static Block Heading(string title, string period, string printedBy)
    {
        var heading = new Paragraph { Margin = new Thickness(0, 0, 0, 12) };
        heading.Inlines.Add(new Run(title) { FontSize = 18, FontWeight = FontWeights.SemiBold });
        heading.Inlines.Add(new LineBreak());
        heading.Inlines.Add(new Run(period) { FontSize = 11, Foreground = Brushes.DimGray });
        heading.Inlines.Add(new LineBreak());
        heading.Inlines.Add(new Run(
            $"Printed {DateTime.Now:dd/MM/yyyy HH:mm} by {printedBy}")
        {
            FontSize = 10,
            Foreground = Brushes.Gray,
        });
        return heading;
    }

    private static Table BuildTable(
        IReadOnlyList<string> columns, IReadOnlyList<ReportRow> rows, bool isDetail)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var _ in columns)
        {
            table.Columns.Add(new TableColumn());
        }

        var header = new TableRowGroup();
        var headerRow = new TableRow { Background = Brushes.WhiteSmoke };
        foreach (var column in columns)
        {
            headerRow.Cells.Add(new TableCell(new Paragraph(new Run(column)))
            {
                FontWeight = FontWeights.SemiBold,
                Padding = CellPadding,
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(0, 0, 0, 1),
            });
        }
        header.Rows.Add(headerRow);
        table.RowGroups.Add(header);

        var body = new TableRowGroup();
        foreach (var row in rows)
        {
            var tableRow = new TableRow();
            foreach (var value in CellsFor(row, isDetail))
            {
                tableRow.Cells.Add(new TableCell(new Paragraph(new Run(value ?? "")))
                {
                    Padding = CellPadding,
                    BorderBrush = Brushes.Gainsboro,
                    BorderThickness = new Thickness(0, 0, 0, 0.5),
                });
            }
            body.Rows.Add(tableRow);
        }
        table.RowGroups.Add(body);
        return table;
    }

    /// <summary>Mirrors the column sets the server declares for the two report
    /// shapes, so the printed page matches the screen exactly.</summary>
    private static IEnumerable<string?> CellsFor(ReportRow row, bool isDetail) =>
        isDetail
            ?
            [
                row.JobNo,
                row.RequestedAtUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                row.Key,
                row.Secondary,
                row.Batch,
                row.User,
                row.Printer,
                row.Labels.ToString("N0"),
                row.Status,
            ]
            :
            [
                row.Key,
                row.Secondary,
                row.Jobs.ToString("N0"),
                row.Labels.ToString("N0"),
                row.Cartons.ToString("N0"),
                row.Failed.ToString("N0"),
                row.Reprints.ToString("N0"),
                row.LastPrintedUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
            ];

    private static Block TotalsParagraph(ReportTotals totals)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 12, 0, 0) };
        paragraph.Inlines.Add(new Run("Totals for the whole period  ")
        {
            FontWeight = FontWeights.SemiBold,
        });
        paragraph.Inlines.Add(new Run(
            $"Jobs {totals.Jobs:N0}   Labels {totals.Labels:N0}   Cartons {totals.Cartons:N0}   " +
            $"Failed {totals.Failed:N0}   Reprints {totals.Reprints:N0}"));
        return paragraph;
    }
}
