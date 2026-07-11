using System.Diagnostics;
using BicepAffected.Core.Git;

namespace BicepAffected.Tests;

public sealed class GitDiffProviderTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), $"bicep-affected-{Guid.NewGuid():N}");

    [Fact]
    public void GetChangedFiles_returns_normalized_paths_between_refs()
    {
        Directory.CreateDirectory(tempRoot);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");

        Directory.CreateDirectory(Path.Combine(tempRoot, "infra"));
        File.WriteAllText(Path.Combine(tempRoot, "infra", "main.bicep"), "param name string");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial");
        var baseRef = RunGit("rev-parse", "HEAD").Trim();

        File.WriteAllText(Path.Combine(tempRoot, "infra", "main.bicep"), "param name string\nparam location string");
        File.WriteAllText(Path.Combine(tempRoot, "infra", "extra.bicep"), "param name string");
        RunGit("add", ".");
        RunGit("commit", "-m", "change infra");
        var headRef = RunGit("rev-parse", "HEAD").Trim();

        var changedFiles = new GitDiffProvider().GetChangedFiles(tempRoot, baseRef, headRef);

        Assert.Equal(["infra/extra.bicep", "infra/main.bicep"], changedFiles);
    }

    [Fact]
    public void GetChangedFiles_includes_deleted_and_both_rename_paths()
    {
        Directory.CreateDirectory(tempRoot);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");

        Directory.CreateDirectory(Path.Combine(tempRoot, "infra"));
        File.WriteAllText(Path.Combine(tempRoot, "infra", "deleted.bicep"), "param name string");
        File.WriteAllText(Path.Combine(tempRoot, "infra", "old.bicep"), "param name string");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial");
        var baseRef = RunGit("rev-parse", "HEAD").Trim();

        File.Delete(Path.Combine(tempRoot, "infra", "deleted.bicep"));
        RunGit("mv", "infra/old.bicep", "infra/new.bicep");
        RunGit("add", ".");
        RunGit("commit", "-m", "delete and rename");
        var headRef = RunGit("rev-parse", "HEAD").Trim();

        var changedFiles = new GitDiffProvider().GetChangedFiles(tempRoot, baseRef, headRef);

        Assert.Contains("infra/deleted.bicep", changedFiles);
        Assert.Contains("infra/old.bicep", changedFiles);
        Assert.Contains("infra/new.bicep", changedFiles);
    }

    [Fact]
    public void GetChangedFiles_preserves_unicode_deleted_and_renamed_paths()
    {
        Directory.CreateDirectory(tempRoot);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test User");

        Directory.CreateDirectory(Path.Combine(tempRoot, "infra"));
        File.WriteAllText(Path.Combine(tempRoot, "infra", "削除.bicep"), "param name string");
        File.WriteAllText(Path.Combine(tempRoot, "infra", "旧名.bicep"), "param name string");
        RunGit("add", ".");
        RunGit("commit", "-m", "initial unicode paths");
        var baseRef = RunGit("rev-parse", "HEAD").Trim();

        File.Delete(Path.Combine(tempRoot, "infra", "削除.bicep"));
        RunGit("mv", "infra/旧名.bicep", "infra/新名.bicep");
        RunGit("add", ".");
        RunGit("commit", "-m", "rename and delete unicode paths");
        var headRef = RunGit("rev-parse", "HEAD").Trim();

        var changedFiles = new GitDiffProvider().GetChangedFiles(tempRoot, baseRef, headRef);

        Assert.Equal(["infra/削除.bicep", "infra/新名.bicep", "infra/旧名.bicep"], changedFiles);
    }

    [Theory]
    [InlineData("--no-index")]
    [InlineData(":(top,glob)**/*.bicep")]
    public void GetChangedFiles_rejects_option_like_and_pathspec_references(string unsafeRef)
    {
        Directory.CreateDirectory(tempRoot);

        var exception = Assert.Throws<ArgumentException>(() =>
            new GitDiffProvider().GetChangedFiles(tempRoot, unsafeRef, "HEAD"));

        Assert.Equal("fromRef", exception.ParamName);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private string RunGit(params string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = tempRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        }

        return output;
    }
}
