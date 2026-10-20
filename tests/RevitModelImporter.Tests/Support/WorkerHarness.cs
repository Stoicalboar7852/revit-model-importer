using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Tests.Support;

/// <summary>Starts the real worker as a separate process, exactly as the add-in does.</summary>
public static class WorkerHarness
{
    public const string RollForwardVariable = "RMI_WORKER_ROLL_FORWARD";
    public const string ExpectedRuntimeMajorVariable = "RMI_EXPECT_WORKER_RUNTIME_MAJOR";

    public static WorkerClient CreateClient()
    {
        var dir = WorkerLocator.FindWorkerDirectory(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException("RevitModelImporter.Worker.dll was not copied next to the tests.");
        var launch = WorkerLocator.CreateLaunchInfo(dir);

        // CI runs the worker tests twice: as shipped (rolls forward to the newest runtime, like Revit 2027's .NET 10)
        // and pinned to .NET 8 (what Revit 2025/2026 install) with RMI_WORKER_ROLL_FORWARD=LatestPatch.
        if (Environment.GetEnvironmentVariable(RollForwardVariable) is { Length: > 0 } rollForward)
        {
            var env = new Dictionary<string, string>(launch.Environment) { ["DOTNET_ROLL_FORWARD"] = rollForward };
            launch = new WorkerLaunchInfo { FileName = launch.FileName, PrefixArguments = launch.PrefixArguments, Environment = env, WorkingDirectory = launch.WorkingDirectory };
        }

        return new WorkerClient(launch);
    }

    public static AnalysisResult Analyze(string file, params string[] textureSearch) =>
        CreateClient().AnalyzeAsync(file, textureSearch, null, CancellationToken.None).GetAwaiter().GetResult();

    public static ConversionResult Convert(ConvertRequest request) =>
        CreateClient().ConvertAsync(request, null, CancellationToken.None).GetAwaiter().GetResult();

    public static WorkerRunResult Run(params string[] args) =>
        CreateClient().RunAsync(args, null, CancellationToken.None).GetAwaiter().GetResult();
}
