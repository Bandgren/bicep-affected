using System.Text.Json;
using BicepAffected.Core.Analysis;
using BicepAffected.Core.Bicep;
using BicepAffected.Core.Config;
using BicepAffected.Core.Domain;
using Xunit.Sdk;

namespace BicepAffected.Tests;

public sealed class CoreHardeningTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"bicep-affected-core-{Guid.NewGuid():N}");

    [Fact]
    public void Extract_tracks_sys_qualified_file_and_directory_loaders()
    {
        Write("main.bicep", "var text = sys.loadTextContent('assets/message.txt')\nvar files = sys.loadDirectoryFileInfo('scripts', '*.ps1')");
        Write("assets/message.txt", "hello");
        Write("scripts/deploy.ps1", "Write-Output deploy");

        var result = new BicepGraphExtractor().Extract(tempRoot, Config(entrypoints: ["main.bicep"]));

        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.ContentLoad && edge.FromPath == "main.bicep" && edge.ToPath == "assets/message.txt");
        Assert.Contains(result.Graph.Edges, edge =>
            edge.Kind == DependencyKind.DirectoryContent && edge.FromPath == "main.bicep" && edge.ToPath == "scripts/*.ps1");
    }

    [Fact]
    public void Extract_includes_nested_sources_but_prunes_generated_dependency_trees()
    {
        Write("services/api/main.bicep", "param location string");
        Write("node_modules/example-module/main.bicep", "param location string");
        Write(".terraform/modules/network/main.bicep", "param location string");

        var result = new BicepGraphExtractor().Extract(tempRoot, Config());

        Assert.Contains("services/api/main.bicep", result.Graph.Nodes.Keys);
        Assert.DoesNotContain("node_modules/example-module/main.bicep", result.Graph.Nodes.Keys);
        Assert.DoesNotContain(".terraform/modules/network/main.bicep", result.Graph.Nodes.Keys);
    }

    [Fact]
    public void Analyze_treats_nested_bicepconfig_as_global_impact()
    {
        Write("services/team/main.bicep", "param location string");
        Write("services/team/bicepconfig.json", "{}");

        var result = new AffectedAnalyzer().Analyze(
            tempRoot,
            ["services/team/bicepconfig.json"],
            Config(entrypoints: ["services/team/main.bicep"]));

        var entrypoint = Assert.Single(result.Entrypoints);
        Assert.Equal("services/team/main.bicep", entrypoint.Path);
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind == "globalImpact");
    }

    [Fact]
    public void Extract_warns_and_classifies_conservatively_for_schema_invalid_metadata()
    {
        Write("modules/widget/main.bicep", "param location string");
        Write("modules/widget/metadata.json", "[{ \"name\": 42 }]");

        var result = new BicepGraphExtractor().Extract(
            tempRoot,
            Config(
                entrypoints: [],
                publishableModules: [new PublishableModuleRule { Path = "modules/**/*.bicep", Metadata = "metadata.json" }]));

        Assert.Equal(NodeKind.PublishableModule, result.Graph.Nodes["modules/widget/main.bicep"].Kind);
        Assert.Contains(result.Warnings, warning => warning.Contains("Invalid publish metadata 'modules/widget/metadata.json'", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyze_falls_back_to_all_buildable_targets_for_unresolvable_malformed_bicepparam()
    {
        Write("deployments/one.bicep", "param location string");
        Write("deployments/two.bicep", "param location string");
        Write("parameters/broken.bicepparam", "using './missing.bicep");

        var result = new AffectedAnalyzer().Analyze(
            tempRoot,
            ["unrelated.txt"],
            Config(entrypoints: ["deployments/*.bicep"]));

        Assert.Equal(["deployments/one.bicep", "deployments/two.bicep"], result.Entrypoints.Select(item => item.Path));
        Assert.All(result.Entrypoints, item =>
            Assert.Contains(item.Reasons, reason =>
                reason.Kind == "parseDiagnosticsFallback" && reason.CausedBy == "parameters/broken.bicepparam"));
    }

    [Fact]
    public void Extract_warns_for_symbolic_links_and_does_not_follow_them_or_escape_dependencies()
    {
        Write("main.bicep", "module escaped '../outside/escape.bicep' = {}");
        var outsideRoot = Path.Combine(Path.GetDirectoryName(tempRoot)!, $"bicep-affected-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideRoot);
        File.WriteAllText(Path.Combine(outsideRoot, "linked.bicep"), "param value string");

        try
        {
            try
            {
                File.CreateSymbolicLink(Path.Combine(tempRoot, "linked.bicep"), Path.Combine(outsideRoot, "linked.bicep"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw SkipException.ForSkip($"The current platform cannot create a symbolic link: {exception.Message}");
            }

            var result = new BicepGraphExtractor().Extract(tempRoot, Config(entrypoints: ["main.bicep"]));

            Assert.DoesNotContain("linked.bicep", result.Graph.Nodes.Keys);
            Assert.DoesNotContain(result.Graph.Edges, edge => edge.ToPath.Contains("outside", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Warnings, warning => warning.Contains("symbolic link or reparse point 'linked.bicep'", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Warnings, warning => warning.Contains("Ignored dependency outside repo root", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(outsideRoot))
            {
                Directory.Delete(outsideRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Analyze_rejects_nonexistent_roots_and_changed_paths_outside_the_repository()
    {
        var missingRoot = Path.Combine(tempRoot, "missing");
        Assert.Throws<DirectoryNotFoundException>(() => new AffectedAnalyzer().Analyze(missingRoot, [], Config()));

        Write("main.bicep", "param value string");
        Assert.Throws<ArgumentException>(() => new AffectedAnalyzer().Analyze(tempRoot, ["../outside.bicep"], Config(entrypoints: ["main.bicep"])));

        var normalized = new AffectedAnalyzer().Analyze(
            tempRoot,
            ["nested/../main.bicep"],
            Config(entrypoints: ["main.bicep"]));
        Assert.Equal(["main.bicep"], normalized.ChangedFiles);
        var entrypoint = Assert.Single(normalized.Entrypoints);
        Assert.Equal("main.bicep", entrypoint.Path);
        Assert.Contains(entrypoint.Reasons, reason =>
            reason.Kind == "directChange" && reason.CausedBy == "main.bicep");

        Assert.Throws<ArgumentException>(() => new AffectedAnalyzer().Analyze(
            tempRoot,
            ["nested/../../outside.bicep"],
            Config(entrypoints: ["main.bicep"])));
    }

    [Fact]
    public void Load_returns_defaults_when_omitted_but_rejects_an_explicit_missing_in_repo_config()
    {
        Directory.CreateDirectory(tempRoot);
        var defaults = ConfigLoader.Load(tempRoot);

        Assert.Empty(defaults.Entrypoints!);
        Assert.Empty(defaults.Helpers!);
        Assert.Single(defaults.PublishableModules!);
        Assert.Equal(["**/bicepconfig.json"], defaults.GlobalImpactFiles);
        Assert.Throws<FileNotFoundException>(() => ConfigLoader.Load(tempRoot, "config/missing.json"));
    }

    [Theory]
    [InlineData("entrypoints", "\"   \"")]
    [InlineData("entrypoints", "42")]
    [InlineData("helpers", "\"   \"")]
    [InlineData("helpers", "42")]
    [InlineData("globalImpactFiles", "\"   \"")]
    [InlineData("globalImpactFiles", "42")]
    public void Load_rejects_whitespace_and_non_string_collection_members(string propertyName, string invalidValue)
    {
        Write("bicep-affected.json", $"{{ \"{propertyName}\": [{invalidValue}] }}");

        var exception = Assert.Throws<JsonException>(() => ConfigLoader.Load(tempRoot));

        Assert.Contains($"Configuration property '{propertyName}' must contain non-empty strings.", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("path", "null")]
    [InlineData("path", "\"   \"")]
    [InlineData("path", "42")]
    [InlineData("metadata", "null")]
    [InlineData("metadata", "\"   \"")]
    [InlineData("metadata", "42")]
    public void Load_rejects_null_blank_and_non_string_publishable_module_values(string propertyName, string invalidValue)
    {
        Write("bicep-affected.json", $"{{ \"publishableModules\": [{{ \"{propertyName}\": {invalidValue} }}] }}");

        var exception = Assert.Throws<JsonException>(() => ConfigLoader.Load(tempRoot));

        Assert.Contains($"Publishable module property '{propertyName}' must be a non-empty string.", exception.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private void Write(string relativePath, string content)
    {
        var fullPath = Path.Combine(tempRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private static BicepAffectedConfig Config(
        List<string>? entrypoints = null,
        List<PublishableModuleRule>? publishableModules = null) => new()
    {
        Entrypoints = entrypoints ?? [],
        Helpers = [],
        PublishableModules = publishableModules ?? [],
        GlobalImpactFiles = ["**/bicepconfig.json"]
    };
}
