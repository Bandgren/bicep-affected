using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace BicepAffected.Core.IO;

public sealed class GlobPattern
{
    private static readonly ConcurrentDictionary<string, Regex> RegexCache = new(PathNormalizer.PathComparer);
    private const int MaxCachedPatterns = 256;
    private static readonly RegexOptions MatcherOptions = RegexOptions.CultureInvariant
        | RegexOptions.Compiled
        | RegexOptions.NonBacktracking
        | (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? RegexOptions.IgnoreCase : RegexOptions.None);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex regex;

    public GlobPattern(string pattern)
    {
        Pattern = NormalizePattern(pattern);
        regex = GetRegex(Pattern);
    }

    public string Pattern { get; }

    public bool IsMatch(string path)
    {
        return regex.IsMatch(NormalizePath(path));
    }

    public static bool IsMatch(string pattern, string path)
    {
        return GetRegex(NormalizePattern(pattern)).IsMatch(NormalizePath(path));
    }

    public static bool MatchesAny(IEnumerable<string> patterns, string path)
    {
        return patterns.Any(pattern => IsMatch(pattern, path));
    }

    private static Regex GetRegex(string normalizedPattern)
    {
        if (RegexCache.TryGetValue(normalizedPattern, out var cachedRegex))
        {
            return cachedRegex;
        }

        var regex = new Regex(ToRegex(normalizedPattern), MatcherOptions, RegexTimeout);
        return RegexCache.Count < MaxCachedPatterns
            ? RegexCache.GetOrAdd(normalizedPattern, regex)
            : regex;
    }

    private static string NormalizePattern(string pattern)
    {
        return PathNormalizer.NormalizeSeparators(pattern).TrimStart('/');
    }

    private static string NormalizePath(string path)
    {
        return PathNormalizer.NormalizeSeparators(path).TrimStart('/');
    }

    private static string ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");

        for (var i = 0; i < pattern.Length; i++)
        {
            var current = pattern[i];
            if (current == '*')
            {
                var isDoubleStar = i + 1 < pattern.Length && pattern[i + 1] == '*';
                if (isDoubleStar)
                {
                    var followedBySlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                    if (followedBySlash)
                    {
                        builder.Append("(?:.*/)?");
                        i += 2;
                    }
                    else
                    {
                        builder.Append(".*");
                        i++;
                    }
                }
                else
                {
                    builder.Append("[^/]*");
                }
            }
            else if (current == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(current.ToString()));
            }
        }

        builder.Append('$');
        return builder.ToString();
    }
}
