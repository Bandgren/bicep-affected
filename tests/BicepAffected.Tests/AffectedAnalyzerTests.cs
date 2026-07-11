using BicepAffected.Core.Analysis;
using BicepAffected.Core.Config;

namespace BicepAffected.Tests;

public sealed class AffectedAnalyzerTests
{
    [Fact]
    public void Analyze_marks_publishable_module_when_shared_import_changes()
    {
        var result = AnalyzeModulesRepo("Config/Utils/types.bicep");

        var module = Assert.Single(result.PublishableModules, item => item.Path == "Function/Infrastructure/functionWithoutSlot.bicep");
        Assert.Contains(module.Reasons, reason => reason.Kind == "reverseDependency");
        Assert.Contains("Config/Utils/types.bicep", module.Reasons.SelectMany(reason => reason.Chain));
    }

    [Fact]
    public void Analyze_marks_publishable_modules_when_metadata_changes()
    {
        var result = AnalyzeModulesRepo("Function/Infrastructure/metadata.json");

        var module = Assert.Single(result.PublishableModules, item => item.Path == "Function/Infrastructure/functionWithoutSlot.bicep");
        Assert.Contains(module.Reasons, reason => reason.Kind == "metadataChange");
    }

    [Fact]
    public void Analyze_marks_publishable_modules_when_configured_version_file_changes()
    {
        var result = AnalyzeModulesRepo(["Function/Infrastructure/version.json"], ["version.json"]);

        var module = Assert.Single(result.PublishableModules, item => item.Path == "Function/Infrastructure/functionWithoutSlot.bicep");
        Assert.Contains(module.Reasons, reason => reason.Kind == "versionFileChange");
    }

    [Fact]
    public void Analyze_marks_all_publishable_modules_when_bicepconfig_changes()
    {
        var result = AnalyzeModulesRepo("bicepconfig.json");

        Assert.Contains(result.PublishableModules, item => item.Path == "Function/Infrastructure/functionWithoutSlot.bicep");
        Assert.Contains(result.PublishableModules, item => item.Path == "Storage/Infrastructure/storage.bicep");
        Assert.All(result.PublishableModules, item => Assert.Contains(item.Reasons, reason => reason.Kind == "globalImpact"));
    }

    [Fact]
    public void Analyze_marks_entrypoint_when_loaded_content_changes()
    {
        var result = AnalyzeMonorepo("apis/employees/openapi.yaml");

        var entrypoint = Assert.Single(result.Entrypoints);
        Assert.Equal("deployments/employees/main.bicep", entrypoint.Path);
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind == "reverseDependency");
    }

    [Theory]
    [InlineData("apis/employees/policy.bin")]
    [InlineData("apis/employees/scripts/deploy.ps1")]
    public void Analyze_marks_entrypoint_when_native_file_load_dependency_changes(string changedFile)
    {
        var result = AnalyzeMonorepo(changedFile);

        var entrypoint = Assert.Single(result.Entrypoints);
        Assert.Equal("deployments/employees/main.bicep", entrypoint.Path);
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind == "reverseDependency");
    }

    [Fact]
    public void Analyze_ignores_directory_file_load_changes_that_do_not_match_search_pattern()
    {
        var result = AnalyzeMonorepo("apis/employees/scripts/ignored.txt");

        Assert.Empty(result.Entrypoints);
        Assert.Empty(result.PublishableModules);
        Assert.Empty(result.Helpers);
    }

    [Fact]
    public void Analyze_ignores_unreferenced_content_file()
    {
        var result = AnalyzeMonorepo("apis/employees/unreferenced.yaml");

        Assert.Empty(result.Entrypoints);
        Assert.Empty(result.PublishableModules);
        Assert.Empty(result.Helpers);
    }

    [Fact]
    public void Analyze_marks_entrypoint_when_local_module_changes()
    {
        var result = AnalyzeMonorepo("infra/modules/app.bicep");

        Assert.Contains(result.Helpers, item => item.Path == "infra/modules/app.bicep");
        Assert.Contains(result.Entrypoints, item => item.Path == "deployments/employees/main.bicep");
    }

    [Theory]
    [InlineData("deployments/employees/main.bicepparam")]
    [InlineData("deployments/employees/main.dev.parameters.json")]
    public void Analyze_marks_entrypoint_when_parameter_file_changes(string changedFile)
    {
        var result = AnalyzeMonorepo(changedFile);

        var entrypoint = Assert.Single(result.Entrypoints);
        Assert.Equal("deployments/employees/main.bicep", entrypoint.Path);
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind is "reverseDependency" or "parameterFileChange");
    }

    [Fact]
    public void Analyze_marks_entrypoint_when_bicepparam_using_target_changes()
    {
        var result = AnalyzeMonorepo("parameters/custom.bicepparam");

        var entrypoint = Assert.Single(result.Entrypoints, item => item.Path == "deployments/shared/shared.bicep");
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind == "reverseDependency");
    }

    [Fact]
    public void Analyze_conservatively_marks_entrypoints_when_parse_diagnostics_exist()
    {
        var root = FixturePaths.Root("malformed-repo");
        var result = new AffectedAnalyzer().Analyze(root, ["unrelated.txt"], ConfigLoader.Load(root));

        var entrypoint = Assert.Single(result.Entrypoints, item => item.Path == "main.bicep");
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind == "parseDiagnostics");
        Assert.Contains(result.Warnings, warning => warning.StartsWith("Parse diagnostic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_only_marks_files_with_parse_diagnostics_and_dependents()
    {
        var root = FixturePaths.Root("parse-scope-repo");
        var result = new AffectedAnalyzer().Analyze(root, ["unrelated.txt"], ConfigLoader.Load(root));

        var entrypoint = Assert.Single(result.Entrypoints);
        Assert.Equal("broken.bicep", entrypoint.Path);
        Assert.Contains(entrypoint.Reasons, reason => reason.Kind == "parseDiagnostics");
        Assert.DoesNotContain(result.Entrypoints, item => item.Path == "good.bicep");
    }

    [Fact]
    public void Analyze_marks_direct_publishable_module_change_without_registry_resolution()
    {
        var result = AnalyzeModulesRepo("Function/Infrastructure/functionWithoutSlot.bicep");

        var module = Assert.Single(result.PublishableModules, item => item.Path == "Function/Infrastructure/functionWithoutSlot.bicep");
        Assert.Contains(module.Reasons, reason => reason.Kind == "directChange");
    }

    private static Core.Domain.AffectedResult AnalyzeModulesRepo(params string[] changedFiles)
    {
        return AnalyzeModulesRepo(changedFiles, []);
    }

    private static Core.Domain.AffectedResult AnalyzeModulesRepo(string[] changedFiles, string[] publishVersionFiles)
    {
        return new AffectedAnalyzer().Analyze(
            FixturePaths.Root("modules-repo"),
            changedFiles,
            BicepAffectedConfig.Default(),
            publishVersionFiles);
    }

    private static Core.Domain.AffectedResult AnalyzeMonorepo(params string[] changedFiles)
    {
        var root = FixturePaths.Root("monorepo");
        return new AffectedAnalyzer().Analyze(root, changedFiles, ConfigLoader.Load(root));
    }
}
