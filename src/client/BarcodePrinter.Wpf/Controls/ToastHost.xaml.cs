using System.Windows;
using BarcodePrinter.Wpf.Services;

namespace BarcodePrinter.Wpf.Controls;

/// <summary>Renders <see cref="ToastService.Instance"/> toasts bottom-right.
/// Place once in the shell, overlaying the content area.</summary>
public partial class ToastHost : System.Windows.Controls.UserControl
{
    public ToastHost()
    {
        InitializeComponent();
        Items.ItemsSource = ToastService.Instance.Toasts;
    }

    private void OnCloseClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Toast toast })
        {
            ToastService.Instance.Dismiss(toast);
        }
    }
}
