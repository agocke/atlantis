using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class RunCommand
{
    public static async Task<int> RunAsync(string? project, string? configuration, bool verbose, string[] passthroughArgs)
    {
        // Auto-detect project if not specified
        var projectPath = project ?? FindProject();
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: No .csproj file found. Specify with --project or run from project directory.");
            return 1;
        }

        var args = new List<string>
        {
            "run",
            "--project", projectPath
        };

        if (!string.IsNullOrEmpty(configuration))
        {
            args.Add("-c");
            args.Add(configuration);
        }

        if (!verbose)
        {
            args.Add("--nologo");
        }

        // Add any passthrough arguments after --
        if (passthroughArgs.Length > 0)
        {
            args.Add("--");
            args.AddRange(passthroughArgs);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
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
        await process.WaitForExitAsync();

        return process.ExitCode;
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
