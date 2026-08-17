using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Wpf.Features.Login;
using BarcodePrinter.Wpf.Services;
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
    public IReadOnlyList<string> Statuses { get; } =
        ["", "Queued", "Dispatching", "Printing", "Completed", "Failed", "Cancelled"];

    public bool CanReprint { get; }
    public bool CanCancel { get; }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? errorReference;
    [ObservableProperty] private string? countText;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReprintCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelJobCommand))]
    private PrintJobDto? selectedJob;

    [ObservableProperty] private DateTime? fromDate = DateTime.Today;
    [ObservableProperty] private DateTime? toDate = DateTime.Today;
    [ObservableProperty] private string? selectedStatus;
    [ObservableProperty] private bool reprintsOnly;
    [ObservableProperty] private string searchText = "";

    partial void OnFromDateChanged(DateTime? value) => _ = SearchAsync();
    partial void OnToDateChanged(DateTime? value) => _ = SearchAsync();
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
        var from = (FromDate ?? DateTime.Today).Date.ToUniversalTime();
        var to = (ToDate ?? DateTime.Today).Date.AddDays(1).ToUniversalTime();

        IsBusy = true;
        ErrorMessage = null;
        ErrorReference = null;
        try
        {
            var page = await _api.HistoryAsync(
                from, to,
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
            IsEmpty = Jobs.Count == 0;
            CountText = Jobs.Count == 0 ? null
                : HasMore ? $"Showing the first {Jobs.Count:N0} jobs"
                : $"{Jobs.Count:N0} job{(Jobs.Count == 1 ? "" : "s")}";
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            ErrorReference = ex.CorrelationId;
            IsEmpty = false;
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check your network connection.";
            IsEmpty = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteReprint() => SelectedJob is not null;

    [RelayCommand(CanExecute = nameof(CanExecuteReprint))]
    private async Task ReprintAsync()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }

        // C-15: whether the reason is mandatory is a server setting; the prompt
        // is always offered and doubles as the confirmation step.
        var reason = await DialogService.PromptAsync($"Reprint job {job.JobNo}", "Reason for reprint");
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;   // cancelled, or no reason given — abort
        }

        IsBusy = true;
        try
        {
            var result = await _api.ReprintAsync(
                new ReprintRequest(job.Id, reason, Environment.MachineName), CancellationToken.None);
            ToastService.Instance.Success($"Reprint queued — job {result.JobNo}.");
            await LoadAsync(reset: true, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            ToastService.Instance.Error("Cannot reach the server. Check your network connection.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecuteCancelJob() => SelectedJob?.Status == "Queued";

    [RelayCommand(CanExecute = nameof(CanExecuteCancelJob))]
    private async Task CancelJobAsync()
    {
        if (SelectedJob is not { } job)
        {
            return;
        }
        var confirmed = await DialogService.ConfirmAsync(
            $"Cancel job {job.JobNo}?",
            $"{job.ProductCode} · {job.LabelCount} labels. The job is removed from the queue and will not print.",
            "Cancel job", danger: true);
        if (!confirmed)
        {
            return;
        }

        try
        {
            await _api.CancelAsync(job.Id, CancellationToken.None);
            ToastService.Instance.Success($"Job {job.JobNo} cancelled.");
            await LoadAsync(reset: true, CancellationToken.None);
        }
        catch (ApiException ex)
        {
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            ToastService.Instance.Error("Cannot reach the server. Check your network connection.");
        }
    }
}
