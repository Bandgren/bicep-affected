namespace BicepAffected.Core.Domain;

public sealed class RepoGraph
{
    public RepoGraph(
        IReadOnlyDictionary<string, GraphNode> nodes,
        IReadOnlyList<DependencyEdge> edges)
    {
        Nodes = nodes;
        Edges = edges;
    }

    public IReadOnlyDictionary<string, GraphNode> Nodes { get; }

    public IReadOnlyList<DependencyEdge> Edges { get; }

    public GraphNode? FindNode(string path)
    {
        return Nodes.TryGetValue(path, out var node) ? node : null;
    }
}
