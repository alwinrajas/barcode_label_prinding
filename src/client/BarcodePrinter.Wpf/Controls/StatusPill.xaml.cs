using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace BarcodePrinter.Wpf.Controls;

/// <summary>Soft-background pill mapping a job/entity status string to the
/// semantic colour family, so status colour is decided once, not per grid.</summary>
public partial class StatusPill : UserControl
{
    public static readonly DependencyProperty StatusProperty = DependencyProperty.Register(
        nameof(Status), typeof(string), typeof(StatusPill),
        new PropertyMetadata("", (d, _) => ((StatusPill)d).Update()));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusPill),
        new PropertyMetadata("", (d, _) => ((StatusPill)d).Update()));

    public string Status
    {
        get => (string)GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    /// <summary>Display text; falls back to <see cref="Status"/> when unset.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusPill()
    {
        InitializeComponent();
        Update();
    }

    private void Update()
    {
        var (softKey, accentKey) = (Status ?? "") switch
        {
            "Queued" or "Printing" or "Dispatching" => ("Brush.Info.Soft", "Accent.Info"),
            "Completed" => ("Brush.Success.Soft", "Accent.Success"),
            "PartiallyCompleted" or "Warning" => ("Brush.Warning.Soft", "Accent.Warning"),
            "Failed" or "Cancelled" => ("Brush.Danger.Soft", "Accent.Danger"),
            _ => ("Surface.Sunken", "Text.Secondary"),
        };
        Bg.Background = (Brush)FindResource(softKey);
        Dot.Fill = (Brush)FindResource(accentKey);
        Label.Text = string.IsNullOrEmpty(Text) ? Status : Text;
    }
}
