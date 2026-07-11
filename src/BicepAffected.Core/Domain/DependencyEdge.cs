namespace BicepAffected.Core.Domain;

public sealed record DependencyEdge(
    string FromPath,
    string ToPath,
    DependencyKind Kind,
    string? SourceSpan = null,
    string? Detail = null);
