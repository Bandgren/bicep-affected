using System.Text.Json;
using BicepAffected.Core.Domain;

namespace BicepAffected.Cli.Output;

internal static class JsonRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Render(
        AffectedResult result,
        string? repoRoot = null,
        IEnumerable<string>? publishVersionFiles = null)
    {
        return JsonSerializer.Serialize(CiPayload.FromResult(result, repoRoot, publishVersionFiles), Options);
    }
    public static string Render(CiPayload payload)
    {
        return JsonSerializer.Serialize(payload, Options);
    }


    public static string RenderGraph(object graphPayload)
    {
        return JsonSerializer.Serialize(graphPayload, Options);
    }
}
