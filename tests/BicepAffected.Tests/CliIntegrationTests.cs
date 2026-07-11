using System.Diagnostics;
using System.Text.Json;

namespace BicepAffected.Tests;

public sealed class CliIntegrationTests : IDisposable
{
    private readonly List<string> tempPaths = [];

    [Fact]
    public void Affected_json_outputs_valid_ci_payload()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/openapi.yaml", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.True(document.RootElement.GetProperty("hasAffected").GetBoolean());
        Assert.Equal("deployments/employees/main.bicep", document.RootElement.GetProperty("entrypoints")[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Affected_without_format_outputs_human_readable_text()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/openapi.yaml");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Changed files:\n  apis/employees/openapi.yaml", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("Affected entrypoints:\n  deployments/employees/main.bicep", result.StdOut, StringComparison.Ordinal);
        Assert.Contains("reason: Affected through content-load dependency. caused by apis/employees/openapi.yaml", result.StdOut, StringComparison.Ordinal);
        Assert.DoesNotContain("\"schemaVersion\"", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Fail_if_none_returns_exit_code_three()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/unreferenced.yaml", "--fail-if-none");

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Fail_if_affected_returns_exit_code_two()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/openapi.yaml", "--fail-if-affected");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Changed_files_stdin_reads_paths()
    {
        var result = RunCliWithInput("apis/employees/openapi.yaml\n", "affected", "--repo", FixturePaths.Root("monorepo"), "--changed-files-stdin", "--format", "json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("deployments/employees/main.bicep", result.StdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_writes_rendered_payload_to_file()
    {
        var outputPath = CreateTempPath("affected.json");
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/openapi.yaml", "--format", "json", "--output", outputPath, "--allow-warnings");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("deployments/employees/main.bicep", File.ReadAllText(outputPath), StringComparison.Ordinal);
    }


    [Fact]
    public void Publish_version_file_option_outputs_publish_metadata_as_json()
    {
        var result = RunCli(
            "affected",
            "--repo",
            FixturePaths.Root("modules-repo"),
            "--changed-file",
            "Function/Infrastructure/version.json",
            "--publish-version-file",
            "version.json",
            "--format",
            "json",
            "--include",
            "modules",
            "--allow-warnings");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        var module = Assert.Single(document.RootElement.GetProperty("publishableModulesToPublish").EnumerateArray());
        Assert.Equal("Function/Infrastructure/functionWithoutSlot.bicep", module.GetProperty("path").GetString());
        Assert.Equal("Function/Infrastructure/version.json", module.GetProperty("versionFile").GetString());
        Assert.Equal("2.1.0-preview", module.GetProperty("version").GetString());
        Assert.Equal("v2.1.0-preview", module.GetProperty("versionTag").GetString());
        Assert.False(document.RootElement.TryGetProperty("matrix", out _));
        Assert.False(document.RootElement.TryGetProperty("publishMatrix", out _));
    }

    [Theory]
    [InlineData("github")]
    [InlineData("yaml")]
    [InlineData("azure-devops")]
    public void Unsupported_formats_fail(string format)
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/openapi.yaml", "--format", format);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--format must be one of: text, json.", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_rejects_affected_only_options()
    {
        var result = RunCli("graph", "--repo", FixturePaths.Root("monorepo"), "--changed-file", "apis/employees/openapi.yaml");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("The graph command does not support: --changed-file", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Graph_supports_output_file()
    {
        var outputPath = CreateTempPath("graph.json");
        var result = RunCli("graph", "--repo", FixturePaths.Root("monorepo"), "--format", "json", "--output", outputPath, "--allow-warnings");
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("deployments/employees/main.bicep", File.ReadAllText(outputPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Changed_file_modes_cannot_be_combined()
    {
        var result = RunCliWithInput(
            "apis/employees/openapi.yaml\n",
            "affected",
            "--repo",
            FixturePaths.Root("monorepo"),
            "--changed-file",
            "apis/employees/openapi.yaml",
            "--changed-files-stdin");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Specify exactly one changed-file mode", result.StdErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--from", "HEAD")]
    [InlineData("--to", "HEAD")]
    public void Partial_git_refs_are_rejected(string option, string value)
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("monorepo"), option, value);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--from and --to must be supplied together", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_changed_files_stdin_is_authoritative()
    {
        var result = RunCliWithInput(string.Empty, "affected", "--repo", FixturePaths.Root("monorepo"), "--changed-files-stdin", "--fail-if-none");

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Warnings_fail_by_default_and_are_emitted_in_json()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("malformed-repo"), "--changed-file", "unrelated.txt", "--format", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("warning: Parse diagnostic", result.StdErr, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.NotEmpty(document.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void Allow_warnings_preserves_analysis_payload_and_succeeds()
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("malformed-repo"), "--changed-file", "unrelated.txt", "--format", "json", "--allow-warnings");

        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.NotEmpty(document.RootElement.GetProperty("warnings").EnumerateArray());
    }

    [Theory]
    [InlineData("../version.json")]
    [InlineData("nested/version.json")]
    [InlineData(".")]
    public void Publish_version_file_must_be_an_adjacent_filename(string versionFile)
    {
        var result = RunCli("affected", "--repo", FixturePaths.Root("modules-repo"), "--changed-file", "Function/Infrastructure/version.json", "--publish-version-file", versionFile);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--publish-version-file must be a simple adjacent filename", result.StdErr, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_semver_metadata_warns_and_fails_closed()
    {
        var repoRoot = CreateTempRepository("modules-repo");
        File.WriteAllText(Path.Combine(repoRoot, "Function", "Infrastructure", "version.json"), """{"version":"1.02.3"}""");

        var result = RunCli(
            "affected",
            "--repo",
            repoRoot,
            "--changed-file",
            "Function/Infrastructure/version.json",
            "--publish-version-file",
            "version.json",
            "--include",
            "modules",
            "--format",
            "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("does not contain a valid SemVer version", result.StdErr, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(result.StdOut);
        Assert.Contains(
            document.RootElement.GetProperty("warnings").EnumerateArray().Select(warning => warning.GetString()),
            warning => warning?.Contains("does not contain a valid SemVer version", StringComparison.Ordinal) == true);
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


    private static CliRunResult RunCli(params string[] args)
    {
        return RunCli(args, input: null);
    }

    private static CliRunResult RunCliWithInput(string input, params string[] args)
    {
        return RunCli(args, input);
    }

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
