using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace BarcodePrinter.Wpf;

/// <summary>Visible when the value is non-null, collapsed otherwise.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when the value IS null (empty-state hints).</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Joins a string collection for compact grid display.</summary>
public sealed class JoinListConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is IEnumerable<string> items ? string.Join(", ", items) : "";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when a collection count is greater than zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is int count && count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible when a collection count IS zero (empty states).</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Scales a 0..1 fraction against a container height so trend bars
/// resize with the window instead of using fixed pixel heights.</summary>
public sealed class FractionOfHeightConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length < 2 || values[0] is not double fraction || values[1] is not double available)
        {
            return 2d;
        }
        // Leave room for the value and day labels under each bar.
        var usable = Math.Max(0, available - 46);
        return Math.Max(2d, fraction * usable);
    }

    public object[] ConvertBack(object? v, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is false;
    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is false;
}

/// <summary>Counts read as prose on the dashboard — "1 label", "5 labels" —
/// rather than the "(s)" shorthand.</summary>
public sealed class PluralConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var noun = p as string ?? "item";
        var count = value is null ? 0 : System.Convert.ToInt64(value, c);
        return $"{count:N0} {noun}{(count == 1 ? "" : "s")}";
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
