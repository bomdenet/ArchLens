using System.Text.RegularExpressions;

namespace ArchLens.Analysis;

/// <summary>
/// Matches namespace and symbol names against simple glob patterns (* and ?).
/// Example: "MyApp.Domain.*" matches "MyApp.Domain.Order" and "MyApp.Domain.Entities.Order".
/// </summary>
public static class GlobMatcher
{
    public static bool IsMatch(string? input, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern == "*")
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";

        return Regex.IsMatch(input, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public static bool MatchesAny(string? input, IEnumerable<string> patterns) =>
        patterns.Any(p => !string.IsNullOrWhiteSpace(p) && IsMatch(input, p));

    /// <summary>
    /// Rejects regex/bracket syntax such as '[' or ']' that is unsupported by ArchLens '*'/'?' globs
    /// and would otherwise silently match nothing.
    /// </summary>
    public static void ValidatePattern(string? pattern, string context)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return;
        }

        if (pattern.Contains('[', StringComparison.Ordinal) || pattern.Contains(']', StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Invalid glob pattern '{pattern}' in {context}: bracket expressions ('[' / ']') are not supported. Use '*' and '?' wildcards.");
        }
    }
}
