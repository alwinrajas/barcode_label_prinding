using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Admin;

public partial class AuditView : UserControl
{
    public AuditView(AuditViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
