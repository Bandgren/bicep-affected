namespace BicepAffected.Core.Config;

public sealed class BicepAffectedConfig
{
    public List<string>? Entrypoints { get; init; }

    public List<string>? Helpers { get; init; }

    public List<PublishableModuleRule>? PublishableModules { get; init; }

    public List<string>? GlobalImpactFiles { get; init; }

    public static BicepAffectedConfig Default() => new()
    {
        Entrypoints = [],
        Helpers = [],
        PublishableModules = [new PublishableModuleRule()],
        GlobalImpactFiles = ["**/bicepconfig.json"]
    };

    public BicepAffectedConfig WithDefaults() => new()
    {
        Entrypoints = Entrypoints ?? [],
        Helpers = Helpers ?? [],
        PublishableModules = PublishableModules ?? [new PublishableModuleRule()],
        GlobalImpactFiles = GlobalImpactFiles ?? ["**/bicepconfig.json"]
    };
}
