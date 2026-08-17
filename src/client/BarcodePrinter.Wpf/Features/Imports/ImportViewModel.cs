using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Imports;
using BarcodePrinter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.AspNetCore.SignalR.Client;

namespace BarcodePrinter.Wpf.Features.Imports;

/// <summary>
/// Excel import screen (§15/§12): the client uploads one file and WATCHES —
/// all heavy work is server-side, so this VM is nothing but state display.
/// Progress arrives over SignalR with a polling fallback (B-16).
/// </summary>
public sealed partial class ImportViewModel : ObservableObject
{
    private readonly ImportsApi _api;
    private HubConnection? _hub;
    private System.Timers.Timer? _pollTimer;
    private bool _terminalNotified;

    public ImportViewModel(ImportsApi api)
    {
        _api = api;
        _ = RefreshRecentAsync();
    }

    public ObservableCollection<ImportBatchDto> Recent { get; } = [];

    [ObservableProperty]
    private ImportBatchDto? current;

    [ObservableProperty]
    private bool isRunning;

    [ObservableProperty]
    private bool isUploading;

    /// <summary>Upload progress fraction (0–1) while the file streams up.</summary>
    [ObservableProperty]
    private double uploadProgress;

    public bool CurrentHasErrors => Current is { HasErrorReport: true };
    public bool CurrentFailed => Current?.Status == "Failed";
    public bool CurrentCompleted => Current?.Status == "Completed";

    /// <summary>How long the server has been working, or took. Shown because a
    /// long import with no elapsed time reads as a frozen screen.</summary>
    public string? DurationText
    {
        get
        {
            if (Current is not { } batch)
            {
                return null;
            }
            var start = batch.StartedAtUtc ?? batch.UploadedAtUtc;
            var end = batch.FinishedAtUtc ?? DateTime.UtcNow;
            var elapsed = end - start;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            var text = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes} min {elapsed.Seconds:00} s"
                : $"{elapsed.TotalSeconds:0.0} s";
            return batch.FinishedAtUtc is null ? $"Elapsed {text}" : $"Took {text}";
        }
    }

    /// <summary>Rows processed as a 0–1 fraction; null until the server knows
    /// the total, which is what switches the bar from indeterminate.</summary>
    public double? RowProgress =>
        Current is { TotalRows: > 0 } batch
            ? Math.Clamp((double)batch.ProcessedRows / batch.TotalRows, 0, 1)
            : null;

    public bool HasRowProgress => RowProgress is not null;

    public string? RowProgressText =>
        Current is { TotalRows: > 0 } batch
            ? $"{batch.ProcessedRows:N0} / {batch.TotalRows:N0} rows"
            : Current is { ProcessedRows: > 0 } running
                ? $"{running.ProcessedRows:N0} rows read"
                : null;

    /// <summary>Step 2 card: visible while the file uploads or the server
    /// validates/processes it.</summary>
    public bool ShowProgress => IsUploading || IsRunning;

    /// <summary>Step 3 card: visible once the batch reached a terminal state.</summary>
    public bool ShowResult => !IsUploading && !IsRunning && Current is not null;

    partial void OnCurrentChanged(ImportBatchDto? value)
    {
        OnPropertyChanged(nameof(CurrentHasErrors));
        OnPropertyChanged(nameof(CurrentFailed));
        OnPropertyChanged(nameof(CurrentCompleted));
        OnPropertyChanged(nameof(ShowResult));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(RowProgress));
        OnPropertyChanged(nameof(HasRowProgress));
        OnPropertyChanged(nameof(RowProgressText));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowResult));
    }

    partial void OnIsUploadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowProgress));
        OnPropertyChanged(nameof(ShowResult));
    }

    // ---- Commands ------------------------------------------------------------

    [RelayCommand]
    private async Task DownloadTemplateAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "product-import-template.xlsx",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var bytes = await _api.DownloadTemplateAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(dialog.FileName, bytes!);
            ToastService.Instance.Success("Template saved.");
        });
    }

    [RelayCommand]
    private async Task ExportProductsAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"products-{DateTime.Now:yyyyMMdd}.xlsx",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var bytes = await _api.ExportProductsAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(dialog.FileName, bytes!);
            ToastService.Instance.Success("Product export saved.");
        });
    }

    [RelayCommand]
    private async Task UploadAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            Title = "Choose the file to import",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await UploadFileAsync(dialog.FileName);
    }

    /// <summary>Path-based entry so drag-and-drop shares the exact same
    /// upload path as the file picker.</summary>
    [RelayCommand]
    private async Task UploadFileAsync(string path)
    {
        if (IsRunning || IsUploading)
        {
            return;
        }
        if (!path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            ToastService.Instance.Warning("Only .xlsx files can be imported.");
            return;
        }

        await GuardAsync(async () =>
        {
            IsRunning = true;
            IsUploading = true;
            UploadProgress = 0;
            _terminalNotified = false;
            long batchId;
            try
            {
                var progress = new Progress<double>(p => UploadProgress = p);
                batchId = await _api.UploadAsync(path, progress, CancellationToken.None);
            }
            finally
            {
                IsUploading = false;
            }
            Current = await _api.GetAsync(batchId, CancellationToken.None);
            await WatchAsync(batchId);
        });
    }

    [RelayCommand]
    private async Task CancelCurrentAsync()
    {
        if (Current is null)
        {
            return;
        }
        await GuardAsync(() => _api.CancelAsync(Current.Id, CancellationToken.None));
    }

    [RelayCommand]
    private async Task DownloadErrorsAsync()
    {
        if (Current is null)
        {
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"import-{Current.Id}-errors.xlsx",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var bytes = await _api.DownloadErrorsAsync(Current.Id, CancellationToken.None);
            await File.WriteAllBytesAsync(dialog.FileName, bytes!);
            ToastService.Instance.Success("Error report saved. Fix the rows and upload the same file again.");
        });
    }

    // ---- Live progress ---------------------------------------------------------

    private async Task WatchAsync(long batchId)
    {
        // Polling is a SAFETY NET here, not merely a fallback for a hub that
        // fails to connect. Observed in the field: the negotiate succeeded, the
        // websocket upgrade never completed, and the await below simply never
        // returned — so no push arrived AND the old failure-only fallback never
        // armed. The screen sat on "Uploaded, 0 rows" while the server had
        // already finished the import. A poll that always runs makes the screen
        // truthful no matter how the live channel misbehaves; it stops the
        // moment the batch reaches a terminal state.
        StartPolling(batchId);

        try
        {
            // Bounded: a handshake that hangs must not hold this task forever.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            _hub = await _api.SubscribeAsync(batchId, OnBatchChanged, timeout.Token);
        }
        catch (Exception)
        {
            // Live push unavailable — the poll above already keeps the UI honest.
        }
    }

    private void StartPolling(long batchId)
    {
        _pollTimer?.Dispose();
        _pollTimer = new System.Timers.Timer(1_500);
        _pollTimer.Elapsed += async (_, _) =>
        {
            try
            {
                OnBatchChanged(await _api.GetAsync(batchId, CancellationToken.None));
            }
            catch (Exception)
            {
                // Transient — next tick retries.
            }
        };
        _pollTimer.Start();
    }

    private void OnBatchChanged(ImportBatchDto dto)
    {
        // SignalR callbacks arrive on background threads; collection and
        // lifecycle changes must land on the dispatcher.
        Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            Current = dto;
            if (dto.Status is "Completed" or "Failed" or "Cancelled")
            {
                IsRunning = false;
                NotifyTerminal(dto);
                await StopWatchingAsync();
                await RefreshRecentAsync();
            }
        });
    }

    /// <summary>Terminal-state toast, raised once even when SignalR and the
    /// polling fallback both report the same final snapshot.</summary>
    private void NotifyTerminal(ImportBatchDto dto)
    {
        if (_terminalNotified)
        {
            return;
        }
        _terminalNotified = true;
        switch (dto.Status)
        {
            case "Completed":
                ToastService.Instance.Success(
                    $"Import completed — {dto.InsertedRows:N0} inserted, {dto.UpdatedRows:N0} updated.");
                break;
            case "Cancelled":
                ToastService.Instance.Info("Import cancelled.");
                break;
                // Failed: the result card carries the reason and the error
                // report download — a toast would just repeat it.
        }
    }

    private async Task StopWatchingAsync()
    {
        _pollTimer?.Stop();
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
    }

    private async Task RefreshRecentAsync()
    {
        try
        {
            var recent = await _api.RecentAsync(CancellationToken.None);
            Recent.Clear();
            foreach (var batch in recent)
            {
                Recent.Add(batch);
            }
        }
        catch (Exception)
        {
            // The recent list is decorative; the upload flow reports its own errors.
        }
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (ApiException ex)
        {
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
            IsRunning = false;
        }
        catch (ApiUnreachableException)
        {
            ToastService.Instance.Error("Cannot reach the server. Check your network connection.");
            IsRunning = false;
        }
    }
}
