using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Wpf.Features.Login;
using BarcodePrinter.Wpf.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Products;

/// <summary>Row wrapper: thumbnails load lazily per row, through the disk
/// cache, decoded at display size (§11.3 — never full images in grids).</summary>
public sealed partial class ProductRow(ProductSummary summary, ProductsApi api) : ObservableObject
{
    public ProductSummary Summary { get; } = summary;
    public long Id => Summary.Id;
    public string Code => Summary.Code;
    public string Description => Summary.Description;
    public string? Uom => Summary.Uom;
    public string? Size => Summary.Size;
    public string? Color => Summary.Color;
    public string? DefaultBatch => Summary.DefaultBatch;
    public bool IsActive => Summary.IsActive;

    /// <summary>StatusPill family key. "Completed" is the pill's success
    /// family; anything unmapped falls back to the neutral grey used for
    /// inactive rows — colour is never the only signal, StatusText carries
    /// the word (§12).</summary>
    public string StatusKey => Summary.IsActive ? "Completed" : "Inactive";
    public string StatusText => Summary.IsActive ? "Active" : "Inactive";

    [ObservableProperty]
    private ImageSource? thumbnail;

    private bool _thumbnailRequested;

    public void EnsureThumbnail()
    {
        if (_thumbnailRequested || !Summary.HasImage || Summary.ImageHash is null)
        {
            return;
        }
        _thumbnailRequested = true;
        _ = LoadThumbnailAsync();
    }

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var bytes = await api.GetImageAsync(Id, Summary.ImageHash!, thumb: true, CancellationToken.None);
            if (bytes is null)
            {
                return;
            }
            var image = new BitmapImage();
            using (var stream = new System.IO.MemoryStream(bytes))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 48;   // decode at display size (§11.3)
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            Thumbnail = image;
        }
        catch (Exception)
        {
            // A missing thumbnail must never break the grid.
        }
    }
}

public sealed partial class ProductsViewModel : ObservableObject
{
    /// <summary>Longest UOM code the server will accept (it uppercases and
    /// truncates, so we validate rather than silently mangle).</summary>
    private const int MaxUomCodeLength = 16;

    private readonly ProductsApi _api;
    private readonly Session _session;
    private CancellationTokenSource _searchCts = new();

    public ProductsViewModel(ProductsApi api, Session session)
    {
        _api = api;
        _session = session;
        CanEdit = session.Has(PermissionCodes.ProductEdit);
        CanAdd = session.Has(PermissionCodes.ProductAdd);
        CanDelete = session.Has(PermissionCodes.ProductDelete);
        _ = InitializeAsync();
    }

    public ObservableCollection<ProductRow> Items { get; } = [];
    public ObservableCollection<UomDto> Uoms { get; } = [];

    public IReadOnlyList<int> PageSizes { get; } = [25, 50, 100];

    public bool CanEdit { get; }
    public bool CanAdd { get; }
    public bool CanDelete { get; }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool hasMore;

    /// <summary>Null while browsing means "end of list"; null while SEARCHING
    /// with HasMore=true means the server capped the result set and cannot
    /// page it — a different message, not a Load more button.</summary>
    [ObservableProperty]
    private string? nextCursor;

    [ObservableProperty]
    private bool includeInactive = true;

    [ObservableProperty]
    private int pageSize = 50;

    [ObservableProperty]
    private string? listErrorMessage;

    [ObservableProperty]
    private string? listErrorReference;

    [ObservableProperty]
    private ProductRow? selectedRow;

    /// <summary>False until the first list response lands, so the initial
    /// paint shows the busy overlay rather than a false "no products" state.</summary>
    [ObservableProperty]
    private bool hasLoadedOnce;

    // ---- Detail drawer state ----
    [ObservableProperty]
    private ProductEditModel? editor;

    [ObservableProperty]
    private ImageSource? editorImage;

    [ObservableProperty]
    private bool isUploadingImage;

    /// <summary>Blocking drawer work (save / activate). Deliberately separate
    /// from Editor.IsBusy so an image upload shows its determinate progress
    /// bar instead of being hidden behind the overlay.</summary>
    [ObservableProperty]
    private bool isEditorBusy;

    /// <summary>Image upload progress fraction (0–1).</summary>
    [ObservableProperty]
    private double uploadProgress;

    [ObservableProperty]
    private bool uomLookupFailed;

    /// <summary>Whatever the preview showed before a pending pick replaced it,
    /// so removing the pick restores the server image instead of blanking it.</summary>
    private ImageSource? _imageBeforePending;

    // ---- Derived list state (the five states of §12) ----

    public bool HasSearchText => !string.IsNullOrEmpty(SearchText);

    public bool ShowInitialBusy => IsLoading && !HasLoadedOnce;

    public bool ShowErrorState => ListErrorMessage is not null;

    private bool IsListEmpty => HasLoadedOnce && !IsLoading && ListErrorMessage is null && Items.Count == 0;

    public bool ShowNoProductsState => IsListEmpty && !HasSearchText;

    public bool ShowNoMatchesState => IsListEmpty && HasSearchText;

    public string NoMatchesMessage => $"No products match \"{SearchText}\".";

    /// <summary>Cursor paging is only possible while the server handed one back.</summary>
    public bool ShowLoadMore => HasMore && NextCursor is not null;

    /// <summary>Search hit the server's 50-row cap: more matches exist but
    /// there is no cursor to follow, so the fix is a narrower term.</summary>
    public bool ShowSearchTruncation => HasMore && NextCursor is null;

    public string SearchTruncationMessage =>
        "Showing the first 50 matches. Refine your search to narrow results.";

    public string? CountText => Items.Count == 0
        ? null
        : $"Showing {Items.Count:N0} product{(Items.Count == 1 ? "" : "s")}";

    public string? UomWarning => UomLookupFailed
        ? "Units could not be loaded. Type a unit code and it will be created when you save."
        : null;

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(HasSearchText));
        OnPropertyChanged(nameof(ShowInitialBusy));
        OnPropertyChanged(nameof(ShowErrorState));
        OnPropertyChanged(nameof(ShowNoProductsState));
        OnPropertyChanged(nameof(ShowNoMatchesState));
        OnPropertyChanged(nameof(NoMatchesMessage));
        OnPropertyChanged(nameof(ShowLoadMore));
        OnPropertyChanged(nameof(ShowSearchTruncation));
        OnPropertyChanged(nameof(CountText));
    }

    partial void OnSearchTextChanged(string value)
    {
        NotifyListState();
        _ = DebouncedSearchAsync();
    }

    partial void OnIsLoadingChanged(bool value) => NotifyListState();

    partial void OnHasMoreChanged(bool value) => NotifyListState();

    partial void OnNextCursorChanged(string? value) => NotifyListState();

    partial void OnHasLoadedOnceChanged(bool value) => NotifyListState();

    partial void OnListErrorMessageChanged(string? value) => NotifyListState();

    partial void OnIncludeInactiveChanged(bool value) => _ = RefreshAsync();

    partial void OnPageSizeChanged(int value) => _ = RefreshAsync();

    partial void OnUomLookupFailedChanged(bool value) => OnPropertyChanged(nameof(UomWarning));

    async partial void OnSelectedRowChanged(ProductRow? value)
    {
        if (value is not null)
        {
            await OpenEditorAsync(value.Id);
        }
    }

    private async Task InitializeAsync()
    {
        await LoadUomsAsync();
        await RefreshAsync();
    }

    /// <summary>Lookup load, retried when the drawer opens: an empty unit
    /// dropdown with no explanation is how the previous version failed
    /// silently.</summary>
    private async Task LoadUomsAsync()
    {
        try
        {
            var uoms = await _api.UomsAsync(CancellationToken.None);
            Uoms.Clear();
            foreach (var uom in uoms)
            {
                Uoms.Add(uom);
            }
            UomLookupFailed = false;
        }
        catch (Exception)
        {
            UomLookupFailed = true;
        }
    }

    private Task EnsureUomsAsync() =>
        Uoms.Count == 0 || UomLookupFailed ? LoadUomsAsync() : Task.CompletedTask;

    /// <summary>250 ms debounce + in-flight cancellation (§11.3): one query in
    /// flight per keystroke burst, stale responses never render.</summary>
    private async Task DebouncedSearchAsync()
    {
        _searchCts.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        try
        {
            await Task.Delay(250, ct);
            await LoadAsync(reset: true, ct);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer keystroke.
        }
    }

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(reset: true, CancellationToken.None);

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(reset: false, CancellationToken.None);

    private async Task LoadAsync(bool reset, CancellationToken ct)
    {
        IsLoading = true;
        ListErrorMessage = null;
        ListErrorReference = null;
        try
        {
            var page = await _api.ListAsync(SearchText, reset ? null : NextCursor, PageSize,
                IncludeInactive, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (reset)
            {
                Items.Clear();
            }
            foreach (var summary in page.Items ?? [])
            {
                Items.Add(new ProductRow(summary, _api));
            }
            NextCursor = page.NextCursor;
            HasMore = page.HasMore;
        }
        catch (ApiException ex)
        {
            ListErrorMessage = ErrorCatalog.MessageFor(ex.Code);
            ListErrorReference = ex.CorrelationId;
        }
        catch (ApiUnreachableException)
        {
            ListErrorMessage = "Cannot reach the server. Check your network connection.";
        }
        catch (Exception ex)
        {
            // This runs fire-and-forget from the constructor, so an unexpected
            // failure here would otherwise leave an empty grid and no
            // explanation while the exception went unobserved.
            System.Diagnostics.Debug.WriteLine(ex);
            ListErrorMessage = "Unable to load products. Please retry or contact support.";
        }
        finally
        {
            IsLoading = false;
            HasLoadedOnce = true;
            NotifyListState();
        }
    }

    // ---- Editor ------------------------------------------------------------

    [RelayCommand]
    private void NewProduct()
    {
        SelectedRow = null;
        Editor = ProductEditModel.CreateNew();
        EditorImage = null;
        _imageBeforePending = null;
        _ = EnsureUomsAsync();
    }

    private async Task OpenEditorAsync(long id)
    {
        await EnsureUomsAsync();
        try
        {
            var detail = await _api.GetAsync(id, CancellationToken.None);
            Editor = ProductEditModel.From(detail);
            EditorImage = null;
            _imageBeforePending = null;
            if (detail.HasImage && detail.ImageHash is not null)
            {
                var bytes = await _api.GetImageAsync(id, detail.ImageHash, thumb: false, CancellationToken.None);
                EditorImage = ToImage(bytes, 480);
            }
        }
        catch (ApiException ex)
        {
            ToastService.Instance.Error(ErrorCatalog.MessageFor(ex.Code), ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            ToastService.Instance.Error("Cannot reach the server. Check your network connection.");
        }
    }

    /// <summary>Closing is a discard: unsaved edits get one confirmation, never
    /// a silent loss.</summary>
    [RelayCommand]
    private async Task CloseEditorAsync()
    {
        if (Editor is null)
        {
            return;
        }
        if (Editor.IsDirty)
        {
            var discard = await DialogService.ConfirmAsync(
                "Discard changes?",
                "This product has unsaved changes. Closing the editor discards them.",
                "Discard", danger: true);
            if (!discard)
            {
                return;
            }
        }
        // Discarding the drawer discards the pending pick with it.
        Editor = null;
        EditorImage = null;
        _imageBeforePending = null;
        SelectedRow = null;
    }

    /// <summary>Typed text that matches a known unit reuses its id; anything
    /// else travels as UomCode and the server find-or-creates it.</summary>
    private (long? UomId, string? UomCode, string? Error) ResolveUom(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return (null, null, null);
        }
        var match = Uoms.FirstOrDefault(u =>
            string.Equals(u.Code, trimmed, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            return (match.Id, null, null);
        }
        if (trimmed.Length > MaxUomCodeLength)
        {
            return (null, null, $"Unit code cannot be longer than {MaxUomCodeLength} characters.");
        }
        return (null, trimmed.ToUpperInvariant(), null);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is null)
        {
            return;
        }

        // A pending image belongs to a product that has no id yet, so it is
        // uploaded after the create returns one. Captured up front because the
        // editor instance is replaced by the freshly read detail below.
        var pendingPath = Editor.PendingImagePath;
        var pendingType = Editor.PendingImageContentType;

        var (uomId, uomCode, uomError) = ResolveUom(Editor.UomText);
        if (uomError is not null)
        {
            Editor.ErrorMessage = uomError;
            return;
        }

        var (request, error) = Editor.ToRequest(uomId, uomCode);
        if (error is not null)
        {
            Editor.ErrorMessage = error;
            return;
        }

        Editor.IsBusy = true;
        IsEditorBusy = true;
        Editor.ErrorMessage = null;
        Editor.SavedMessage = null;
        var saved = false;
        try
        {
            if (Editor.Id is { } id)
            {
                await _api.UpdateAsync(id, request!, CancellationToken.None);
            }
            else
            {
                Editor.Id = await _api.CreateAsync(request!, CancellationToken.None);
            }
            await LoadAsync(reset: true, CancellationToken.None);
            // Re-read for the fresh concurrency stamp. The drawer stays open so
            // a brand-new product can get its image without being reopened.
            var detail = await _api.GetAsync(Editor.Id!.Value, CancellationToken.None);
            Editor = ProductEditModel.From(detail);
            RestorePendingImage(pendingPath, pendingType);
            saved = true;
            ToastService.Instance.Success("Product saved.");
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.ConcurrencyConflict)
        {
            const string message =
                "This product was changed by another user while you were editing. Reload to see their changes.";
            if (Editor is not null)
            {
                Editor.ErrorMessage = message;
            }
            ToastService.Instance.Error(message, ex.CorrelationId);
        }
        catch (ApiException ex)
        {
            if (Editor is not null)
            {
                Editor.ErrorMessage = ex.Message;
            }
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            const string message = "Cannot reach the server. Check your network connection.";
            if (Editor is not null)
            {
                Editor.ErrorMessage = message;
            }
            ToastService.Instance.Error(message);
        }
        finally
        {
            IsEditorBusy = false;
            if (Editor is not null)
            {
                Editor.IsBusy = false;
            }
        }

        // The product row exists from here on, so an image failure can never
        // cost the operator the product — it only leaves the pick to retry.
        if (saved && pendingPath is not null)
        {
            await UploadPendingImageAsync();
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (Editor?.Id is not { } id)
        {
            return;
        }

        var pendingPath = Editor.PendingImagePath;
        var pendingType = Editor.PendingImageContentType;
        var deactivating = Editor.IsActive;
        var verb = deactivating ? "Deactivate" : "Activate";
        var confirmed = await DialogService.ConfirmAsync(
            $"{verb} product?",
            deactivating
                ? $"{Editor.Code} will stop appearing for new print jobs. Existing history is unaffected."
                : $"{Editor.Code} will become available for print jobs again.",
            verb, danger: deactivating);
        if (!confirmed)
        {
            return;
        }

        Editor.IsBusy = true;
        IsEditorBusy = true;
        try
        {
            if (deactivating)
            {
                await _api.DeactivateAsync(id, CancellationToken.None);
            }
            else
            {
                await _api.ActivateAsync(id, CancellationToken.None);
            }
            await LoadAsync(reset: true, CancellationToken.None);
            var detail = await _api.GetAsync(id, CancellationToken.None);
            Editor = ProductEditModel.From(detail);
            RestorePendingImage(pendingPath, pendingType);
            ToastService.Instance.Success(deactivating ? "Product deactivated." : "Product activated.");
        }
        catch (ApiException ex)
        {
            if (Editor is not null)
            {
                Editor.ErrorMessage = ex.Message;
            }
            ToastService.Instance.Error(ex.Message, ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            ToastService.Instance.Error("Cannot reach the server. Check your network connection.");
        }
        finally
        {
            IsEditorBusy = false;
            if (Editor is not null)
            {
                Editor.IsBusy = false;
            }
        }
    }

    /// <summary>"Choose image…". A saved product uploads straight away (the
    /// endpoint needs its id); a new one holds the pick and previews it from
    /// disk until Save has an id to upload against.</summary>
    [RelayCommand]
    private async Task UploadImageAsync()
    {
        if (Editor is null)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.jpg;*.jpeg;*.png;*.webp)|*.jpg;*.jpeg;*.png;*.webp",
            Title = "Choose a product image",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var validation = ImageFileValidator.Validate(dialog.FileName);
        if (!validation.IsValid)
        {
            Editor.ErrorMessage = validation.Error;
            return;
        }

        if (Editor.Id is { } id)
        {
            await UploadImageCoreAsync(id, dialog.FileName, validation.ContentType,
                "Image updated.", toastFailures: false);
            return;
        }

        // Deferred: POST /api/products/{id}/image has no id to aim at yet.
        if (Editor.PendingImagePath is null)
        {
            _imageBeforePending = EditorImage;
        }
        Editor.PendingImagePath = dialog.FileName;
        Editor.PendingImageContentType = validation.ContentType;
        Editor.ErrorMessage = null;
        EditorImage = ToImageFromFile(dialog.FileName, 480);
    }

    /// <summary>Drops a pick that was never uploaded, restoring whatever the
    /// preview showed before it — a mistaken choice is never stuck.</summary>
    [RelayCommand]
    private void RemovePendingImage()
    {
        if (Editor?.PendingImagePath is null)
        {
            return;
        }
        Editor.ClearPendingImage();
        Editor.ErrorMessage = null;
        EditorImage = _imageBeforePending;
        _imageBeforePending = null;
    }

    /// <summary>Second chance after the create succeeded but its image upload
    /// did not. Pressing Save again does the same thing.</summary>
    [RelayCommand]
    private Task RetryImageUploadAsync() => UploadPendingImageAsync();

    private void RestorePendingImage(string? path, string? contentType)
    {
        if (Editor is null || path is null)
        {
            return;
        }
        Editor.PendingImagePath = path;
        Editor.PendingImageContentType = contentType;
    }

    private async Task UploadPendingImageAsync()
    {
        if (Editor?.Id is not { } id || Editor.PendingImagePath is not { } path)
        {
            return;
        }

        // Re-checked at upload time: the file may have been moved, replaced or
        // truncated between the pick and the save.
        var validation = ImageFileValidator.Validate(path);
        if (!validation.IsValid)
        {
            var message = validation.Error ?? "The chosen image can no longer be read.";
            Editor.ErrorMessage = message;
            Editor.ClearPendingImage();
            EditorImage = _imageBeforePending;
            _imageBeforePending = null;
            ToastService.Instance.Error(message);
            return;
        }

        await UploadImageCoreAsync(id, path, validation.ContentType,
            "Product image uploaded.", toastFailures: true);
    }

    /// <summary>The one upload path: client-validated bytes go up through
    /// _api.UploadImageAsync (401-refresh-safe multipart with progress), then
    /// the stored image is read back so the preview shows what the server kept.</summary>
    private async Task UploadImageCoreAsync(
        long id, string path, string contentType, string successMessage, bool toastFailures)
    {
        if (Editor is null)
        {
            return;
        }
        Editor.IsBusy = true;
        IsUploadingImage = true;
        UploadProgress = 0;
        try
        {
            var progress = new Progress<double>(p => UploadProgress = p);
            var hash = await _api.UploadImageAsync(
                id, path, contentType, progress, CancellationToken.None);
            var bytes = await _api.GetImageAsync(id, hash, thumb: false, CancellationToken.None);
            EditorImage = ToImage(bytes, 480);
            _imageBeforePending = null;
            await LoadAsync(reset: true, CancellationToken.None);
            if (Editor is not null)
            {
                Editor.ClearPendingImage();
                Editor.ErrorMessage = null;
                Editor.SavedMessage = successMessage;
            }
        }
        catch (ApiException ex)
        {
            Fail(
                ex.CorrelationId is { } reference ? $"{ex.Message} (Reference: {reference})" : ex.Message,
                ex.Message, ex.CorrelationId);
        }
        catch (ApiUnreachableException)
        {
            const string message = "Cannot reach the server. Check the connection and try again.";
            Fail(message, message, null);
        }
        catch (System.IO.IOException)
        {
            const string message = "The file could not be read. It may be open in another program.";
            Fail(message, message, null);
        }
        finally
        {
            IsUploadingImage = false;
            if (Editor is not null)
            {
                Editor.IsBusy = false;
            }
        }

        // The pick is deliberately left in place on failure, so Retry (or Save)
        // can try the very same file again.
        void Fail(string inline, string toast, string? reference)
        {
            if (Editor is not null)
            {
                Editor.ErrorMessage = inline;
            }
            if (toastFailures)
            {
                ToastService.Instance.Error(toast, reference);
            }
        }
    }

    /// <summary>Preview for a pick that has not been uploaded yet: decoded from
    /// disk at display size and frozen, mirroring ToImage. The stream is closed
    /// before the bitmap is used, so the chosen file is never left locked.</summary>
    private static ImageSource? ToImageFromFile(string path, int decodeWidth)
    {
        try
        {
            var image = new BitmapImage();
            using (var stream = System.IO.File.OpenRead(path))
            {
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = decodeWidth;
                image.StreamSource = stream;
                image.EndInit();
            }
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            // No installed decoder (WebP on an older Windows) or an unreadable
            // file: the pick is still uploadable, it just cannot be shown.
            return null;
        }
    }

    private static ImageSource? ToImage(byte[]? bytes, int decodeWidth)
    {
        if (bytes is null)
        {
            return null;
        }
        var image = new BitmapImage();
        using (var stream = new System.IO.MemoryStream(bytes))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodeWidth;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }
}

/// <summary>Editable form state for the drawer. String-backed date/number
/// fields so partial input never throws; parsing happens on save.</summary>
public sealed partial class ProductEditModel : ObservableObject
{
    private const char SnapshotSeparator = '\u001f';

    private string _snapshot = string.Empty;

    public long? Id { get; set; }
    public bool IsNew => Id is null;
    public bool IsActive { get; private set; } = true;
    public string? ConcurrencyStamp { get; private set; }

    [ObservableProperty] private string code = string.Empty;
    [ObservableProperty] private string description = string.Empty;
    [ObservableProperty] private long? uomId;
    /// <summary>What the editable UOM combo shows: an existing code, or a new
    /// one the operator typed. Resolved to UomId / UomCode on save.</summary>
    [ObservableProperty] private string? uomText;
    [ObservableProperty] private string? size;
    [ObservableProperty] private string? color;
    [ObservableProperty] private string? defaultBatch;
    [ObservableProperty] private string? defaultQuantityText;
    [ObservableProperty] private string? cartonQuantityText;
    [ObservableProperty] private string? cartonsPerPalletText;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? savedMessage;

    /// <summary>A chosen-but-not-yet-uploaded image file. The image endpoint is
    /// POST /api/products/{id}/image, so a new product has to hold its pick
    /// here until the create hands back an id.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingImage))]
    [NotifyPropertyChangedFor(nameof(CanRetryImageUpload))]
    [NotifyPropertyChangedFor(nameof(ImageCaption))]
    [NotifyPropertyChangedFor(nameof(ImagePlaceholderText))]
    private string? pendingImagePath;

    /// <summary>Sniffed by ImageFileValidator when the file was chosen, so the
    /// upload never re-guesses the type from a (possibly renamed) extension.</summary>
    public string? PendingImageContentType { get; set; }

    public bool HasPendingImage => PendingImagePath is not null;

    /// <summary>A pending pick that already has an id is a failed upload: it is
    /// the only state where retrying alone (without re-saving) makes sense.</summary>
    public bool CanRetryImageUpload => !IsNew && HasPendingImage;

    public string? ImageCaption => IsNew
        ? HasPendingImage
            ? "Will be uploaded when you save the product."
            : "Choose an image now — it is uploaded when you save the product."
        : HasPendingImage
            ? "This image has not been uploaded yet. Retry, or press Save to try again."
            : null;

    public string ImagePlaceholderText => HasPendingImage
        ? "Image selected — it cannot be previewed on this PC, but it will still be uploaded."
        : "No image yet — used on the printed label";

    public void ClearPendingImage()
    {
        PendingImagePath = null;
        PendingImageContentType = null;
    }

    public string Title => IsNew ? "New product" : "Edit product";
    public string ActiveActionLabel => IsActive ? "Deactivate" : "Activate";
    public bool CanDeactivate => !IsNew && IsActive;
    public bool CanActivate => !IsNew && !IsActive;

    public static ProductEditModel CreateNew()
    {
        var model = new ProductEditModel();
        model.MarkClean();
        return model;
    }

    public static ProductEditModel From(ProductDetail d, bool keepSavedMessage = false)
    {
        var model = new ProductEditModel
        {
            Id = d.Id,
            IsActive = d.IsActive,
            ConcurrencyStamp = d.ConcurrencyStamp,
            Code = d.Code,
            Description = d.Description,
            UomId = d.UomId,
            UomText = d.Uom,
            Size = d.Size,
            Color = d.Color,
            DefaultBatch = d.DefaultBatch,
            DefaultQuantityText = d.DefaultQuantityText ?? d.DefaultQuantity?.ToString("0.###"),
            CartonQuantityText = d.CartonQuantity?.ToString("0.###"),
            CartonsPerPalletText = d.CartonsPerPallet?.ToString(),
            SavedMessage = keepSavedMessage ? "Saved." : null,
        };
        model.MarkClean();
        return model;
    }

    /// <summary>Baseline for dirty tracking, taken whenever the form is loaded
    /// or freshly saved.</summary>
    public void MarkClean() => _snapshot = Snapshot();

    public bool IsDirty => !string.Equals(_snapshot, Snapshot(), StringComparison.Ordinal);

    /// <summary>PendingImagePath is part of the baseline: a chosen image that
    /// has not reached the server is an unsaved change like any other.</summary>
    private string Snapshot() => string.Join(SnapshotSeparator,
        Code, Description, UomText, Size, Color, DefaultBatch,
        DefaultQuantityText, CartonQuantityText, CartonsPerPalletText, PendingImagePath);

    public (SaveProductRequest? Request, string? Error) ToRequest(long? uomId, string? uomCode)
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return (null, "Product code is required.");
        }
        if (string.IsNullOrWhiteSpace(Description))
        {
            return (null, "Product name is required.");
        }

        decimal? cartonQty = null;
        if (!string.IsNullOrWhiteSpace(CartonQuantityText))
        {
            if (!decimal.TryParse(CartonQuantityText, out var v))
            {
                return (null, "Carton quantity must be a number.");
            }
            cartonQty = v;
        }
        int? perPallet = null;
        if (!string.IsNullOrWhiteSpace(CartonsPerPalletText))
        {
            if (!int.TryParse(CartonsPerPalletText, out var v))
            {
                return (null, "Cartons per pallet must be a whole number.");
            }
            perPallet = v;
        }
        // No production/expiry dates here: they describe a print run, and the
        // Print Labels screen supplies them (today / today + 1 year).
        return (new SaveProductRequest(
            Code.Trim(), Description.Trim(), uomId, Size, Color,
            DefaultBatch, null, DefaultQuantityText, cartonQty, perPallet,
            ConcurrencyStamp, uomCode), null);
    }
}
