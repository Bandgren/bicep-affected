using BicepAffected.Core.Domain;

namespace BicepAffected.Cli.Output;

internal static class ResultFilter
{
    public static AffectedResult Apply(AffectedResult result, IncludeFilter include)
    {
        return include switch
        {
            IncludeFilter.Entrypoints => result with { PublishableModules = [], Helpers = [] },
            IncludeFilter.Modules => result with { Entrypoints = [], Helpers = [] },
            IncludeFilter.Helpers => result with { Entrypoints = [], PublishableModules = [] },
            _ => result
        };
    }
}
