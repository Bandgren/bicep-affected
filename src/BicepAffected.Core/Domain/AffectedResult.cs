namespace BicepAffected.Core.Domain;

public sealed record AffectedResult(
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<AffectedItem> Entrypoints,
    IReadOnlyList<AffectedItem> PublishableModules,
    IReadOnlyList<AffectedItem> Helpers,
    IReadOnlyList<string> Warnings);
