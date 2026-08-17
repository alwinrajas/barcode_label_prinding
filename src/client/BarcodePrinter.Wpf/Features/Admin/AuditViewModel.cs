using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Admin;
using BarcodePrinter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

public sealed partial class AuditViewModel : ObservableObject
{
    private const int PageSize = 100;
    private readonly AdminApi _api;
    private string? _nextCursor;

    public AuditViewModel(AdminApi api)
    {
        _api = api;
        _ = InitializeAsync();
    }

    public ObservableCollection<AuditEntryDto> Entries { get; } = [];
    public ObservableCollection<string> Actions { get; } = [];
    public IReadOnlyList<string> Severities { get; } = ["", "Info", "Warning", "Security"];
    public IReadOnlyList<string> RangePresets { get; } = ["Today", "Last 7 days", "Last 30 days"];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool hasMore;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private AuditEntryDto? selectedEntry;

    // Screen states
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool loadFailed;
    [ObservableProperty] private string? loadErrorMessage;
    [ObservableProperty] private string? loadErrorReference;
    [ObservableProperty] private string? countText;

    [ObservableProperty] private string selectedRange = "Last 7 days";
    [ObservableProperty] private string? selectedAction;
    [ObservableProperty] private string? selectedSeverity;

    partial void OnSelectedRangeChanged(string value) => _ = SearchAsync();
    partial void OnSelectedActionChanged(string? value) => _ = SearchAsync();
    partial void OnSelectedSeverityChanged(string? value) => _ = SearchAsync();

    private async Task InitializeAsync()
    {
        await GuardAsync(async () =>
        {
            var actions = await _api.ListAuditActionsAsync(CancellationToken.None);
            Actions.Clear();
            Actions.Add("");
            foreach (var action in actions)
            {
                Actions.Add(action);
            }
        });
        await SearchAsync();
    }

    [RelayCommand]
    private Task SearchAsync() => LoadAsync(reset: true);

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(reset: false);

    private async Task LoadAsync(bool reset)
    {
        var from = SelectedRange switch
        {
            "Today" => DateTime.UtcNow.Date,
            "Last 30 days" => DateTime.UtcNow.AddDays(-30),
            _ => DateTime.UtcNow.AddDays(-7),
        };

        await GuardAsync(async () =>
        {
            var page = await _api.QueryAuditAsync(
                from, DateTime.UtcNow.AddDays(1),
                string.IsNullOrWhiteSpace(SelectedAction) ? null : SelectedAction,
                string.IsNullOrWhiteSpace(SelectedSeverity) ? null : SelectedSeverity,
                reset ? null : _nextCursor, PageSize, CancellationToken.None);

            if (reset)
            {
                Entries.Clear();
            }
            foreach (var entry in page.Items)
            {
                Entries.Add(entry);
            }
            _nextCursor = page.NextCursor;
            HasMore = page.HasMore;
            IsEmpty = Entries.Count == 0;
            StatusMessage = Entries.Count == 0 ? "No audit entries match these filters." : null;
            CountText = Entries.Count == 0
                ? null
                : HasMore
                    ? $"Showing {Entries.Count} entries — more available"
                    : $"Showing all {Entries.Count} entries";
        }, isLoad: true);
    }

    private async Task GuardAsync(Func<Task> action, bool isLoad = false)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
            if (isLoad)
            {
                LoadFailed = false;
                LoadErrorMessage = null;
                LoadErrorReference = null;
            }
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
            if (isLoad)
            {
                SetLoadFailed(ex.Message, ex.CorrelationId);
            }
        }
        catch (ApiUnreachableException)
        {
            const string message = "Cannot reach the server. Check your network connection.";
            ErrorMessage = message;
            ToastService.Instance.Error(message);
            if (isLoad)
            {
                SetLoadFailed(message, null);
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetLoadFailed(string message, string? reference)
    {
        LoadFailed = true;
        LoadErrorMessage = message;
        LoadErrorReference = reference;
        IsEmpty = false;
        StatusMessage = null;
    }
}
