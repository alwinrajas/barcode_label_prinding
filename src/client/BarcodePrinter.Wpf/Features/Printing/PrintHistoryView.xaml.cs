using System.Windows.Controls;
using System.Windows.Input;

namespace BarcodePrinter.Wpf.Features.Printing;

public partial class PrintHistoryView : UserControl
{
    public PrintHistoryView(PrintHistoryViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>Escape closes the open row-details panel by clearing the
    /// selection (details show for the selected row only).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is PrintHistoryViewModel { SelectedJob: not null } vm)
        {
            vm.SelectedJob = null;
            e.Handled = true;
        }
    }
}
