using System.Diagnostics;
using System.Xml;
using System.Xml.Linq;

namespace Atlantis.Cli.Commands;

public static class FixCommand
{
    internal const string AnalyzerPackageId = "Atlantis.Analyzers";

    public static async Task<int> RunAsync(string? project, bool dryRun, bool verbose)
    {
        // Auto-detect project if not specified
        var projectPath = project ?? FindProject(Directory.GetCurrentDirectory());
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: No .csproj file found. Specify with --project or run from project directory.");
            return 1;
        }

        if (!File.Exists(projectPath))
        {
            Console.Error.WriteLine($"Error: Project not found: {projectPath}");
            return 1;
        }

        Console.WriteLine($"Analyzing {Path.GetFileName(projectPath)}...");

        // The analyzers/code fixes ship as a NuGet package. dotnet format only runs
        // analyzers that the target project references, so ensure the reference exists.
        if (!HasAnalyzerPackageReference(await File.ReadAllTextAsync(projectPath)))
        {
            if (dryRun)
            {
                Console.WriteLine($"Project does not reference {AnalyzerPackageId}. Run without --dry-run to add it and apply fixes.");
                return 1;
            }
            else
            {
                if (verbose)
                    Console.WriteLine($"Adding {AnalyzerPackageId} {ToolVersion.Current} package reference...");

                var added = await RunDotnetAsync(BuildAddPackageArgs(projectPath, ToolVersion.Current), verbose);
                if (added != 0)
                {
                    Console.Error.WriteLine($"Error: Failed to add {AnalyzerPackageId} package reference.");
                    return added;
                }
            }
        }

        // Run Atlantis analyzers
        var result = await RunDotnetAsync(
            BuildFormatArgs(projectPath, ["ATL001", "ATL002", "ATL003"], dryRun, verbose),
            verbose,
            swallowError: dryRun);

        if (dryRun && result != 0)
        {
            Console.WriteLine("Found issues that can be fixed. Run without --dry-run to apply fixes.");
            return 1;
        }

        if (result != 0)
            return result;

        Console.WriteLine("✓ Fixes applied");
        return 0;
    }

    /// <summary>
    /// True if the csproj already declares a &lt;PackageReference&gt; to the Atlantis
    /// analyzers. Pure (no I/O) so it can be unit tested directly. Malformed XML is
    /// treated as "not referenced".
    /// </summary>
    internal static bool HasAnalyzerPackageReference(string csprojContents)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(csprojContents);
        }
        catch (XmlException)
        {
            return false;
        }

        return doc.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Any(e => string.Equals(
                (string?)e.Attribute("Include"),
                AnalyzerPackageId,
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Builds the <c>dotnet add</c> argument list that pins the analyzer package to a
    /// specific version. The package is a development dependency, so NuGet adds it with
    /// <c>PrivateAssets="all"</c> automatically.
    /// </summary>
    internal static IReadOnlyList<string> BuildAddPackageArgs(string projectPath, string version) =>
        ["add", projectPath, "package", AnalyzerPackageId, "--version", version];

    /// <summary>
    /// Builds the <c>dotnet format analyzers</c> argument list for the given diagnostics.
    /// </summary>
    internal static IReadOnlyList<string> BuildFormatArgs(
        string projectPath,
        IReadOnlyList<string> diagnostics,
        bool dryRun,
        bool verbose)
    {
        var args = new List<string> { "format", "analyzers", projectPath, "--diagnostics" };
        args.AddRange(diagnostics);

        if (dryRun)
            args.Add("--verify-no-changes");

        args.Add("--verbosity");
        args.Add(verbose ? "detailed" : "quiet");
        return args;
    }

    private static async Task<int> RunDotnetAsync(
        IReadOnlyList<string> args,
        bool verbose,
        bool swallowError = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = !verbose,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        if (verbose)
            Console.WriteLine($"Running: dotnet {string.Join(" ", args)}");

        using var process = Process.Start(psi)!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && !swallowError && !string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine(stderr);

        return process.ExitCode;
    }

    /// <summary>
    /// Locates a single buildable project starting from <paramref name="cwd"/> by
    /// enumerating candidates, then applying <see cref="SelectProject"/>.
    /// </summary>
    internal static string? FindProject(string cwd)
    {
        var cwdProjects = Directory.GetFiles(cwd, "*.csproj");

        var srcDir = Path.Combine(cwd, "src");
        var srcProjects = Directory.Exists(srcDir)
            ? Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
            : [];

        return SelectProject(cwdProjects, srcProjects);
    }

    /// <summary>
    /// Pure project-selection rule: prefer a single project in the current directory,
    /// otherwise a single buildable project under <c>src</c>. Returns null when the
    /// choice is ambiguous or empty. Kept I/O-free so it can be unit tested directly.
    /// </summary>
    internal static string? SelectProject(
        IReadOnlyList<string> cwdProjects,
        IReadOnlyList<string> srcProjects)
    {
        if (cwdProjects.Count == 1)
            return cwdProjects[0];

        var buildable = srcProjects.Where(IsBuildableProject).ToArray();
        if (buildable.Length == 1)
            return buildable[0];

        return null;
    }

    /// <summary>
    /// A project is "buildable" (a fix target) if it is not a test, CLI, or analyzer project.
    /// </summary>
    internal static bool IsBuildableProject(string path) =>
        !path.Contains(".Tests") && !path.Contains(".Cli") && !path.Contains(".Analyzers");
}
