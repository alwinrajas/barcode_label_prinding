using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BarcodePrinter.Wpf.Controls;

/// <summary>Centered error state with the support reference (when the server
/// supplied one) and an optional Retry action.</summary>
public partial class ErrorState : UserControl
{
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(ErrorState), new PropertyMetadata(""));

    public static readonly DependencyProperty ReferenceProperty = DependencyProperty.Register(
        nameof(Reference), typeof(string), typeof(ErrorState), new PropertyMetadata(null));

    public static readonly DependencyProperty RetryCommandProperty = DependencyProperty.Register(
        nameof(RetryCommand), typeof(ICommand), typeof(ErrorState), new PropertyMetadata(null));

    public string Message
    {
        get => (string)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    public string? Reference
    {
        get => (string?)GetValue(ReferenceProperty);
        set => SetValue(ReferenceProperty, value);
    }

    public ICommand? RetryCommand
    {
        get => (ICommand?)GetValue(RetryCommandProperty);
        set => SetValue(RetryCommandProperty, value);
    }

    public ErrorState() => InitializeComponent();
}
