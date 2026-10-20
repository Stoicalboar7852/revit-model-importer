using System.Windows;
using RevitModelImporter.Core.Presentation;

namespace RevitModelImporter.Revit.UI;

public partial class SummaryWindow : Window
{
    private readonly ImportSummary _summary;

    public SummaryWindow(ImportSummary summary)
    {
        InitializeComponent();
        _summary = summary;
        Headline.Text = summary.Headline;
        Lines.ItemsSource = summary.Lines().Select(l => Tuple.Create(l.Label, l.Value)).ToList();
        if (summary.TextureFolder is { } folder && !summary.Cancelled)
        {
            TextureNoteText.Text = $"Textures were copied to {folder}. Revit stores only the path to each image: keep this folder with the project (copy it along when you move or share the .rvt).";
        }
        else
        {
            TextureNote.Visibility = Visibility.Collapsed;
        }

        WarningsList.ItemsSource = summary.Warnings;
        if (summary.Warnings.Count == 0)
        {
            WarningsHeader.Visibility = Visibility.Collapsed;
            WarningsList.Visibility = Visibility.Collapsed;
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_summary.ToText());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy; ignore.
        }
    }
}
