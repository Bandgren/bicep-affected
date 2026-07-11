namespace BicepAffected.Core.Domain;

public sealed record Reason(
    string Kind,
    string CausedBy,
    IReadOnlyList<string> Chain,
    string Message);
