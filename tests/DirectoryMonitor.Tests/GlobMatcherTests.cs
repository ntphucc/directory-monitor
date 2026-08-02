using DirectoryMonitor.Infrastructure.Monitoring;

namespace DirectoryMonitor.Tests;

public sealed class GlobMatcherTests
{
    [Theory]
    [InlineData("reports/2026/a.txt", "**/*.txt", true)]
    [InlineData("a.txt", "**/*.txt", true)]
    [InlineData("reports/2026/a.csv", "**/*.txt", false)]
    [InlineData("node_modules/pkg/a.js", "node_modules/**", true)]
    public void Glob_patterns_are_matched_case_insensitively(string path, string pattern, bool expected)
        => Assert.Equal(expected, GlobMatcher.IsMatch(path, [pattern], []));

    [Fact]
    public void Exclude_has_precedence_over_include()
        => Assert.False(GlobMatcher.IsMatch("cache/a.txt", ["**/*.txt"], ["cache/**"]));
}
