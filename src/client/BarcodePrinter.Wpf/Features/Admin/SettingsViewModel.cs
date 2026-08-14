using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

public sealed partial class SettingItem : ObservableObject
{
    public SettingItem(SettingDto dto)
    {
        Key = dto.Key;
        ValueType = dto.ValueType;
        IsSecret = dto.IsSecret;
        Description = dto.Description;
        OriginalValue = dto.Value ?? "";
        Label = Humanise(dto.Key);
        value = dto.Value ?? "";

        if (IsFeedbackUrl)
        {
            QrPreviewImage = GenerateQrCode(Value);
        }
    }

    public string Key { get; }
    public string ValueType { get; }
    public bool IsSecret { get; }
    public string? Description { get; }
    public string OriginalValue { get; }
    public string Label { get; }

    public bool IsBool => ValueType == "Bool";
    public bool IsFeedbackUrl => Key.EndsWith("FeedbackFormUrl", StringComparison.OrdinalIgnoreCase) || Key.Contains("FeedbackUrl", StringComparison.OrdinalIgnoreCase);

    [ObservableProperty] private string value = "";
    [ObservableProperty] private System.Windows.Media.ImageSource? qrPreviewImage;

    partial void OnValueChanged(string value)
    {
        if (IsFeedbackUrl)
        {
            QrPreviewImage = GenerateQrCode(value);
        }
    }

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value.ToString().ToLowerInvariant();
    }

    public bool IsDirty => !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

    private static System.Windows.Media.ImageSource? GenerateQrCode(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var writer = new ZXing.BarcodeWriterPixelData
            {
                Format = ZXing.BarcodeFormat.QR_CODE,
                Options = new ZXing.QrCode.QrCodeEncodingOptions
                {
                    Height = 180,
                    Width = 180,
                    Margin = 1
                }
            };
            var pixelData = writer.Write(text);
            return System.Windows.Media.Imaging.BitmapSource.Create(
                pixelData.Width, pixelData.Height,
                96, 96,
                System.Windows.Media.PixelFormats.Bgra32,
                null,
                pixelData.Pixels,
                pixelData.Width * 4);
        }
        catch
        {
            return null;
        }
    }

    private static string Humanise(string key)
    {
        var name = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
        var spaced = System.Text.RegularExpressions.Regex.Replace(name, "(?<!^)([A-Z])", " $1");
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
    }
}

public sealed class SettingGroup(string name, IEnumerable<SettingItem> items)
{
    public string Name { get; } = name;
    public IReadOnlyList<SettingItem> Items { get; } = items.ToList();
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AdminApi _api;

    public SettingsViewModel(AdminApi api, Session session)
    {
        _api = api;
        CanManage = session.Has(PermissionCodes.SettingsManage);
        _ = LoadAsync();
    }

    public ObservableCollection<SettingGroup> Groups { get; } = [];
    public bool CanManage { get; }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private string? errorMessage;

    [RelayCommand]
    private async Task LoadAsync()
    {
        await GuardAsync(async () =>
        {
            var settings = await _api.ListSettingsAsync(CancellationToken.None);
            Groups.Clear();
            foreach (var group in settings
                .GroupBy(s => s.Key.Contains(':') ? s.Key[..s.Key.IndexOf(':')] : "General")
                .OrderBy(g => g.Key))
            {
                Groups.Add(new SettingGroup(GroupLabel(group.Key),
                    group.OrderBy(s => s.Key).Select(s => new SettingItem(s))));
            }
            StatusMessage = null;
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var changed = Groups.SelectMany(g => g.Items).Where(i => i.IsDirty)
            .ToDictionary(i => i.Key, i => (string?)i.Value);
        if (changed.Count == 0)
        {
            StatusMessage = "No changes to save.";
            return;
        }

        await GuardAsync(async () =>
        {
            await _api.SaveSettingsAsync(changed, CancellationToken.None);
            StatusMessage = $"{changed.Count} setting(s) saved. Changes take effect immediately.";
            await LoadAsync();
        });
    }

    private static string GroupLabel(string prefix) => prefix switch
    {
        "Label" => "Label & QR",
        "Print" => "Printing",
        "Printing" => "Printing",
        "Import" => "Excel import",
        "Auth" => "Security",
        "Company" => "Company",
        _ => prefix,
    };

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
