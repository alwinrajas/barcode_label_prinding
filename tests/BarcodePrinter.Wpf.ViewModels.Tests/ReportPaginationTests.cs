using System.Windows;
using System.Windows.Documents;
using BarcodePrinter.Contracts.Reports;
using BarcodePrinter.Wpf.Features.Reports;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BarcodePrinter.Wpf.ViewModels.Tests;

public class ReportPaginationTests(ITestOutputHelper output)
{
    private static readonly string[] Columns =
        ["Job", "When", "Product", "Description", "Batch", "User", "Printer", "Labels", "Status"];

    [StaTheory]
    [InlineData(5)]
    [InlineData(40)]
    public void Pagination_is_proportional_to_the_number_of_rows(int rowCount)
    {
        var rows = Enumerable.Range(1, rowCount).Select(i => Row($"PJ-{i:000000}")).ToList();
        var document = ReportDocumentBuilder.Build(
            "Barcode printing log", "Last 7 days", "Ravi",
            Columns, rows, new ReportTotals(rowCount, rowCount * 5, 0, 0, 0),
            isDetail: true, truncated: false);

        // A4 portrait at 96 dpi, as "Microsoft Print to PDF" reports it.
        document.PageWidth = 793;
        document.PageHeight = 1122;
        document.PagePadding = new Thickness(40);
        document.ColumnWidth = double.PositiveInfinity;

        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.ComputePageCount();
        output.WriteLine($"{rowCount} rows -> {paginator.PageCount} page(s)");

        paginator.PageCount.Should().BeLessThanOrEqualTo(rowCount switch
        {
            5 => 1,
            _ => 3,
        }, "a report must not balloon into pages of near-empty paper");
    }

    private static ReportRow Row(string jobNo) => new(
        "5GCAPM2N", "5G M2 CAP", 1, 5, 5, 0, 0,
        new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc),
        1, jobNo, "CONE", "admin", "Line-2", "Completed",
        new DateTime(2026, 8, 13, 9, 0, 0, DateTimeKind.Utc));
}
