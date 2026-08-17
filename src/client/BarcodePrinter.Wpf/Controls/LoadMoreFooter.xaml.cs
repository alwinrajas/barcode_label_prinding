using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BarcodePrinter.Wpf.Controls;

/// <summary>Paged-list footer: "Load more" while the server has more rows,
/// plus a caption like "Showing 50 of 1,240".</summary>
public partial class LoadMoreFooter : UserControl
{
    public static readonly DependencyProperty HasMoreProperty = DependencyProperty.Register(
        nameof(HasMore), typeof(bool), typeof(LoadMoreFooter), new PropertyMetadata(false));

    public static readonly DependencyProperty IsLoadingProperty = DependencyProperty.Register(
        nameof(IsLoading), typeof(bool), typeof(LoadMoreFooter), new PropertyMetadata(false));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(LoadMoreFooter), new PropertyMetadata(null));

    public static readonly DependencyProperty CountTextProperty = DependencyProperty.Register(
        nameof(CountText), typeof(string), typeof(LoadMoreFooter), new PropertyMetadata(null));

    public bool HasMore
    {
        get => (bool)GetValue(HasMoreProperty);
        set => SetValue(HasMoreProperty, value);
    }

    public bool IsLoading
    {
        get => (bool)GetValue(IsLoadingProperty);
        set => SetValue(IsLoadingProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public string? CountText
    {
        get => (string?)GetValue(CountTextProperty);
        set => SetValue(CountTextProperty, value);
    }

    public LoadMoreFooter() => InitializeComponent();
}
