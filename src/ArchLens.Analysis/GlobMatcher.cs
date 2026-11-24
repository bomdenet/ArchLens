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
        patterns.Any(p => IsMatch(input, p));
}
