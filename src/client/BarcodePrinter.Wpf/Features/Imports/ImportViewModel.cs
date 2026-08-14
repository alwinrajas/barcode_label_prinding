using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts.Imports;
using BarcodePrinter.Wpf.Features.Login;
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
    private string? statusMessage;

    public bool CurrentHasErrors => Current is { HasErrorReport: true };
    public bool CurrentFailed => Current?.Status == "Failed";
    public bool CurrentCompleted => Current?.Status == "Completed";

    partial void OnCurrentChanged(ImportBatchDto? value)
    {
        OnPropertyChanged(nameof(CurrentHasErrors));
        OnPropertyChanged(nameof(CurrentFailed));
        OnPropertyChanged(nameof(CurrentCompleted));
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
            StatusMessage = "Template saved.";
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
            StatusMessage = "Exporting…";
            var bytes = await _api.ExportProductsAsync(CancellationToken.None);
            await File.WriteAllBytesAsync(dialog.FileName, bytes!);
            StatusMessage = "Export saved.";
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

        await GuardAsync(async () =>
        {
            IsRunning = true;
            StatusMessage = null;
            var batchId = await _api.UploadAsync(dialog.FileName, CancellationToken.None);
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
            StatusMessage = "Error report saved. Fix the rows and upload the same file again.";
        });
    }

    // ---- Live progress ---------------------------------------------------------

    private async Task WatchAsync(long batchId)
    {
        try
        {
            _hub = await _api.SubscribeAsync(batchId, OnBatchChanged, CancellationToken.None);
        }
        catch (Exception)
        {
            // SignalR unavailable → polling fallback (B-16).
            _pollTimer = new System.Timers.Timer(500);
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
                await StopWatchingAsync();
                await RefreshRecentAsync();
            }
        });
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
            StatusMessage = ex.Message;
            IsRunning = false;
        }
        catch (ApiUnreachableException)
        {
            StatusMessage = "Cannot reach the server. Check your network connection.";
            IsRunning = false;
        }
    }
}
