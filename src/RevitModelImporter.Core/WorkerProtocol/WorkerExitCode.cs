namespace RevitModelImporter.Core.WorkerProtocol;

/// <summary>Process exit codes of RevitModelImporter.Worker.</summary>
public enum WorkerExitCode
{
    Success = 0,
    UsageError = 2,
    FileNotFound = 3,
    UnsupportedFormat = 4,

    /// <summary>The file could not be parsed (corrupt, truncated or not really in that format).</summary>
    ParseFailed = 5,
    OutputFailed = 6,
    OutOfMemory = 7,
    Unexpected = 10,
}

public static class WorkerExitCodes
{
    public static bool IsKnown(int code) => Enum.IsDefined(typeof(WorkerExitCode), code);

    /// <summary>User-facing explanation for an exit code.</summary>
    public static string Describe(int code) => code switch
    {
        (int)WorkerExitCode.Success => "Finished.",
        (int)WorkerExitCode.UsageError => "The importer started its worker with invalid arguments. Please report this.",
        (int)WorkerExitCode.FileNotFound => "The file could not be found or opened.",
        (int)WorkerExitCode.UnsupportedFormat => "This file format is not supported.",
        (int)WorkerExitCode.ParseFailed => "The file could not be read. It may be damaged, incomplete or not really in the format its extension suggests.",
        (int)WorkerExitCode.OutputFailed => "The converted model could not be written. Check free disk space and folder permissions.",
        (int)WorkerExitCode.OutOfMemory => "The model is too large to convert with the memory available.",
        (int)WorkerExitCode.Unexpected => "The converter hit an unexpected error.",
        _ => $"The converter process stopped unexpectedly (exit code {code}, 0x{code:X8}). This usually means the file triggered a crash in the 3D-format reader. Revit and your project are not affected.",
    };
}
