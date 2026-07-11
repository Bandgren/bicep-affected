using System.Text.Json;

namespace BicepAffected.Cli.Output;

internal static class JsonRenderer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string Render(ActionPayload payload)
    {
        return JsonSerializer.Serialize(payload, Options);
    }


    public static string RenderGraph(object graphPayload)
    {
        return JsonSerializer.Serialize(graphPayload, Options);
    }
}
