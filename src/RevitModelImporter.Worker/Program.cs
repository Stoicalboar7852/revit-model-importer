using System.Globalization;
using System.Reflection;
using System.Text;
using RevitModelImporter.Core.Intermediate;
using RevitModelImporter.Core.Processing;
using RevitModelImporter.Core.Simplification;
using RevitModelImporter.Core.Units;
using RevitModelImporter.Core.WorkerProtocol;

namespace RevitModelImporter.Worker;

/// <summary>
/// RevitModelImporter.Worker — reads 3D files with Assimp outside of Revit.
///
///   formats                       list importable extensions (JSON)
///   analyze &lt;file&gt; [--texture-search DIR]...
///   convert &lt;file&gt; --out PATH [--simplify RATIO|COUNT] [--units mm|cm|dm|m|km|in|ft|yd] [--up y|z]
///           [--material-mode textured|baked|flat] [--palette-size N] [--texture-dir DIR]
///           [--texture-search DIR]... [--vertex-tolerance FEET] [--min-edge FEET] [--merge]
///
/// stdout: one JSON object per line ({"type":"progress"|"warning"|"result"|"error", ...}).
/// Exit codes: see <see cref="WorkerExitCode"/>.
/// </summary>
public static class Program
{
    private static readonly object OutputLock = new();

    public static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return Run(args);
        }
        catch (UsageException e)
        {
            return Fail(WorkerExitCode.UsageError, e.Message);
        }
        catch (FileNotFoundException e)
        {
            return Fail(WorkerExitCode.FileNotFound, e.Message);
        }
        catch (DirectoryNotFoundException e)
        {
            return Fail(WorkerExitCode.FileNotFound, e.Message);
        }
        catch (UnsupportedFormatException e)
        {
            return Fail(WorkerExitCode.UnsupportedFormat, e.Message);
        }
        catch (ModelLoadException e)
        {
            return Fail(WorkerExitCode.ParseFailed, e.Message);
        }
        catch (Assimp.AssimpException e)
        {
            return Fail(WorkerExitCode.ParseFailed, e.Message);
        }
        catch (OutputException e)
        {
            return Fail(WorkerExitCode.OutputFailed, e.Message);
        }
        catch (OutOfMemoryException)
        {
            return Fail(WorkerExitCode.OutOfMemory, "Out of memory while processing the model.");
        }
        catch (Exception e)
        {
            Console.Error.WriteLine(e);
            return Fail(WorkerExitCode.Unexpected, $"{e.GetType().Name}: {e.Message}");
        }
    }

    private static int Run(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.Error.WriteLine(Usage);
            return args.Length == 0 ? (int)WorkerExitCode.UsageError : 0;
        }

        if (args[0] is "--version")
        {
            Console.WriteLine(Generator);
            return 0;
        }

        var command = args[0];
        var options = CommandLine.Parse(args.Skip(1).ToArray());
        return command switch
        {
            "formats" => Formats(),
            "analyze" => Analyze(options),
            "convert" => Convert(options),
            _ => throw new UsageException($"Unknown command '{command}'.\n{Usage}"),
        };
    }

    private static int Formats()
    {
        var formats = AssimpSceneLoader.SupportedFormats();
        Emit(new WorkerMessage
        {
            Type = WorkerMessage.ResultType,
            Formats = new FormatsResult
            {
                AssimpVersion = AssimpSceneLoader.AssimpVersion,
                Formats = formats.Select(f => new FormatInfo { Extension = f.Extension, Description = f.Description }).ToList(),
            },
        });
        return 0;
    }

    private static int Analyze(CommandLine cl)
    {
        var input = RequireInput(cl);
        var scene = new AssimpSceneLoader().Load(input, cl.GetAll("--texture-search"), Progress);
        Progress("Analyzing", 0.8, "Counting faces and checking textures");
        var analysis = ModelAnalyzer.Analyze(scene);
        foreach (var w in analysis.Warnings)
        {
            EmitWarning(w);
        }

        Emit(new WorkerMessage { Type = WorkerMessage.ResultType, Analysis = analysis });
        return 0;
    }

    private static int Convert(CommandLine cl)
    {
        var input = RequireInput(cl);
        var output = cl.Get("--out") ?? throw new UsageException("convert needs --out <path>.");
        output = Path.GetFullPath(output);

        var scene = new AssimpSceneLoader().Load(input, cl.GetAll("--texture-search"), Progress);

        var unit = scene.DetectedUnit ?? FormatDefaults.GuessUnit(scene.FormatId);
        if (cl.Get("--units") is { } u && !LengthUnits.TryParse(u, out unit))
        {
            throw new UsageException($"Unknown unit '{u}'.");
        }

        var up = scene.DefaultUpAxis;
        if (cl.Get("--up") is { } upText && !AxisConversion.TryParse(upText, out up))
        {
            throw new UsageException($"Unknown up axis '{upText}'. Use y or z.");
        }

        var mode = MaterialMode.FlatColors;
        if (cl.Get("--material-mode") is { } modeText)
        {
            if (!MaterialModes.TryParse(modeText, out mode))
            {
                throw new UsageException($"Unknown material mode '{modeText}'.");
            }
        }
        else
        {
            mode = ModelAnalyzer.Analyze(scene).RecommendedMaterialMode;
        }

        SimplifyTarget? simplify = null;
        if (cl.Get("--simplify") is { } s)
        {
            if (!SimplifyTarget.TryParse(s, out var t))
            {
                throw new UsageException($"Invalid --simplify value '{s}'. Use a ratio such as 0.25 or 25%, or a triangle count such as 50000.");
            }

            simplify = t;
        }

        var defaults = CleanupOptions.RevitDefaults;
        var cleanup = new CleanupOptions(
            cl.GetDouble("--vertex-tolerance") ?? defaults.VertexTolerance,
            cl.GetDouble("--min-edge") ?? defaults.MinEdgeLength);

        var options = new ConversionOptions
        {
            Unit = unit,
            UpAxis = up,
            MaterialMode = mode,
            Simplify = simplify,
            PaletteSize = Math.Clamp(cl.GetInt("--palette-size") ?? 32, 1, 256),
            TextureDirectory = cl.Get("--texture-dir") is { } td ? Path.GetFullPath(td) : null,
            Cleanup = cleanup,
            Merge = cl.Has("--merge"),
        };

        var (header, meshes) = new ConversionPipeline(new ImageDecoder()).Run(scene, options, new ProgressSink(), CancellationToken.None);
        header.Generator = Generator;

        Progress("Writing", 0.95, "Writing converted model");
        try
        {
            var dir = Path.GetDirectoryName(output);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            IntermediateFile.Write(output, header, meshes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new OutputException($"Could not write '{output}': {e.Message}", e);
        }

        Progress("Done", 1.0, null);
        Emit(new WorkerMessage
        {
            Type = WorkerMessage.ResultType,
            Conversion = new ConversionResult { OutputPath = output, Stats = header.Stats, Warnings = header.Warnings },
        });
        return 0;
    }

    private static string RequireInput(CommandLine cl)
    {
        var input = cl.Positional.FirstOrDefault() ?? throw new UsageException("Missing input file.");
        input = Path.GetFullPath(input);
        if (!File.Exists(input))
        {
            throw new FileNotFoundException($"File not found: {input}", input);
        }

        var ext = FormatDefaults.NormalizeExtension(input);
        if (FormatDefaults.ExcludedExtensions.Contains(ext))
        {
            throw new UnsupportedFormatException($".{ext} files are imported by Revit itself (Insert ▸ Import CAD / Link), not by this add-in.");
        }

        if (!AssimpSceneLoader.SupportedFormats().Any(f => f.Extension == ext))
        {
            throw new UnsupportedFormatException($".{ext} is not a supported 3D format.");
        }

        return input;
    }

    private static string Generator
    {
        get
        {
            var v = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";
            var plus = v.IndexOf('+', StringComparison.Ordinal);
            return $"RevitModelImporter.Worker {(plus > 0 ? v[..plus] : v)} (Assimp {AssimpSceneLoader.AssimpVersion}, .NET {Environment.Version})";
        }
    }

    internal static void Progress(string stage, double fraction, string? message) =>
        Emit(new WorkerMessage { Type = WorkerMessage.ProgressType, Stage = stage, Fraction = Math.Round(fraction, 4), Message = message });

    internal static void EmitWarning(string message) =>
        Emit(new WorkerMessage { Type = WorkerMessage.WarningType, Message = message });

    private static int Fail(WorkerExitCode code, string message)
    {
        Emit(new WorkerMessage { Type = WorkerMessage.ErrorType, Code = (int)code, Message = message });
        return (int)code;
    }

    private static void Emit(WorkerMessage message)
    {
        var line = message.ToJsonLine();
        lock (OutputLock)
        {
            Console.Out.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private const string Usage = """
        RevitModelImporter.Worker - converts 3D models for the Revit Model Importer add-in.

        Usage:
          formats
          analyze <file> [--texture-search <dir>]...
          convert <file> --out <path> [--simplify <ratio|count>] [--units mm|cm|dm|m|km|in|ft|yd]
                  [--up y|z] [--material-mode textured|baked|flat] [--palette-size <n>]
                  [--texture-dir <dir>] [--texture-search <dir>]... [--vertex-tolerance <feet>]
                  [--min-edge <feet>] [--merge]

        Output: JSON lines on stdout. Exit codes: 0 ok, 2 usage, 3 not found, 4 unsupported format,
        5 unreadable/corrupt file, 6 cannot write output, 7 out of memory, 10 unexpected error.
        """;

    private sealed class ProgressSink : IConversionProgress
    {
        public void Report(string stage, double fraction, string? message = null) => Progress(stage, fraction, message);

        public void Warn(string message) => EmitWarning(message);
    }
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class UnsupportedFormatException(string message) : Exception(message);

internal sealed class OutputException(string message, Exception inner) : Exception(message, inner);

/// <summary>Tiny parser: positional arguments plus --name value pairs (repeatable) and --flags.</summary>
internal sealed class CommandLine
{
    private static readonly HashSet<string> Flags = ["--merge"];
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    public List<string> Positional { get; } = [];

    public static CommandLine Parse(string[] args)
    {
        var cl = new CommandLine();
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                if (Flags.Contains(a))
                {
                    cl.Add(a, "true");
                    continue;
                }

                if (i + 1 >= args.Length)
                {
                    throw new UsageException($"Option {a} needs a value.");
                }

                cl.Add(a, args[++i]);
            }
            else
            {
                cl.Positional.Add(a);
            }
        }

        return cl;
    }

    private void Add(string key, string value)
    {
        if (!_values.TryGetValue(key, out var list))
        {
            list = [];
            _values.Add(key, list);
        }

        list.Add(value);
    }

    public bool Has(string key) => _values.ContainsKey(key);

    public string? Get(string key) => _values.TryGetValue(key, out var l) ? l[^1] : null;

    public IReadOnlyList<string> GetAll(string key) => _values.TryGetValue(key, out var l) ? l : [];

    public double? GetDouble(string key)
    {
        var s = Get(key);
        if (s is null)
        {
            return null;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0
            ? v
            : throw new UsageException($"Invalid number for {key}: '{s}'.");
    }

    public int? GetInt(string key)
    {
        var s = Get(key);
        if (s is null)
        {
            return null;
        }

        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new UsageException($"Invalid integer for {key}: '{s}'.");
    }
}
