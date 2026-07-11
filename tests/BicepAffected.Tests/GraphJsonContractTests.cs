using System.Text.Json;
using BicepAffected.Cli.Output;
using BicepAffected.Core.Bicep;
using BicepAffected.Core.Domain;

namespace BicepAffected.Tests;

public sealed class GraphJsonContractTests
{
    [Fact]
    public void Graph_json_uses_schema_one_and_string_kinds()
    {
        var payload = GraphPayload.FromExtraction(CreateExtraction(reverseInsertionOrder: true));

        using var document = JsonDocument.Parse(JsonRenderer.RenderGraph(payload));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            ["entrypoint", "publishableModule"],
            document.RootElement.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("kind").GetString()));
        Assert.Equal("localModule", document.RootElement.GetProperty("edges")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public void Graph_json_is_byte_stable_regardless_of_source_enumeration_order()
    {
        var first = JsonRenderer.RenderGraph(GraphPayload.FromExtraction(CreateExtraction(reverseInsertionOrder: false)));
        var second = JsonRenderer.RenderGraph(GraphPayload.FromExtraction(CreateExtraction(reverseInsertionOrder: true)));

        Assert.Equal(first, second);
    }

    private static GraphExtractionResult CreateExtraction(bool reverseInsertionOrder)
    {
        var firstNode = new GraphNode("apps/main.bicep", NodeKind.Entrypoint, true);
        var secondNode = new GraphNode("modules/shared.bicep", NodeKind.PublishableModule, true);
        var entries = reverseInsertionOrder
            ? new[] { KeyValuePair.Create(secondNode.Path, secondNode), KeyValuePair.Create(firstNode.Path, firstNode) }
            : new[] { KeyValuePair.Create(firstNode.Path, firstNode), KeyValuePair.Create(secondNode.Path, secondNode) };
        var edges = reverseInsertionOrder
            ? new[]
            {
                new DependencyEdge("apps/main.bicep", "modules/shared.bicep", DependencyKind.LocalModule, "9:1", "second"),
                new DependencyEdge("apps/main.bicep", "modules/shared.bicep", DependencyKind.LocalModule, "1:1", "first")
            }
            : new[]
            {
                new DependencyEdge("apps/main.bicep", "modules/shared.bicep", DependencyKind.LocalModule, "1:1", "first"),
                new DependencyEdge("apps/main.bicep", "modules/shared.bicep", DependencyKind.LocalModule, "9:1", "second")
            };

        return new GraphExtractionResult(
            new RepoGraph(entries.ToDictionary(), edges),
            reverseInsertionOrder ? ["z warning", "a warning"] : ["a warning", "z warning"],
            reverseInsertionOrder ? ["z.bicep", "a.bicep"] : ["a.bicep", "z.bicep"]);
    }
}
