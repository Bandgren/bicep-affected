using System.Text.Json;
using Bicep.Core.Parsing;
using Bicep.Core.Features;
using Bicep.Core.Syntax;
using Bicep.Core.Syntax.Visitors;
using Bicep.IO.Abstraction;
using BicepAffected.Core.Config;
using BicepAffected.Core.Domain;
using BicepAffected.Core.IO;

namespace BicepAffected.Core.Bicep;

public sealed partial class BicepGraphExtractor
{
    private static readonly EnumerationOptions TopLevelEnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0
    };

    public GraphExtractionResult Extract(string repoRoot, BicepAffectedConfig config)
    {
        var root = PathNormalizer.NormalizeRoot(repoRoot);
        var warnings = new List<string>();
        var parseDiagnosticFiles = new HashSet<string>(PathNormalizer.PathComparer);
        var nodes = new Dictionary<string, GraphNode>(PathNormalizer.PathComparer);
        var edges = new List<DependencyEdge>();
        var globalImpactPatterns = (config.GlobalImpactFiles ?? [])
            .Select(pattern => new GlobPattern(pattern))
            .ToArray();
        var repositoryFiles = EnumerateFiles(root, warnings)
            .Select(path => PathNormalizer.NormalizeSeparators(Path.GetRelativePath(root, path)))
            .Where(path => IsRelevantRepositoryFile(path, globalImpactPatterns))
            .Distinct(PathNormalizer.PathComparer)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        foreach (var relativePath in repositoryFiles.Where(path => path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase)))
        {
            nodes[relativePath] = new GraphNode(relativePath, NodeKind.UnknownBicepFile, IsBicepFile: true);
        }

        foreach (var globalFile in EnumerateGlobalImpactFiles(repositoryFiles, globalImpactPatterns))
        {
            nodes[globalFile] = new GraphNode(globalFile, NodeKind.ConfigFile, IsBicepFile: false);
        }

        foreach (var parameterFile in EnumerateParameterFiles(repositoryFiles))
        {
            nodes[parameterFile] = new GraphNode(parameterFile, NodeKind.ContentFile, IsBicepFile: false);
        }

        foreach (var sourcePath in nodes.Values.Where(node => node.IsBicepFile).Select(node => node.Path).Order(StringComparer.OrdinalIgnoreCase).ToArray())
        {
            ExtractDependencies(root, sourcePath, nodes, edges, warnings, parseDiagnosticFiles);
        }

        AddParameterFileDependencies(root, nodes, edges, warnings, parseDiagnosticFiles);

        var classifiedNodes = ClassifyNodes(root, nodes, edges, config, warnings);
        return new GraphExtractionResult(new RepoGraph(classifiedNodes, edges), warnings, parseDiagnosticFiles.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static IEnumerable<string> EnumerateFiles(string root, ICollection<string> warnings)
    {
        var directories = new Stack<string>();
        directories.Push(root);

        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory, "*", TopLevelEnumerationOptions)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                var relativeDirectory = PathNormalizer.NormalizeSeparators(Path.GetRelativePath(root, directory));
                warnings.Add($"Unable to enumerate repository directory '{relativeDirectory}': {exception.Message}");
                continue;
            }

            foreach (var entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    var relativeEntry = PathNormalizer.NormalizeSeparators(Path.GetRelativePath(root, entry));
                    warnings.Add($"Unable to inspect repository entry '{relativeEntry}': {exception.Message}");
                    continue;
                }

                var relativePath = PathNormalizer.NormalizeSeparators(Path.GetRelativePath(root, entry));
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add($"Unsupported symbolic link or reparse point '{relativePath}' was not analyzed.");
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!HasSkippedSegment(relativePath))
                    {
                        directories.Push(entry);
                    }

                    continue;
                }

                yield return entry;
            }
        }
    }

    private static bool HasSkippedSegment(string relativePath)
    {
        var segments = PathNormalizer.NormalizeSeparators(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Any(segment => segment is
            ".git" or
            ".cache" or
            ".terraform" or
            ".terragrunt-cache" or
            ".venv" or
            "__pycache__" or
            "node_modules" or
            "bin" or
            "obj" or
            "artifacts" or
            "TestResults");
    }

    private static IEnumerable<string> EnumerateGlobalImpactFiles(
        IReadOnlyList<string> repositoryFiles,
        IReadOnlyList<GlobPattern> globalImpactPatterns)
    {
        return repositoryFiles.Where(path => globalImpactPatterns.Any(pattern => pattern.IsMatch(path)));
    }

    private static bool IsRelevantRepositoryFile(string path, IReadOnlyList<GlobPattern> globalImpactPatterns) =>
        path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".parameters.json", StringComparison.OrdinalIgnoreCase) ||
        globalImpactPatterns.Any(pattern => pattern.IsMatch(path));

    private static void ExtractDependencies(
        string root,
        string sourcePath,
        IDictionary<string, GraphNode> nodes,
        ICollection<DependencyEdge> edges,
        ICollection<string> warnings,
        ISet<string> parseDiagnosticFiles)
    {
        var absoluteSourcePath = Path.Combine(root, sourcePath);
        if (!File.Exists(absoluteSourcePath))
        {
            warnings.Add($"Bicep file '{sourcePath}' was discovered but no longer exists.");
            return;
        }

        var content = File.ReadAllText(absoluteSourcePath);

        var program = new Parser(content).Program();
        if (AddParseDiagnostics(sourcePath, content, program, warnings))
        {
            parseDiagnosticFiles.Add(sourcePath);
        }

        foreach (var module in SyntaxAggregator.AggregateByType<ModuleDeclarationSyntax>(program, true))
        {
            AddBicepDependency(root, sourcePath, content, module.Path, DependencyKind.LocalModule, nodes, edges, warnings);
        }

        foreach (var import in SyntaxAggregator.AggregateByType<CompileTimeImportDeclarationSyntax>(program, true))
        {
            var pathSyntax = import.FromClause is CompileTimeImportFromClauseSyntax fromClause
                ? fromClause.Path
                : import.ImportExpression;

            AddBicepDependency(root, sourcePath, content, pathSyntax, DependencyKind.CompileTimeImport, nodes, edges, warnings);
        }

        foreach (var functionCall in SyntaxAggregator.AggregateByType<FunctionCallSyntax>(program, true))
        {
            AddContentDependency(root, sourcePath, content, functionCall, nodes, edges, warnings);
        }
        foreach (var functionCall in SyntaxAggregator.AggregateByType<InstanceFunctionCallSyntax>(program, true))
        {
            AddContentDependency(root, sourcePath, content, functionCall, nodes, edges, warnings);
        }
    }

    private static void AddBicepDependency(
        string root,
        string sourcePath,
        string content,
        SyntaxBase pathSyntax,
        DependencyKind dependencyKind,
        IDictionary<string, GraphNode> nodes,
        ICollection<DependencyEdge> edges,
        ICollection<string> warnings)
    {
        if (TryGetLiteralString(pathSyntax) is not { } rawReference)
        {
            warnings.Add($"Unsupported dynamic Bicep dependency in '{sourcePath}' at line {LineNumber(content, pathSyntax.Span.Position)}.");
            return;
        }

        if (IsExternalReference(rawReference))
        {
            edges.Add(new DependencyEdge(sourcePath, rawReference, DependencyKind.ExternalModule, ToSourceSpan(sourcePath, content, pathSyntax), rawReference));
            return;
        }

        var targetPath = PathNormalizer.ResolveDependencyPath(root, sourcePath, rawReference);
        if (targetPath is null)
        {
            warnings.Add($"Ignored dependency outside repo root from '{sourcePath}' to '{rawReference}'.");
            return;
        }

        edges.Add(new DependencyEdge(sourcePath, targetPath, dependencyKind, ToSourceSpan(sourcePath, content, pathSyntax), rawReference));

        if (!nodes.ContainsKey(targetPath))
        {
            nodes[targetPath] = new GraphNode(targetPath, NodeKind.Helper, IsBicepFile: targetPath.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase));
        }

        if (!File.Exists(Path.Combine(root, targetPath)))
        {
            warnings.Add($"Missing local Bicep dependency '{targetPath}' referenced from '{sourcePath}'.");
        }
    }

    private static void AddContentDependency(
        string root,
        string sourcePath,
        string content,
        FunctionCallSyntaxBase functionCall,
        IDictionary<string, GraphNode> nodes,
        ICollection<DependencyEdge> edges,
        ICollection<string> warnings)
    {
        var functionName = GetContentLoadFunctionName(functionCall);
        if (functionName is null)
        {
            return;
        }

        if (functionCall.Arguments.Length == 0)
        {
            warnings.Add($"Content load without arguments in '{sourcePath}' at line {LineNumber(content, functionCall.Span.Position)}.");
            return;
        }

        var argumentExpression = functionCall.Arguments[0].Expression;
        if (TryGetLiteralString(argumentExpression) is not { } rawReference)
        {
            warnings.Add($"Unsupported dynamic content load in '{sourcePath}' at line {LineNumber(content, functionCall.Span.Position)}.");
            return;
        }

        if (functionName == "loadDirectoryFileInfo")
        {
            AddDirectoryContentDependency(root, sourcePath, content, functionCall, rawReference, argumentExpression, edges, warnings);
            return;
        }

        var targetPath = PathNormalizer.ResolveDependencyPath(root, sourcePath, rawReference);
        if (targetPath is null)
        {
            warnings.Add($"Ignored content dependency outside repo root from '{sourcePath}' to '{rawReference}'.");
            return;
        }

        edges.Add(new DependencyEdge(sourcePath, targetPath, DependencyKind.ContentLoad, ToSourceSpan(sourcePath, content, argumentExpression), rawReference));

        if (!nodes.ContainsKey(targetPath))
        {
            nodes[targetPath] = new GraphNode(targetPath, NodeKind.ContentFile, IsBicepFile: false);
        }

        if (!File.Exists(Path.Combine(root, targetPath)))
        {
            warnings.Add($"Missing content dependency '{targetPath}' referenced from '{sourcePath}'.");
        }
    }

    private static void AddDirectoryContentDependency(
        string root,
        string sourcePath,
        string content,
        FunctionCallSyntaxBase functionCall,
        string rawDirectoryReference,
        SyntaxBase argumentExpression,
        ICollection<DependencyEdge> edges,
        ICollection<string> warnings)
    {
        var directoryPath = PathNormalizer.ResolveDependencyPath(root, sourcePath, rawDirectoryReference);
        if (directoryPath is null)
        {
            warnings.Add($"Ignored directory content dependency outside repo root from '{sourcePath}' to '{rawDirectoryReference}'.");
            return;
        }

        var searchPattern = "*";
        if (functionCall.Arguments.Length > 1)
        {
            var searchPatternExpression = functionCall.Arguments[1].Expression;
            if (TryGetLiteralString(searchPatternExpression) is not { } rawSearchPattern)
            {
                warnings.Add($"Unsupported dynamic directory search pattern in '{sourcePath}' at line {LineNumber(content, functionCall.Span.Position)}.");
                return;
            }

            searchPattern = string.IsNullOrWhiteSpace(rawSearchPattern) ? "*" : rawSearchPattern;
        }

        var normalizedDirectory = directoryPath.TrimEnd('/');
        var dependencyPattern = PathNormalizer.NormalizeSeparators(Path.Combine(normalizedDirectory, searchPattern));
        edges.Add(new DependencyEdge(sourcePath, dependencyPattern, DependencyKind.DirectoryContent, ToSourceSpan(sourcePath, content, argumentExpression), rawDirectoryReference));

        if (!Directory.Exists(Path.Combine(root, normalizedDirectory)))
        {
            warnings.Add($"Missing directory content dependency '{normalizedDirectory}' referenced from '{sourcePath}'.");
        }
    }

    private static IEnumerable<string> EnumerateParameterFiles(IEnumerable<string> repositoryFiles)
    {
        return repositoryFiles
            .Where(path => path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".parameters.json", StringComparison.OrdinalIgnoreCase))
            .Distinct(PathNormalizer.PathComparer)
            .Order(StringComparer.Ordinal);
    }

    private static void AddParameterFileDependencies(
        string root,
        IReadOnlyDictionary<string, GraphNode> nodes,
        ICollection<DependencyEdge> edges,
        ICollection<string> warnings,
        ISet<string> parseDiagnosticFiles)
    {
        var bicepFiles = nodes.Values
            .Where(node => node.IsBicepFile)
            .Select(node => node.Path)
            .ToHashSet(PathNormalizer.PathComparer);

        foreach (var parameterFile in nodes.Values.Where(node => node.Kind == NodeKind.ContentFile && IsParameterFile(node.Path)))
        {
            foreach (var targetBicep in GetBicepTargetsForParameterFile(root, parameterFile.Path, bicepFiles, warnings, parseDiagnosticFiles).Where(bicepFiles.Contains))
            {
                edges.Add(new DependencyEdge(targetBicep, parameterFile.Path, DependencyKind.ParameterFile, Detail: "parameter file"));
            }
        }
    }

    private static IEnumerable<string> GetBicepTargetsForParameterFile(
        string root,
        string parameterFile,
        IReadOnlySet<string> bicepFiles,
        ICollection<string> warnings,
        ISet<string> parseDiagnosticFiles)
    {
        if (parameterFile.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase))
        {
            var targets = GetBicepParamUsingTargets(root, parameterFile, warnings, parseDiagnosticFiles).ToArray();
            if (targets.Length > 0)
            {
                return targets;
            }
        }

        return GetConventionalBicepTargets(parameterFile).Where(bicepFiles.Contains);
    }

    private static IEnumerable<string> GetBicepParamUsingTargets(string root, string parameterFile, ICollection<string> warnings, ISet<string> parseDiagnosticFiles)
    {
        var absoluteParameterFile = Path.Combine(root, parameterFile);
        if (!File.Exists(absoluteParameterFile))
        {
            yield break;
        }

        var content = File.ReadAllText(absoluteParameterFile);
        var parser = new ParamsParser(content, DisabledFeatureProvider.Instance);
        var program = parser.Program();
        if (AddParseDiagnostics(parameterFile, content, program, warnings))
        {
            parseDiagnosticFiles.Add(parameterFile);
        }

        foreach (var usingDeclaration in SyntaxAggregator.AggregateByType<UsingDeclarationSyntax>(program, true))
        {
            if (TryGetLiteralString(usingDeclaration.Path) is not { } rawReference)
            {
                warnings.Add($"Unsupported dynamic using declaration in '{parameterFile}' at line {LineNumber(content, usingDeclaration.Path.Span.Position)}.");
                continue;
            }

            if (IsExternalReference(rawReference))
            {
                continue;
            }

            var targetPath = PathNormalizer.ResolveDependencyPath(root, parameterFile, rawReference);
            if (targetPath is null)
            {
                warnings.Add($"Ignored using declaration outside repo root from '{parameterFile}' to '{rawReference}'.");
                continue;
            }

            yield return targetPath;
        }
    }

    private static IEnumerable<string> GetConventionalBicepTargets(string parameterFile)
    {
        var directory = Path.GetDirectoryName(parameterFile)?.Replace('\\', '/') ?? string.Empty;
        var fileName = Path.GetFileName(parameterFile);

        if (fileName.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase))
        {
            yield return PathNormalizer.NormalizeSeparators(Path.Combine(directory, Path.GetFileNameWithoutExtension(fileName) + ".bicep"));
        }

        const string parametersJsonSuffix = ".parameters.json";
        if (fileName.EndsWith(parametersJsonSuffix, StringComparison.OrdinalIgnoreCase))
        {
            var stem = fileName[..^parametersJsonSuffix.Length];
            yield return PathNormalizer.NormalizeSeparators(Path.Combine(directory, stem + ".bicep"));

            var lastDot = stem.LastIndexOf('.');
            if (lastDot > 0)
            {
                yield return PathNormalizer.NormalizeSeparators(Path.Combine(directory, stem[..lastDot] + ".bicep"));
            }
        }
    }

    private static bool IsParameterFile(string path)
    {
        return path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".parameters.json", StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, GraphNode> ClassifyNodes(
        string root,
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyCollection<DependencyEdge> edges,
        BicepAffectedConfig config,
        ICollection<string> warnings)
    {
        var referencedBicepFiles = edges
            .Where(edge => edge.Kind is DependencyKind.LocalModule or DependencyKind.CompileTimeImport)
            .Select(edge => edge.ToPath)
            .Where(path => path.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(PathNormalizer.PathComparer);

        var classified = new Dictionary<string, GraphNode>(PathNormalizer.PathComparer);
        var entrypoints = config.Entrypoints ?? [];
        var helpers = config.Helpers ?? [];
        var hasConfiguredEntrypoints = entrypoints.Count > 0;

        foreach (var node in nodes.Values.OrderBy(node => node.Path, StringComparer.OrdinalIgnoreCase))
        {
            if (!node.IsBicepFile)
            {
                classified[node.Path] = node;
                continue;
            }

            var kind = node.Kind;
            if (!File.Exists(Path.Combine(root, node.Path)))
            {
                kind = NodeKind.Helper;
            }
            else if (IsPublishableModule(root, node.Path, config, warnings))
            {
                kind = NodeKind.PublishableModule;
            }
            else if (hasConfiguredEntrypoints && GlobPattern.MatchesAny(entrypoints, node.Path))
            {
                kind = NodeKind.Entrypoint;
            }
            else if (GlobPattern.MatchesAny(helpers, node.Path))
            {
                kind = NodeKind.Helper;
            }
            else if (referencedBicepFiles.Contains(node.Path))
            {
                kind = NodeKind.Helper;
            }
            else
            {
                kind = NodeKind.Entrypoint;
            }

            classified[node.Path] = node with { Kind = kind };
        }

        return classified;
    }

    private static bool IsExternalReference(string reference)
    {
        return reference.StartsWith("br:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("br/", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("ts:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("oci://", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetLiteralString(SyntaxBase syntax)
    {
        return syntax is StringSyntax stringSyntax
            ? stringSyntax.TryGetLiteralValue()
            : null;
    }

    private static string? GetContentLoadFunctionName(FunctionCallSyntaxBase functionCall)
    {
        var name = functionCall.Name.IdentifierName;
        if (functionCall is InstanceFunctionCallSyntax instanceFunctionCall)
        {
            if (instanceFunctionCall.BaseExpression is not VariableAccessSyntax namespaceAccess ||
                !namespaceAccess.Name.IdentifierName.Equals("sys", StringComparison.Ordinal))
            {
                return null;
            }
        }

        return IsContentLoadFunction(name) ? name : null;
    }

    private static bool IsContentLoadFunction(string functionName) =>
        functionName is "loadTextContent" or "loadJsonContent" or "loadYamlContent" or "loadFileAsBase64" or "loadDirectoryFileInfo";

    private static bool AddParseDiagnostics(
        string sourcePath,
        string content,
        ProgramSyntax program,
        ICollection<string> warnings)
    {
        var hasDiagnostics = false;
        foreach (var skippedTrivia in SyntaxAggregator.AggregateByType<SkippedTriviaSyntax>(program, true))
        {
            foreach (var diagnostic in skippedTrivia.Diagnostics)
            {
                hasDiagnostics = true;
                warnings.Add($"Parse diagnostic in '{sourcePath}' at line {LineNumber(content, skippedTrivia.Span.Position)}: {diagnostic.Message}");
            }
        }

        return hasDiagnostics;
    }

    private static string ToSourceSpan(string sourcePath, string content, SyntaxBase syntax)
    {
        return $"{sourcePath}:{LineNumber(content, syntax.Span.Position)}";
    }

    private static int LineNumber(string content, int index)
    {
        var line = 1;
        for (var i = 0; i < index; i++)
        {
            if (content[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    private static bool IsPublishableModule(string root, string bicepPath, BicepAffectedConfig config, ICollection<string> warnings)
    {
        if (!File.Exists(Path.Combine(root, bicepPath)))
        {
            return false;
        }

        foreach (var rule in config.PublishableModules ?? [])
        {
            if (!new GlobPattern(rule.Path).IsMatch(bicepPath))
            {
                continue;
            }

            if (!IsAdjacentFileName(rule.Metadata))
            {
                warnings.Add($"Invalid publish metadata '{PathNormalizer.NormalizeSeparators(rule.Metadata)}'; treating matching Bicep files in the same directory as publishable.");
                return true;
            }

            var metadataPath = PathNormalizer.ResolveDependencyPath(root, bicepPath, rule.Metadata);
            var metadata = metadataPath is null
                ? InvalidMetadata(rule.Metadata, warnings)
                : ReadMetadataNames(root, metadataPath, warnings);
            if (metadata.IsInvalid || metadata.Names.Contains(Path.GetFileNameWithoutExtension(bicepPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAdjacentFileName(string metadata)
    {
        var normalized = PathNormalizer.NormalizeSeparators(metadata);
        return !string.IsNullOrWhiteSpace(normalized) &&
            normalized is not "." and not ".." &&
            !Path.IsPathRooted(normalized) &&
            Path.GetFileName(normalized) == normalized;
    }

    private static MetadataNamesResult ReadMetadataNames(string root, string metadataPath, ICollection<string> warnings)
    {
        var normalizedPath = PathNormalizer.NormalizeSeparators(metadataPath);
        var absolutePath = Path.Combine(root, normalizedPath);

        try
        {
            using var stream = File.OpenRead(absolutePath);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return InvalidMetadata(normalizedPath, warnings);
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameProperty) ||
                    nameProperty.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(nameProperty.GetString()))
                {
                    return InvalidMetadata(normalizedPath, warnings);
                }

                names.Add(nameProperty.GetString()!);
            }

            return new MetadataNamesResult(names, IsInvalid: false);
        }
        catch (FileNotFoundException)
        {
            return new MetadataNamesResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IsInvalid: false);
        }
        catch (DirectoryNotFoundException)
        {
            return new MetadataNamesResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IsInvalid: false);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return InvalidMetadata(normalizedPath, warnings);
        }
    }

    private static MetadataNamesResult InvalidMetadata(string metadataPath, ICollection<string> warnings)
    {
        warnings.Add($"Invalid publish metadata '{PathNormalizer.NormalizeSeparators(metadataPath)}'; treating matching Bicep files in the same directory as publishable.");
        return new MetadataNamesResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase), IsInvalid: true);
    }

    private sealed record MetadataNamesResult(HashSet<string> Names, bool IsInvalid);

    private sealed class DisabledFeatureProvider : IFeatureProvider
    {
        public static readonly DisabledFeatureProvider Instance = new();

        private DisabledFeatureProvider()
        {
        }

        public string AssemblyVersion => typeof(Parser).Assembly.GetName().Version?.ToString() ?? "0.0.0";

        public bool AssertsEnabled => false;

        public IDirectoryHandle CacheRootDirectory => null!;

        public bool DeployCommandsEnabled => false;

        public IEnumerable<(string, bool, bool)> EnabledFeatureMetadata => [];

        public bool LegacyFormatterEnabled => false;

        public bool LocalDeployEnabled => false;

        public bool ModuleExtensionConfigsEnabled => false;

        public bool ResourceInfoCodegenEnabled => false;

        public bool ResourceTypedParamsAndOutputsEnabled => false;

        public bool SourceMappingEnabled => false;

        public bool SymbolicNameCodegenEnabled => false;

        public bool TestFrameworkEnabled => false;

        public bool UserDefinedConstraintsEnabled => false;

        public bool WaitAndRetryEnabled => false;
    }
}
