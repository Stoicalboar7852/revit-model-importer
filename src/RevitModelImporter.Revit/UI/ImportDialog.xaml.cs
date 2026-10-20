using System.Windows;
using RevitModelImporter.Core.Presentation;

namespace RevitModelImporter.Revit.UI;

public partial class ImportDialog : Window
{
    public ImportDialog(ImportDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => DialogResult = ok;
    }
}
