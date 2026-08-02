using System.Xml.Linq;

namespace DirectoryMonitor.Tests;

public sealed class RepositoryQualityTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void Public_repository_contains_professional_governance_and_automation_files()
    {
        string[] requiredFiles =
        [
            ".editorconfig",
            ".gitattributes",
            ".github/CODEOWNERS",
            ".github/dependabot.yml",
            ".github/pull_request_template.md",
            ".github/workflows/ci.yml",
            ".github/workflows/codeql.yml",
            "CHANGELOG.md",
            "CONTRIBUTING.md",
            "Directory.Packages.props",
            "SECURITY.md",
            "docs/ARCHITECTURE.md",
            "global.json"
        ];

        foreach (var relativePath in requiredFiles)
        {
            Assert.True(File.Exists(Path.Combine(RepositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))),
                $"Required repository file is missing: {relativePath}");
        }
    }

    [Fact]
    public void Ci_uses_a_locked_least_privilege_windows_build_with_test_artifacts()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "ci.yml"));

        Assert.Contains("runs-on: windows-latest", workflow);
        Assert.Contains("permissions:\n  contents: read", workflow.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Contains("actions/checkout@v6", workflow);
        Assert.Contains("actions/setup-dotnet@v4", workflow);
        Assert.Contains("dotnet restore DirectoryMonitor.slnx --locked-mode", workflow);
        Assert.Contains("dotnet build DirectoryMonitor.slnx", workflow);
        Assert.Contains("dotnet test DirectoryMonitor.slnx", workflow);
        Assert.Contains("XPlat Code Coverage", workflow);
        Assert.Contains("actions/upload-artifact@v4", workflow);
    }

    [Fact]
    public void Package_versions_are_centralized_and_every_project_has_a_lock_file()
    {
        var centralPackagesPath = Path.Combine(RepositoryRoot, "Directory.Packages.props");
        var centralPackages = XDocument.Load(centralPackagesPath);
        Assert.True(centralPackages.Descendants("PackageVersion").Count() >= 10);

        var projects = new[] { "src", "tests" }
            .SelectMany(folder => Directory.GetFiles(Path.Combine(RepositoryRoot, folder), "*.csproj", SearchOption.AllDirectories))
            .Where(path => !ContainsBuildDirectory(path))
            .ToArray();

        Assert.Equal(5, projects.Length);
        foreach (var project in projects)
        {
            var document = XDocument.Load(project);
            Assert.All(document.Descendants("PackageReference"), reference => Assert.Null(reference.Attribute("Version")));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")),
                $"Lock file is missing beside {project}");
        }
    }

    [Fact]
    public void Build_treats_warnings_as_errors_and_xml_import_prohibits_dtd_processing()
    {
        var buildProperties = File.ReadAllText(Path.Combine(RepositoryRoot, "Directory.Build.props"));
        var importExport = File.ReadAllText(Path.Combine(RepositoryRoot, "src", "DirectoryMonitor.Infrastructure", "Transfer", "ImportExportService.cs"));

        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", buildProperties);
        Assert.Contains("<EnableNETAnalyzers>true</EnableNETAnalyzers>", buildProperties);
        Assert.Contains("<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>", buildProperties);
        Assert.Contains("DtdProcessing = DtdProcessing.Prohibit", importExport);
        Assert.Contains("XmlResolver = null", importExport);
    }

    [Fact]
    public void Gitignore_excludes_runtime_data_secrets_and_private_exports()
    {
        var gitignore = File.ReadAllText(Path.Combine(RepositoryRoot, ".gitignore"));
        string[] requiredPatterns = ["**/bin/", "**/obj/", "*.db", "*.db-wal", "*.log", "*.dm", ".env", "*.pfx", "*.key"];
        Assert.All(requiredPatterns, pattern => Assert.Contains(pattern, gitignore));
    }

    private static bool ContainsBuildDirectory(string path)
    {
        var normalized = path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        return normalized.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "DirectoryMonitor.slnx"))) return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the Directory Monitor repository root.");
    }
}
