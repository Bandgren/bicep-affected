using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BicepAffected.Core.Domain;
using BicepAffected.Core.IO;

namespace BicepAffected.Cli.Output;

internal enum ActionTarget
{
    Build,
    Deploy,
    Publish
}

internal sealed record ActionSelection(
    ActionTarget Target,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ActionItem> Targets,
    IReadOnlyList<string> Warnings)
{
    public static ActionSelection Select(
        AffectedResult result,
        ActionTarget target,
        string repoRoot,
        IReadOnlyList<string> publishVersionFiles)
    {
        var moduleDetails = result.PublishableModules
            .Select(item => (Item: item, Metadata: PublishVersionMetadata.Read(
                repoRoot,
                item.Path,
                publishVersionFiles,
                item.Reasons
                    .Where(reason => reason.Kind is "metadataChange" or "versionFileChange")
                    .Select(reason => Path.GetFileName(reason.CausedBy))
                    .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase))))
            .ToArray();
        var moduleItems = moduleDetails.Select(module => ActionItem.FromAffectedItem(module.Item, module.Metadata)).ToArray();
        var entrypoints = result.Entrypoints.Select(item => ActionItem.FromAffectedItem(item, PublishVersionMetadata.Empty));
        var targets = target switch
        {
            ActionTarget.Build => entrypoints.Concat(moduleItems),
            ActionTarget.Deploy => entrypoints,
            ActionTarget.Publish => moduleItems.Where(item => item.HasVersionChange && item.VersionFile is not null && item.Version is not null),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };

        return new ActionSelection(
            target,
            result.ChangedFiles.Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray(),
            targets.GroupBy(item => item.Path, StringComparer.Ordinal)
                .Select(group => group.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.ArtifactName, StringComparer.Ordinal).First())
                .OrderBy(item => item.Path, StringComparer.Ordinal)
                .ToArray(),
            result.Warnings.Concat(moduleDetails.SelectMany(module => module.Metadata.Warnings))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(warning => warning, StringComparer.Ordinal)
                .ToArray());
    }
}

internal sealed record ActionPayload(
    int SchemaVersion,
    string Target,
    bool HasTargets,
    int TargetCount,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<ActionItem> Targets,
    IReadOnlyList<string> Warnings)
{
    public static ActionPayload FromSelection(ActionSelection selection) => new(
        SchemaVersion: 3,
        Target: selection.Target.ToString().ToLowerInvariant(),
        HasTargets: selection.Targets.Count > 0,
        TargetCount: selection.Targets.Count,
        ChangedFiles: selection.ChangedFiles,
        Targets: selection.Targets,
        Warnings: selection.Warnings);
}

internal sealed record ActionItem(
    string Path,
    string Kind,
    string Directory,
    string FileName,
    string ArtifactName,
    string? VersionFile,
    string? Version,
    string? VersionTag,
    bool HasVersionChange,
    IReadOnlyList<ActionReason> Reasons)
{
    public static ActionItem FromAffectedItem(AffectedItem item, PublishVersionMetadata metadata)
    {
        var normalizedPath = PathNormalizer.NormalizeSeparators(item.Path);
        var directory = System.IO.Path.GetDirectoryName(normalizedPath)?.Replace('\\', '/') ?? string.Empty;
        return new ActionItem(
            normalizedPath,
            ToCamelCase(item.Kind),
            directory,
            System.IO.Path.GetFileNameWithoutExtension(normalizedPath),
            CreateArtifactName(normalizedPath),
            metadata.VersionFile,
            metadata.Version,
            metadata.VersionTag,
            item.Reasons.Any(reason => reason.Kind is "metadataChange" or "versionFileChange"),
            item.Reasons.OrderBy(reason => reason.Kind, StringComparer.Ordinal)
                .ThenBy(reason => reason.CausedBy, StringComparer.Ordinal)
                .ThenBy(reason => reason.Message, StringComparer.Ordinal)
                .Select(ActionReason.FromReason).ToArray());
    }

    private static string ToCamelCase(NodeKind kind) => char.ToLowerInvariant(kind.ToString()[0]) + kind.ToString()[1..];

    private static string CreateArtifactName(string path)
    {
        var stem = new string((System.IO.Path.ChangeExtension(path, null) ?? path)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-').ToArray()).Trim('-');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..12];
        return $"{(stem.Length == 0 ? "bicep" : stem)}-{hash}-bicep";
    }
}

internal sealed record ActionReason(string Kind, string CausedBy, IReadOnlyList<string> Chain, string Message)
{
    public static ActionReason FromReason(Reason reason) => new(reason.Kind, reason.CausedBy, reason.Chain.ToArray(), reason.Message);
}

internal sealed record PublishVersionMetadata(string? VersionFile, string? Version, string? VersionTag, IReadOnlyList<string> Warnings)
{
    private static readonly Regex SemVerPattern = new(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-((0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?\z", RegexOptions.CultureInvariant);
    public static readonly PublishVersionMetadata Empty = new(null, null, null, []);

    public static PublishVersionMetadata Read(string repoRoot, string modulePath, IReadOnlyList<string> publishVersionFiles, IReadOnlySet<string> changedVersionFileNames)
    {
        if (changedVersionFileNames.Count == 0) return Empty;
        var directory = Path.GetDirectoryName(modulePath)?.Replace('\\', '/') ?? string.Empty;
        var moduleName = Path.GetFileNameWithoutExtension(modulePath);
        var warnings = new List<string>();
        var configuredFiles = publishVersionFiles.Where(changedVersionFileNames.Contains).ToArray();
        if (configuredFiles.Length == 0)
            return new(null, null, null, [$"Changed publish version metadata for module '{modulePath}' is not configured as a publish version file."]);

        foreach (var versionFileName in configuredFiles)
        {
            var relativePath = PathNormalizer.ResolvePathWithinRoot(repoRoot, Path.Combine(directory, versionFileName));
            if (relativePath is null) continue;
            var absolutePath = Path.Combine(repoRoot, relativePath);
            if (!File.Exists(absolutePath))
            {
                warnings.Add($"Changed publish version metadata '{relativePath}' for module '{modulePath}' could not be read because it does not exist.");
                continue;
            }
            if (TryReadVersion(absolutePath, moduleName, out var version)) return new(relativePath, version, ToVersionTag(version), warnings);
            warnings.Add($"Changed publish version metadata '{relativePath}' for module '{modulePath}' does not contain a valid SemVer version.");
        }
        return new(null, null, null, warnings);
    }

    private static bool TryReadVersion(string path, string moduleName, out string version)
    {
        version = string.Empty;
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in root.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && string.Equals(name.GetString(), moduleName, StringComparison.OrdinalIgnoreCase))
                        return item.TryGetProperty("version", out var candidate) && TryFormatVersion(candidate, out version);
                return false;
            }
            if (root.ValueKind != JsonValueKind.Object || (root.TryGetProperty("name", out var rootName) && !string.Equals(rootName.GetString(), moduleName, StringComparison.OrdinalIgnoreCase))) return false;
            return root.TryGetProperty("version", out var nested) ? TryFormatVersion(nested, out version) : TryFormatVersion(root, out version);
        }
        catch (JsonException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static bool TryFormatVersion(JsonElement element, out string version)
    {
        version = string.Empty;
        if (element.ValueKind == JsonValueKind.String)
        {
            var value = element.GetString();
            if (string.IsNullOrWhiteSpace(value) || !SemVerPattern.IsMatch(value)) return false;
            version = value;
            return true;
        }
        if (element.ValueKind != JsonValueKind.Object || !TryGetInt(element, "major", out var major) || !TryGetInt(element, "minor", out var minor) || !(TryGetInt(element, "hotfix", out var patch) || TryGetInt(element, "patch", out patch))) return false;
        version = $"{major}.{minor}.{patch}";
        if ((TryGetString(element, "postFix", out var postfix) || TryGetString(element, "postfix", out postfix)) && postfix.Trim().TrimStart('-').Length > 0) version += $"-{postfix.Trim().TrimStart('-')}";
        return SemVerPattern.IsMatch(version);
    }

    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= 0;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return true;
    }
    private static string ToVersionTag(string version) => version.StartsWith('v') ? version : $"v{version}";
}
