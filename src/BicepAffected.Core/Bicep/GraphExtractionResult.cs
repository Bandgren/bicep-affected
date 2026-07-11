using BicepAffected.Core.Domain;

namespace BicepAffected.Core.Bicep;

public sealed record GraphExtractionResult(
    RepoGraph Graph,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> ParseDiagnosticFiles);
