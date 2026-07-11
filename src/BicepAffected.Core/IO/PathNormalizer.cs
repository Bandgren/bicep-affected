namespace BicepAffected.Core.IO;

public static class PathNormalizer
{
    public static readonly StringComparer PathComparer = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    public static string NormalizeRoot(string repoRoot)
    {
        var root = TrimTrailingSeparators(Path.GetFullPath(repoRoot));
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"Repository root '{repoRoot}' does not exist.");
        }

        return ResolveExistingPath(root);
    }

    public static string NormalizeRelativePath(string repoRoot, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Repository paths must not contain NUL characters.", nameof(path));
        }

        var root = NormalizeRoot(repoRoot);
        var normalizedInput = NormalizeSeparators(path);
        var absolutePath = Path.GetFullPath(
            Path.IsPathRooted(normalizedInput)
                ? normalizedInput
                : Path.Combine(root, normalizedInput));
        var normalized = NormalizeSeparators(Path.GetRelativePath(root, absolutePath));
        if (string.IsNullOrWhiteSpace(normalized) || IsOutsideRoot(normalized))
        {
            throw new ArgumentException($"Path '{path}' must identify a file inside repository root '{root}'.", nameof(path));
        }

        return normalized;
    }

    public static string NormalizeSeparators(string path) => path.Replace('\\', '/');

    public static string? ResolveDependencyPath(string repoRoot, string fromFile, string dependencyPath)
    {
        if (Path.IsPathRooted(dependencyPath))
        {
            return null;
        }

        var fromDirectory = Path.GetDirectoryName(fromFile) ?? string.Empty;
        return ResolvePathWithinRoot(repoRoot, Path.Combine(fromDirectory, dependencyPath));
    }

    public static string? ResolvePathWithinRoot(string repoRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var root = NormalizeRoot(repoRoot);
        var absolutePath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, absolutePath);
        if (IsOutsideRoot(relative) || !IsPhysicalPathWithinRoot(root, absolutePath))
        {
            return null;
        }

        return NormalizeSeparators(relative);
    }

    private static bool IsOutsideRoot(string relativePath) =>
        relativePath == "." ||
        relativePath == ".." ||
        relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
        relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);

    private static bool IsPhysicalPathWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        if (IsOutsideRoot(relativePath))
        {
            return false;
        }

        var currentPath = root;
        foreach (var segment in relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            currentPath = Path.Combine(currentPath, segment);
            if (!File.Exists(currentPath) && !Directory.Exists(currentPath))
            {
                break;
            }

            currentPath = ResolveExistingPath(currentPath);
            if (!IsAtOrWithinRoot(root, currentPath))
            {
                return false;
            }
        }

        return IsAtOrWithinRoot(root, currentPath);
    }

    private static bool IsAtOrWithinRoot(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        return relativePath == "." || !IsOutsideRoot(relativePath);
    }

    private static string ResolveExistingPath(string path)
    {
        FileSystemInfo info = Directory.Exists(path)
            ? new DirectoryInfo(path)
            : new FileInfo(path);

        return TrimTrailingSeparators(info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path));
    }

    private static string TrimTrailingSeparators(string path)
    {
        var root = Path.GetPathRoot(path);
        return PathComparer.Equals(path, root)
            ? path
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
