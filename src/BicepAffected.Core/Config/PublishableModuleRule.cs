namespace BicepAffected.Core.Config;

public sealed class PublishableModuleRule
{
    public string Path { get; init; } = "**/*.bicep";

    public string Metadata { get; init; } = "metadata.json";
}
