using System.Text.RegularExpressions;

namespace DirectoryMonitor.Infrastructure.Monitoring;

public static class GlobMatcher
{
    public static bool IsMatch(string relativePath, IReadOnlyList<string> includes, IReadOnlyList<string> excludes)
    {
        var normalized = relativePath.Replace('\\', '/');
        if (excludes.Any(pattern => Match(normalized, pattern))) return false;
        return includes.Count == 0 || includes.Any(pattern => Match(normalized, pattern));
    }

    public static bool IsValid(string pattern)
    {
        try { _ = BuildRegex(pattern); return !string.IsNullOrWhiteSpace(pattern); }
        catch { return false; }
    }

    private static bool Match(string value, string pattern) => Regex.IsMatch(value, BuildRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string BuildRegex(string pattern)
    {
        var escaped = Regex.Escape(pattern.Trim().Replace('\\', '/'))
            .Replace(@"\*\*/", "(?:.*/)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]");
        return "^" + escaped + "$";
    }
}
