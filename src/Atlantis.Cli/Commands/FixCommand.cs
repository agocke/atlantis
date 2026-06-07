using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class FixCommand
{
    public static async Task<int> RunAsync(string? project, bool dryRun, bool verbose)
    {
        // Auto-detect project if not specified
        var projectPath = project ?? FindProject(Directory.GetCurrentDirectory());
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: No .csproj file found. Specify with --project or run from project directory.");
            return 1;
        }

        // Find the analyzer DLL bundled with the CLI
        var cliDir = AppContext.BaseDirectory;
        var analyzerPath = Path.Combine(cliDir, "Atlantis.Analyzers.dll");
        
        if (!File.Exists(analyzerPath))
        {
            Console.Error.WriteLine($"Error: Analyzer not found at {analyzerPath}");
            return 1;
        }

        Console.WriteLine($"Analyzing {Path.GetFileName(projectPath)}...");

        // Add analyzer reference via Directory.Build.targets if not already present
        var projectDir = Path.GetDirectoryName(projectPath)!;

        if (!dryRun)
        {
            var created = EnsureAnalyzerTargets(projectDir, analyzerPath);
            if (created && verbose)
                Console.WriteLine($"Created Directory.Build.targets with analyzer reference");
        }

        // Run Atlantis analyzers
        var result = await RunFormatAsync(
            "analyzers", projectPath, 
            ["ATL001", "ATL002", "ATL003"], 
            dryRun, verbose);

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
    /// Builds the contents of a Directory.Build.targets that adds the Atlantis analyzer.
    /// </summary>
    internal static string BuildAnalyzerTargets(string analyzerPath) =>
        $"""
        <Project>
          <ItemGroup>
            <Analyzer Include="{analyzerPath}" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>
    /// Creates a Directory.Build.targets in <paramref name="projectDir"/> that adds the
    /// Atlantis analyzer, unless one already exists. Returns true if a file was created.
    /// </summary>
    internal static bool EnsureAnalyzerTargets(string projectDir, string analyzerPath)
    {
        var targetsPath = Path.Combine(projectDir, "Directory.Build.targets");
        if (File.Exists(targetsPath))
            return false;

        File.WriteAllText(targetsPath, BuildAnalyzerTargets(analyzerPath));
        return true;
    }

    private static async Task<int> RunFormatAsync(
        string subcommand, 
        string projectPath, 
        string[] diagnostics, 
        bool dryRun, 
        bool verbose)
    {
        var args = new List<string> { "format", subcommand, projectPath, "--diagnostics" };
        args.AddRange(diagnostics);

        if (dryRun)
            args.Add("--verify-no-changes");

        args.Add("--verbosity");
        args.Add(verbose ? "detailed" : "quiet");

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

        if (process.ExitCode != 0 && !dryRun)
        {
            Console.Error.WriteLine(stderr);
        }

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
