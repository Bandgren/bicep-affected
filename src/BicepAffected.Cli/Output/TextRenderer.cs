using System.Text;
using BicepAffected.Core.Domain;

namespace BicepAffected.Cli.Output;

internal static class TextRenderer
{
    public static string RenderAffected(ActionSelection selection)
    {
        return string.Join(Environment.NewLine, selection.Targets.Select(item => item.Path));
    }

    public static string RenderExplain(ActionSelection selection)
    {
        var builder = new StringBuilder();
        AppendSection(builder, "Changed files", selection.ChangedFiles);
        AppendActionTargetSection(builder, $"Selected {selection.Target.ToString().ToLowerInvariant()} targets", selection.Targets);
        if (selection.Targets.Count == 0)
        {
            builder.AppendLine("No action targets selected.");
            builder.AppendLine();
        }

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

    private static void AppendActionTargetSection(StringBuilder builder, string title, IReadOnlyList<ActionItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        builder.AppendLine($"{title}:");
        foreach (var item in items)
        {
            builder.AppendLine($"  {item.Path}");
            foreach (var reason in item.Reasons)
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
