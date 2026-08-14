using System.Windows.Controls;

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
}
