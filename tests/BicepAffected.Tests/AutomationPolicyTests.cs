using System.Text.Json;
using System.Xml.Linq;

namespace BicepAffected.Tests;

public sealed class AutomationPolicyTests
{
    private const string CheckoutActionSha = "93cb6efe18208431cddfb8368fd83d5badbf9bfd";
    private const string SetupDotnetActionSha = "26b0ec14cb23fa6904739307f278c14f94c95bf1";
    private const string NuGetLoginActionSha = "8d196754b4036150537f80ac539e15c2f1028841";
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Global_json_pins_the_required_sdk_without_roll_forward()
    {
        using var document = JsonDocument.Parse(ReadRepositoryFile("global.json"));
        var sdk = document.RootElement.GetProperty("sdk");

        Assert.Equal("10.0.301", sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
    }

    [Fact]
    public void Directory_build_props_requires_lock_files()
    {
        var document = XDocument.Parse(ReadRepositoryFile("Directory.Build.props"));
        var lockFileSetting = document.Descendants("RestorePackagesWithLockFile").SingleOrDefault();

        Assert.NotNull(lockFileSetting);
        Assert.Equal("true", lockFileSetting!.Value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_project_has_a_valid_committed_lock_file()
    {
        foreach (var project in new[]
                 {
                     "src/BicepAffected.Core",
                     "src/BicepAffected.Cli",
                     "tests/BicepAffected.Tests",
                 })
        {
            var lockFile = Path.Combine(RepositoryRoot, project, "packages.lock.json");
            Assert.True(File.Exists(lockFile), $"Expected committed lock file: {lockFile}");

            using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.NotEmpty(document.RootElement.EnumerateObject());
        }
    }

    [Fact]
    public void Cli_project_is_the_expected_public_mit_licensed_beta_tool()
    {
        var document = XDocument.Parse(ReadRepositoryFile("src/BicepAffected.Cli/BicepAffected.Cli.csproj"));
        var license = ReadRepositoryFile("LICENSE");
        var unwrappedLicense = license.ReplaceLineEndings(" ");

        Assert.Equal("true", PropertyValue(document, "PackAsTool"));
        Assert.Equal("BicepAffected", PropertyValue(document, "PackageId"));
        Assert.Equal("bicep-affected", PropertyValue(document, "ToolCommandName"));
        Assert.Equal("0.1.0-beta.1", PropertyValue(document, "Version"));
        Assert.Equal("MIT", PropertyValue(document, "PackageLicenseExpression"));
        Assert.Equal("https://github.com/Bandgren/bicep-affected", PropertyValue(document, "PackageProjectUrl"));
        Assert.Equal("https://github.com/Bandgren/bicep-affected.git", PropertyValue(document, "RepositoryUrl"));
        Assert.Equal("git", PropertyValue(document, "RepositoryType"));
        Assert.Equal("true", PropertyValue(document, "PublishRepositoryUrl"));
        Assert.Empty(document.Descendants("PackageLicenseFile"));
        Assert.Contains(
            "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:",
            unwrappedLicense,
            StringComparison.Ordinal);
        Assert.Contains(
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.",
            unwrappedLicense,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/publish-tool.yml")]
    public void Workflows_pin_every_action_to_the_approved_immutable_revision(string workflowPath)
    {
        var actions = UsesActions(ReadRepositoryFile(workflowPath));
        var approvedActions = new List<string>
        {
            $"actions/checkout@{CheckoutActionSha}",
            $"actions/setup-dotnet@{SetupDotnetActionSha}",
        };

        if (workflowPath == ".github/workflows/publish-tool.yml")
        {
            approvedActions.Add($"NuGet/login@{NuGetLoginActionSha}");
        }

        Assert.Contains($"actions/checkout@{CheckoutActionSha}", actions);
        Assert.Contains($"actions/setup-dotnet@{SetupDotnetActionSha}", actions);
        Assert.All(actions, action => Assert.Contains(action, approvedActions));
    }

    [Theory]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData(".github/workflows/publish-tool.yml")]
    public void Workflows_restore_with_locked_mode_and_never_bypass_duplicate_protection(string workflowPath)
    {
        var workflow = ReadRepositoryFile(workflowPath);

        Assert.Contains("dotnet restore --locked-mode", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-duplicate", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_runs_for_pull_requests_and_pushes_to_master()
    {
        var workflow = ReadRepositoryFile(".github/workflows/ci.yml");

        Assert.Contains("pull_request:\n    branches:\n      - master", workflow, StringComparison.Ordinal);
        Assert.Contains("push:\n    branches:\n      - master", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_smoke_tests_the_exact_packed_tool_from_an_isolated_artifact_source()
    {
        var workflow = ReadRepositoryFile(".github/workflows/ci.yml");
        var smokeScript = StepScript(workflow, "Smoke Test Packed Tool");

        Assert.Contains("package_path=\"artifacts/BicepAffected.0.0.0-ci.nupkg\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("<clear />", smokeScript, StringComparison.Ordinal);
        Assert.Contains("$PWD/artifacts", smokeScript, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install", smokeScript, StringComparison.Ordinal);
        Assert.Contains("--configfile \"$config_path\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("--tool-path \"$tool_path\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("--version \"0.0.0-ci\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("BicepAffected", smokeScript, StringComparison.Ordinal);
        Assert.Contains("\"$tool_path/bicep-affected\" --help", smokeScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_is_environment_protected_and_requires_an_authorized_master_ref_or_matching_version_tag()
    {
        var workflow = ReadRepositoryFile(".github/workflows/publish-tool.yml");
        var validationScript = StepScript(workflow, "Validate Package Version");

        Assert.Contains("environment: nuget", workflow, StringComparison.Ordinal);
        Assert.Contains("github.ref == 'refs/heads/master' || startsWith(github.ref, 'refs/tags/v')", workflow, StringComparison.Ordinal);
        Assert.Contains("semver_pattern=", validationScript, StringComparison.Ordinal);
        Assert.Contains("\"$GITHUB_REF\" != \"refs/heads/master\"", validationScript, StringComparison.Ordinal);
        Assert.Contains("\"$GITHUB_REF\" != \"refs/tags/v$PACKAGE_VERSION\"", validationScript, StringComparison.Ordinal);
    }

    [Fact]
    public void Publishing_transfers_dispatch_version_through_environment_not_shell_interpolation()
    {
        var workflow = ReadRepositoryFile(".github/workflows/publish-tool.yml");

        Assert.Contains("PACKAGE_VERSION: ${{ inputs.version }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("${{ inputs.version }}", WorkflowRunScripts(workflow), StringComparison.Ordinal);
        Assert.DoesNotContain("inputs.version", WorkflowRunScripts(workflow), StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_smoke_tests_and_pushes_exactly_one_deterministic_public_package_with_trusted_publishing()
    {
        var workflow = ReadRepositoryFile(".github/workflows/publish-tool.yml");
        var smokeScript = StepScript(workflow, "Smoke Test Packed Tool");
        var publishScript = StepScript(workflow, "Publish to NuGet.org");

        Assert.Contains("id-token: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("packages: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("nuget.pkg.github.com", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GitHub Packages", workflow, StringComparison.Ordinal);
        Assert.Contains($"uses: NuGet/login@{NuGetLoginActionSha}", workflow, StringComparison.Ordinal);
        Assert.Contains("user: Bandgren", workflow, StringComparison.Ordinal);
        Assert.Contains("NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.NUGET_API_KEY", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.NUGET_AUTH_TOKEN", workflow, StringComparison.Ordinal);
        Assert.Contains("package_path=\"artifacts/BicepAffected.${PACKAGE_VERSION}.nupkg\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("dotnet tool install", smokeScript, StringComparison.Ordinal);
        Assert.Contains("--version \"$PACKAGE_VERSION\"", smokeScript, StringComparison.Ordinal);
        Assert.Contains("\"$tool_path/bicep-affected\" --help", smokeScript, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(workflow, "dotnet nuget push"));
        Assert.Contains("dotnet nuget push \"$package_path\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("--source https://api.nuget.org/v3/index.json", publishScript, StringComparison.Ordinal);
        Assert.Contains("--api-key \"$NUGET_API_KEY\"", publishScript, StringComparison.Ordinal);
        Assert.Contains("package_path=\"artifacts/BicepAffected.${PACKAGE_VERSION}.nupkg\"", publishScript, StringComparison.Ordinal);
        Assert.DoesNotContain("--skip-duplicate", publishScript, StringComparison.Ordinal);
    }

    private static string PropertyValue(XDocument document, string propertyName)
    {
        var property = document.Descendants(propertyName).SingleOrDefault();
        Assert.NotNull(property);
        return property!.Value.Trim();
    }

    private static IReadOnlyList<string> UsesActions(string workflow)
    {
        return workflow.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("uses: ", StringComparison.Ordinal))
            .Select(line => line["uses: ".Length..])
            .ToArray();
    }

    private static string StepScript(string workflow, string stepName)
    {
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var stepStart = Array.FindIndex(lines, line => line.Trim() == $"- name: {stepName}");
        Assert.True(stepStart >= 0, $"Workflow is missing the '{stepName}' step.");

        var runLine = Array.FindIndex(lines, stepStart, line => line.Trim() == "run: |");
        Assert.True(runLine >= 0, $"The '{stepName}' step is missing a block run script.");

        var script = new List<string>();
        for (var index = runLine + 1; index < lines.Length; index++)
        {
            if (lines[index].StartsWith("      - name: ", StringComparison.Ordinal))
            {
                break;
            }

            script.Add(lines[index]);
        }

        return string.Join("\n", script);
    }

    private static string WorkflowRunScripts(string workflow)
    {
        var lines = workflow.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var scripts = new List<string>();
        var inRunBlock = false;

        foreach (var line in lines)
        {
            if (line.Trim() == "run: |")
            {
                inRunBlock = true;
                continue;
            }

            if (inRunBlock && line.StartsWith("      - name: ", StringComparison.Ordinal))
            {
                inRunBlock = false;
            }

            if (inRunBlock)
            {
                scripts.Add(line);
            }
        }

        return string.Join("\n", scripts);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot, relativePath));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")) &&
                File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test output directory.");
    }
}
