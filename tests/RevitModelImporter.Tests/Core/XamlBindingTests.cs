using System.Text.RegularExpressions;
using System.Xml.Linq;
using RevitModelImporter.Core.Presentation;

namespace RevitModelImporter.Tests.Core;

/// <summary>WPF bindings fail silently at runtime. The views cannot be shown on Linux, but their bindings can be
/// checked against the view models they bind to.</summary>
public partial class XamlBindingTests
{
    [Theory]
    [InlineData("ImportDialog.xaml", typeof(ImportDialogViewModel))]
    [InlineData("SettingsWindow.xaml", typeof(SettingsViewModel))]
    public void EveryBindingPath_ExistsOnTheViewModel(string xaml, Type viewModel)
    {
        var paths = BindingPaths(Path.Combine(RepoRoot(), "src", "RevitModelImporter.Revit", "UI", xaml)).ToList();
        Assert.NotEmpty(paths);

        foreach (var path in paths)
        {
            var type = viewModel;
            foreach (var segment in path.Split('.'))
            {
                var property = type.GetProperty(segment);
                Assert.True(property is not null, $"{xaml}: '{path}' — '{segment}' is not a public property of {type.Name}.");
                type = property!.PropertyType;
            }
        }
    }

    [Fact]
    public void ImportDialog_BindsTheRecommendationAndTheImportButton()
    {
        var paths = BindingPaths(Path.Combine(RepoRoot(), "src", "RevitModelImporter.Revit", "UI", "ImportDialog.xaml")).ToHashSet();
        foreach (var required in new[] { "IsRecommendationVisible", "RecommendationText", "ApplyRecommendationCommand", "ImportCommand", "IsSimplifyEnabled", "EstimateText", "FaceCountText", "SizeText" })
        {
            Assert.Contains(required, paths);
        }

        // The Import button must not be bound to anything that could disable it.
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "RevitModelImporter.Revit", "UI", "ImportDialog.xaml"));
        var import = doc.Descendants().Single(e => e.Name.LocalName == "Button" && (string?)e.Attribute("Command") == "{Binding ImportCommand}");
        Assert.Null(import.Attribute("IsEnabled"));
    }

    [Fact]
    public void ImportDialog_NumberBoxesApplyTheirMinimumOnlyWhenLeft()
    {
        // These setters clamp (face count ≥ 4, percent ≥ 0.1, palette 2–256). Updating on every keystroke would
        // rewrite a number while it is typed ("2" of "2500" becomes 4), so they must update on LostFocus.
        var doc = XDocument.Load(Path.Combine(RepoRoot(), "src", "RevitModelImporter.Revit", "UI", "ImportDialog.xaml"));
        var boxes = doc.Descendants().Where(e => e.Name.LocalName == "TextBox").Select(e => (string?)e.Attribute("Text") ?? "").ToList();
        foreach (var property in new[] { "SimplifyTriangleCount", "SimplifyPercent", "PaletteSize" })
        {
            var binding = Assert.Single(boxes, b => b.Contains("{Binding " + property + ",", StringComparison.Ordinal));
            Assert.Contains("UpdateSourceTrigger=LostFocus", binding, StringComparison.Ordinal);
        }
    }

    private static IEnumerable<string> BindingPaths(string file)
    {
        var doc = XDocument.Load(file);
        foreach (var element in doc.Descendants())
        {
            // Bindings inside templates bind to items, not to the view model.
            if (element.Ancestors().Any(a => a.Name.LocalName is "DataTemplate" or "ControlTemplate"))
            {
                continue;
            }

            foreach (var attribute in element.Attributes())
            {
                foreach (Match m in BindingRegex().Matches(attribute.Value))
                {
                    var body = m.Groups[1].Value;
                    if (body.Contains("Source=", StringComparison.Ordinal) || body.Contains("ElementName=", StringComparison.Ordinal) || body.Contains("RelativeSource", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var first = body.Split(',')[0].Trim();
                    var path = first.StartsWith("Path=", StringComparison.Ordinal) ? first[5..] : first;
                    if (path.Length > 0 && !path.Contains('=', StringComparison.Ordinal))
                    {
                        yield return path;
                    }
                }
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RevitModelImporter.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"\{Binding\s*([^}]*)\}")]
    private static partial Regex BindingRegex();
}
