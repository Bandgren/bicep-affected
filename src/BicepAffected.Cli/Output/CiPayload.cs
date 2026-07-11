using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BicepAffected.Core.Domain;
using BicepAffected.Core.IO;
using System.Text.Json;

namespace BicepAffected.Cli.Output;

internal sealed record CiPayload(
    int SchemaVersion,
    bool HasAffected,
    bool HasPublishableModulesToPublish,
    CiCounts Counts,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<CiAffectedItem> Entrypoints,
    IReadOnlyList<CiAffectedItem> PublishableModules,
    IReadOnlyList<CiAffectedItem> PublishableModulesToPublish,
    IReadOnlyList<CiAffectedItem> PublishableModulesWithoutVersionChange,
    IReadOnlyList<CiAffectedItem> Helpers,
    GitHubMatrix GitHubMatrix,
    GitHubMatrix PublishMatrix,
    IReadOnlyList<string> Warnings)
{
    public static CiPayload FromResult(
        AffectedResult result,
        string? repoRoot = null,
        IEnumerable<string>? publishVersionFiles = null)
    {
        var versionFiles = (publishVersionFiles ?? ["metadata.json"])
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file, StringComparer.Ordinal)
            .ToArray();
        var modulePayloads = result.PublishableModules
            .Select(item => (Item: item, Metadata: PublishVersionMetadata.Read(
                repoRoot,
                item.Path,
                versionFiles,
                item.Reasons
                    .Where(reason => reason.Kind is "metadataChange" or "versionFileChange")
                    .Select(reason => Path.GetFileName(reason.CausedBy))
                    .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase))))
            .ToArray();
        var entrypoints = OrderItems(result.Entrypoints.Select(item => CiAffectedItem.FromAffectedItem(item, PublishVersionMetadata.Empty)));
        var modules = OrderItems(modulePayloads.Select(module => CiAffectedItem.FromAffectedItem(module.Item, module.Metadata)));
        var helpers = OrderItems(result.Helpers.Select(item => CiAffectedItem.FromAffectedItem(item, PublishVersionMetadata.Empty)));
        var publishModules = OrderItems(modules.Where(item => item.HasVersionChange));
        var modulesWithoutVersionChange = OrderItems(modules.Where(item => !item.HasVersionChange));
        var matrixItems = OrderItems(entrypoints.Concat(modules));

        return new CiPayload(
            SchemaVersion: 1,
            HasAffected: matrixItems.Length > 0 || helpers.Length > 0,
            HasPublishableModulesToPublish: publishModules.Length > 0,
            Counts: new CiCounts(entrypoints.Length, modules.Length, helpers.Length),
            ChangedFiles: result.ChangedFiles.OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            Entrypoints: entrypoints,
            PublishableModules: modules,
            PublishableModulesToPublish: publishModules,
            PublishableModulesWithoutVersionChange: modulesWithoutVersionChange,
            Helpers: helpers,
            GitHubMatrix: new GitHubMatrix(matrixItems),
            PublishMatrix: new GitHubMatrix(publishModules),
            Warnings: result.Warnings
                .Concat(modulePayloads.SelectMany(module => module.Metadata.Warnings))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(warning => warning, StringComparer.Ordinal)
                .ToArray());
    }

    private static CiAffectedItem[] OrderItems(IEnumerable<CiAffectedItem> items) =>
        items.OrderBy(item => item.Path, StringComparer.Ordinal)
            .ThenBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.ArtifactName, StringComparer.Ordinal)
            .ToArray();
}
internal sealed record CiCounts(int Entrypoints, int PublishableModules, int Helpers);

internal sealed record GitHubMatrix(IReadOnlyList<CiAffectedItem> Include);

internal sealed record CiAffectedItem(
    string Path,
    string Kind,
    string Directory,
    string FileName,
    string ArtifactName,
    string? VersionFile,
    string? Version,
    string? VersionTag,
    bool HasVersionChange,
    IReadOnlyList<CiReason> Reasons)
{
    public static CiAffectedItem FromAffectedItem(AffectedItem item, PublishVersionMetadata publishVersionMetadata)
    {
        var normalizedPath = PathNormalizer.NormalizeSeparators(item.Path);
        var directory = System.IO.Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? string.Empty;
        var fileName = System.IO.Path.GetFileNameWithoutExtension(normalizedPath);

        return new CiAffectedItem(
            normalizedPath,
            ToCamelCase(item.Kind),
            directory,
            fileName,
            CreateArtifactName(normalizedPath),
            publishVersionMetadata.VersionFile,
            publishVersionMetadata.Version,
            publishVersionMetadata.VersionTag,
            item.Reasons.Any(reason => reason.Kind is "metadataChange" or "versionFileChange"),
            item.Reasons
                .OrderBy(reason => reason.Kind, StringComparer.Ordinal)
                .ThenBy(reason => reason.CausedBy, StringComparer.Ordinal)
                .ThenBy(reason => reason.Message, StringComparer.Ordinal)
                .Select(CiReason.FromReason)
                .ToArray());
    }

    private static string ToCamelCase(NodeKind kind) =>
        char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    private static string CreateArtifactName(string path)
    {
        var pathWithoutExtension = System.IO.Path.ChangeExtension(path, null) ?? path;
        var stem = new string(pathWithoutExtension.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray())
            .Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..12];
        return $"{(stem.Length == 0 ? "bicep" : stem)}-{hash}-bicep";
    }
}

internal sealed record CiReason(string Kind, string CausedBy, IReadOnlyList<string> Chain, string Message)
{
    public static CiReason FromReason(Reason reason) =>
        new(reason.Kind, reason.CausedBy, reason.Chain.ToArray(), reason.Message);
}

internal sealed record PublishVersionMetadata(
    string? VersionFile,
    string? Version,
    string? VersionTag,
    IReadOnlyList<string> Warnings)
{
    private static readonly Regex SemVerPattern = new(
        @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\z",
        RegexOptions.CultureInvariant);

    public static readonly PublishVersionMetadata Empty = new(null, null, null, []);

    public static PublishVersionMetadata Read(
        string? repoRoot,
        string modulePath,
        IReadOnlyList<string> publishVersionFiles,
        IReadOnlySet<string> changedVersionFileNames)
    {
        if (string.IsNullOrWhiteSpace(repoRoot) || changedVersionFileNames.Count == 0)
        {
            return Empty;
        }

        var directory = Path.GetDirectoryName(modulePath)?.Replace('\\', '/') ?? string.Empty;
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        var warnings = new List<string>();

        var matchingVersionFiles = publishVersionFiles.Where(changedVersionFileNames.Contains).ToArray();
        if (matchingVersionFiles.Length == 0)
        {
            return new PublishVersionMetadata(
                null,
                null,
                null,
                [$"Changed publish version metadata for module '{modulePath}' is not configured as a publish version file."]);
        }

        foreach (var versionFileName in matchingVersionFiles)
        {
            var relativePath = PathNormalizer.ResolvePathWithinRoot(repoRoot, Path.Combine(directory, versionFileName));
            if (relativePath is null)
            {
                continue;
            }

            var absolutePath = Path.Combine(repoRoot, relativePath);
            if (!File.Exists(absolutePath))
            {
                warnings.Add($"Changed publish version metadata '{relativePath}' for module '{modulePath}' could not be read because it does not exist.");
                continue;
            }

            if (TryReadVersion(absolutePath, moduleName, out var version))
            {
                return new PublishVersionMetadata(relativePath, version, ToVersionTag(version), warnings);
            }

            warnings.Add($"Changed publish version metadata '{relativePath}' for module '{modulePath}' does not contain a valid SemVer version.");
        }

        return new PublishVersionMetadata(null, null, null, warnings);
    }

    private static bool TryReadVersion(string path, string moduleName, out string version)
    {
        version = string.Empty;

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object
                        || !item.TryGetProperty("name", out var nameProperty)
                        || !string.Equals(nameProperty.GetString(), moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return item.TryGetProperty("version", out var versionProperty)
                        && TryFormatVersion(versionProperty, out version);
                }

                return false;
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (root.TryGetProperty("name", out var rootName)
                && !string.Equals(rootName.GetString(), moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return root.TryGetProperty("version", out var nestedVersion)
                ? TryFormatVersion(nestedVersion, out version)
                : TryFormatVersion(root, out version);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool TryFormatVersion(JsonElement element, out string version)
    {

        version = string.Empty;

        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value) || !SemVerPattern.IsMatch(value))
            {
                return false;
            }

            version = value;
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object
            || !TryGetIntProperty(element, "major", out var major)
            || !TryGetIntProperty(element, "minor", out var minor)
            || !TryGetPatch(element, out var patch))
        {
            return false;
        }

        version = $"{major}.{minor}.{patch}";
        if (TryGetStringProperty(element, "postFix", out var postfix)
            || TryGetStringProperty(element, "postfix", out postfix))
        {
            postfix = postfix.Trim().TrimStart('-');
            if (postfix.Length > 0)
            {
                version += $"-{postfix}";
            }
        }

        return SemVerPattern.IsMatch(version);
    }

    private static bool TryGetPatch(JsonElement element, out int patch)
    {
        return TryGetIntProperty(element, "hotfix", out patch)
            || TryGetIntProperty(element, "patch", out patch);
    }

    private static bool TryGetIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryGetStringProperty(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string ToVersionTag(string version)
    {
        return version.StartsWith('v') ? version : $"v{version}";
    }
}
