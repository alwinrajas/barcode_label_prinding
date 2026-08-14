using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Products;
using BarcodePrinter.Wpf.Features.Login;
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
    private const int PageSize = 50;

    private readonly ProductsApi _api;
    private readonly Session _session;
    private CancellationTokenSource _searchCts = new();
    private string? _nextCursor;

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

    public bool CanEdit { get; }
    public bool CanAdd { get; }
    public bool CanDelete { get; }

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private bool hasMore;

    [ObservableProperty]
    private string? statusMessage;

    [ObservableProperty]
    private ProductRow? selectedRow;

    // ---- Detail drawer state ----
    [ObservableProperty]
    private ProductEditModel? editor;

    [ObservableProperty]
    private ImageSource? editorImage;

    partial void OnSearchTextChanged(string value) => _ = DebouncedSearchAsync();

    async partial void OnSelectedRowChanged(ProductRow? value)
    {
        if (value is not null)
        {
            await OpenEditorAsync(value.Id);
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            foreach (var uom in await _api.UomsAsync(CancellationToken.None))
            {
                Uoms.Add(uom);
            }
        }
        catch (Exception)
        {
            // Lookups retry next open; the grid is the priority.
        }
        await RefreshAsync();
    }

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
    private Task RefreshAsync() => LoadAsync(reset: true, CancellationToken.None);

    [RelayCommand]
    private Task LoadMoreAsync() => LoadAsync(reset: false, CancellationToken.None);

    private async Task LoadAsync(bool reset, CancellationToken ct)
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var page = await _api.ListAsync(SearchText, reset ? null : _nextCursor, PageSize,
                includeInactive: true, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
            if (reset)
            {
                Items.Clear();
            }
            foreach (var summary in page.Items)
            {
                Items.Add(new ProductRow(summary, _api));
            }
            _nextCursor = page.NextCursor;
            HasMore = page.HasMore;
            StatusMessage = Items.Count == 0
                ? (string.IsNullOrWhiteSpace(SearchText)
                    ? "No products yet. Add the first one, or import from Excel."
                    : $"No products match \"{SearchText}\".")
                : null;
        }
        catch (ApiException ex)
        {
            StatusMessage = ErrorCatalog.MessageFor(ex.Code);
        }
        catch (ApiUnreachableException)
        {
            StatusMessage = "Cannot reach the server. Check your network connection.";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ---- Editor ------------------------------------------------------------

    [RelayCommand]
    private void NewProduct()
    {
        SelectedRow = null;
        Editor = ProductEditModel.CreateNew();
        EditorImage = null;
    }

    private async Task OpenEditorAsync(long id)
    {
        try
        {
            var detail = await _api.GetAsync(id, CancellationToken.None);
            Editor = ProductEditModel.From(detail);
            EditorImage = null;
            if (detail.HasImage && detail.ImageHash is not null)
            {
                var bytes = await _api.GetImageAsync(id, detail.ImageHash, thumb: false, CancellationToken.None);
                EditorImage = ToImage(bytes, 480);
            }
        }
        catch (ApiException ex)
        {
            StatusMessage = ErrorCatalog.MessageFor(ex.Code);
        }
        catch (ApiUnreachableException)
        {
            StatusMessage = "Cannot reach the server. Check your network connection.";
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Editor is null)
        {
            return;
        }
        var (request, error) = Editor.ToRequest();
        if (error is not null)
        {
            Editor.ErrorMessage = error;
            return;
        }

        Editor.IsBusy = true;
        Editor.ErrorMessage = null;
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
            Editor.SavedMessage = "Saved.";
            // Re-read for the fresh concurrency stamp.
            var detail = await _api.GetAsync(Editor.Id.Value, CancellationToken.None);
            Editor = ProductEditModel.From(detail, keepSavedMessage: true);
        }
        catch (ApiException ex) when (ex.Code == ErrorCodes.ConcurrencyConflict)
        {
            Editor.ErrorMessage =
                "This product was changed by another user while you were editing. Reload to see their changes.";
        }
        catch (ApiException ex)
        {
            Editor.ErrorMessage = ex.Message;
        }
        catch (ApiUnreachableException)
        {
            Editor.ErrorMessage = "Cannot reach the server. Check your network connection.";
        }
        finally
        {
            if (Editor is not null)
            {
                Editor.IsBusy = false;
            }
        }
    }

    [RelayCommand]
    private async Task ToggleActiveAsync()
    {
        if (Editor?.Id is not { } id)
        {
            return;
        }

        var verb = Editor.IsActive ? "Deactivate" : "Activate";
        var confirmed = System.Windows.MessageBox.Show(
            $"{verb} product {Editor.Code}?",
            "Confirm", System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
        if (!confirmed)
        {
            return;
        }

        try
        {
            if (Editor.IsActive)
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
        }
        catch (ApiException ex)
        {
            Editor.ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private async Task UploadImageAsync()
    {
        if (Editor?.Id is not { } id)
        {
            return;
        }
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Images (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
            Title = "Choose a product image",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        Editor.IsBusy = true;
        try
        {
            var hash = await _api.UploadImageAsync(id, dialog.FileName, CancellationToken.None);
            var bytes = await _api.GetImageAsync(id, hash, thumb: false, CancellationToken.None);
            EditorImage = ToImage(bytes, 480);
            await LoadAsync(reset: true, CancellationToken.None);
            Editor.SavedMessage = "Image updated.";
        }
        catch (ApiException ex)
        {
            Editor.ErrorMessage = ex.Message;
        }
        finally
        {
            Editor.IsBusy = false;
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
    public long? Id { get; set; }
    public bool IsNew => Id is null;
    public bool IsActive { get; private set; } = true;
    public string? ConcurrencyStamp { get; private set; }

    [ObservableProperty] private string code = string.Empty;
    [ObservableProperty] private string description = string.Empty;
    [ObservableProperty] private string? barcodeValue;
    [ObservableProperty] private long? uomId;
    [ObservableProperty] private string? size;
    [ObservableProperty] private string? color;
    [ObservableProperty] private string? defaultBatch;
    [ObservableProperty] private DateTime? defaultProductionDate;
    [ObservableProperty] private DateTime? defaultExpiryDate;
    [ObservableProperty] private string? defaultQuantityText;
    [ObservableProperty] private string? cartonQuantityText;
    [ObservableProperty] private string? cartonsPerPalletText;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private string? savedMessage;

    public string Title => IsNew ? "New product" : $"Edit {Code}";
    public string ActiveActionLabel => IsActive ? "Deactivate" : "Activate";

    public static ProductEditModel CreateNew() => new();

    public static ProductEditModel From(ProductDetail d, bool keepSavedMessage = false) => new()
    {
        Id = d.Id,
        IsActive = d.IsActive,
        ConcurrencyStamp = d.ConcurrencyStamp,
        Code = d.Code,
        Description = d.Description,
        BarcodeValue = d.BarcodeValue,
        UomId = d.UomId,
        Size = d.Size,
        Color = d.Color,
        DefaultBatch = d.DefaultBatch,
        DefaultProductionDate = d.DefaultProductionDate?.ToDateTime(TimeOnly.MinValue),
        DefaultExpiryDate = d.DefaultExpiryDate?.ToDateTime(TimeOnly.MinValue),
        DefaultQuantityText = d.DefaultQuantityText ?? d.DefaultQuantity?.ToString("0.###"),
        CartonQuantityText = d.CartonQuantity?.ToString("0.###"),
        CartonsPerPalletText = d.CartonsPerPallet?.ToString(),
        SavedMessage = keepSavedMessage ? "Saved." : null,
    };

    public (SaveProductRequest? Request, string? Error) ToRequest()
    {
        if (string.IsNullOrWhiteSpace(Code))
        {
            return (null, "Product code is required.");
        }
        if (string.IsNullOrWhiteSpace(Description))
        {
            return (null, "Description is required.");
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
        if (DefaultExpiryDate is { } exp && DefaultProductionDate is { } prod && exp < prod)
        {
            return (null, "Expiry date cannot be before production date.");
        }

        return (new SaveProductRequest(
            Code.Trim(), Description.Trim(), BarcodeValue, UomId, Size, Color, null,
            DefaultBatch,
            DefaultProductionDate is { } p ? DateOnly.FromDateTime(p) : null,
            DefaultExpiryDate is { } e ? DateOnly.FromDateTime(e) : null,
            null, DefaultQuantityText, cartonQty, perPallet,
            ConcurrencyStamp), null);
    }
}
