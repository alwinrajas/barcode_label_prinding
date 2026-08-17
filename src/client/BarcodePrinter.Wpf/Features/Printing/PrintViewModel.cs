using System.Collections.ObjectModel;
using System.IO;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Wpf.Features.Login;
using BarcodePrinter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Printing;

/// <summary>
/// The operational screen (blueprint §12.4). One page, keyboard-first:
/// search → confirm run values → preview → print. Everything is async; the UI
/// never blocks on the server or the printer.
///
/// Operators do not pick templates (§15): the request is submitted with
/// TemplateId = null and the server resolves product default → printer
/// default → global default.
/// </summary>
public sealed partial class PrintViewModel : ObservableObject, IDisposable
{
    private readonly PrintApi _api;
    private readonly ProductsApi _products;
    private CancellationTokenSource _searchCts = new();
    private CancellationTokenSource _previewCts = new();
    private Microsoft.AspNetCore.SignalR.Client.HubConnection? _statusHub;

    /// <summary>Re-checks the selected printer's reachability every 20s so
    /// "Online" next to the combo is a live fact, not a login-time one.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _printerStatusTimer;

    /// <summary>Monotonic guard: a slow status probe for a printer the operator
    /// has already switched away from must not overwrite the newer answer.</summary>
    private int _printerStatusGeneration;

    /// <summary>True once the operator has typed an expiry date themselves.
    /// While false, expiry is derived (production + 1 year) whenever the
    /// production date changes; the first manual edit takes ownership and the
    /// derivation stops. Reset on every product change.</summary>
    private bool _expiryManuallyEdited;

    /// <summary>Set while the VM itself writes the date fields (product
    /// defaults, derivation) so those writes are not mistaken for operator
    /// edits.</summary>
    private bool _applyingDates;

    public PrintViewModel(PrintApi api, ProductsApi products, Session session)
    {
        _api = api;
        _products = products;
        CanPrint = session.Has(PermissionCodes.PrintExecute);

        _printerStatusTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(20),
        };
        _printerStatusTimer.Tick += (_, _) => _ = CheckPrinterStatusAsync();
        _printerStatusTimer.Start();

        _ = InitializeAsync();
        _ = SubscribeToJobStatusAsync();
    }

    public ObservableCollection<ProductSummary> SearchResults { get; } = [];
    public ObservableCollection<PrinterDto> Printers { get; } = [];
    public ObservableCollection<PrintJobDto> RecentJobs { get; } = [];

    public bool CanPrint { get; }

    [ObservableProperty] private string searchText = "";
    [ObservableProperty] private bool isSearching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private ProductSummary? selectedProduct;

    [ObservableProperty] private ProductDetail? productDetail;

    // Section 2 — this print run (A-9: master defaults, overridable here)
    [ObservableProperty] private string? batch;
    [ObservableProperty] private DateTime? productionDate;
    [ObservableProperty] private DateTime? expiryDate;
    [ObservableProperty] private string? quantityText;
    [ObservableProperty] private string cartonFrom = "1";
    [ObservableProperty] private string cartonTo = "1";
    [ObservableProperty] private string copies = "1";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private PrinterDto? selectedPrinter;

    /// <summary>"Online" / "Offline" / "Unknown" / "None" — drives the colour
    /// of the status caption next to the printer combo.</summary>
    [ObservableProperty] private string printerStatusKind = "None";
    [ObservableProperty] private string? printerStatusText;

    [ObservableProperty] private bool isPreviewLoading;

    /// <summary>Non-fatal server note about the rendered label (e.g. the
    /// feedback QR is blank). Shown as an amber banner over the preview.</summary>
    [ObservableProperty] private string? previewWarning;

    /// <summary>The rendered label. This is what an operator actually checks —
    /// a wrong batch or a truncated description is visible here and invisible
    /// in a dump of printer commands.</summary>
    [ObservableProperty] private System.Windows.Media.Imaging.BitmapImage? previewImage;

    /// <summary>Set when the label cannot be drawn (a client-supplied printer
    /// file, or missing required data), explaining why rather than showing an
    /// empty box.</summary>
    [ObservableProperty] private string? previewUnavailable;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? successMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private bool isPrinting;

    // Initialization honesty: if printers cannot be loaded the screen says so
    // and offers Retry, instead of a permanently dead Print button.
    [ObservableProperty] private bool hasInitError;
    [ObservableProperty] private string? initErrorMessage;

    /// <summary>Restated in words next to the inputs AND on the button: a
    /// mis-keyed carton range wastes media and mislabels cartons.</summary>
    public string RunSummary
    {
        get
        {
            if (!TryParseRange(out var from, out var to, out _))
            {
                return "Enter a valid carton range.";
            }
            var count = to - from + 1;
            var copiesEach = int.TryParse(Copies, out var c) && c > 1 ? $" × {c} copies" : "";
            return $"{count} label{(count == 1 ? "" : "s")}, cartons {from}–{to}{copiesEach}";
        }
    }

    /// <summary>The Print button restates the commitment when it is ready.</summary>
    public string PrintButtonText
    {
        get
        {
            if (SelectedProduct is not null && SelectedPrinter is not null &&
                TryParseRange(out var from, out var to, out _))
            {
                var count = to - from + 1;
                return $"Print {count} label{(count == 1 ? "" : "s")}";
            }
            return "Print";
        }
    }

    /// <summary>Why the Print button is disabled, in words — surfaced as its
    /// tooltip so a dead button is never a mystery. Null when printing is
    /// possible.</summary>
    public string? PrintDisabledReason
    {
        get
        {
            if (!CanPrint)
            {
                return "You do not have permission to print.";
            }
            if (SelectedProduct is null)
            {
                return "Select a product first.";
            }
            if (SelectedPrinter is null)
            {
                return "No printer selected.";
            }
            if (!TryParseRange(out _, out _, out var rangeError))
            {
                return rangeError;
            }
            return null;
        }
    }

    public string? OverrideNotice
    {
        get
        {
            if (ProductDetail is null)
            {
                return null;
            }
            var changes = 0;
            if (!string.Equals(Batch, ProductDetail.DefaultBatch, StringComparison.Ordinal)) changes++;
            if (AsDateOnly(ProductionDate) != ProductDetail.DefaultProductionDate) changes++;
            if (AsDateOnly(ExpiryDate) != ProductDetail.DefaultExpiryDate) changes++;
            if (!string.Equals(QuantityText, ProductDetail.DefaultQuantityText, StringComparison.Ordinal)) changes++;
            return changes == 0 ? null : $"ⓘ {changes} value(s) differ from the product master.";
        }
    }

    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync();

    async partial void OnSelectedProductChanged(ProductSummary? value)
    {
        NotifyPrintGateChanged();
        if (value is not null)
        {
            await LoadProductAsync(value.Id);
        }
    }

    partial void OnBatchChanged(string? value) => AfterRunValueChanged();

    partial void OnProductionDateChanged(DateTime? value)
    {
        // Derive expiry from production only while the operator has not taken
        // ownership of the expiry field; a manual expiry edit stops this.
        if (!_applyingDates && !_expiryManuallyEdited)
        {
            _applyingDates = true;
            ExpiryDate = value?.AddYears(1);
            _applyingDates = false;
        }
        AfterRunValueChanged();
    }

    partial void OnExpiryDateChanged(DateTime? value)
    {
        if (!_applyingDates)
        {
            _expiryManuallyEdited = true;
        }
        AfterRunValueChanged();
    }

    partial void OnQuantityTextChanged(string? value) => AfterRunValueChanged();
    partial void OnCartonFromChanged(string value) => AfterRangeChanged();
    partial void OnCartonToChanged(string value) => AfterRangeChanged();
    partial void OnCopiesChanged(string value) => OnPropertyChanged(nameof(RunSummary));

    partial void OnSelectedPrinterChanged(PrinterDto? value)
    {
        NotifyPrintGateChanged();
        _ = CheckPrinterStatusAsync();
        // The printer can change how the label renders (DPI, resolved template).
        _ = DebouncedPreviewAsync();
    }

    private void AfterRunValueChanged()
    {
        OnPropertyChanged(nameof(OverrideNotice));
        _ = DebouncedPreviewAsync();
    }

    private void AfterRangeChanged()
    {
        OnPropertyChanged(nameof(RunSummary));
        NotifyPrintGateChanged();
        _ = DebouncedPreviewAsync();
    }

    /// <summary>Everything derived from "can we print right now".</summary>
    private void NotifyPrintGateChanged()
    {
        OnPropertyChanged(nameof(PrintButtonText));
        OnPropertyChanged(nameof(PrintDisabledReason));
        PrintCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task InitializeAsync()
    {
        HasInitError = false;
        InitErrorMessage = null;
        try
        {
            Printers.Clear();
            foreach (var printer in await _api.ListPrintersAsync(true, CancellationToken.None))
            {
                Printers.Add(printer);
            }

            if (Printers.Count == 0)
            {
                HasInitError = true;
                InitErrorMessage = "No active printers are configured. " +
                    "Ask an administrator to add one, then retry.";
            }
            else
            {
                SelectedPrinter = Printers.FirstOrDefault(p => p.IsDefault) ?? Printers[0];
            }
        }
        catch (Exception ex)
        {
            HasInitError = true;
            InitErrorMessage = Describe(ex);
            System.Diagnostics.Debug.WriteLine($"PrintView initialization failed: {ex}");
        }
        NotifyPrintGateChanged();
        await RefreshRecentAsync();
    }

    private async Task DebouncedSearchAsync()
    {
        await _searchCts.CancelAsync();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        try
        {
            await Task.Delay(250, ct);
            IsSearching = true;
            var page = await _products.ListAsync(SearchText, null, 20, false, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            SearchResults.Clear();
            foreach (var item in page.Items)
            {
                SearchResults.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsSearching = false;
        }
    }

    /// <summary>Escape closes the result list without losing the selection.</summary>
    public void ClearSearchResults() => SearchResults.Clear();

    private async Task LoadProductAsync(long id)
    {
        await GuardAsync(async () =>
        {
            var detail = await _products.GetAsync(id, CancellationToken.None);
            ProductDetail = detail;

            // Pre-fill from the master; the operator may override (A-9).
            Batch = detail.DefaultBatch;
            QuantityText = detail.DefaultQuantityText;

            // Date defaults: master value when the product has one, otherwise
            // today / production + 1 year. A new product resets expiry
            // ownership so derivation works again.
            _expiryManuallyEdited = false;
            _applyingDates = true;
            var production = detail.DefaultProductionDate?.ToDateTime(TimeOnly.MinValue)
                ?? DateTime.Today;
            ProductionDate = production;
            ExpiryDate = detail.DefaultExpiryDate?.ToDateTime(TimeOnly.MinValue)
                ?? production.AddYears(1);
            _applyingDates = false;

            OnPropertyChanged(nameof(OverrideNotice));
            await DebouncedPreviewAsync();
        });
    }

    private async Task DebouncedPreviewAsync()
    {
        if (SelectedProduct is null)
        {
            return;
        }

        await _previewCts.CancelAsync();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;
        try
        {
            await Task.Delay(300, ct);
            IsPreviewLoading = true;
            long? cartonNumber = null, cartonTotal = null;
            if (TryParseRange(out var from, out var to, out _))
            {
                cartonNumber = from;
                cartonTotal = to - from + 1;
            }
            // TemplateId null → the server resolves the effective template the
            // same way it will at submit time, so the preview is honest.
            var preview = await _api.PreviewAsync(new PrintPreviewRequest(
                SelectedProduct.Id, null, Batch,
                AsDateOnly(ProductionDate), AsDateOnly(ExpiryDate), QuantityText,
                cartonNumber, cartonTotal, SelectedPrinter?.Id), ct);
            if (!ct.IsCancellationRequested)
            {
                // preview.Zpl is deliberately not surfaced: operators check the
                // rendered label, not the printer command stream.
                PreviewImage = Decode(preview.PngBase64);
                PreviewUnavailable = preview.Unavailable;
                PreviewWarning = preview.Warning;
                ErrorMessage = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded.
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            PreviewWarning = null;
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    /// <summary>Live reachability of the selected printer, polled every 20s
    /// and on demand. Best-effort: an unreachable status endpoint reads as
    /// "Status unknown", never as an error and never as a block on printing.</summary>
    [RelayCommand]
    private async Task CheckPrinterStatusAsync()
    {
        var printer = SelectedPrinter;
        if (printer is null)
        {
            PrinterStatusKind = "None";
            PrinterStatusText = null;
            return;
        }

        var generation = ++_printerStatusGeneration;
        try
        {
            var status = await _api.GetPrinterStatusAsync(printer.Id, CancellationToken.None);
            if (generation != _printerStatusGeneration || SelectedPrinter?.Id != printer.Id)
            {
                return; // A newer probe (or another printer) owns the caption now.
            }
            if (status.Online)
            {
                PrinterStatusKind = "Online";
                PrinterStatusText = "✓ Online";
            }
            else
            {
                PrinterStatusKind = "Offline";
                PrinterStatusText = string.IsNullOrWhiteSpace(status.Detail)
                    ? "⚠ Offline"
                    : $"⚠ Offline — {status.Detail}";
            }
        }
        catch (Exception ex)
        {
            if (generation != _printerStatusGeneration)
            {
                return;
            }
            PrinterStatusKind = "Unknown";
            PrinterStatusText = "Status unknown";
            System.Diagnostics.Debug.WriteLine($"Printer status check failed: {ex}");
        }
    }

    private bool CanExecutePrint() =>
        CanPrint && !IsPrinting && SelectedProduct is not null &&
        SelectedPrinter is not null && TryParseRange(out _, out _, out _);

    [RelayCommand(CanExecute = nameof(CanExecutePrint))]
    private async Task PrintAsync()
    {
        // Snapshot: the bound selections can change while dialogs are open.
        var product = SelectedProduct;
        var printer = SelectedPrinter;
        if (product is null)
        {
            ErrorMessage = "Select a product first.";
            return;
        }
        if (printer is null)
        {
            ErrorMessage = "No printer selected.";
            return;
        }
        if (!TryParseRange(out var from, out var to, out var rangeError))
        {
            ErrorMessage = rangeError;
            return;
        }
        if (!short.TryParse(Copies, out var copies) || copies < 1)
        {
            ErrorMessage = "Copies must be 1 or more.";
            return;
        }

        // Offline is a warning, not a block: queuing against an offline
        // printer is legitimate (it prints on reconnect) but must be a choice.
        if (PrinterStatusKind == "Offline")
        {
            var proceed = await DialogService.ConfirmAsync(
                "Printer appears offline",
                $"'{printer.Name}' is not responding right now. The job will be " +
                "queued and should print when the printer is available again. Print anyway?",
                "Print anyway");
            if (!proceed)
            {
                return;
            }
        }

        IsPrinting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            // TemplateId null: the server resolves product default → printer
            // default → global default (§15).
            var result = await _api.SubmitAsync(new PrintRequest(
                product.Id, null, printer.Id,
                Batch, AsDateOnly(ProductionDate), AsDateOnly(ExpiryDate), QuantityText,
                from, to, (int)(to - from + 1), copies, Environment.MachineName),
                CancellationToken.None);

            // Honest about where the job actually went: client-dispatched jobs
            // print when the owning workstation collects them, not "now".
            SuccessMessage = result.DispatchMode == "Client"
                ? $"Job {result.JobNo} queued for workstation '{result.OwnerWorkstation}' — " +
                  $"{result.LabelCount} label(s) will print when it collects the job."
                : $"Sent {result.LabelCount} label(s) to {printer.Name} — job {result.JobNo}.";
            ToastService.Instance.Success(SuccessMessage);

            // Advance the range so the next run continues where this one ended.
            CartonFrom = (result.CartonTo + 1).ToString();
            CartonTo = (result.CartonTo + 1).ToString();

            await RefreshRecentAsync();
        }
        catch (ApiException ex)
        {
            // Includes NO_TEMPLATE — the server message is operator-actionable
            // ("ask an administrator"), so it is shown verbatim.
            ErrorMessage = ex.Message;
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            ErrorMessage = "Cannot reach the server. Check the connection.";
            ToastService.Instance.Error(ErrorMessage);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Print submit failed unexpectedly: {ex}");
            ErrorMessage = "Something went wrong. Please try again.";
            ToastService.Instance.Error(ErrorMessage);
        }
        finally
        {
            IsPrinting = false;
        }
    }

    [RelayCommand]
    private async Task RefreshRecentAsync()
    {
        try
        {
            var page = await _api.HistoryAsync(
                DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(1),
                null, false, null, null, 10, CancellationToken.None);
            RecentJobs.Clear();
            foreach (var job in page.Items)
            {
                RecentJobs.Add(job);
            }
        }
        catch (Exception)
        {
            // The recent list is informational; the print flow reports its own errors.
        }
    }

    /// <summary>
    /// Live status (B-16). Without this the job card is only as fresh as the
    /// last refresh, so a job that fails twenty seconds after submit looks like
    /// it succeeded until somebody happens to look again.
    ///
    /// Best-effort by design: if the hub cannot connect, the screen keeps
    /// working exactly as before and simply is not live.
    /// </summary>
    private async Task SubscribeToJobStatusAsync()
    {
        try
        {
            _statusHub = await _api.SubscribeToJobsAsync(OnJobChanged, CancellationToken.None);
        }
        catch (Exception)
        {
            // Never surfaced: a notification channel is not worth an error on the
            // screen an operator uses all day.
        }
    }

    /// <summary>Pushes arrive on a background thread; collection updates and
    /// property changes have to land on the dispatcher.</summary>
    private void OnJobChanged(PrintJobDto job)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            var index = RecentJobs.ToList().FindIndex(j => j.Id == job.Id);
            if (index >= 0)
            {
                RecentJobs[index] = job;   // in place, so the row does not jump
            }
            else
            {
                RecentJobs.Insert(0, job);
                while (RecentJobs.Count > 10)
                {
                    RecentJobs.RemoveAt(RecentJobs.Count - 1);
                }
            }

            // A job that fails after the operator has moved on is the case this
            // whole channel exists for — say so (covers watchdog failures such
            // as WORKSTATION_UNAVAILABLE) instead of leaving a green
            // "submitted" message standing over a failed print.
            if (job.Status == "Failed")
            {
                SuccessMessage = null;
                ErrorMessage = $"Job {job.JobNo} failed: " +
                    (job.ErrorMessage ?? job.ErrorCode ?? "the printer reported an error.");
            }
        });
    }

    public void Dispose()
    {
        _printerStatusTimer.Stop();
        if (_statusHub is not null)
        {
            _ = _statusHub.DisposeAsync();
            _statusHub = null;
        }
        _searchCts.Dispose();
        _previewCts.Dispose();
    }

    /// <summary>
    /// Decodes the preview to a frozen bitmap. Frozen so it can be handed
    /// straight to the UI thread from wherever the await resumed, and cached
    /// on load so the source stream can be released immediately.
    /// </summary>
    private static System.Windows.Media.Imaging.BitmapImage? Decode(string? base64)
    {
        if (string.IsNullOrEmpty(base64))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(base64));
            var image = new System.Windows.Media.Imaging.BitmapImage();
            image.BeginInit();
            image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // A corrupt preview must not take down the print screen; the screen
            // falls back to its "preview unavailable" state.
            return null;
        }
    }

    private bool TryParseRange(out long from, out long to, out string? error)
    {
        from = to = 0;
        if (!long.TryParse(CartonFrom, out from) || !long.TryParse(CartonTo, out to))
        {
            error = "Carton start and end must be numbers.";
            return false;
        }
        if (from < 1)
        {
            error = "Carton start must be 1 or greater.";
            return false;
        }
        if (to < from)
        {
            error = "Carton end must not be less than carton start.";
            return false;
        }
        if (to - from + 1 > 10_000)
        {
            error = "That range is more than 10,000 labels.";
            return false;
        }
        error = null;
        return true;
    }

    private static DateOnly? AsDateOnly(DateTime? value) =>
        value is { } d ? DateOnly.FromDateTime(d) : null;

    private static string Describe(Exception ex) => ex switch
    {
        ApiException api => api.Message,
        ApiUnreachableException => "Cannot reach the server. Check the connection.",
        _ => "Something went wrong. Please try again.",
    };

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
        }
    }
}
