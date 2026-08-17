using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Printing.Abstractions;
using BarcodePrinter.Printing.Client;
using BarcodePrinter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

/// <summary>
/// One grid row: the stored printer plus its live reachability, which is polled
/// separately and must not force the whole list to reload.
/// </summary>
public sealed partial class PrinterRow : ObservableObject
{
    private DateTime? _lastSeenUtc;

    public PrinterRow(PrinterDto printer)
    {
        Printer = printer;
        _lastSeenUtc = printer.LastSeenUtc;
        lastSeenText = Relative(_lastSeenUtc);
        if (!printer.IsActive)
        {
            statusText = "Disabled";
        }
    }

    public PrinterDto Printer { get; }

    public long Id => Printer.Id;
    public string Code => Printer.Code;
    public string Name => Printer.Name;
    public bool IsActive => Printer.IsActive;
    public bool IsDefault => Printer.IsDefault;

    /// <summary>Connection type in the operator's words, not the enum's.</summary>
    public string ConnectionLabel => Printer.ConnectionType switch
    {
        "NetworkTcp" => "Network",
        "WindowsRaw" => "Windows RAW",
        "WindowsGraphics" => "Windows",
        "File" => "File",
        _ => Printer.ConnectionType,
    };

    /// <summary>Where the bytes go: an address for network printers, the Windows
    /// queue name for spooler printers.</summary>
    public string AddressLabel => Printer.ConnectionType == "NetworkTcp"
        ? string.IsNullOrWhiteSpace(Printer.Host)
            ? "—"
            : Printer.Port is null ? Printer.Host : $"{Printer.Host}:{Printer.Port}"
        : string.IsNullOrWhiteSpace(Printer.WindowsPrinterName) ? "—" : Printer.WindowsPrinterName;

    /// <summary>Which machine actually sends the job — the answer to "why is
    /// nothing printing".</summary>
    public string DispatchLabel => Printer.DispatchMode == "Client"
        ? string.IsNullOrWhiteSpace(Printer.OwnerWorkstation) ? "Workstation (unset)" : Printer.OwnerWorkstation
        : "Server";

    public string DefaultGlyph => IsDefault ? "★" : "";

    public string DefaultAutomationName => IsDefault
        ? $"{Name} is the default printer"
        : $"{Name} is not the default printer";

    /// <summary>StatusPill family key; empty renders the neutral pill.</summary>
    [ObservableProperty] private string statusKind = "";
    [ObservableProperty] private string statusText = "Unknown";
    [ObservableProperty] private string? statusDetail;
    [ObservableProperty] private string lastSeenText = "—";

    public void ApplyStatus(PrinterStatusDto status)
    {
        if (!IsActive)
        {
            MarkDisabled();
            return;
        }
        StatusKind = status.Online ? "Completed" : "Failed";
        StatusText = status.Online ? "Online" : "Offline";
        StatusDetail = status.Detail;
        if (status.LastSeenUtc is not null)
        {
            _lastSeenUtc = status.LastSeenUtc;
        }
        RefreshRelativeTime();
    }

    /// <summary>A status probe that failed tells us nothing about the printer —
    /// say "Unknown" rather than inventing "Offline".</summary>
    public void MarkStatusUnknown()
    {
        if (!IsActive)
        {
            MarkDisabled();
            return;
        }
        StatusKind = "";
        StatusText = "Unknown";
        StatusDetail = "The server could not be asked about this printer just now.";
    }

    private void MarkDisabled()
    {
        StatusKind = "";
        StatusText = "Disabled";
        StatusDetail = "This printer is switched off in its settings and accepts no jobs.";
    }

    public void RefreshRelativeTime() => LastSeenText = Relative(_lastSeenUtc);

    private static string Relative(DateTime? utc)
    {
        if (utc is null)
        {
            return "—";
        }
        var elapsed = DateTime.UtcNow - utc.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }
        return elapsed.TotalSeconds switch
        {
            < 60 => "just now",
            < 3600 => $"{(int)elapsed.TotalMinutes} min ago",
            < 86400 => $"{(int)elapsed.TotalHours} h ago",
            < 2592000 => $"{(int)elapsed.TotalDays} d ago",
            _ => utc.Value.ToLocalTime().ToString("dd/MM/yyyy"),
        };
    }
}

public sealed partial class PrintersViewModel : ObservableObject
{
    /// <summary>Edit-form properties that flip the drawer's dirty flag.</summary>
    private static readonly HashSet<string> DirtyProps =
    [
        nameof(EditCode), nameof(EditName), nameof(EditLocation), nameof(EditConnectionType),
        nameof(EditDispatchMode), nameof(EditHost), nameof(EditPort), nameof(EditWindowsPrinterName),
        nameof(EditOwnerWorkstation), nameof(EditDpi), nameof(EditLanguage),
        nameof(EditSupportsStatusQuery), nameof(EditIsActive),
    ];

    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromSeconds(30);

    private readonly PrintApi _api;
    private readonly DispatcherTimer? _statusTimer;
    private bool _suppressDirty;
    private bool _pollingStatuses;

    public PrintersViewModel(PrintApi api, Session session)
    {
        _api = api;
        CanManage = session.Has(PermissionCodes.SettingsManagePrinters);
        InstalledPrinters = LoadInstalledPrinters();
        _ = RefreshAsync();

        // Live status ages fast: a printer switched off a minute ago must not
        // still read "Online". Polling is silent — it never raises the busy
        // overlay and never surfaces a toast when it fails.
        if (Application.Current is not null)
        {
            _statusTimer = new DispatcherTimer { Interval = StatusPollInterval };
            _statusTimer.Tick += (_, _) => _ = RefreshStatusesAsync();
            _statusTimer.Start();
        }
    }

    public ObservableCollection<PrinterDto> Printers { get; } = [];
    public ObservableCollection<PrinterRow> Rows { get; } = [];
    public bool CanManage { get; }

    /// <summary>Windows queues installed on THIS PC. A Windows printer only
    /// exists on the machine it is installed on, so this list is a helper for
    /// the common case, never a constraint — the field stays editable.</summary>
    public IReadOnlyList<string> InstalledPrinters { get; }

    public string MachineName { get; } = Environment.MachineName;

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
    [ObservableProperty] private PrinterRow? selectedRow;

    // Screen states
    [ObservableProperty] private bool isEmpty;
    [ObservableProperty] private bool loadFailed;
    [ObservableProperty] private string? loadErrorMessage;
    [ObservableProperty] private string? loadErrorReference;

    [ObservableProperty] private bool isEditorOpen;
    [ObservableProperty] private bool isNew;
    [ObservableProperty] private bool isDirty;
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

    /// <summary>Only an existing, saved printer can be tested or made default.</summary>
    public bool CanUseDeviceActions => EditingId != 0;

    /// <summary>Shown when the queue list on this PC cannot possibly describe
    /// the machine that will do the printing.</summary>
    public string? WindowsPrinterHint =>
        ShowWindowsFields
        && !string.IsNullOrWhiteSpace(EditOwnerWorkstation)
        && !string.Equals(EditOwnerWorkstation, MachineName, StringComparison.OrdinalIgnoreCase)
            ? $"This list shows printers installed on THIS PC ({MachineName}). "
              + $"The printer must exist on '{EditOwnerWorkstation}'."
            : null;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_suppressDirty && IsEditorOpen && e.PropertyName is not null && DirtyProps.Contains(e.PropertyName))
        {
            IsDirty = true;
        }
    }

    partial void OnEditingIdChanged(long value) => OnPropertyChanged(nameof(CanUseDeviceActions));

    partial void OnEditConnectionTypeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowNetworkFields));
        OnPropertyChanged(nameof(ShowWindowsFields));
        // Windows queues can only be reached from their own PC — steer the
        // admin to the only valid combination instead of failing on save.
        if (ShowWindowsFields)
        {
            EditDispatchMode = "Client";
            EditOwnerWorkstation ??= MachineName;
        }
        OnPropertyChanged(nameof(WindowsPrinterHint));
    }

    partial void OnEditDispatchModeChanged(string value)
    {
        OnPropertyChanged(nameof(ShowWorkstation));
        OnPropertyChanged(nameof(WindowsPrinterHint));
    }

    partial void OnEditOwnerWorkstationChanged(string? value) =>
        OnPropertyChanged(nameof(WindowsPrinterHint));

    async partial void OnSelectedPrinterChanged(PrinterDto? value)
    {
        if (value is not null)
        {
            await OpenAsync(value);
        }
    }

    partial void OnSelectedRowChanged(PrinterRow? value) => SelectedPrinter = value?.Printer;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await GuardAsync(async () =>
        {
            var printers = await _api.ListPrintersAsync(activeOnly: false, CancellationToken.None);
            Printers.Clear();
            Rows.Clear();
            foreach (var printer in printers)
            {
                Printers.Add(printer);
                Rows.Add(new PrinterRow(printer));
            }
            IsEmpty = Rows.Count == 0;
        }, isLoad: true);

        await RefreshStatusesAsync();
    }

    [RelayCommand]
    private Task RetryLoadAsync() => RefreshAsync();

    /// <summary>Polls live reachability for the listed printers. Failures are
    /// absorbed into "Unknown": a status probe is a courtesy, not a reason to
    /// break the screen.</summary>
    private async Task RefreshStatusesAsync()
    {
        // A slow round of probes must not stack up behind the next tick.
        if (_pollingStatuses)
        {
            return;
        }
        _pollingStatuses = true;
        try
        {
            foreach (var row in Rows.ToList())
            {
                row.RefreshRelativeTime();
                if (!row.IsActive)
                {
                    continue;
                }
                try
                {
                    var status = await _api.GetPrinterStatusAsync(row.Id, CancellationToken.None);
                    row.ApplyStatus(status);
                }
                catch (ApiException)
                {
                    row.MarkStatusUnknown();
                }
                catch (ApiUnreachableException)
                {
                    row.MarkStatusUnknown();
                }
            }
        }
        finally
        {
            _pollingStatuses = false;
        }
    }

    [RelayCommand]
    private void NewPrinter()
    {
        _suppressDirty = true;
        try
        {
            SelectedRow = null;
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
        finally
        {
            _suppressDirty = false;
        }
        IsDirty = false;
    }

    private Task OpenAsync(PrinterDto printer)
    {
        _suppressDirty = true;
        try
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
        }
        finally
        {
            _suppressDirty = false;
        }
        IsDirty = false;
        return Task.CompletedTask;
    }

    /// <summary>Drawer close (X / Cancel / Escape) with a discard confirm when
    /// there are unsaved edits.</summary>
    [RelayCommand]
    private async Task CloseEditorAsync()
    {
        if (!IsEditorOpen)
        {
            return;
        }
        if (IsDirty && !await DialogService.ConfirmAsync(
                "Discard changes?", "You have unsaved changes. Close the editor without saving?",
                "Discard", danger: true))
        {
            return;
        }
        IsEditorOpen = false;
        IsDirty = false;
        SelectedRow = null;
        SelectedPrinter = null;
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
                ToastService.Instance.Success("Printer added.");
            }
            else
            {
                await _api.UpdatePrinterAsync(EditingId, request, CancellationToken.None);
                ToastService.Instance.Success("Printer saved.");
            }
            StatusMessage = "Saved.";
            IsDirty = false;
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
        if (!await DialogService.ConfirmAsync("Set default printer",
                $"Make {EditName} the default printer for everyone who has not chosen one?",
                "Set default"))
        {
            return;
        }

        await GuardAsync(async () =>
        {
            await _api.SetDefaultPrinterAsync(EditingId, CancellationToken.None);
            StatusMessage = "This is now the default printer.";
            ToastService.Instance.Success($"{EditName} is now the default printer.");
            await RefreshAsync();
        });
    }

    /// <summary>
    /// Test print. The server can only test printers it dispatches itself; a
    /// client-dispatched Windows queue exists on one PC and nowhere else. So we
    /// print locally when this IS that PC, and say plainly which PC to use when
    /// it is not — rather than asking the server to do something it cannot.
    /// </summary>
    [RelayCommand]
    private async Task TestAsync()
    {
        if (EditingId == 0)
        {
            return;
        }

        if (EditDispatchMode == "Client" && ShowWindowsFields)
        {
            var owner = EditOwnerWorkstation;
            if (!string.IsNullOrWhiteSpace(owner)
                && !string.Equals(owner, MachineName, StringComparison.OrdinalIgnoreCase))
            {
                ToastService.Instance.Warning(
                    $"Run the test from workstation '{owner}' — this printer prints from that PC.");
                return;
            }
            await TestLocallyAsync();
            return;
        }

        await GuardAsync(async () =>
        {
            var result = await _api.TestPrinterAsync(EditingId, CancellationToken.None);
            if (result.Success)
            {
                StatusMessage = result.Message;
                ErrorMessage = null;
                ToastService.Instance.Success(result.Message);
            }
            else
            {
                ErrorMessage = result.Message;
                StatusMessage = null;
                ToastService.Instance.Error(result.Message);
            }
        });
    }

    /// <summary>Sends a test label straight to the Windows spooler on this PC.</summary>
    private async Task TestLocallyAsync()
    {
        if (string.IsNullOrWhiteSpace(EditWindowsPrinterName))
        {
            const string missing = "Set the Windows printer name before testing.";
            ErrorMessage = missing;
            ToastService.Instance.Error(missing);
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var isRaw = EditConnectionType == "WindowsRaw";

            // The picture must be produced on the UI thread; the spooler call
            // inside the transport already moves itself off it.
            var data = isRaw
                ? BuildZplTestLabel(EditName)
                : RasterLabelPayload.Pack([RenderTestLabelPng(EditName, EditWindowsPrinterName!)]);

            IPrintTransport transport = isRaw
                ? new WindowsRawTransport()
                : new WindowsGraphicsTransport();

            var target = new PrinterTarget(
                EditingId, EditName,
                isRaw ? PrinterConnectionKind.WindowsRaw : PrinterConnectionKind.WindowsGraphics,
                EditHost, int.TryParse(EditPort, out var port) ? port : null,
                EditWindowsPrinterName, EditSupportsStatusQuery);

            var payload = new PrintPayload($"TEST-{DateTime.Now:yyyyMMdd-HHmmss}", data);
            var outcome = await transport.SendAsync(target, payload, CancellationToken.None);

            if (outcome.Kind == PrintOutcomeKind.Failed)
            {
                var message = outcome.ErrorMessage ?? "The test label could not be printed.";
                ErrorMessage = message;
                StatusMessage = null;
                ToastService.Instance.Error(message, outcome.ErrorCode);
            }
            else
            {
                const string message = "Test label sent to the Windows spooler on this PC.";
                StatusMessage = message;
                ErrorMessage = null;
                ToastService.Instance.Success(message);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            ToastService.Instance.Error($"The test label could not be printed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A minimal, printer-agnostic ZPL label: text the operator can
    /// read plus a barcode they can scan to prove the head works.</summary>
    private static byte[] BuildZplTestLabel(string printerName)
    {
        var stamp = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        var zpl = new StringBuilder()
            .Append("^XA^CI28")
            .Append("^FO30,30^A0N,40,40^FDTEST LABEL^FS")
            .Append($"^FO30,85^A0N,26,26^FD{Sanitise(printerName)}^FS")
            .Append($"^FO30,120^A0N,26,26^FD{stamp}^FS")
            .Append("^FO30,165^BY2^BCN,90,Y,N,N^FDTESTLABEL^FS")
            .Append("^XZ")
            .ToString();
        return Encoding.ASCII.GetBytes(zpl);
    }

    /// <summary>ZPL control characters would be read as commands.</summary>
    private static string Sanitise(string value) =>
        new string(value.Where(c => c is not ('^' or '~') && !char.IsControl(c)).ToArray());

    /// <summary>Renders the test label as a PNG, which is what
    /// WindowsGraphicsTransport expects inside a RasterLabelPayload.</summary>
    private static byte[] RenderTestLabelPng(string printerName, string windowsPrinterName)
    {
        const double width = 600;
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = "TEST LABEL",
            FontSize = 34,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.Black,
        });
        body.Children.Add(new TextBlock
        {
            Text = printerName,
            FontSize = 20,
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
        });
        body.Children.Add(new TextBlock
        {
            Text = windowsPrinterName,
            FontSize = 16,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
        });
        body.Children.Add(new TextBlock
        {
            Text = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss"),
            FontSize = 16,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = Brushes.Black,
        });
        body.Children.Add(new TextBlock
        {
            Text = "If you can read this, the printer is reachable from this PC.",
            FontSize = 14,
            Margin = new Thickness(0, 14, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Black,
        });

        var host = new Border
        {
            Width = width,
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(2),
            Padding = new Thickness(24),
            Child = body,
        };

        host.Measure(new Size(width, double.PositiveInfinity));
        host.Arrange(new Rect(new Point(0, 0), host.DesiredSize));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.DesiredSize.Width),
            (int)Math.Ceiling(host.DesiredSize.Height),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Enumerating queues can fail on a machine with a broken spooler;
    /// an empty list simply means the admin types the name instead.</summary>
    private static IReadOnlyList<string> LoadInstalledPrinters()
    {
        try
        {
            return [.. System.Drawing.Printing.PrinterSettings.InstalledPrinters
                .Cast<string>()
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception)
        {
            return [];
        }
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
    }
}
