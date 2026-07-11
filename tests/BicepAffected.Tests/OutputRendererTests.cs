using System.Text.Json;
using BicepAffected.Cli.Output;
using BicepAffected.Core.Domain;

namespace BicepAffected.Tests;

public sealed class OutputRendererTests
{
    [Fact]
    public void Json_output_uses_v3_selected_target_contract()
    {
        var payload = ActionPayload.FromSelection(SelectOne());

        using var document = JsonDocument.Parse(JsonRenderer.Render(payload));
        var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("deploy", root.GetProperty("target").GetString());
        Assert.True(root.GetProperty("hasTargets").GetBoolean());
        Assert.Equal(1, root.GetProperty("targetCount").GetInt32());
        Assert.Equal(["apis/employees/openapi.yaml"], root.GetProperty("changedFiles").EnumerateArray().Select(value => value.GetString()));
        var item = Assert.Single(root.GetProperty("targets").EnumerateArray());
        Assert.Equal("deployments/employees/main.bicep", item.GetProperty("path").GetString());
        Assert.Equal("entrypoint", item.GetProperty("kind").GetString());
        Assert.Equal("apis/employees/openapi.yaml", item.GetProperty("reasons")[0].GetProperty("causedBy").GetString());
        Assert.False(root.TryGetProperty("entrypoints", out _));
        Assert.False(root.TryGetProperty("helpers", out _));
    }

    [Fact]
    public void Renderers_are_deterministic_and_text_affected_contains_only_target_paths()
    {
        var first = Select(ActionTarget.Deploy, reverse: false);
        var second = Select(ActionTarget.Deploy, reverse: true);

        Assert.Equal(JsonRenderer.Render(ActionPayload.FromSelection(first)), JsonRenderer.Render(ActionPayload.FromSelection(second)));
        Assert.Equal(
            $"a/main.bicep{Environment.NewLine}z/main.bicep",
            TextRenderer.RenderAffected(first));
    }

    [Fact]
    public void Selection_deduplicates_targets_and_explain_preserves_reasons_and_chains()
    {
        var selection = ActionSelection.Select(
            new AffectedResult(
                ["content.yaml"],
                [
                    Item("main.bicep", "content.yaml"),
                    Item("main.bicep", "content.yaml")
                ],
                [],
                [],
                []),
            ActionTarget.Deploy,
            FixturePaths.Root("monorepo"),
            ["metadata.json"]);

        var target = Assert.Single(selection.Targets);
        Assert.Equal("main.bicep", target.Path);
        var explain = TextRenderer.RenderExplain(selection);
        Assert.Contains("Changed files:", explain, StringComparison.Ordinal);
        Assert.Contains("Selected deploy targets:", explain, StringComparison.Ordinal);
        Assert.Contains("reason: Affected through content-load dependency. caused by content.yaml", explain, StringComparison.Ordinal);
        Assert.Contains("chain: content.yaml -> main.bicep", explain, StringComparison.Ordinal);
    }

    private static ActionSelection SelectOne() =>
        ActionSelection.Select(
            new AffectedResult(
                ["apis/employees/openapi.yaml"],
                [Item("deployments/employees/main.bicep", "apis/employees/openapi.yaml")],
                [],
                [],
                []),
            ActionTarget.Deploy,
            FixturePaths.Root("monorepo"),
            ["metadata.json"]);

    private static ActionSelection Select(ActionTarget target, bool reverse = false)
    {
        var items = new[]
        {
            Item("z/main.bicep", "z.yaml"),
            Item("a/main.bicep", "a.yaml")
        };
        if (!reverse)
        {
            Array.Reverse(items);
        }

        return ActionSelection.Select(
            new AffectedResult(
                reverse ? ["z.yaml", "a.yaml"] : ["a.yaml", "z.yaml"],
                items,
                [],
                [],
                reverse ? ["z warning", "a warning"] : ["a warning", "z warning"]),
            target,
            FixturePaths.Root("monorepo"),
            ["metadata.json"]);
    }

    private static AffectedItem Item(string path, string causedBy) =>
        new(
            path,
            NodeKind.Entrypoint,
            [new Reason(
                "reverseDependency",
                causedBy,
                [causedBy, path],
                "Affected through content-load dependency.")]);
}
