using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Reports;

public partial class ReportsView : UserControl
{
    public ReportsView(ReportsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
