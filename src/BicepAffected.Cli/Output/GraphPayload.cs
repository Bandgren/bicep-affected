using BicepAffected.Core.Bicep;
using BicepAffected.Core.Domain;

namespace BicepAffected.Cli.Output;

internal sealed record GraphPayload(
    int SchemaVersion,
    IReadOnlyList<GraphNodePayload> Nodes,
    IReadOnlyList<GraphEdgePayload> Edges,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ParseDiagnosticFiles)
{
    public static GraphPayload FromExtraction(GraphExtractionResult extraction)
    {
        var nodes = extraction.Graph.Nodes.Values
            .OrderBy(node => node.Path, StringComparer.Ordinal)
            .ThenBy(node => node.Kind)
            .ThenBy(node => node.IsBicepFile)
            .Select(GraphNodePayload.FromNode)
            .ToArray();
        var edges = extraction.Graph.Edges
            .OrderBy(edge => edge.FromPath, StringComparer.Ordinal)
            .ThenBy(edge => edge.ToPath, StringComparer.Ordinal)
            .ThenBy(edge => edge.Kind)
            .ThenBy(edge => edge.SourceSpan, StringComparer.Ordinal)
            .ThenBy(edge => edge.Detail, StringComparer.Ordinal)
            .Select(GraphEdgePayload.FromEdge)
            .ToArray();

        return new GraphPayload(
            SchemaVersion: 1,
            Nodes: nodes,
            Edges: edges,
            Warnings: extraction.Warnings.OrderBy(warning => warning, StringComparer.Ordinal).ToArray(),
            ParseDiagnosticFiles: extraction.ParseDiagnosticFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray());
    }
}

internal sealed record GraphNodePayload(string Path, string Kind, bool IsBicepFile)
{
    public static GraphNodePayload FromNode(GraphNode node) =>
        new(node.Path, ToCamelCase(node.Kind), node.IsBicepFile);

    private static string ToCamelCase(NodeKind kind) =>
        char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];
}

internal sealed record GraphEdgePayload(string FromPath, string ToPath, string Kind, string? SourceSpan, string? Detail)
{
    public static GraphEdgePayload FromEdge(DependencyEdge edge) =>
        new(edge.FromPath, edge.ToPath, ToCamelCase(edge.Kind), edge.SourceSpan, edge.Detail);

    private static string ToCamelCase(DependencyKind kind) =>
        char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];
}
