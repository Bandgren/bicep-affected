using System.Text.Json;
using BicepAffected.Cli.Output;
using BicepAffected.Core.Domain;

namespace BicepAffected.Tests;

public sealed class OutputRendererTests
{
    private static readonly AffectedResult Result = new(
        ["apis/employees/openapi.yaml"],
        [new AffectedItem(
            "deployments/employees/main.bicep",
            NodeKind.Entrypoint,
            [new Reason("reverseDependency", "apis/employees/openapi.yaml", ["apis/employees/openapi.yaml", "deployments/employees/main.bicep"], "Affected through content-load dependency.")])],
        [],
        [],
        []);

    [Fact]
    public void Json_output_contains_schema_version_and_collision_safe_artifact_name()
    {
        var json = JsonRenderer.Render(Result);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("hasAffected").GetBoolean());
        var entrypoint = document.RootElement.GetProperty("entrypoints")[0];
        Assert.Equal("main", entrypoint.GetProperty("fileName").GetString());
        Assert.Matches("^deployments-employees-main-[0-9a-f]{12}-bicep$", entrypoint.GetProperty("artifactName").GetString());
        Assert.False(entrypoint.TryGetProperty("buildCommand", out _));
    }

    [Fact]
    public void Json_output_contains_publish_matrix_for_modules_with_version_changes()
    {
        var result = new AffectedResult(
            ["Function/Infrastructure/metadata.json"],
            [],
            [new AffectedItem(
                "Function/Infrastructure/functionWithoutSlot.bicep",
                NodeKind.PublishableModule,
                [new Reason("metadataChange", "Function/Infrastructure/metadata.json", ["Function/Infrastructure/metadata.json", "Function/Infrastructure/functionWithoutSlot.bicep"], "Version changed.")])],
            [],
            []);

        var json = JsonRenderer.Render(result, FixturePaths.Root("modules-repo"), ["metadata.json"]);
        using var document = JsonDocument.Parse(json);

        Assert.True(document.RootElement.GetProperty("hasPublishableModulesToPublish").GetBoolean());
        Assert.Equal("1.0.0-beta", document.RootElement.GetProperty("publishableModulesToPublish")[0].GetProperty("version").GetString());
        Assert.Equal("v1.0.0-beta", document.RootElement.GetProperty("publishMatrix").GetProperty("include")[0].GetProperty("versionTag").GetString());
        Assert.Empty(document.RootElement.GetProperty("publishableModulesWithoutVersionChange").EnumerateArray());
    }

    [Fact]
    public void Json_output_reports_modules_without_version_change()
    {
        var result = new AffectedResult(
            ["Function/Infrastructure/functionWithoutSlot.bicep"],
            [],
            [new AffectedItem(
                "Function/Infrastructure/functionWithoutSlot.bicep",
                NodeKind.PublishableModule,
                [new Reason("directChange", "Function/Infrastructure/functionWithoutSlot.bicep", ["Function/Infrastructure/functionWithoutSlot.bicep"], "File changed.")])],
            [],
            []);

        var json = JsonRenderer.Render(result, FixturePaths.Root("modules-repo"), ["metadata.json"]);
        using var document = JsonDocument.Parse(json);

        Assert.False(document.RootElement.GetProperty("hasPublishableModulesToPublish").GetBoolean());
        Assert.Empty(document.RootElement.GetProperty("publishableModulesToPublish").EnumerateArray());
        Assert.Equal("Function/Infrastructure/functionWithoutSlot.bicep", document.RootElement.GetProperty("publishableModulesWithoutVersionChange")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Json_output_is_byte_stable_and_orders_data_fields()
    {
        var first = new AffectedResult(
            ["z.yaml", "a.yaml"],
            [
                Item("z/main.bicep", NodeKind.Entrypoint),
                Item("a/main.bicep", NodeKind.Entrypoint)
            ],
            [],
            [],
            ["z warning", "a warning"]);
        var second = new AffectedResult(
            ["a.yaml", "z.yaml"],
            [
                Item("a/main.bicep", NodeKind.Entrypoint),
                Item("z/main.bicep", NodeKind.Entrypoint)
            ],
            [],
            [],
            ["a warning", "z warning"]);

        var firstJson = JsonRenderer.Render(first);
        var secondJson = JsonRenderer.Render(second);

        Assert.Equal(firstJson, secondJson);
        using var document = JsonDocument.Parse(firstJson);
        Assert.Equal(["a.yaml", "z.yaml"], document.RootElement.GetProperty("changedFiles").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(["a/main.bicep", "z/main.bicep"], document.RootElement.GetProperty("entrypoints").EnumerateArray().Select(value => value.GetProperty("path").GetString()));
        Assert.Equal(["a warning", "z warning"], document.RootElement.GetProperty("warnings").EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void Json_output_uses_distinct_artifacts_for_same_basename()
    {
        var result = new AffectedResult(
            [],
            [Item("one/main.bicep", NodeKind.Entrypoint), Item("two/main.bicep", NodeKind.Entrypoint)],
            [],
            [],
            []);

        using var document = JsonDocument.Parse(JsonRenderer.Render(result));
        var artifactNames = document.RootElement.GetProperty("entrypoints")
            .EnumerateArray()
            .Select(item => item.GetProperty("artifactName").GetString())
            .ToArray();

        Assert.Equal(2, artifactNames.Distinct(StringComparer.Ordinal).Count());
        Assert.All(artifactNames, artifactName => Assert.Matches("^[a-z0-9-]+-[0-9a-f]{12}-bicep$", artifactName));
    }

    [Fact]
    public void GitHub_output_contains_data_only_outputs_and_warnings_json()
    {
        var output = PipelineOutputRenderer.RenderGitHub(CiPayload.FromResult(Result with { Warnings = ["z warning", "a warning"] }));

        Assert.Contains("has_affected=true", output, StringComparison.Ordinal);
        Assert.Contains("has_publish_modules=", output, StringComparison.Ordinal);
        Assert.Contains("matrix=", output, StringComparison.Ordinal);
        Assert.Contains("publish_matrix=", output, StringComparison.Ordinal);
        Assert.Contains("modules_without_version_change_json=", output, StringComparison.Ordinal);
        Assert.Contains("entrypoints_json=", output, StringComparison.Ordinal);
        Assert.Contains("warnings_json=[\"a warning\",\"z warning\"]", output, StringComparison.Ordinal);
        Assert.DoesNotContain("buildCommand", output, StringComparison.Ordinal);
    }

    private static AffectedItem Item(string path, NodeKind kind) =>
        new(path, kind, [new Reason("directChange", path, [path], "File changed.")]);
}
