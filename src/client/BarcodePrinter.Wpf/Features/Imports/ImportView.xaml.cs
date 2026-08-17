using System.Windows;
using System.Windows.Controls;

namespace BarcodePrinter.Wpf.Features.Imports;

public partial class ImportView : UserControl
{
    public ImportView(ImportViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        var canDrop = DataContext is ImportViewModel { ShowProgress: false } && GetXlsxPath(e) is not null;
        e.Effects = canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (DataContext is ImportViewModel vm && GetXlsxPath(e) is { } path)
        {
            // Same command path as "Choose file and import…".
            vm.UploadFileCommand.Execute(path);
        }
    }

    private static string? GetXlsxPath(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] files
            ? files.FirstOrDefault(f => f.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            : null;
}
