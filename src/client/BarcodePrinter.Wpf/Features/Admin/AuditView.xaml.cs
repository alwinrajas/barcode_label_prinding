using System.Globalization;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Data;

namespace BarcodePrinter.Wpf.Features.Admin;

public partial class AuditView : UserControl
{
    public AuditView(AuditViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

/// <summary>One changed field in an audit entry, as the reader needs it: what
/// it was called, what it was, what it became.</summary>
public sealed record AuditFieldChange(string Field, string OldValue, string NewValue);

/// <summary>
/// Turns the before/after JSON blobs of an audit entry into a readable list of
/// changed fields. Raw JSON is a developer's artefact; an auditor asks "what
/// changed", and the answer is a three-column table. Unreadable payloads fall
/// back to the raw text, which is still better than a blank panel.
/// </summary>
public sealed class AuditDiffConverter : IMultiValueConverter
{
    private const string Absent = "—";

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var before = Parse(values.ElementAtOrDefault(0) as string);
        var after = Parse(values.ElementAtOrDefault(1) as string);
        if (before is null && after is null)
        {
            return Array.Empty<AuditFieldChange>();
        }

        IEnumerable<string> beforeKeys = before?.Keys ?? Enumerable.Empty<string>();
        IEnumerable<string> afterKeys = after?.Keys ?? Enumerable.Empty<string>();
        var keys = new SortedSet<string>(beforeKeys.Concat(afterKeys), StringComparer.OrdinalIgnoreCase);

        var changes = new List<AuditFieldChange>();
        foreach (var key in keys)
        {
            var oldValue = before is not null && before.TryGetValue(key, out var o) ? o : Absent;
            var newValue = after is not null && after.TryGetValue(key, out var n) ? n : Absent;
            if (!string.Equals(oldValue, newValue, StringComparison.Ordinal))
            {
                changes.Add(new AuditFieldChange(key, oldValue, newValue));
            }
        }
        return changes;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    /// <summary>Flattens a JSON object one level; nested structures keep their
    /// JSON text so nothing is silently dropped.</summary>
    private static Dictionary<string, string>? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                map[property.Name] = Render(property.Value);
            }
            return map;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Render(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => Absent,
        JsonValueKind.String => element.GetString() is { Length: > 0 } s ? s : Absent,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText(),
    };
}

/// <summary>Severity to the StatusPill family: security incidents read red,
/// warnings amber, routine activity neutral.</summary>
public sealed class SeverityToPillStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string) switch
        {
            "Security" => "Failed",
            "Warning" => "Warning",
            _ => "",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
