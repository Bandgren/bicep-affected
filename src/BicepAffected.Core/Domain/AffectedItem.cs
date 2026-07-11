namespace BicepAffected.Core.Domain;

public sealed record AffectedItem(
    string Path,
    NodeKind Kind,
    IReadOnlyList<Reason> Reasons);
