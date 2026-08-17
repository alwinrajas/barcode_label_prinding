using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace BarcodePrinter.Wpf.Features.Printing;

public partial class PrintView : UserControl
{
    /// <summary>Content-area width below which the preview stacks under the
    /// form instead of sitting beside it.</summary>
    private const double StackBreakpoint = 1000;

    /// <summary>Smallest width either column is allowed to claim side by side.
    /// Below the breakpoint there is only one column, so both drop to zero.</summary>
    private const double ColumnMinWidth = 320;

    private bool _stacked;
    private bool _layoutApplied;

    public PrintView(PrintViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Ctrl+F jumps to the product search box; Escape closes an open
    /// search-result list. Ctrl+P stays a KeyBinding in XAML (tests parse it).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape &&
                 DataContext is PrintViewModel vm && vm.SearchResults.Count > 0)
        {
            vm.ClearSearchResults();
            e.Handled = true;
        }
    }

    /// <summary>Two columns on a wide screen, one below the breakpoint. There is
    /// no page-level scroller: side by side the preview sits in the grid, outside
    /// FormScroller, so only the form scrolls and the preview stays put. Stacked,
    /// the preview is re-homed into FormScroller's content (row 1, under the form)
    /// and that one scroller scrolls the page as a whole — the alternative, an
    /// unscrolled stack, would simply clip.</summary>
    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < StackBreakpoint;
        if (_layoutApplied && stacked == _stacked)
        {
            return;
        }
        _layoutApplied = true;
        _stacked = stacked;

        if (stacked)
        {
            StackPreview();
        }
        else
        {
            PinPreviewBeside();
        }
    }

    /// <summary>One column: the preview moves inside the scroller, below the
    /// form, so form and preview scroll together instead of being clipped.</summary>
    private void StackPreview()
    {
        if (ContentGrid.Children.Contains(PreviewHost))
        {
            ContentGrid.Children.Remove(PreviewHost);
            Grid.SetRow(PreviewHost, 1);
            Grid.SetColumn(PreviewHost, 0);
            ScrollHost.Children.Add(PreviewHost);
        }
        PreviewHost.Margin = new Thickness(0, 16, 0, 0);

        // The preview column collapses entirely; the form column takes the lot.
        GutterColumn.Width = new GridLength(0);
        PreviewColumn.Width = new GridLength(0);
        PreviewColumn.MinWidth = 0;
        FormColumn.MinWidth = 0;
        // The width cap moves from the column to the form panel, so the fields
        // stay readable while the stacked preview uses the full content width.
        FormColumn.MaxWidth = double.PositiveInfinity;
        FormHost.MaxWidth = 520;
        FormHost.HorizontalAlignment = HorizontalAlignment.Left;
    }

    /// <summary>Two columns: the preview goes back to its own grid cell, outside
    /// FormScroller, filling the row's full height however long the form gets.</summary>
    private void PinPreviewBeside()
    {
        if (ScrollHost.Children.Contains(PreviewHost))
        {
            ScrollHost.Children.Remove(PreviewHost);
            Grid.SetRow(PreviewHost, 0);
            Grid.SetColumn(PreviewHost, 2);
            ContentGrid.Children.Add(PreviewHost);
        }
        PreviewHost.Margin = default;

        GutterColumn.Width = new GridLength(16);
        PreviewColumn.Width = new GridLength(7, GridUnitType.Star);
        PreviewColumn.MinWidth = ColumnMinWidth;
        FormColumn.MinWidth = ColumnMinWidth;
        FormColumn.MaxWidth = 520;
        FormHost.MaxWidth = double.PositiveInfinity;
        FormHost.HorizontalAlignment = HorizontalAlignment.Stretch;
    }
}

/// <summary>Recent-job timestamps arrive in UTC; the operator reads wall-clock
/// time. Same-day jobs show the time alone, older ones include the date.</summary>
public sealed class UtcToLocalTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime utc)
        {
            return "";
        }
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm", culture)
            : local.ToString("dd MMM HH:mm", culture);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
