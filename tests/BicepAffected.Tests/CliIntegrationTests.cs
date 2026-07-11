using System.Diagnostics;
using System.Text.Json;

namespace BicepAffected.Tests;

public sealed class CliIntegrationTests : IDisposable
{
    private readonly List<string> tempPaths = [];

    [Theory]
    [InlineData("apis/employees/policy.xml")]
    [InlineData("apis/employees/policy.bin")]
    [InlineData("apis/employees/scripts/deploy.ps1")]
    public void Affected_deploy_selects_entrypoint_for_loaded_content(string changedFile)
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", changedFile);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"deployments/employees/main.bicep{Environment.NewLine}", result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public void Affected_deploy_excludes_nonmatching_directory_content()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/scripts/ignored.txt");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public void Affected_deploy_selects_entrypoint_for_transitive_local_module()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "infra/shared/tags.bicep");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"deployments/employees/main.bicep{Environment.NewLine}", result.StdOut);
    }

    [Fact]
    public void Affected_deploy_selects_direct_entrypoint()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "deployments/employees/main.bicep");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"deployments/employees/main.bicep{Environment.NewLine}", result.StdOut);
    }

    [Fact]
    public void Affected_empty_deploy_selection_succeeds_with_empty_text_output()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/unreferenced.yaml");

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public void Affected_build_includes_standalone_publishable_module_while_deploy_excludes_it()
    {
        var deploy = RunCli(
            "affected", "--repo", FixturePaths.Root("modules-repo"),
            "--changed-file", "Function/Infrastructure/functionWithoutSlot.bicep",
            "--allow-warnings");
        var build = RunCli(
            "affected", "--repo", FixturePaths.Root("modules-repo"),
            "--changed-file", "Function/Infrastructure/functionWithoutSlot.bicep",
            "--target", "build", "--allow-warnings");

        Assert.Equal(0, deploy.ExitCode);
        Assert.Empty(deploy.StdOut);
        Assert.Equal(0, build.ExitCode);
        Assert.Equal($"Function/Infrastructure/functionWithoutSlot.bicep{Environment.NewLine}", build.StdOut);
    }

    [Fact]
    public void Affected_publish_requires_changed_valid_adjacent_metadata()
    {
        var noMetadataChange = RunCli(
            "affected", "--repo", FixturePaths.Root("modules-repo"),
            "--changed-file", "Function/Infrastructure/functionWithoutSlot.bicep",
            "--target", "publish", "--allow-warnings");
        var metadataChange = RunCli(
            "affected", "--repo", FixturePaths.Root("modules-repo"),
            "--changed-file", "Function/Infrastructure/metadata.json",
            "--target", "publish", "--allow-warnings");

        Assert.Equal(0, noMetadataChange.ExitCode);
        Assert.Empty(noMetadataChange.StdOut);
        Assert.Equal(0, metadataChange.ExitCode);
        Assert.Equal($"Function/Infrastructure/functionWithoutSlot.bicep{Environment.NewLine}", metadataChange.StdOut);
    }

    [Fact]
    public void Affected_publish_invalid_metadata_warns_and_fails_before_payload()
    {
        var repoRoot = CreateTempRepository("modules-repo");
        File.WriteAllText(
            Path.Combine(repoRoot, "Function", "Infrastructure", "metadata.json"),
            """[{"name":"functionWithoutSlot","version":"1.02.3"}]""");
        var outputPath = CreateTempPath("publish.json");

        var result = RunCli(
            "affected", "--repo", repoRoot,
            "--changed-file", "Function/Infrastructure/metadata.json",
            "--target", "publish", "--format", "json", "--output", outputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.False(File.Exists(outputPath));
        Assert.Contains("warning:", result.StdErr, StringComparison.Ordinal);
        Assert.Contains("valid SemVer", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Affected_publish_json_includes_changed_version_metadata()
    {
        var result = RunCli(
            "affected", "--repo", FixturePaths.Root("modules-repo"),
            "--changed-file", "Function/Infrastructure/metadata.json",
            "--target", "publish", "--format", "json", "--allow-warnings");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        var payload = document.RootElement;
        Assert.Equal(3, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("publish", payload.GetProperty("target").GetString());
        Assert.True(payload.GetProperty("hasTargets").GetBoolean());
        Assert.Equal(1, payload.GetProperty("targetCount").GetInt32());
        var target = Assert.Single(payload.GetProperty("targets").EnumerateArray());
        Assert.Equal("Function/Infrastructure/functionWithoutSlot.bicep", target.GetProperty("path").GetString());
        Assert.Equal("Function/Infrastructure/metadata.json", target.GetProperty("versionFile").GetString());
        Assert.Equal("1.0.0-beta", target.GetProperty("version").GetString());
        Assert.Equal("v1.0.0-beta", target.GetProperty("versionTag").GetString());
        Assert.True(target.GetProperty("hasVersionChange").GetBoolean());
    }

    [Fact]
    public void Affected_publish_invalid_metadata_with_allow_warnings_emits_empty_json_targets_and_warning()
    {
        var repoRoot = CreateTempRepository("modules-repo");
        Directory.Delete(Path.Combine(repoRoot, "Invalid"), recursive: true);
        File.WriteAllText(
            Path.Combine(repoRoot, "Function", "Infrastructure", "metadata.json"),
            """[{"name":"functionWithoutSlot","version":"1.02.3"}]""");

        var result = RunCli(
            "affected", "--repo", repoRoot,
            "--changed-file", "Function/Infrastructure/metadata.json",
            "--target", "publish", "--format", "json", "--allow-warnings");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        var payload = document.RootElement;
        Assert.Equal(3, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("publish", payload.GetProperty("target").GetString());
        Assert.False(payload.GetProperty("hasTargets").GetBoolean());
        Assert.Equal(0, payload.GetProperty("targetCount").GetInt32());
        Assert.Empty(payload.GetProperty("targets").EnumerateArray());
        var warning = Assert.Single(payload.GetProperty("warnings").EnumerateArray()).GetString();
        Assert.Equal(
            "Changed publish version metadata 'Function/Infrastructure/metadata.json' for module 'Function/Infrastructure/functionWithoutSlot.bicep' does not contain a valid SemVer version.",
            warning);
        Assert.Equal($"warning: {warning}{Environment.NewLine}", result.StdErr);
    }

    [Fact]
    public void Affected_json_uses_v3_selected_target_schema_and_is_deterministic()
    {
        var first = RunCli(
            "affected", "--repo", FixturePaths.Root("monorepo"),
            "--changed-file", "deployments/employees/main.bicep",
            "--changed-file", "apis/employees/openapi.yaml",
            "--format", "json");
        var second = RunCli(
            "affected", "--repo", FixturePaths.Root("monorepo"),
            "--changed-file", "apis/employees/openapi.yaml",
            "--changed-file", "deployments/employees/main.bicep",
            "--format", "json");

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(first.StdOut, second.StdOut);
        using var document = JsonDocument.Parse(first.StdOut);
        var payload = document.RootElement;
        Assert.Equal(3, payload.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("deploy", payload.GetProperty("target").GetString());
        Assert.True(payload.GetProperty("hasTargets").GetBoolean());
        Assert.Equal(1, payload.GetProperty("targetCount").GetInt32());
        Assert.Equal(
            ["apis/employees/openapi.yaml", "deployments/employees/main.bicep"],
            payload.GetProperty("changedFiles").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["deployments/employees/main.bicep"],
            payload.GetProperty("targets").EnumerateArray().Select(item => item.GetProperty("path").GetString()));
        Assert.False(payload.TryGetProperty("entrypoints", out _));
        Assert.False(payload.TryGetProperty("publishableModulesToPublish", out _));
    }

    [Fact]
    public void Explain_renders_selected_targets_reasons_and_dependency_chains()
    {
        var result = RunCli(
            "explain", "--repo", FixturePaths.Root("monorepo"),
            "--changed-file", "apis/employees/openapi.yaml");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Changed files:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Selected deploy targets:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("deployments/employees/main.bicep", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("reason:", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("chain: apis/employees/openapi.yaml -> deployments/employees/main.bicep", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_topology_schema_remains_unchanged()
    {
        var result = RunCli("graph", "--repo", FixturePaths.Root("monorepo"), "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("nodes", out _));
        Assert.True(document.RootElement.TryGetProperty("edges", out _));
    }

    [Fact]
    public void Output_file_receives_payload_and_suppresses_stdout()
    {
        var outputPath = CreateTempPath("affected.json");
        var result = RunCli(
            "affected", "--repo", FixturePaths.Root("monorepo"),
            "--changed-file", "apis/employees/openapi.yaml",
            "--format", "json", "--output", outputPath);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.Empty(result.StdErr);
        using var document = JsonDocument.Parse(File.ReadAllText(outputPath));
        Assert.Equal("deployments/employees/main.bicep", document.RootElement.GetProperty("targets")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Warnings_fail_closed_before_writing_payload()
    {
        var outputPath = CreateTempPath("affected.json");
        var result = RunCli(
            "affected", "--repo", FixturePaths.Root("malformed-repo"),
            "--changed-file", "unrelated.txt",
            "--format", "json", "--output", outputPath);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(result.StdOut);
        Assert.False(File.Exists(outputPath));
        Assert.Contains("warning: Parse diagnostic", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Include_is_rejected_as_removed_option()
    {
        var result = RunCli(
            "affected", "--repo", FixturePaths.Root("monorepo"),
            "--changed-file", "apis/employees/openapi.yaml",
            "--include", "entrypoints");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Unknown option '--include'", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("all")]
    [InlineData("entrypoints")]
    [InlineData("unknown")]
    public void Target_requires_build_deploy_or_publish(string target)
    {
        var result = RunCli(
            "affected", "--repo", FixturePaths.Root("monorepo"),
            "--changed-file", "apis/employees/openapi.yaml",
            "--target", target);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--target must be one of: build, deploy, publish.", result.StdErr, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (var tempPath in tempPaths)
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
            else if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, recursive: true);
            }
        }
    }

    private string CreateTempPath(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"bicep-affected-{Guid.NewGuid():N}-{fileName}");
        tempPaths.Add(path);
        return path;
    }

    private string CreateTempRepository(string fixtureName)
    {
        var destination = Path.Combine(Path.GetTempPath(), $"bicep-affected-{Guid.NewGuid():N}");
        CopyDirectory(FixturePaths.Root(fixtureName), destination);
        tempPaths.Add(destination);
        return destination;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static CliRunResult RunCli(params string[] args) => RunCli(args, input: null);

    private static CliRunResult RunCli(string[] args, string? input = null)
    {
        var dotnetHostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        var processPath = Environment.ProcessPath;
        var dotnet = !string.IsNullOrWhiteSpace(dotnetHostPath)
            ? dotnetHostPath
            : !string.IsNullOrWhiteSpace(processPath) && Path.GetFileNameWithoutExtension(processPath) == "dotnet"
                ? processPath
                : "dotnet";
        var cliAssembly = Path.Combine(AppContext.BaseDirectory, "BicepAffected.Cli.dll");
        var startInfo = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = FindRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null
        };

        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add(cliAssembly);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start CLI process.");
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new CliRunResult(process.ExitCode, stdout, stderr);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BicepAffected.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Could not find repository root.");
    }

    private sealed record CliRunResult(int ExitCode, string StdOut, string StdErr);
}
