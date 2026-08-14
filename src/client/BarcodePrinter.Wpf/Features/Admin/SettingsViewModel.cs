using System.Collections.ObjectModel;
using BarcodePrinter.Client.Core;
using BarcodePrinter.Contracts;
using BarcodePrinter.Contracts.Admin;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BarcodePrinter.Wpf.Features.Admin;

public sealed partial class SettingItem(SettingDto dto) : ObservableObject
{
    public string Key { get; } = dto.Key;
    public string ValueType { get; } = dto.ValueType;
    public bool IsSecret { get; } = dto.IsSecret;
    public string? Description { get; } = dto.Description;
    public string OriginalValue { get; } = dto.Value ?? "";

    /// <summary>Human label from the key: "Label:FeedbackFormUrl" → "Feedback form URL".</summary>
    public string Label { get; } = Humanise(dto.Key);

    public bool IsBool => ValueType == "Bool";

    [ObservableProperty] private string value = dto.Value ?? "";

    public bool BoolValue
    {
        get => bool.TryParse(Value, out var b) && b;
        set => Value = value.ToString().ToLowerInvariant();
    }

    public bool IsDirty => !string.Equals(Value, OriginalValue, StringComparison.Ordinal);

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
