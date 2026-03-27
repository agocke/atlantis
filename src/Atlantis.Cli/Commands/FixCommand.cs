using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class FixCommand
{
    public static async Task<int> RunAsync(string? project, bool dryRun, bool verbose)
    {
        // Auto-detect project if not specified
        var projectPath = project ?? FindProject();
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: No .csproj file found. Specify with --project or run from project directory.");
            return 1;
        }

        Console.WriteLine($"Analyzing {Path.GetFileName(projectPath)}...");

        // Use dotnet format with the Atlantis analyzers
        var args = new List<string>
        {
            "format",
            "analyzers",
            projectPath,
            "--diagnostics", "ATL001", "ATL002", "ATL003"
        };

        if (dryRun)
        {
            args.Add("--verify-no-changes");
        }

        if (verbose)
        {
            args.Add("--verbosity");
            args.Add("detailed");
        }
        else
        {
            args.Add("--verbosity");
            args.Add("quiet");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = !verbose,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (verbose)
        {
            Console.WriteLine($"Running: dotnet {string.Join(" ", args)}");
        }

        using var process = Process.Start(psi)!;

        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            if (dryRun)
            {
                Console.WriteLine("Found issues that can be fixed. Run without --dry-run to apply fixes.");
                // Show stderr if it has useful info
                if (!string.IsNullOrWhiteSpace(stderr) && verbose)
                {
                    Console.Error.WriteLine(stderr);
                }
                return 1;
            }
            
            Console.Error.WriteLine("Fix failed:");
            Console.Error.WriteLine(stderr);
            return process.ExitCode;
        }

        if (dryRun)
        {
            Console.WriteLine("✓ No issues found");
        }
        else
        {
            Console.WriteLine("✓ Fixes applied");
        }
        
        return 0;
    }

    private static string? FindProject()
    {
        var cwd = Directory.GetCurrentDirectory();
        
        // Check current directory
        var projects = Directory.GetFiles(cwd, "*.csproj");
        if (projects.Length == 1)
            return projects[0];

        // Check src subdirectory
        var srcDir = Path.Combine(cwd, "src");
        if (Directory.Exists(srcDir))
        {
            var srcProjects = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories)
                .Where(p => !p.Contains(".Tests") && !p.Contains(".Cli") && !p.Contains(".Analyzers"))
                .ToArray();
            
            if (srcProjects.Length == 1)
                return srcProjects[0];
        }

        return null;
    }
}
