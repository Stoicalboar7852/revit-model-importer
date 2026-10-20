using System.Windows;
using RevitModelImporter.Core.Presentation;

namespace RevitModelImporter.Revit.UI;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => DialogResult = ok;
    }
}
