using System.Collections.ObjectModel;
using System.IO;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Reports;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Reports;

public sealed record ReportOption(string Type, string Title);

public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly ReportsApi _api;
    private readonly string _userDisplay;
    private string? _nextCursor;
    private IReadOnlyList<string> _columns = [];

    public ReportsViewModel(ReportsApi api, Session session)
    {
        _api = api;
        CanExport = session.Has(PermissionCodes.ReportExport);
        CanPrint = session.Has(PermissionCodes.ReportPrint);
        _userDisplay = session.User.FullName;
        // Select explicitly so the combo shows what is actually displayed
        // (setting the property also triggers the first load).
        SelectedReport = ReportTypes[0];
    }

    public IReadOnlyList<ReportOption> ReportTypes { get; } =
    [
        new(nameof(ReportType.PrintLog), "Barcode printing log"),
        new(nameof(ReportType.ByProduct), "Product-wise printing"),
        new(nameof(ReportType.ByUser), "User-wise printing"),
        new(nameof(ReportType.ByPrinter), "Printer-wise printing"),
        new(nameof(ReportType.ByDate), "Date-wise printing"),
        new(nameof(ReportType.Reprints), "Reprint history"),
    ];

    public IReadOnlyList<string> RangePresets { get; } =
        ["Today", "Last 7 days", "Last 30 days", "This month"];

    public ObservableCollection<ReportRow> Rows { get; } = [];

    public bool CanExport { get; }
    public bool CanPrint { get; }

    [ObservableProperty] private ReportOption? selectedReport;
    [ObservableProperty] private string selectedRange = "Last 7 days";
    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string title = "Reports";
    [ObservableProperty] private bool isDetailReport = true;
    [ObservableProperty] private ReportTotals? totals;

    partial void OnSelectedReportChanged(ReportOption? value) => _ = RunAsync();
    partial void OnSelectedRangeChanged(string value) => _ = RunAsync();

    private (DateTime From, DateTime To) Range() => SelectedRange switch
    {
        "Today" => (DateTime.UtcNow.Date, DateTime.UtcNow.AddDays(1)),
        "Last 30 days" => (DateTime.UtcNow.AddDays(-30), DateTime.UtcNow.AddDays(1)),
        "This month" => (new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            DateTime.UtcNow.AddDays(1)),
        _ => (DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1)),
    };

    [RelayCommand]
    private Task RunAsync() => LoadAsync(reset: true);

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(reset: false);

    private async Task LoadAsync(bool reset)
    {
        var type = (SelectedReport ?? ReportTypes[0]).Type;
        var (from, to) = Range();

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _api.RunAsync(type, from, to, SearchText,
                reset ? null : _nextCursor, 200, CancellationToken.None);

            if (reset)
            {
                Rows.Clear();
            }
            foreach (var row in result.Rows)
            {
                Rows.Add(row);
            }

            Title = result.Title;
            _columns = result.Columns;
            IsDetailReport = type is nameof(ReportType.PrintLog) or nameof(ReportType.Reprints);
            Totals = result.Totals;
            _nextCursor = result.NextCursor;
            HasMore = result.HasMore;
            StatusMessage = Rows.Count == 0 ? "No printing activity in this period." : null;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check your network connection.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        var type = (SelectedReport ?? ReportTypes[0]).Type;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{type}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var (from, to) = Range();
            var bytes = await _api.ExportAsync(type, from, to, SearchText, CancellationToken.None);
            await File.WriteAllBytesAsync(dialog.FileName, bytes!);
            StatusMessage = "Report exported.";
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check your network connection.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Report printing (A-24). Prints what is on screen, through the standard
    /// Windows print dialog, so the user picks any printer and gets a preview
    /// via "Microsoft Print to PDF" if they want one.
    ///
    /// Deliberately prints the LOADED rows rather than silently re-querying the
    /// whole period: a report that quietly differs from the screen it was
    /// printed from is worse than one that says it is partial. Export to Excel
    /// remains the route to the complete set, and the document says so.
    /// </summary>
    [RelayCommand]
    private void PrintReport()
    {
        if (Rows.Count == 0)
        {
            StatusMessage = "There is nothing to print for this period.";
            return;
        }

        try
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var (from, to) = Range();
            var period = $"{SelectedRange} · {from.ToLocalTime():dd/MM/yyyy} to {to.ToLocalTime():dd/MM/yyyy}";
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                period += $" · filtered by \"{SearchText}\"";
            }

            var document = ReportDocumentBuilder.Build(
                Title, period, _userDisplay, _columns, Rows, Totals, IsDetailReport, HasMore);

            // Fit the page to the printer the user actually chose, rather than
            // assuming the A4 landscape the builder defaults to.
            document.PageHeight = dialog.PrintableAreaHeight;
            document.PageWidth = dialog.PrintableAreaWidth;
            document.PagePadding = new System.Windows.Thickness(40);
            document.ColumnWidth = double.PositiveInfinity;

            dialog.PrintDocument(
                ((System.Windows.Documents.IDocumentPaginatorSource)document).DocumentPaginator,
                Title);
            StatusMessage = "Report sent to the printer.";
        }
        catch (Exception ex)
        {
            // A printer problem must not take the reports screen down with it.
            ErrorMessage = $"Could not print the report: {ex.Message}";
        }
    }
}
