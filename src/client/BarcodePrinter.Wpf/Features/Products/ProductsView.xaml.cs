using System.Windows.Controls;
using System.Windows.Input;

namespace BarcodePrinter.Wpf.Features.Products;

public partial class ProductsView : UserControl
{
    public ProductsView(ProductsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Thumbnails load only for rows the virtualizer actually
    /// materialises — scrolling 10k rows never fetches 10k images (§11.3).</summary>
    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is ProductRow row)
        {
            row.EnsureThumbnail();
        }
    }

    /// <summary>Ctrl+F focuses search. Escape peels one layer at a time:
    /// a live search term first, then the drawer — so it never discards
    /// edits the operator has not looked at yet.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ProductsViewModel vm)
        {
            return;
        }

        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape)
        {
            return;
        }

        if (!string.IsNullOrEmpty(vm.SearchText))
        {
            vm.ClearSearchCommand.Execute(null);
            e.Handled = true;
        }
        else if (vm.Editor is not null)
        {
            vm.CloseEditorCommand.Execute(null);
            e.Handled = true;
        }
    }
}
