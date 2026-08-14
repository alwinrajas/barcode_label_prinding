using System.Collections.ObjectModel;
using System.IO;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Printing;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Contracts.Templates;
using BarcodePrinter.Wpf.Features.Login;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Printing;

/// <summary>
/// The operational screen (blueprint §12.4). One page, four numbered sections,
/// keyboard-first: search → confirm run values → preview → print. Everything is
/// async; the UI never blocks on the server or the printer.
/// </summary>
public sealed partial class PrintViewModel : ObservableObject, IDisposable
{
    private readonly PrintApi _api;
    private readonly ProductsApi _products;
    private CancellationTokenSource _searchCts = new();
    private CancellationTokenSource _previewCts = new();
    private Microsoft.AspNetCore.SignalR.Client.HubConnection? _statusHub;

    public PrintViewModel(PrintApi api, ProductsApi products, Session session)
    {
        _api = api;
        _products = products;
        CanPrint = session.Has(PermissionCodes.PrintExecute);
        _ = InitializeAsync();
        _ = SubscribeToJobStatusAsync();
    }

    public ObservableCollection<ProductSummary> SearchResults { get; } = [];
    public ObservableCollection<PrinterDto> Printers { get; } = [];
    public ObservableCollection<TemplateSummary> Templates { get; } = [];
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
    private TemplateSummary? selectedTemplate;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    private PrinterDto? selectedPrinter;

    [ObservableProperty] private string? previewZpl;
    [ObservableProperty] private bool isPreviewLoading;

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
    [ObservableProperty] private bool isPrinting;

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
        if (value is not null)
        {
            await LoadProductAsync(value.Id);
        }
    }

    partial void OnBatchChanged(string? value) => AfterRunValueChanged();
    partial void OnProductionDateChanged(DateTime? value) => AfterRunValueChanged();
    partial void OnExpiryDateChanged(DateTime? value) => AfterRunValueChanged();
    partial void OnQuantityTextChanged(string? value) => AfterRunValueChanged();
    partial void OnCartonFromChanged(string value) => AfterRangeChanged();
    partial void OnCartonToChanged(string value) => AfterRangeChanged();
    partial void OnCopiesChanged(string value) => OnPropertyChanged(nameof(RunSummary));
    partial void OnSelectedTemplateChanged(TemplateSummary? value) => _ = DebouncedPreviewAsync();

    private void AfterRunValueChanged()
    {
        OnPropertyChanged(nameof(OverrideNotice));
        _ = DebouncedPreviewAsync();
    }

    private void AfterRangeChanged()
    {
        OnPropertyChanged(nameof(RunSummary));
        PrintCommand.NotifyCanExecuteChanged();
        _ = DebouncedPreviewAsync();
    }

    private async Task InitializeAsync()
    {
        await GuardAsync(async () =>
        {
            foreach (var printer in await _api.ListPrintersAsync(true, CancellationToken.None))
            {
                Printers.Add(printer);
            }
            SelectedPrinter = Printers.FirstOrDefault(p => p.IsDefault) ?? Printers.FirstOrDefault();

            foreach (var template in (await _api.ListTemplatesAsync(CancellationToken.None))
                     .Where(t => t.IsActive))
            {
                Templates.Add(template);
            }
            SelectedTemplate = Templates.FirstOrDefault(t => t.IsDefault) ?? Templates.FirstOrDefault();
        });
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

    private async Task LoadProductAsync(long id)
    {
        await GuardAsync(async () =>
        {
            var detail = await _products.GetAsync(id, CancellationToken.None);
            ProductDetail = detail;

            // Pre-fill from the master; the operator may override (A-9).
            Batch = detail.DefaultBatch;
            ProductionDate = detail.DefaultProductionDate?.ToDateTime(TimeOnly.MinValue);
            ExpiryDate = detail.DefaultExpiryDate?.ToDateTime(TimeOnly.MinValue);
            QuantityText = detail.DefaultQuantityText;

            OnPropertyChanged(nameof(OverrideNotice));
            await DebouncedPreviewAsync();
        });
    }

    private async Task DebouncedPreviewAsync()
    {
        if (SelectedProduct is null || SelectedTemplate is null)
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
            TryParseRange(out var from, out var to, out _);
            var preview = await _api.PreviewAsync(new PrintPreviewRequest(
                SelectedProduct.Id, SelectedTemplate.Id, Batch,
                AsDateOnly(ProductionDate), AsDateOnly(ExpiryDate), QuantityText,
                from, to - from + 1), ct);
            if (!ct.IsCancellationRequested)
            {
                PreviewImage = Decode(preview.PngBase64);
                PreviewZpl = preview.Zpl;
                PreviewUnavailable = preview.Unavailable;
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
            PreviewZpl = null;
            ErrorMessage = Describe(ex);
        }
        finally
        {
            IsPreviewLoading = false;
        }
    }

    private bool CanExecutePrint() =>
        CanPrint && !IsPrinting && SelectedProduct is not null &&
        SelectedTemplate is not null && SelectedPrinter is not null &&
        TryParseRange(out _, out _, out _);

    [RelayCommand(CanExecute = nameof(CanExecutePrint))]
    private async Task PrintAsync()
    {
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

        IsPrinting = true;
        ErrorMessage = null;
        SuccessMessage = null;
        try
        {
            var result = await _api.SubmitAsync(new PrintRequest(
                SelectedProduct!.Id, SelectedTemplate!.Id, SelectedPrinter!.Id,
                Batch, AsDateOnly(ProductionDate), AsDateOnly(ExpiryDate), QuantityText,
                from, to, (int)(to - from + 1), copies, Environment.MachineName),
                CancellationToken.None);

            SuccessMessage = $"Sent {result.LabelCount} label(s) to {SelectedPrinter.Name} — job {result.JobNo}.";

            // Advance the range so the next run continues where this one ended.
            CartonFrom = (result.CartonTo + 1).ToString();
            CartonTo = (result.CartonTo + 1).ToString();

            await RefreshRecentAsync();
        }
        catch (Exception ex)
        {
            ErrorMessage = Describe(ex);
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
            // whole channel exists for — say so instead of leaving a green
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
            // A corrupt preview must not take down the print screen; the ZPL
            // panel still shows what would be sent.
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
        ApiUnreachableException => "Cannot reach the server. Check your network connection.",
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
