using System.Text;
using BicepAffected.Core.Domain;

namespace BicepAffected.Cli.Output;

internal static class TextRenderer
{
    public static string Render(AffectedResult result)
    {
        var builder = new StringBuilder();

        AppendSection(builder, "Changed files", result.ChangedFiles);
        AppendAffectedSection(builder, "Affected entrypoints", result.Entrypoints);
        AppendAffectedSection(builder, "Affected publishable modules", result.PublishableModules);
        AppendAffectedSection(builder, "Affected helpers", result.Helpers);

        if (result.Entrypoints.Count == 0 && result.PublishableModules.Count == 0 && result.Helpers.Count == 0)
        {
            builder.AppendLine("No affected Bicep files found.");
            builder.AppendLine();
        }

        AppendSection(builder, "Warnings", result.Warnings);

        return builder.ToString().TrimEnd();
    }

    public static string RenderGraph(IEnumerable<GraphNode> nodes, IEnumerable<DependencyEdge> edges, IEnumerable<string> warnings)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "Nodes", nodes.Select(node => $"{node.Path} [{node.Kind}]"));
        AppendSection(builder, "Edges", edges.Select(edge => $"{edge.FromPath} -> {edge.ToPath} [{edge.Kind}]"));
        AppendSection(builder, "Warnings", warnings);

        return builder.ToString().TrimEnd();
    }

    private static void AppendAffectedSection(StringBuilder builder, string title, IReadOnlyList<AffectedItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{title}:");
        foreach (var item in items.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Kind))
        {
            builder.AppendLine($"  {item.Path}");
            foreach (var reason in item.Reasons
                .OrderBy(reason => reason.Kind, StringComparer.Ordinal)
                .ThenBy(reason => reason.CausedBy, StringComparer.Ordinal)
                .ThenBy(reason => reason.Message, StringComparer.Ordinal))
            {
                builder.AppendLine($"    reason: {reason.Message} caused by {reason.CausedBy}");
                builder.AppendLine($"    chain: {string.Join(" -> ", reason.Chain)}");
            }
        }

        builder.AppendLine();
    }

    private static void AppendSection(StringBuilder builder, string title, IEnumerable<string> values)
    {
        var materialized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        builder.AppendLine($"{title}:");
        foreach (var value in materialized)
        {
            builder.AppendLine($"  {value}");
        }

        builder.AppendLine();
    }
}
