using BicepAffected.Core.Bicep;
using BicepAffected.Core.Config;
using BicepAffected.Core.Domain;
using BicepAffected.Core.IO;

namespace BicepAffected.Core.Analysis;

public sealed class AffectedAnalyzer
{
    private readonly BicepGraphExtractor graphExtractor = new();

    public AffectedResult Analyze(
        string repoRoot,
        IEnumerable<string> changedFiles,
        BicepAffectedConfig config,
        IEnumerable<string>? publishVersionFiles = null)
    {
        var root = PathNormalizer.NormalizeRoot(repoRoot);
        var versionFiles = GetPublishVersionFiles(config, publishVersionFiles);
        var normalizedChangedFiles = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => PathNormalizer.NormalizeRelativePath(root, path))
            .Distinct(PathNormalizer.PathComparer)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var extraction = graphExtractor.Extract(root, config);
        var graph = extraction.Graph;
        var warnings = extraction.Warnings.ToList();
        var affected = new Dictionary<string, AffectedAccumulator>(PathNormalizer.PathComparer);
        var reverseEdges = graph.Edges
            .Where(edge => edge.Kind is not (DependencyKind.ExternalModule or DependencyKind.DirectoryContent))
            .GroupBy(edge => edge.ToPath, PathNormalizer.PathComparer)
            .ToDictionary(group => group.Key, group => group.ToArray(), PathNormalizer.PathComparer);
        var directoryContentEdges = new DirectoryContentIndex(graph.Edges
            .Where(edge => edge.Kind == DependencyKind.DirectoryContent));

        if (extraction.ParseDiagnosticFiles.Count > 0)
        {
            MarkParseDiagnosticsImpact(graph, reverseEdges, directoryContentEdges, affected, extraction.ParseDiagnosticFiles);
        }

        foreach (var changedFile in normalizedChangedFiles)
        {
            if (IsGlobalImpactFile(config, changedFile))
            {
                MarkGlobalImpact(changedFile, graph, affected);
                continue;
            }

            if (IsPublishVersionFile(versionFiles, changedFile))
            {
                MarkPublishVersionFileImpact(changedFile, graph, affected, IsMetadataFile(config, changedFile) ? "metadataChange" : "versionFileChange");
            }

            if (IsParameterFile(changedFile))
            {
                MarkParameterFileImpact(changedFile, graph, affected);
            }

            MarkDirectChange(changedFile, graph, affected, warnings);
            WalkReverseDependencies(changedFile, graph, reverseEdges, directoryContentEdges, affected);
        }

        return new AffectedResult(
            normalizedChangedFiles,
            BuildItems(affected, NodeKind.Entrypoint),
            BuildItems(affected, NodeKind.PublishableModule),
            BuildItems(affected, NodeKind.Helper),
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void MarkDirectChange(
        string changedFile,
        RepoGraph graph,
        IDictionary<string, AffectedAccumulator> affected,
        ICollection<string> warnings)
    {
        var node = graph.FindNode(changedFile);
        if (node is null)
        {
            if (changedFile.EndsWith(".bicep", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Changed Bicep file '{changedFile}' is not present in the current graph; reverse dependencies may be incomplete.");
            }

            return;
        }

        AddReason(
            affected,
            node,
            new Reason(
                "directChange",
                changedFile,
                [changedFile],
                "File changed directly."));
    }

    private static void MarkPublishVersionFileImpact(
        string changedFile,
        RepoGraph graph,
        IDictionary<string, AffectedAccumulator> affected,
        string reasonKind)
    {
        var metadataDirectory = Path.GetDirectoryName(changedFile)?.Replace('\\', '/') ?? string.Empty;
        foreach (var node in graph.Nodes.Values.Where(node => node.Kind == NodeKind.PublishableModule))
        {
            var nodeDirectory = Path.GetDirectoryName(node.Path)?.Replace('\\', '/') ?? string.Empty;
            if (!PathNormalizer.PathComparer.Equals(metadataDirectory, nodeDirectory))
            {
                continue;
            }

            AddReason(
                affected,
                node,
                new Reason(
                    reasonKind,
                    changedFile,
                    [changedFile, node.Path],
                    "Adjacent publish version file changed for this publishable module."));
        }
    }

    private static void MarkParameterFileImpact(
        string changedFile,
        RepoGraph graph,
        IDictionary<string, AffectedAccumulator> affected)
    {
        foreach (var targetPath in GetConventionalBicepTargets(changedFile))
        {
            if (graph.FindNode(targetPath) is not { IsBicepFile: true } node)
            {
                continue;
            }

            AddReason(
                affected,
                node,
                new Reason(
                    "parameterFileChange",
                    changedFile,
                    [changedFile, node.Path],
                    "Parameter file changed for this Bicep file."));
        }
    }

    private static void MarkGlobalImpact(
        string changedFile,
        RepoGraph graph,
        IDictionary<string, AffectedAccumulator> affected)
    {
        foreach (var node in graph.Nodes.Values.Where(node => node.Kind is NodeKind.Entrypoint or NodeKind.PublishableModule))
        {
            AddReason(
                affected,
                node,
                new Reason(
                    "globalImpact",
                    changedFile,
                    [changedFile, node.Path],
                    "Global Bicep configuration changed."));
        }
    }

    private static void MarkParseDiagnosticsImpact(
        RepoGraph graph,
        IReadOnlyDictionary<string, DependencyEdge[]> reverseEdges,
        DirectoryContentIndex directoryContentEdges,
        IDictionary<string, AffectedAccumulator> affected,
        IReadOnlyList<string> parseDiagnosticFiles)
    {
        foreach (var parseDiagnosticFile in parseDiagnosticFiles.Order(PathNormalizer.PathComparer))
        {
            var reachedBuildableNode = false;
            if (graph.FindNode(parseDiagnosticFile) is { IsBicepFile: true } node)
            {
                AddReason(
                    affected,
                    node,
                    new Reason(
                        "parseDiagnostics",
                        parseDiagnosticFile,
                        [parseDiagnosticFile],
                        "Bicep parse diagnostics were found in this file."));
                reachedBuildableNode = IsBuildable(node);
            }

            reachedBuildableNode |= WalkReverseDependencies(
                parseDiagnosticFile,
                graph,
                reverseEdges,
                directoryContentEdges,
                affected,
                "parseDiagnostics",
                "Affected because Bicep parse diagnostics were found in a dependency.");

            if (!reachedBuildableNode)
            {
                MarkParseDiagnosticsFallback(parseDiagnosticFile, graph, affected);
            }
        }
    }

    private static void MarkParseDiagnosticsFallback(
        string parseDiagnosticFile,
        RepoGraph graph,
        IDictionary<string, AffectedAccumulator> affected)
    {
        foreach (var node in graph.Nodes.Values
            .Where(IsBuildable)
            .OrderBy(node => node.Path, PathNormalizer.PathComparer))
        {
            AddReason(
                affected,
                node,
                new Reason(
                    "parseDiagnosticsFallback",
                    parseDiagnosticFile,
                    [parseDiagnosticFile, node.Path],
                    "Bicep parse diagnostics did not reach a buildable node; conservatively affecting all buildable nodes."));
        }
    }

    private static bool WalkReverseDependencies(
        string changedFile,
        RepoGraph graph,
        IReadOnlyDictionary<string, DependencyEdge[]> reverseEdges,
        DirectoryContentIndex directoryContentEdges,
        IDictionary<string, AffectedAccumulator> affected,
        string reasonKind = "reverseDependency",
        string? reasonMessage = null)
    {
        var queue = new Queue<IReadOnlyList<string>>();
        var visited = new HashSet<string>(PathNormalizer.PathComparer) { changedFile };
        queue.Enqueue([changedFile]);

        var reachedBuildableNode = false;
        while (queue.Count > 0)
        {
            var chain = queue.Dequeue();
            var current = chain[^1];
            var dependents = GetDependents(current, reverseEdges, directoryContentEdges).ToArray();
            if (dependents.Length == 0)
            {
                continue;
            }

            foreach (var edge in dependents.OrderBy(edge => edge.FromPath, StringComparer.OrdinalIgnoreCase))
            {
                if (!visited.Add(edge.FromPath))
                {
                    continue;
                }

                var nextChain = chain.Concat([edge.FromPath]).ToArray();
                if (graph.FindNode(edge.FromPath) is { IsBicepFile: true } node)
                {
                    AddReason(
                        affected,
                        node,
                        new Reason(
                            reasonKind,
                            changedFile,
                            nextChain,
                            reasonMessage ?? $"Affected through {FormatDependencyKind(edge.Kind)} dependency."));
                    reachedBuildableNode |= IsBuildable(node);
                }

                queue.Enqueue(nextChain);
            }
        }

        return reachedBuildableNode;
    }

    private static IEnumerable<DependencyEdge> GetDependents(
        string path,
        IReadOnlyDictionary<string, DependencyEdge[]> reverseEdges,
        DirectoryContentIndex directoryContentEdges)
    {
        if (reverseEdges.TryGetValue(path, out var exactDependents))
        {
            foreach (var dependent in exactDependents)
            {
                yield return dependent;
            }
        }

        foreach (var edge in directoryContentEdges.GetCandidates(path))
        {
            if (MatchesPattern(edge.ToPath, path))
            {
                yield return edge;
            }
        }
    }

    private static IReadOnlyList<AffectedItem> BuildItems(
        IReadOnlyDictionary<string, AffectedAccumulator> affected,
        NodeKind kind)
    {
        return affected.Values
            .Where(item => item.Node.Kind == kind)
            .OrderBy(item => item.Node.Path, StringComparer.OrdinalIgnoreCase)
            .Select(item => new AffectedItem(
                item.Node.Path,
                item.Node.Kind,
                item.Reasons
                    .OrderBy(reason => reason.Kind, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(reason => reason.CausedBy, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(reason => string.Join('\0', reason.Chain), StringComparer.Ordinal)
                    .ToArray()))
            .ToArray();
    }

    private static void AddReason(
        IDictionary<string, AffectedAccumulator> affected,
        GraphNode node,
        Reason reason)
    {
        if (node.Kind is not (NodeKind.Entrypoint or NodeKind.PublishableModule or NodeKind.Helper))
        {
            return;
        }

        if (!affected.TryGetValue(node.Path, out var item))
        {
            item = new AffectedAccumulator(node);
            affected[node.Path] = item;
        }

        if (!item.Reasons.Any(existing => AreSameReason(existing, reason)))
        {
            item.Reasons.Add(reason);
        }
    }

    private static bool AreSameReason(Reason left, Reason right)
    {
        return string.Equals(left.Kind, right.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.CausedBy, right.CausedBy, StringComparison.OrdinalIgnoreCase)
            && left.Chain.SequenceEqual(right.Chain, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGlobalImpactFile(BicepAffectedConfig config, string path)
    {
        return (config.GlobalImpactFiles ?? []).Any(pattern => MatchesPattern(pattern, path));
    }

    private static bool IsMetadataFile(BicepAffectedConfig config, string path)
    {
        var fileName = Path.GetFileName(path);
        return (config.PublishableModules ?? []).Any(rule => PathNormalizer.PathComparer.Equals(rule.Metadata, fileName));
    }

    private static bool IsPublishVersionFile(IReadOnlyCollection<string> publishVersionFiles, string path)
    {
        var fileName = Path.GetFileName(path);
        return publishVersionFiles.Any(versionFile => PathNormalizer.PathComparer.Equals(versionFile, fileName));
    }

    private static IReadOnlyCollection<string> GetPublishVersionFiles(
        BicepAffectedConfig config,
        IEnumerable<string>? publishVersionFiles)
    {
        return (publishVersionFiles ?? [])
            .Concat((config.PublishableModules ?? []).Select(rule => rule.Metadata))
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(PathNormalizer.PathComparer)
            .ToArray();
    }

    private static bool MatchesPattern(string pattern, string path)
    {
        return pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal)
            ? GlobPattern.IsMatch(pattern, path)
            : PathNormalizer.PathComparer.Equals(PathNormalizer.NormalizeSeparators(pattern), path);
    }

    private static string FormatDependencyKind(DependencyKind kind)
    {
        return kind switch
        {
            DependencyKind.LocalModule => "local module",
            DependencyKind.CompileTimeImport => "compile-time import",
            DependencyKind.ContentLoad => "content-load",
            DependencyKind.DirectoryContent => "directory content-load",
            DependencyKind.ParameterFile => "parameter-file",
            DependencyKind.GlobalConfig => "global config",
            DependencyKind.ExternalModule => "external module",
            _ => kind.ToString()
        };
    }

    private static bool IsParameterFile(string path)
    {
        return path.EndsWith(".bicepparam", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".parameters.json", StringComparison.OrdinalIgnoreCase);
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

    private static bool IsBuildable(GraphNode node)
    {
        return node.Kind is NodeKind.Entrypoint or NodeKind.PublishableModule;
    }

    private sealed class DirectoryContentIndex
    {
        private readonly IReadOnlyDictionary<string, DependencyEdge[]> edgesByDirectoryPrefix;

        public DirectoryContentIndex(IEnumerable<DependencyEdge> directoryContentEdges)
        {
            edgesByDirectoryPrefix = directoryContentEdges
                .GroupBy(edge => GetDirectoryPrefix(edge.ToPath), PathNormalizer.PathComparer)
                .ToDictionary(group => group.Key, group => group.ToArray(), PathNormalizer.PathComparer);
        }

        public IEnumerable<DependencyEdge> GetCandidates(string path)
        {
            var directory = GetDirectoryPrefix(PathNormalizer.NormalizeSeparators(path).TrimStart('/'));
            while (true)
            {
                if (edgesByDirectoryPrefix.TryGetValue(directory, out var edges))
                {
                    foreach (var edge in edges)
                    {
                        yield return edge;
                    }
                }

                if (directory.Length == 0)
                {
                    yield break;
                }

                var separator = directory.LastIndexOf('/');
                directory = separator < 0 ? string.Empty : directory[..separator];
            }
        }

        private static string GetDirectoryPrefix(string pattern)
        {
            var normalizedPattern = PathNormalizer.NormalizeSeparators(pattern).TrimStart('/');
            var wildcard = normalizedPattern.IndexOfAny(['*', '?']);
            var literalPrefix = wildcard >= 0 ? normalizedPattern[..wildcard] : normalizedPattern;
            var separator = literalPrefix.LastIndexOf('/');
            return separator < 0 ? string.Empty : literalPrefix[..separator].TrimEnd('/');
        }
    }

    private sealed class AffectedAccumulator(GraphNode node)
    {
        public GraphNode Node { get; } = node;

        public List<Reason> Reasons { get; } = [];
    }
}
