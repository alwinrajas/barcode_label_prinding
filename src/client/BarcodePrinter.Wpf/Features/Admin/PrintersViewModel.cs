using System.Collections.ObjectModel;
using System.Windows;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

public sealed partial class PrintersViewModel : ObservableObject
{
    private readonly PrintApi _api;

    public PrintersViewModel(PrintApi api, Session session)
    {
        _api = api;
        CanManage = session.Has(PermissionCodes.SettingsManagePrinters);
        _ = RefreshAsync();
    }

    public ObservableCollection<PrinterDto> Printers { get; } = [];
    public bool CanManage { get; }

    /// <summary>Connection type drives where the job can be dispatched from —
    /// a Windows queue only exists on the PC it is installed on (§7.3).</summary>
    public IReadOnlyList<string> ConnectionTypes { get; } =
        ["NetworkTcp", "WindowsRaw", "WindowsGraphics", "File"];
    public IReadOnlyList<string> DispatchModes { get; } = ["Server", "Client"];
    public IReadOnlyList<string> Languages { get; } = ["Zpl", "Windows"];

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private PrinterDto? selectedPrinter;

    [ObservableProperty] private bool isEditorOpen;
    [ObservableProperty] private bool isNew;
    [ObservableProperty] private long editingId;
    [ObservableProperty] private string editorTitle = "";
    [ObservableProperty] private string editCode = "";
    [ObservableProperty] private string editName = "";
    [ObservableProperty] private string? editLocation;
    [ObservableProperty] private string editConnectionType = "NetworkTcp";
    [ObservableProperty] private string editDispatchMode = "Server";
    [ObservableProperty] private string? editHost;
    [ObservableProperty] private string? editPort = "9100";
    [ObservableProperty] private string? editWindowsPrinterName;
    [ObservableProperty] private string? editOwnerWorkstation;
    [ObservableProperty] private string? editDpi = "203";
    [ObservableProperty] private string editLanguage = "Zpl";
    [ObservableProperty] private bool editSupportsStatusQuery;
    [ObservableProperty] private bool editIsActive = true;

    public bool ShowNetworkFields => EditConnectionType == "NetworkTcp";
    public bool ShowWindowsFields => EditConnectionType is "WindowsRaw" or "WindowsGraphics";
    public bool ShowWorkstation => EditDispatchMode == "Client";

    partial void OnEditConnectionTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowNetworkFields));
        OnPropertyChanged(nameof(ShowWindowsFields));
        // Windows queues can only be reached from their own PC — steer the
        // admin to the only valid combination instead of failing on save.
        if (ShowWindowsFields)
        {
            EditDispatchMode = "Client";
            EditOwnerWorkstation ??= Environment.MachineName;
        }
    }

    partial void OnEditDispatchModeChanged(string value) => OnPropertyChanged(nameof(ShowWorkstation));

    async partial void OnSelectedPrinterChanged(PrinterDto? value)
    {
        if (value is not null)
        {
            await OpenAsync(value);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await GuardAsync(async () =>
        {
            var printers = await _api.ListPrintersAsync(activeOnly: false, CancellationToken.None);
            Printers.Clear();
            foreach (var printer in printers)
            {
                Printers.Add(printer);
            }
        });
    }

    [RelayCommand]
    private void NewPrinter()
    {
        SelectedPrinter = null;
        IsNew = true;
        IsEditorOpen = true;
        EditorTitle = "New printer";
        EditingId = 0;
        EditCode = "";
        EditName = "";
        EditLocation = null;
        EditConnectionType = "NetworkTcp";
        EditDispatchMode = "Server";
        EditHost = null;
        EditPort = "9100";
        EditWindowsPrinterName = null;
        EditOwnerWorkstation = null;
        EditDpi = "203";
        EditLanguage = "Zpl";
        EditSupportsStatusQuery = false;
        EditIsActive = true;
        ErrorMessage = null;
        StatusMessage = null;
    }

    private Task OpenAsync(PrinterDto printer)
    {
        IsNew = false;
        IsEditorOpen = true;
        EditorTitle = $"Edit {printer.Name}";
        EditingId = printer.Id;
        EditCode = printer.Code;
        EditName = printer.Name;
        EditLocation = printer.Location;
        EditConnectionType = printer.ConnectionType;
        EditDispatchMode = printer.DispatchMode;
        EditHost = printer.Host;
        EditPort = printer.Port?.ToString();
        EditWindowsPrinterName = printer.WindowsPrinterName;
        EditOwnerWorkstation = printer.OwnerWorkstation;
        EditDpi = printer.Dpi?.ToString();
        EditLanguage = printer.Language;
        EditSupportsStatusQuery = printer.SupportsStatusQuery;
        EditIsActive = printer.IsActive;
        ErrorMessage = null;
        StatusMessage = null;
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var request = new SavePrinterRequest(
            EditCode.Trim(), EditName.Trim(), EditLocation,
            EditConnectionType, EditDispatchMode, EditHost,
            int.TryParse(EditPort, out var port) ? port : null,
            EditWindowsPrinterName, EditOwnerWorkstation,
            short.TryParse(EditDpi, out var dpi) ? dpi : null,
            EditLanguage, EditSupportsStatusQuery, EditIsActive);

        await GuardAsync(async () =>
        {
            if (IsNew)
            {
                await _api.CreatePrinterAsync(request, CancellationToken.None);
            }
            else
            {
                await _api.UpdatePrinterAsync(EditingId, request, CancellationToken.None);
            }
            StatusMessage = "Saved.";
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task SetDefaultAsync()
    {
        if (EditingId == 0)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            await _api.SetDefaultPrinterAsync(EditingId, CancellationToken.None);
            StatusMessage = "This is now the default printer.";
            await RefreshAsync();
        });
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (EditingId == 0)
        {
            return;
        }
        await GuardAsync(async () =>
        {
            var result = await _api.TestPrinterAsync(EditingId, CancellationToken.None);
            if (result.Success)
            {
                StatusMessage = result.Message;
                ErrorMessage = null;
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = null;
            }
        });
    }

    private async Task GuardAsync(Func<Task> action)
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            await action();
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
}
