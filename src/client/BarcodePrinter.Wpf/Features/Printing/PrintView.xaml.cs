using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Printing;

public partial class PrintView : UserControl
{
    public PrintView(PrintViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
