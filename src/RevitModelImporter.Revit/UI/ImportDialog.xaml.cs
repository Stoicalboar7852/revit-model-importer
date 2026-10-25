using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RevitModelImporter.Core.Presentation;

namespace RevitModelImporter.Revit.UI;

public partial class ImportDialog : Window
{
    public ImportDialog(ImportDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) =>
        {
            if (ok)
            {
                // Number boxes apply their value (and its minimum) when they lose focus, so typing is never rewritten
                // mid-number. Enter and Alt+I import without moving focus: apply the box being edited first.
                (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }

            DialogResult = ok;
        };
    }
}
