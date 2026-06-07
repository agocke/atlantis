using Atlantis.Cli.Commands;

namespace Atlantis.Cli.Tests;

/// <summary>
/// Pure selection logic — no filesystem access.
/// </summary>
public class SelectProjectTests
{
    [Fact]
    public void ReturnsSingleProjectInCurrentDirectory()
    {
        var result = FixCommand.SelectProject(["/repo/MyApp.csproj"], []);

        Assert.Equal("/repo/MyApp.csproj", result);
    }

    [Fact]
    public void PrefersCurrentDirectory_OverSrc()
    {
        var result = FixCommand.SelectProject(
            ["/repo/MyApp.csproj"],
            ["/repo/src/Other/Other.csproj"]);

        Assert.Equal("/repo/MyApp.csproj", result);
    }

    [Fact]
    public void ReturnsNull_WhenMultipleProjectsInCurrentDirectory()
    {
        var result = FixCommand.SelectProject(
            ["/repo/First.csproj", "/repo/Second.csproj"],
            []);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsNull_WhenNothingFound()
    {
        var result = FixCommand.SelectProject([], []);

        Assert.Null(result);
    }

    [Fact]
    public void ReturnsSingleBuildableProjectUnderSrc()
    {
        var result = FixCommand.SelectProject(
            [],
            ["/repo/src/MyApp/MyApp.csproj"]);

        Assert.Equal("/repo/src/MyApp/MyApp.csproj", result);
    }

    [Fact]
    public void ReturnsNull_WhenMultipleBuildableProjectsUnderSrc()
    {
        var result = FixCommand.SelectProject(
            [],
            ["/repo/src/AppA/AppA.csproj", "/repo/src/AppB/AppB.csproj"]);

        Assert.Null(result);
    }

    [Fact]
    public void ExcludesTestsCliAndAnalyzerProjectsUnderSrc()
    {
        var result = FixCommand.SelectProject(
            [],
            [
                "/repo/src/MyApp/MyApp.csproj",
                "/repo/src/MyApp.Tests/MyApp.Tests.csproj",
                "/repo/src/MyApp.Cli/MyApp.Cli.csproj",
                "/repo/src/MyApp.Analyzers/MyApp.Analyzers.csproj",
            ]);

        Assert.Equal("/repo/src/MyApp/MyApp.csproj", result);
    }

    [Theory]
    [InlineData("/repo/src/MyApp/MyApp.csproj", true)]
    [InlineData("/repo/src/MyApp.Tests/MyApp.Tests.csproj", false)]
    [InlineData("/repo/src/MyApp.Cli/MyApp.Cli.csproj", false)]
    [InlineData("/repo/src/MyApp.Analyzers/MyApp.Analyzers.csproj", false)]
    public void IsBuildableProject_ClassifiesByProjectKind(string path, bool expected)
    {
        Assert.Equal(expected, FixCommand.IsBuildableProject(path));
    }
}

/// <summary>
/// Pure content generation — no filesystem access.
/// </summary>
public class BuildAnalyzerTargetsTests
{
    [Fact]
    public void IncludesAnalyzerPath()
    {
        var content = FixCommand.BuildAnalyzerTargets("/tools/Atlantis.Analyzers.dll");

        Assert.Contains("<Analyzer Include=\"/tools/Atlantis.Analyzers.dll\" />", content);
    }

    [Fact]
    public void ProducesWellFormedProjectElement()
    {
        var content = FixCommand.BuildAnalyzerTargets("/tools/Atlantis.Analyzers.dll");

        Assert.StartsWith("<Project>", content);
        Assert.EndsWith("</Project>", content.TrimEnd());
    }
}

/// <summary>
/// I/O-backed tests that exercise actual filesystem behavior.
/// </summary>
public class FixCommandIoTests
{
    [Fact]
    public void FindProject_EnumeratesProjectUnderSrc()
    {
        using var dir = new TempDir();
        var proj = dir.CreateFile(Path.Combine("src", "MyApp", "MyApp.csproj"));

        var result = FixCommand.FindProject(dir.Path);

        Assert.Equal(proj, result);
    }

    [Fact]
    public void FindProject_ReturnsNull_WhenDirectoryEmpty()
    {
        using var dir = new TempDir();

        var result = FixCommand.FindProject(dir.Path);

        Assert.Null(result);
    }

    [Fact]
    public void EnsureAnalyzerTargets_CreatesFile_WhenMissing()
    {
        using var dir = new TempDir();

        var created = FixCommand.EnsureAnalyzerTargets(dir.Path, "/tools/Atlantis.Analyzers.dll");

        var targetsPath = Path.Combine(dir.Path, "Directory.Build.targets");
        Assert.True(created);
        Assert.Equal(
            FixCommand.BuildAnalyzerTargets("/tools/Atlantis.Analyzers.dll"),
            File.ReadAllText(targetsPath));
    }

    [Fact]
    public void EnsureAnalyzerTargets_DoesNotOverwrite_WhenFileExists()
    {
        using var dir = new TempDir();
        var existing = "<Project><!-- user content --></Project>";
        dir.CreateFile("Directory.Build.targets", existing);

        var created = FixCommand.EnsureAnalyzerTargets(dir.Path, "/tools/Atlantis.Analyzers.dll");

        var targetsPath = Path.Combine(dir.Path, "Directory.Build.targets");
        Assert.False(created);
        Assert.Equal(existing, File.ReadAllText(targetsPath));
    }
}

/// <summary>
/// Creates a unique temporary directory and removes it on dispose.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "atl-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string CreateFile(string relativePath, string contents = "")
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
