using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Printing;

public partial class PrintHistoryView : UserControl
{
    public PrintHistoryView(PrintHistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
