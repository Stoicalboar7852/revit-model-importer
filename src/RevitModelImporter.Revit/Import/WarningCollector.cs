using Autodesk.Revit.DB;
namespace RevitModelImporter.Revit.Import;

/// <summary>Keeps Revit from showing one dialog per warning (an import can raise hundreds). Warnings are
/// deleted and counted; errors are resolved with Revit's default resolution where one exists, otherwise the
/// transaction is rolled back. Messages are collected for the import summary.</summary>
internal sealed class WarningCollector : IFailuresPreprocessor
{
    private readonly HashSet<string> _messages = new(StringComparer.Ordinal);

    public int WarningsSuppressed { get; private set; }

    public int ErrorsResolved { get; private set; }

    public IReadOnlyCollection<string> Messages => _messages;

    public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
    {
        var resolved = false;
        foreach (var failure in failuresAccessor.GetFailureMessages())
        {
            var text = failure.GetDescriptionText();
            if (_messages.Count < 50)
            {
                _messages.Add(text);
            }

            if (failure.GetSeverity() == FailureSeverity.Warning)
            {
                failuresAccessor.DeleteWarning(failure);
                WarningsSuppressed++;
            }
            else if (failure.HasResolutions())
            {
                failuresAccessor.ResolveFailure(failure);
                ErrorsResolved++;
                resolved = true;
            }
            else
            {
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }

        return resolved ? FailureProcessingResult.ProceedWithCommit : FailureProcessingResult.Continue;
    }

    public void Attach(Transaction transaction)
    {
        var options = transaction.GetFailureHandlingOptions();
        options.SetFailuresPreprocessor(this);
        options.SetClearAfterRollback(true);
        transaction.SetFailureHandlingOptions(options);
    }
}
