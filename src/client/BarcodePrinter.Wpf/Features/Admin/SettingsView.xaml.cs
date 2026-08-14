using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Admin;

public partial class SettingsView : UserControl
{
    public SettingsView(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
