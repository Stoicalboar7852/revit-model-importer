using System.Diagnostics;
using System.Text;

namespace RevitModelImporter.Core.WorkerProtocol;

/// <summary>How to start the worker process.</summary>
public sealed class WorkerLaunchInfo
{
    /// <summary>Executable to start: the worker's apphost (RevitModelImporter.Worker.exe) or a dotnet host.</summary>
    public required string FileName { get; init; }

    /// <summary>Arguments placed before the command (e.g. the worker .dll when started through dotnet).</summary>
    public IReadOnlyList<string> PrefixArguments { get; init; } = [];

    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    public string? WorkingDirectory { get; init; }

    public override string ToString() => string.Join(' ', new[] { FileName }.Concat(PrefixArguments));
}

public sealed record WorkerProgress(string Stage, double? Fraction, string? Message);

public sealed class WorkerRunResult
{
    public int ExitCode { get; init; }

    public WorkerMessage? Result { get; init; }

    public string? ErrorMessage { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public string StandardError { get; init; } = "";

    public TimeSpan Elapsed { get; init; }

    public bool Succeeded => ExitCode == 0 && Result is not null;
}

/// <summary>A worker run that did not succeed. <see cref="Message"/> is meant for the user.</summary>
public sealed class WorkerException : Exception
{
    public WorkerException(string message, int exitCode, string? details = null)
        : base(message)
    {
        ExitCode = exitCode;
        Details = details;
    }

    public WorkerException(string message, Exception inner)
        : base(message, inner)
    {
        ExitCode = -1;
    }

    public int ExitCode { get; }

    /// <summary>Worker stderr / diagnostic text.</summary>
    public string? Details { get; }
}

/// <summary>Runs the worker out of process and reads its JSON-lines protocol. Cancelling kills the process
/// tree immediately, which is the point of having a separate process.</summary>
public sealed class WorkerClient
{
    private readonly WorkerLaunchInfo _launch;

    public WorkerClient(WorkerLaunchInfo launch) => _launch = launch;

    public WorkerLaunchInfo Launch => _launch;

    public async Task<WorkerRunResult> RunAsync(IEnumerable<string> arguments, IProgress<WorkerProgress>? progress, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _launch.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = _launch.WorkingDirectory ?? Path.GetDirectoryName(_launch.FileName) ?? Environment.CurrentDirectory,
        };
        foreach (var a in _launch.PrefixArguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var a in arguments)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var (k, v) in _launch.Environment)
        {
            psi.Environment[k] = v;
        }

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new WorkerException($"Could not start the converter ({_launch}).", -1);
            }
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new WorkerException($"Could not start the converter ({_launch}): {e.Message}", e);
        }

        using var registration = cancellationToken.Register(() => Kill(process));

        var stderrTask = ReadTailAsync(process.StandardError, 64 * 1024);
        WorkerMessage? result = null;
        string? error = null;
        var warnings = new List<string>();
        string? line;
        // Not cancelled directly: cancelling kills the process, which ends the stream.
        while ((line = await process.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false)) is not null)
        {
            var msg = WorkerMessage.TryParse(line);
            if (msg is null)
            {
                continue;
            }

            switch (msg.Type)
            {
                case WorkerMessage.ProgressType:
                    progress?.Report(new WorkerProgress(msg.Stage ?? "", msg.Fraction, msg.Message));
                    break;
                case WorkerMessage.WarningType when msg.Message is not null:
                    warnings.Add(msg.Message);
                    break;
                case WorkerMessage.ResultType:
                    result = msg;
                    break;
                case WorkerMessage.ErrorType:
                    error = msg.Message;
                    break;
            }
        }

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        return new WorkerRunResult
        {
            ExitCode = process.ExitCode,
            Result = result,
            ErrorMessage = error,
            Warnings = warnings,
            StandardError = stderr,
            Elapsed = sw.Elapsed,
        };
    }

    public async Task<FormatsResult> GetFormatsAsync(CancellationToken cancellationToken)
    {
        var run = await RunAsync(["formats"], null, cancellationToken).ConfigureAwait(false);
        return EnsureSuccess(run).Formats ?? throw Malformed(run);
    }

    public async Task<AnalysisResult> AnalyzeAsync(string file, IEnumerable<string> textureSearchFolders, IProgress<WorkerProgress>? progress, CancellationToken cancellationToken)
    {
        var args = new List<string> { "analyze", file };
        foreach (var f in textureSearchFolders)
        {
            args.Add("--texture-search");
            args.Add(f);
        }

        var run = await RunAsync(args, progress, cancellationToken).ConfigureAwait(false);
        var analysis = EnsureSuccess(run).Analysis ?? throw Malformed(run);
        foreach (var w in run.Warnings)
        {
            if (!analysis.Warnings.Contains(w))
            {
                analysis.Warnings.Add(w);
            }
        }

        return analysis;
    }

    public async Task<ConversionResult> ConvertAsync(ConvertRequest request, IProgress<WorkerProgress>? progress, CancellationToken cancellationToken)
    {
        var run = await RunAsync(request.ToArguments(), progress, cancellationToken).ConfigureAwait(false);
        var conversion = EnsureSuccess(run).Conversion ?? throw Malformed(run);
        foreach (var w in run.Warnings)
        {
            if (!conversion.Warnings.Contains(w))
            {
                conversion.Warnings.Add(w);
            }
        }

        return conversion;
    }

    private static WorkerMessage EnsureSuccess(WorkerRunResult run)
    {
        if (run.ExitCode == 0 && run.Result is not null)
        {
            return run.Result;
        }

        var description = WorkerExitCodes.Describe(run.ExitCode);
        var message = run.ErrorMessage is { Length: > 0 } e && WorkerExitCodes.IsKnown(run.ExitCode)
            ? $"{description}\n\nDetails: {e}"
            : description;
        throw new WorkerException(message, run.ExitCode, run.StandardError);
    }

    private static WorkerException Malformed(WorkerRunResult run) =>
        new("The converter finished without returning a result.", run.ExitCode, run.StandardError);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting concurrently.
        }
    }

    private static async Task<string> ReadTailAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            sb.Append(buffer, 0, read);
            if (sb.Length > maxChars)
            {
                sb.Remove(0, sb.Length - maxChars);
            }
        }

        return sb.ToString();
    }
}
