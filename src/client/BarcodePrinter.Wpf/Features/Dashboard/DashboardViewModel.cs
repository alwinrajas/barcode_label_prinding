using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Dashboard;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Dashboard;

/// <summary>One bar of the seven-day trend, pre-scaled for display.</summary>
public sealed record TrendBar(string Label, int Labels, int Failed, double HeightFraction, bool IsToday);

public sealed partial class DashboardViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DashboardApi _api;
    private readonly System.Timers.Timer _timer;

    /// <summary>Raised when an alert or quick action wants the shell to
    /// navigate; the ViewModel does not know about views.</summary>
    public event EventHandler<string>? NavigationRequested;

    public DashboardViewModel(DashboardApi api)
    {
        _api = api;
        _timer = new System.Timers.Timer(RefreshInterval.TotalMilliseconds) { AutoReset = true };
        _timer.Elapsed += (_, _) => _ = RefreshOnUiThreadAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    public ObservableCollection<RecentJobDto> RecentJobs { get; } = [];
    public ObservableCollection<PrinterHealthDto> Printers { get; } = [];
    public ObservableCollection<DashboardAlertDto> Alerts { get; } = [];
    public ObservableCollection<TrendBar> Trend { get; } = [];

    [ObservableProperty] private DashboardKpis? kpis;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private DateTime lastUpdated;

    public string Greeting => DateTime.Now.Hour switch
    {
        < 12 => "Good morning",
        < 18 => "Good afternoon",
        _ => "Good evening",
    };

    /// <summary>Today vs yesterday, stated in words rather than as a bare
    /// percentage the operator has to interpret.</summary>
    public string TrendSummary
    {
        get
        {
            if (Kpis is null)
            {
                return "";
            }
            if (Kpis.LabelsYesterday == 0)
            {
                return Kpis.LabelsToday == 0 ? "No labels printed yet today." : "First printing of the week.";
            }
            var change = Kpis.LabelsToday - Kpis.LabelsYesterday;
            var percent = Math.Abs(change) * 100.0 / Kpis.LabelsYesterday;
            return change switch
            {
                0 => "Same as yesterday.",
                > 0 => $"{percent:F0}% more than yesterday.",
                _ => $"{percent:F0}% fewer than yesterday.",
            };
        }
    }

    public bool HasAlerts => Alerts.Count > 0;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var data = await _api.GetAsync(CancellationToken.None);

            Kpis = data.Kpis;
            Replace(RecentJobs, data.RecentJobs);
            Replace(Printers, data.Printers);
            Replace(Alerts, data.Alerts);

            var peak = Math.Max(1, data.LastSevenDays.Max(d => d.Labels));
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            Replace(Trend, data.LastSevenDays.Select(d => new TrendBar(
                d.Date.ToString("ddd"), d.Labels, d.Failed,
                Math.Max(d.Labels / (double)peak, d.Labels > 0 ? 0.06 : 0.0),
                d.Date == today)));

            LastUpdated = DateTime.Now;
            OnPropertyChanged(nameof(TrendSummary));
            OnPropertyChanged(nameof(HasAlerts));
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Showing the last known figures.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Navigate(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            NavigationRequested?.Invoke(this, key);
        }
    }

    /// <summary>The timer fires on a pool thread; collection updates must land
    /// on the dispatcher.</summary>
    private Task RefreshOnUiThreadAsync() =>
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () => await RefreshAsync())
            .Task.Unwrap() ?? Task.CompletedTask;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }
    }

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
    }
}
