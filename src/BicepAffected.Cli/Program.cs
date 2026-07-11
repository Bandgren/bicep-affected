using BicepAffected.Cli.Output;
using BicepAffected.Core.Analysis;
using BicepAffected.Core.Bicep;
using BicepAffected.Core.Config;
using BicepAffected.Core.Domain;
using BicepAffected.Core.Git;
using BicepAffected.Core.IO;

return Cli.Run(args);

internal static class Cli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help", StringComparer.OrdinalIgnoreCase) || args.Contains("-h", StringComparer.OrdinalIgnoreCase))
            {
                PrintHelp();
                return args.Length == 0 ? 1 : 0;
            }

            var command = args[0];
            var options = ParseOptions(args[1..]);

            return command.ToLowerInvariant() switch
            {
                "affected" => RunAffected(options),
                "graph" => RunGraph(options),
                _ => Fail($"Unknown command '{command}'.")
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }

    private static int RunAffected(CliOptions options)
    {
        ValidateAffectedOptions(options);
        var repoRoot = PathNormalizer.NormalizeRoot(options.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(repoRoot, options.ConfigPath);
        var changedFiles = ResolveChangedFiles(repoRoot, options);
        var publishVersionFiles = GetPublishVersionFiles(config, options.PublishVersionFiles);
        var result = new AffectedAnalyzer().Analyze(repoRoot, changedFiles, config, publishVersionFiles);
        var filteredResult = ResultFilter.Apply(result, options.Include);

        var payload = CiPayload.FromResult(filteredResult, repoRoot, publishVersionFiles);
        WriteRenderedOutput(options, RenderAffectedResult(options.Format, filteredResult, payload));
        WriteWarnings(payload.Warnings);

        if (payload.Warnings.Count > 0 && !options.AllowWarnings)
        {
            return 1;
        }

        var hasAffected = filteredResult.Entrypoints.Count > 0
            || filteredResult.PublishableModules.Count > 0
            || filteredResult.Helpers.Count > 0;

        if (options.FailIfAffected && hasAffected)
        {
            return 2;
        }

        if (options.FailIfNone && !hasAffected)
        {
            return 3;
        }

        return 0;
    }

    private static int RunGraph(CliOptions options)
    {
        ValidateGraphOptions(options);

        var repoRoot = PathNormalizer.NormalizeRoot(options.Repo ?? Directory.GetCurrentDirectory());
        var config = ConfigLoader.Load(repoRoot, options.ConfigPath);
        var extraction = new BicepGraphExtractor().Extract(repoRoot, config);
        var nodes = extraction.Graph.Nodes.Values
            .OrderBy(node => node.Path, StringComparer.Ordinal)
            .ThenBy(node => node.Kind)
            .ThenBy(node => node.IsBicepFile)
            .ToArray();
        var edges = extraction.Graph.Edges
            .OrderBy(edge => edge.FromPath, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToPath, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.SourceSpan, StringComparer.Ordinal)
            .ThenBy(edge => edge.Detail, StringComparer.Ordinal)
            .ToArray();
        var warnings = extraction.Warnings.OrderBy(warning => warning, StringComparer.Ordinal).ToArray();
        var payload = GraphPayload.FromExtraction(extraction);

        var rendered = options.Format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? JsonRenderer.RenderGraph(payload)
            : TextRenderer.RenderGraph(nodes, edges, warnings);

        WriteRenderedOutput(options, rendered);
        WriteWarnings(warnings);
        return warnings.Length > 0 && !options.AllowWarnings ? 1 : 0;
    }

    private static void ValidateAffectedOptions(CliOptions options)
    {
        var hasChangedFiles = options.ChangedFiles.Count > 0;
        var hasStdin = options.ChangedFilesStdin;
        var hasFrom = options.FromRefSet;
        var hasTo = options.ToRefSet;
        var hasRefPair = hasFrom && hasTo;
        var modeCount = (hasChangedFiles ? 1 : 0) + (hasStdin ? 1 : 0) + (hasRefPair ? 1 : 0);

        if (hasFrom != hasTo)
        {
            throw new InvalidOperationException("--from and --to must be supplied together.");
        }

        if (hasRefPair && (string.IsNullOrWhiteSpace(options.FromRef) || string.IsNullOrWhiteSpace(options.ToRef)))
        {
            throw new InvalidOperationException("--from and --to must both have non-empty values.");
        }

        if (modeCount != 1)
        {
            throw new InvalidOperationException("Specify exactly one changed-file mode: --changed-file, --changed-files-stdin, or paired --from and --to.");
        }
    }

    private static void ValidateGraphOptions(CliOptions options)
    {
        var ignoredOptions = new List<string>();
        if (options.FromRefSet)
        {
            ignoredOptions.Add("--from");
        }

        if (options.ToRefSet)
        {
            ignoredOptions.Add("--to");
        }

        if (options.ChangedFiles.Count > 0)
        {
            ignoredOptions.Add("--changed-file");
        }

        if (options.ChangedFilesStdin)
        {
            ignoredOptions.Add("--changed-files-stdin");
        }

        if (options.IncludeSet)
        {
            ignoredOptions.Add("--include");
        }

        if (options.PublishVersionFiles.Count > 0)
        {
            ignoredOptions.Add("--publish-version-file");
        }

        if (options.FailIfAffected)
        {
            ignoredOptions.Add("--fail-if-affected");
        }

        if (options.FailIfNone)
        {
            ignoredOptions.Add("--fail-if-none");
        }

        if (ignoredOptions.Count > 0)
        {
            throw new InvalidOperationException($"The graph command does not support: {string.Join(", ", ignoredOptions)}.");
        }

    }

    private static IReadOnlyList<string> ResolveChangedFiles(string repoRoot, CliOptions options)
    {
        if (options.ChangedFilesStdin)
        {
            var stdinFiles = new List<string>();
            string? line;
            while ((line = Console.In.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    stdinFiles.Add(line);
                }
            }

            return stdinFiles
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        if (options.ChangedFiles.Count > 0)
        {
            return options.ChangedFiles
                .Distinct(StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
        }

        return new GitDiffProvider().GetChangedFiles(repoRoot, options.FromRef!, options.ToRef!);
    }

    private static CliOptions ParseOptions(string[] args)
    {
        var options = new CliOptions();
        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            switch (current)
            {
                case "--repo":
                    options.Repo = ReadValue(args, ref index, current);
                    break;
                case "--from":
                    options.FromRefSet = true;
                    options.FromRef = ReadValue(args, ref index, current);
                    break;
                case "--to":
                    options.ToRefSet = true;
                    options.ToRef = ReadValue(args, ref index, current);
                    break;
                case "--changed-file":
                    options.ChangedFiles.Add(ReadValue(args, ref index, current));
                    break;
                case "--changed-files-stdin":
                    options.ChangedFilesStdin = true;
                    break;
                case "--config":
                    options.ConfigPath = ReadValue(args, ref index, current);
                    break;
                case "--format":
                    options.Format = ReadValue(args, ref index, current).ToLowerInvariant();
                    break;
                case "--include":
                    options.IncludeSet = true;
                    options.Include = ParseInclude(ReadValue(args, ref index, current));
                    break;
                case "--publish-version-file":
                    var publishVersionFile = ReadValue(args, ref index, current);
                    ValidatePublishVersionFileName(publishVersionFile);
                    options.PublishVersionFiles.Add(publishVersionFile);
                    break;
                case "--output":
                    options.OutputPath = ReadValue(args, ref index, current);
                    break;
                case "--fail-if-affected":
                    options.FailIfAffected = true;
                    break;
                case "--fail-if-none":
                    options.FailIfNone = true;
                    break;
                case "--allow-warnings":
                    options.AllowWarnings = true;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '{current}'.");
            }
        }

        if (options.Format is not ("text" or "json"))
        {
            throw new InvalidOperationException("--format must be one of: text, json.");
        }

        return options;
    }

    private static string ReadValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"Missing value for {optionName}.");
        }

        index++;
        return args[index];
    }

    private static IncludeFilter ParseInclude(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "all" => IncludeFilter.All,
            "entrypoints" => IncludeFilter.Entrypoints,
            "modules" => IncludeFilter.Modules,
            "helpers" => IncludeFilter.Helpers,
            _ => throw new InvalidOperationException("--include must be one of: all, entrypoints, modules, helpers.")
        };
    }
    private static void ValidatePublishVersionFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.Contains('/')
            || value.Contains('\\')
            || !string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("--publish-version-file must be a simple adjacent filename.");
        }
    }


    private static string RenderAffectedResult(string format, AffectedResult result, CiPayload payload)
    {
        return format.ToLowerInvariant() switch
        {
            "json" => JsonRenderer.Render(payload),
            _ => TextRenderer.Render(result with { Warnings = payload.Warnings })
        };
    }

    private static IReadOnlyList<string> GetPublishVersionFiles(
        BicepAffectedConfig config,
        IEnumerable<string> publishVersionFiles)
    {
        var versionFiles = publishVersionFiles
            .Concat((config.PublishableModules ?? []).Select(rule => rule.Metadata))
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .ToArray();

        foreach (var versionFile in versionFiles)
        {
            ValidatePublishVersionFileName(versionFile);
        }

        return versionFiles
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
    }

    private static void WriteRenderedOutput(CliOptions options, string rendered)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputPath))
        {
            File.WriteAllText(options.OutputPath, rendered + Environment.NewLine);
        }


        Console.WriteLine(rendered);
    }
    private static void WriteWarnings(IEnumerable<string> warnings)
    {
        foreach (var warning in warnings.Where(warning => !string.IsNullOrWhiteSpace(warning)).OrderBy(warning => warning, StringComparer.Ordinal))
        {
            Console.Error.WriteLine($"warning: {warning}");
        }
    }


    private static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("bicep-affected");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  bicep-affected affected [options]");
        Console.WriteLine("  bicep-affected graph [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --repo <path>                 Repo root. Defaults to current directory.");
        Console.WriteLine("  --from <git-ref>              Base ref for git diff.");
        Console.WriteLine("  --to <git-ref>                Target ref for git diff.");
        Console.WriteLine("  --changed-file <path>         Explicit changed file. Repeatable.");
        Console.WriteLine("  --changed-files-stdin         Read changed files from stdin.");
        Console.WriteLine("  --config <path>               Optional bicep-affected.json path.");
        Console.WriteLine("  --format text|json             Output format. Defaults to text.");
        Console.WriteLine("  --include all|entrypoints|modules|helpers");
        Console.WriteLine("  --output <path>               Write rendered output to a file.");
        Console.WriteLine("  --publish-version-file <file> Extra adjacent version file name for publish gating. Repeatable.");
        Console.WriteLine("  --fail-if-affected            Exit 2 when affected items exist.");
        Console.WriteLine("  --fail-if-none                Exit 3 when no affected items exist.");
        Console.WriteLine("  --allow-warnings              Continue successfully when analysis warnings are emitted.");
    }

    private sealed class CliOptions
    {
        public string? Repo { get; set; }

        public string? FromRef { get; set; }

        public bool FromRefSet { get; set; }

        public string? ToRef { get; set; }

        public bool ToRefSet { get; set; }

        public List<string> ChangedFiles { get; } = [];

        public bool ChangedFilesStdin { get; set; }

        public string? ConfigPath { get; set; }

        public string Format { get; set; } = "text";

        public IncludeFilter Include { get; set; } = IncludeFilter.All;

        public bool IncludeSet { get; set; }

        public string? OutputPath { get; set; }

        public List<string> PublishVersionFiles { get; } = [];

        public bool FailIfAffected { get; set; }

        public bool FailIfNone { get; set; }
        public bool AllowWarnings { get; set; }


    }
}
