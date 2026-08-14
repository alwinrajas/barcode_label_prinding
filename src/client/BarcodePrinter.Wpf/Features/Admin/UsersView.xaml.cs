using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Admin;

public partial class UsersView : UserControl
{
    public UsersView(UsersViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
