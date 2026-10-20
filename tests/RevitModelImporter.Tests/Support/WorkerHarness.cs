using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Tests.Support;

/// <summary>Starts the real worker as a separate process, exactly as the add-in does.</summary>
public static class WorkerHarness
{
    public static WorkerClient CreateClient()
    {
        var dir = WorkerLocator.FindWorkerDirectory(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("RevitModelImporter.Worker.dll was not copied next to the tests.");
        return new WorkerClient(WorkerLocator.CreateLaunchInfo(dir));
    }

    public static AnalysisResult Analyze(string file, params string[] textureSearch) =>
        CreateClient().AnalyzeAsync(file, textureSearch, null, CancellationToken.None).GetAwaiter().GetResult();

    public static ConversionResult Convert(ConvertRequest request) =>
        CreateClient().ConvertAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();

    public static WorkerRunResult Run(params string[] args) =>
        CreateClient().RunAsync(args, null, CancellationToken.None).GetAwaiter().GetResult();
}
