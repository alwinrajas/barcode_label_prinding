using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Admin;

public partial class RolesView : UserControl
{
    public RolesView(RolesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
