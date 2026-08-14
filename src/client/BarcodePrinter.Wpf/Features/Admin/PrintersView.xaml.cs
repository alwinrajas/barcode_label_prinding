using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Admin;

public partial class PrintersView : UserControl
{
    public PrintersView(PrintersViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
