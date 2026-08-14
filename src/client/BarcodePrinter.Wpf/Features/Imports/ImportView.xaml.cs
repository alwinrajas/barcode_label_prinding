using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Imports;

public partial class ImportView : UserControl
{
    public ImportView(ImportViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
