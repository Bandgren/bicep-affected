using BicepAffected.Core.Bicep;
using BicepAffected.Core.Config;
using BicepAffected.Core.Domain;

namespace BicepAffected.Tests;

public sealed class BicepGraphExtractorTests
{
    [Fact]
    public void Extract_classifies_publishable_modules_from_metadata_array()
    {
        var result = new BicepGraphExtractor().Extract(
            FixturePaths.Root("modules-repo"),
            BicepAffectedConfig.Default());

        Assert.Equal(
            NodeKind.PublishableModule,
            result.Graph.Nodes["Function/Infrastructure/functionWithoutSlot.bicep"].Kind);
        Assert.Equal(
            NodeKind.Helper,
            result.Graph.Nodes["Function/Alerts/siteStopped.bicep"].Kind);
        Assert.Equal(
            NodeKind.Helper,
            result.Graph.Nodes["Config/Utils/types.bicep"].Kind);
    }

    [Fact]
    public void Extract_creates_edges_for_local_modules_imports_and_content_loads()
    {
        var config = ConfigLoader.Load(FixturePaths.Root("monorepo"));
        var result = new BicepGraphExtractor().Extract(FixturePaths.Root("monorepo"), config);

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.LocalModule
            && edge.FromPath == "deployments/employees/main.bicep"
            && edge.ToPath == "infra/modules/app.bicep");

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.CompileTimeImport
            && edge.FromPath == "infra/modules/app.bicep"
            && edge.ToPath == "infra/shared/tags.bicep");

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ContentLoad
            && edge.FromPath == "deployments/employees/main.bicep"
            && edge.ToPath == "apis/employees/openapi.yaml");

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ContentLoad
            && edge.FromPath == "deployments/employees/main.bicep"
            && edge.ToPath == "apis/employees/policy.bin");

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.DirectoryContent
            && edge.FromPath == "deployments/employees/main.bicep"
            && edge.ToPath == "apis/employees/scripts/*.ps1");

        Assert.DoesNotContain(result.Graph.Edges, edge => edge.ToPath == "infra/modules/ignored.bicep");
    }

    [Fact]
    public void Extract_creates_edges_from_bicep_files_to_sidecar_parameter_files()
    {
        var config = ConfigLoader.Load(FixturePaths.Root("monorepo"));
        var result = new BicepGraphExtractor().Extract(FixturePaths.Root("monorepo"), config);

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ParameterFile
            && edge.FromPath == "deployments/employees/main.bicep"
            && edge.ToPath == "deployments/employees/main.bicepparam");

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ParameterFile
            && edge.FromPath == "deployments/employees/main.bicep"
            && edge.ToPath == "deployments/employees/main.dev.parameters.json");
    }

    [Fact]
    public void Extract_creates_edges_from_bicepparam_using_declarations()
    {
        var config = ConfigLoader.Load(FixturePaths.Root("monorepo"));
        var result = new BicepGraphExtractor().Extract(FixturePaths.Root("monorepo"), config);

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ParameterFile
            && edge.FromPath == "deployments/shared/shared.bicep"
            && edge.ToPath == "parameters/custom.bicepparam");
    }

    [Fact]
    public void Extract_warns_on_parse_diagnostics()
    {
        var root = FixturePaths.Root("malformed-repo");
        var result = new BicepGraphExtractor().Extract(root, ConfigLoader.Load(root));

        Assert.Contains(result.Warnings, warning => warning.StartsWith("Parse diagnostic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("main.bicep", result.ParseDiagnosticFiles);
    }

    [Fact]
    public void Extract_warns_and_keeps_modules_publishable_when_metadata_is_invalid()
    {
        var result = new BicepGraphExtractor().Extract(
            FixturePaths.Root("modules-repo"),
            BicepAffectedConfig.Default());

        Assert.Equal(NodeKind.PublishableModule, result.Graph.Nodes["Invalid/Infrastructure/broken.bicep"].Kind);
        Assert.Contains(result.Warnings, warning => warning.Contains("Invalid publish metadata 'Invalid/Infrastructure/metadata.json'", StringComparison.Ordinal));
    }

    [Fact]
    public void Extract_keeps_registry_references_external_without_local_node()
    {
        var result = new BicepGraphExtractor().Extract(
            FixturePaths.Root("modules-repo"),
            BicepAffectedConfig.Default());

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ExternalModule
            && edge.ToPath == "br/core:servicebus/alerts/queue:v0.5.1");

        Assert.DoesNotContain("br/core:servicebus/alerts/queue:v0.5.1", result.Graph.Nodes.Keys);
    }
}
