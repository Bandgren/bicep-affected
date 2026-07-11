using System.Text.Json;

namespace BicepAffected.Cli.Output;

internal static class PipelineOutputRenderer
{
    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string RenderGitHub(CiPayload payload)
    {
        return string.Join(Environment.NewLine,
            $"has_affected={payload.HasAffected.ToString().ToLowerInvariant()}",
            $"has_publish_modules={payload.HasPublishableModulesToPublish.ToString().ToLowerInvariant()}",
            $"entrypoints_json={JsonSerializer.Serialize(payload.Entrypoints, CompactJsonOptions)}",
            $"modules_json={JsonSerializer.Serialize(payload.PublishableModules, CompactJsonOptions)}",
            $"publish_modules_json={JsonSerializer.Serialize(payload.PublishableModulesToPublish, CompactJsonOptions)}",
            $"modules_without_version_change_json={JsonSerializer.Serialize(payload.PublishableModulesWithoutVersionChange, CompactJsonOptions)}",
            $"helpers_json={JsonSerializer.Serialize(payload.Helpers, CompactJsonOptions)}",
            $"warnings_json={JsonSerializer.Serialize(payload.Warnings, CompactJsonOptions)}",
            $"matrix={JsonSerializer.Serialize(payload.GitHubMatrix, CompactJsonOptions)}",
            $"publish_matrix={JsonSerializer.Serialize(payload.PublishMatrix, CompactJsonOptions)}");
    }

}
