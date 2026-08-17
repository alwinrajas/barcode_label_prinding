using System.Windows;
using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Controls;

/// <summary>Semi-transparent overlay with a centered indeterminate bar, for
/// blocking work (saves, deletes). Non-blocking refreshes should use the top
/// State.BusyBar instead so data stays readable.</summary>
public partial class BusyOverlay : UserControl
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(BusyOverlay), new PropertyMetadata(false));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(BusyOverlay), new PropertyMetadata(null));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public BusyOverlay() => InitializeComponent();
}
