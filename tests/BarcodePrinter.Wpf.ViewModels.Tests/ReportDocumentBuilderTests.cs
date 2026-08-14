using System.Reflection;
using System.Windows.Documents;
using BarcodePrinter.Contracts.Reports;
using BarcodePrinter.Wpf.Features.Reports;
using FluentAssertions;
using Xunit;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

/// <summary>
/// A printed report leaves the building — it gets filed, or sent to a customer.
/// These pin the things that would make it misleading rather than merely ugly.
/// </summary>
public class ReportDocumentBuilderTests
{
    private static readonly string[] DetailColumns =
        ["Job", "When", "Product", "Description", "Batch", "User", "Printer", "Labels", "Status"];

    private static readonly string[] SummaryColumns =
        ["Product", "Description", "Jobs", "Labels", "Cartons", "Failed", "Reprints", "Last printed"];

    [StaFact]
    public void A_detail_report_prints_a_row_per_job_under_the_server_declared_headers()
    {
        var document = ReportDocumentBuilder.Build(
            "Barcode printing log", "Last 7 days", "Ravi",
            DetailColumns, [DetailRow("PJ-260813-000001"), DetailRow("PJ-260813-000002")],
            new ReportTotals(2, 10, 10, 0, 0), isDetail: true, truncated: false);

        var table = document.Blocks.OfType<Table>().Single();
        table.Columns.Should().HaveCount(DetailColumns.Length);

        var headers = table.RowGroups[0].Rows[0].Cells
            .Select(c => TextOf(c)).ToList();
        headers.Should().Equal(DetailColumns, "the printed page must match the screen");

        table.RowGroups[1].Rows.Should().HaveCount(2);
        TextOf(table.RowGroups[1].Rows[0].Cells[0]).Should().Be("PJ-260813-000001");
    }

    [StaFact]
    public void A_summary_report_prints_the_aggregate_columns_instead()
    {
        var document = ReportDocumentBuilder.Build(
            "Product-wise printing", "Last 30 days", "Ravi",
            SummaryColumns,
            [new ReportRow("5GCAPM2N", "5G M2 CAP", 4, 120, 120, 1, 2,
                new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
                null, null, null, null, null, null, null)],
            new ReportTotals(4, 120, 120, 1, 2), isDetail: false, truncated: false);

        var cells = document.Blocks.OfType<Table>().Single()
            .RowGroups[1].Rows[0].Cells.Select(c => TextOf(c)).ToList();

        cells.Should().HaveCount(SummaryColumns.Length);
        cells[0].Should().Be("5GCAPM2N");
        cells[3].Should().Be("120", "labels are the number the report is read for");
    }

    /// <summary>Totals must be the period's totals, not the sum of the printed
    /// page — otherwise a partial print reads as a smaller month.</summary>
    [StaFact]
    public void Totals_are_stated_as_covering_the_whole_period()
    {
        var document = ReportDocumentBuilder.Build(
            "Barcode printing log", "This month", "Ravi",
            DetailColumns, [DetailRow("PJ-1")],
            new ReportTotals(500, 12_345, 12_345, 7, 3), isDetail: true, truncated: true);

        var text = string.Join(" ", document.Blocks.OfType<Paragraph>().Select(TextOf));
        text.Should().Contain("Totals for the whole period");
        text.Should().Contain("12,345");
    }

    [StaFact]
    public void A_partial_print_says_so_rather_than_looking_complete()
    {
        var truncated = ReportDocumentBuilder.Build(
            "Barcode printing log", "This month", "Ravi",
            DetailColumns, [DetailRow("PJ-1")], null, isDetail: true, truncated: true);
        var complete = ReportDocumentBuilder.Build(
            "Barcode printing log", "This month", "Ravi",
            DetailColumns, [DetailRow("PJ-1")], null, isDetail: true, truncated: false);

        TextIn(truncated).Should().Contain("Export to Excel for the complete set");
        TextIn(complete).Should().NotContain("complete set");
    }

    [StaFact]
    public void An_empty_period_prints_a_statement_rather_than_an_empty_grid()
    {
        var document = ReportDocumentBuilder.Build(
            "Reprint history", "Today", "Ravi", DetailColumns, [], null,
            isDetail: true, truncated: false);

        document.Blocks.OfType<Table>().Should().BeEmpty();
        TextIn(document).Should().Contain("No activity in this period");
    }

    /// <summary>
    /// A printed report carries the CLIENT's context and no vendor or product
    /// branding — they file it, or send it on to their own customers.
    ///
    /// The company name is read from assembly metadata rather than written as a
    /// literal, so this keeps holding whatever that metadata is later set to,
    /// and the check itself never becomes a place branding lives.
    /// </summary>
    [StaFact]
    public void The_document_carries_no_application_branding()
    {
        var company = typeof(ReportDocumentBuilder).Assembly
            .GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        var product = typeof(ReportDocumentBuilder).Assembly
            .GetCustomAttribute<AssemblyProductAttribute>()?.Product;

        var document = ReportDocumentBuilder.Build(
            "Barcode printing log", "Today", "Ravi",
            DetailColumns, [DetailRow("PJ-1")], null, isDetail: true, truncated: false);

        var text = TextIn(document);
        if (!string.IsNullOrWhiteSpace(company))
        {
            text.Should().NotContain(company, "a report is the client's document, not ours");
        }
        if (!string.IsNullOrWhiteSpace(product))
        {
            text.Should().NotContain(product);
        }

        text.Should().Contain("Barcode printing log", "the report must say what it is");
        text.Should().Contain("Ravi", "who printed it is part of the audit trail");
    }

    private static ReportRow DetailRow(string jobNo) => new(
        "5GCAPM2N", "5G M2 CAP", 1, 5, 5, 0, 0,
        new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
        1, jobNo, "CONE", "admin", "Line-2", "Completed",
        new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc));

    private static string TextIn(FlowDocument document) =>
        new TextRange(document.ContentStart, document.ContentEnd).Text;

    private static string TextOf(TableCell cell) =>
        string.Concat(cell.Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text)).Trim();

    private static string TextOf(Paragraph paragraph) =>
        string.Concat(paragraph.Inlines.OfType<Run>().Select(r => r.Text));
}
