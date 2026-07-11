namespace BicepAffected.Core.Domain;

public sealed record GraphNode(string Path, NodeKind Kind, bool IsBicepFile);
