using System.Text.Json;
using System.Text.Json.Serialization;
using BicepAffected.Core.IO;

namespace BicepAffected.Core.Config;

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static BicepAffectedConfig Load(string repoRoot, string? configPath = null)
    {
        var root = PathNormalizer.NormalizeRoot(repoRoot);
        var resolvedPath = ResolveConfigPath(root, configPath);
        if (resolvedPath is null)
        {
            return BicepAffectedConfig.Default();
        }

        if (!File.Exists(resolvedPath))
        {
            if (!string.IsNullOrWhiteSpace(configPath))
            {
                throw new FileNotFoundException($"Configuration file '{configPath}' does not exist.", resolvedPath);
            }

            return BicepAffectedConfig.Default();
        }

        var json = File.ReadAllText(resolvedPath);
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        ValidateCollections(document.RootElement);

        return (JsonSerializer.Deserialize<BicepAffectedConfig>(json, Options)
            ?? throw new JsonException("Configuration root must be an object.")).WithDefaults();
    }

    private static string? ResolveConfigPath(string root, string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            var relativePath = PathNormalizer.ResolvePathWithinRoot(root, configPath)
                ?? throw new ArgumentException($"Configuration path '{configPath}' must be inside the repository.", nameof(configPath));
            return Path.Combine(root, relativePath);
        }

        var defaultPath = PathNormalizer.ResolvePathWithinRoot(root, "bicep-affected.json");
        if (defaultPath is null && File.Exists(Path.Combine(root, "bicep-affected.json")))
        {
            throw new IOException("Default configuration path escapes the repository.");
        }

        return defaultPath is null ? null : Path.Combine(root, defaultPath);
    }

    private static void ValidateCollections(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Configuration root must be an object.");
        }

        foreach (var property in root.EnumerateObject())
        {
            var isStringCollection =
                property.Name.Equals("entrypoints", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("helpers", StringComparison.OrdinalIgnoreCase) ||
                property.Name.Equals("globalImpactFiles", StringComparison.OrdinalIgnoreCase);
            var isPublishableModuleCollection =
                property.Name.Equals("publishableModules", StringComparison.OrdinalIgnoreCase);
            if (!isStringCollection && !isPublishableModuleCollection)
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException($"Configuration property '{property.Name}' must be an array.");
            }

            foreach (var item in property.Value.EnumerateArray())
            {
                if (isStringCollection)
                {
                    if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        throw new JsonException($"Configuration property '{property.Name}' must contain non-empty strings.");
                    }

                    continue;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Configuration property 'publishableModules' must contain objects.");
                }

                ValidateOptionalNonEmptyString(item, "path");
                ValidateOptionalNonEmptyString(item, "metadata");
            }
        }
    }

    private static void ValidateOptionalNonEmptyString(JsonElement item, string propertyName)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (!property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                throw new JsonException($"Publishable module property '{propertyName}' must be a non-empty string.");
            }

            return;
        }
    }
}
