using System.Collections.ObjectModel;
using System.Windows;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Wpf.Features.Login;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Printing;

public sealed partial class PrintHistoryViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly PrintApi _api;
    private string? _nextCursor;
    private CancellationTokenSource _searchCts = new();

    public PrintHistoryViewModel(PrintApi api, Session session)
    {
        _api = api;
        CanReprint = session.Has(PermissionCodes.PrintReprint);
        CanCancel = session.Has(PermissionCodes.PrintCancel);
        _ = SearchAsync();
    }

    public ObservableCollection<PrintJobDto> Jobs { get; } = [];
    public IReadOnlyList<string> RangePresets { get; } = ["Today", "Last 7 days", "Last 30 days"];
    public IReadOnlyList<string> Statuses { get; } =
        ["", "Queued", "Dispatching", "Printing", "Completed", "Failed", "Cancelled"];

    public bool CanReprint { get; }
    public bool CanCancel { get; }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private PrintJobDto? selectedJob;
    [ObservableProperty] private string selectedRange = "Today";
    [ObservableProperty] private string? selectedStatus;
    [ObservableProperty] private bool reprintsOnly;
    [ObservableProperty] private string searchText = "";

    partial void OnSelectedRangeChanged(string value) => _ = SearchAsync();
    partial void OnSelectedStatusChanged(string? value) => _ = SearchAsync();
    partial void OnReprintsOnlyChanged(bool value) => _ = SearchAsync();
    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync();

    private async Task DebouncedSearchAsync()
    {
        await _searchCts.CancelAsync();
        _searchCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(250, _searchCts.Token);
            await LoadAsync(reset: true, _searchCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
    }

    [RelayCommand]
    private Task SearchAsync() => LoadAsync(reset: true, CancellationToken.None);

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(reset: false, CancellationToken.None);

    private async Task LoadAsync(bool reset, CancellationToken ct)
    {
        var from = SelectedRange switch
        {
            "Last 7 days" => DateTime.UtcNow.AddDays(-7),
            "Last 30 days" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.UtcNow.Date,
        };

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var page = await _api.HistoryAsync(
                from, DateTime.UtcNow.AddDays(1),
                string.IsNullOrWhiteSpace(SelectedStatus) ? null : SelectedStatus,
                ReprintsOnly, SearchText, reset ? null : _nextCursor, PageSize, ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (reset)
            {
                Jobs.Clear();
            }
            foreach (var job in page.Items)
            {
                Jobs.Add(job);
            }
            _nextCursor = page.NextCursor;
            HasMore = page.HasMore;
            StatusMessage = Jobs.Count == 0 ? "No print jobs match these filters." : null;
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
    private async Task ReprintAsync()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            $"Reprint job {job.JobNo}?\n\n{job.ProductCode} · cartons {job.CartonFrom}–{job.CartonTo} " +
            $"({job.LabelCount} labels)\n\nThe original labels are reproduced exactly, " +
            "including the same carton numbers.",
            "Confirm reprint", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        var reason = ReprintReasonPrompt.Ask(job.JobNo);
        if (reason is null)
        {
            return;   // cancelled at the reason step
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var result = await _api.ReprintAsync(
                new ReprintRequest(job.Id, reason, Environment.MachineName), CancellationToken.None);
            StatusMessage = $"Reprint sent — job {result.JobNo}.";
            await LoadAsync(reset: true, CancellationToken.None);
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
    private async Task CancelJobAsync()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }
        if (MessageBox.Show($"Cancel job {job.JobNo}?", "Confirm",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _api.CancelAsync(job.Id, CancellationToken.None);
            StatusMessage = "Job cancelled.";
            await LoadAsync(reset: true, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }
}

/// <summary>Small modal for the reprint reason (C-15: whether it is mandatory
/// is a server setting; the prompt is always offered).</summary>
public static class ReprintReasonPrompt
{
    public static string? Ask(string jobNo)
    {
        var window = new Window
        {
            Title = $"Reprint {jobNo}",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            ResizeMode = ResizeMode.NoResize,
        };

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Reason for reprint (recorded in the audit log)",
            Margin = new Thickness(0, 0, 0, 8),
        });
        var input = new System.Windows.Controls.TextBox { Height = 32, Padding = new Thickness(8, 0, 8, 0) };
        panel.Children.Add(input);

        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };
        var ok = new System.Windows.Controls.Button
        {
            Content = "Reprint", Width = 100, Height = 32, IsDefault = true,
        };
        var cancel = new System.Windows.Controls.Button
        {
            Content = "Cancel", Width = 90, Height = 32,
            Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        window.Content = panel;

        string? result = null;
        ok.Click += (_, _) => { result = input.Text; window.DialogResult = true; };
        input.Focus();

        return window.ShowDialog() == true ? result ?? "" : null;
    }
}
