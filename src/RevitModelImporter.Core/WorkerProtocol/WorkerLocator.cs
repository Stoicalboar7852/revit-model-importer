using System.Runtime.InteropServices;

namespace RevitModelImporter.Core.WorkerProtocol;

/// <summary>Finds the worker next to the add-in and builds launch info that runs it on the same .NET
/// installation the host process (Revit) is using.</summary>
public static class WorkerLocator
{
    public const string WorkerAssemblyName = "RevitModelImporter.Worker";
    public const string WorkerFolderName = "Worker";

    /// <summary>Candidate folders: <c>&lt;dir&gt;\Worker</c> (installed layout) then <c>&lt;dir&gt;</c> itself (tests, dev).</summary>
    public static string? FindWorkerDirectory(string addinDirectory)
    {
        foreach (var dir in new[] { Path.Combine(addinDirectory, WorkerFolderName), addinDirectory })
        {
            if (File.Exists(Path.Combine(dir, WorkerAssemblyName + ".dll")))
            {
                return dir;
            }
        }

        return null;
    }

    public static WorkerLaunchInfo CreateLaunchInfo(string workerDirectory)
    {
        var dll = Path.Combine(workerDirectory, WorkerAssemblyName + ".dll");
        if (!File.Exists(dll))
        {
            throw new FileNotFoundException("The converter (RevitModelImporter.Worker) is missing. Reinstall the add-in.", dll);
        }

        var env = new Dictionary<string, string>
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            // The worker targets net8.0; let it run on a newer runtime if that is all there is (Revit 2027).
            ["DOTNET_ROLL_FORWARD"] = "LatestMajor",
        };

        var root = CurrentDotnetRoot();
        if (root is not null)
        {
            env["DOTNET_ROOT"] = root;
            env[RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "DOTNET_ROOT_X64" : "DOTNET_ROOT_ARM64"] = root;
        }

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var apphost = Path.Combine(workerDirectory, WorkerAssemblyName + (isWindows ? ".exe" : ""));
        if (isWindows && File.Exists(apphost))
        {
            return new WorkerLaunchInfo { FileName = apphost, Environment = env, WorkingDirectory = workerDirectory };
        }

        // Fall back to "dotnet Worker.dll" using the host of the running runtime, then whatever is on PATH.
        var dotnet = root is not null ? Path.Combine(root, isWindows ? "dotnet.exe" : "dotnet") : null;
        if (dotnet is null || !File.Exists(dotnet))
        {
            dotnet = isWindows ? "dotnet.exe" : "dotnet";
        }

        return new WorkerLaunchInfo { FileName = dotnet, PrefixArguments = [dll], Environment = env, WorkingDirectory = workerDirectory };
    }

    /// <summary>Root of the .NET installation this process runs on (the folder containing host/fxr),
    /// derived from the runtime directory, e.g. C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.11\ → C:\Program Files\dotnet.</summary>
    public static string? CurrentDotnetRoot()
    {
        var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
        var dir = new DirectoryInfo(runtimeDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var root = dir.Parent?.Parent?.Parent;
        if (root is not null && Directory.Exists(Path.Combine(root.FullName, "host", "fxr")))
        {
            return root.FullName;
        }

        return null;
    }
}
