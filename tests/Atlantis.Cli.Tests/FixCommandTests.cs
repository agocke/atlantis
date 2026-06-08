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
/// Pure detection of the analyzer package reference — no filesystem access.
/// </summary>
public class HasAnalyzerPackageReferenceTests
{
    [Fact]
    public void DetectsReference_WhenPresent()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Atlantis.Analyzers" Version="0.1.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(FixCommand.HasAnalyzerPackageReference(csproj));
    }

    [Fact]
    public void DetectsReference_CaseInsensitively()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="atlantis.analyzers" Version="0.1.0" />
              </ItemGroup>
            </Project>
            """;

        Assert.True(FixCommand.HasAnalyzerPackageReference(csproj));
    }

    [Fact]
    public void ReturnsFalse_WhenOnlyOtherPackagesReferenced()
    {
        var csproj = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Photino.NET" Version="4.0.16" />
              </ItemGroup>
            </Project>
            """;

        Assert.False(FixCommand.HasAnalyzerPackageReference(csproj));
    }

    [Fact]
    public void ReturnsFalse_WhenMalformedXml()
    {
        Assert.False(FixCommand.HasAnalyzerPackageReference("<Project> not closed"));
    }
}

/// <summary>
/// Pure argument construction — no process execution.
/// </summary>
public class FixCommandArgsTests
{
    [Fact]
    public void BuildAddPackageArgs_PinsIdAndVersion()
    {
        var args = FixCommand.BuildAddPackageArgs("/repo/MyApp.csproj", "0.1.0-ci.34.abc1234");

        Assert.Equal(
            ["add", "/repo/MyApp.csproj", "package", "Atlantis.Analyzers", "--version", "0.1.0-ci.34.abc1234"],
            args);
    }

    [Fact]
    public void BuildFormatArgs_IncludesDiagnostics()
    {
        var args = FixCommand.BuildFormatArgs("/repo/MyApp.csproj", ["ATL001", "ATL002"], dryRun: false, verbose: false);

        Assert.Equal(
            ["format", "analyzers", "/repo/MyApp.csproj", "--diagnostics", "ATL001", "ATL002", "--verbosity", "quiet"],
            args);
    }

    [Fact]
    public void BuildFormatArgs_AddsVerifyNoChanges_WhenDryRun()
    {
        var args = FixCommand.BuildFormatArgs("/repo/MyApp.csproj", ["ATL001"], dryRun: true, verbose: false);

        Assert.Contains("--verify-no-changes", args);
    }

    [Fact]
    public void BuildFormatArgs_UsesDetailedVerbosity_WhenVerbose()
    {
        var args = FixCommand.BuildFormatArgs("/repo/MyApp.csproj", ["ATL001"], dryRun: false, verbose: true);

        Assert.DoesNotContain("--verify-no-changes", args);
        Assert.Equal("detailed", args[^1]);
    }
}

/// <summary>
/// Pure tool-version normalization — no reflection or I/O.
/// </summary>
public class ToolVersionTests
{
    [Fact]
    public void Normalize_StripsBuildMetadata()
    {
        Assert.Equal("0.1.0-ci.34", ToolVersion.Normalize("0.1.0-ci.34+abc1234"));
    }

    [Fact]
    public void Normalize_PreservesPrereleaseLabel()
    {
        Assert.Equal("0.1.0-ci.34.abc1234", ToolVersion.Normalize("0.1.0-ci.34.abc1234"));
    }

    [Fact]
    public void Normalize_PassesThroughPlainVersion()
    {
        Assert.Equal("0.1.0", ToolVersion.Normalize("0.1.0"));
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
