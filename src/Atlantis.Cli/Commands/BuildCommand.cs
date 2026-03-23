using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class BuildCommand
{
    public static async Task<int> RunAsync(string? project, string? rid, string configuration, bool verbose)
    {
        // Auto-detect project if not specified
        var projectPath = project ?? FindProject();
        if (projectPath == null)
        {
            Console.Error.WriteLine("Error: No .csproj file found. Specify with --project or run from project directory.");
            return 1;
        }

        Console.WriteLine($"Building {Path.GetFileName(projectPath)}...");

        var args = new List<string>
        {
            "publish",
            projectPath,
            "-c", configuration
        };

        if (!string.IsNullOrEmpty(rid))
        {
            args.Add("-r");
            args.Add(rid);
        }

        if (!verbose)
        {
            args.Add("-v");
            args.Add("quiet");
            args.Add("--nologo");
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

        using var process = Process.Start(psi)!;
        
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine("Build failed:");
            Console.Error.WriteLine(stderr);
            return process.ExitCode;
        }

        // Find output
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var outputDir = FindPublishOutput(projectDir, configuration, rid);

        Console.WriteLine();
        Console.WriteLine($"✓ Build succeeded");
        if (outputDir != null)
        {
            Console.WriteLine($"  Output: {outputDir}");
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
                .Where(p => !p.Contains(".Tests") && !p.Contains(".Cli"))
                .ToArray();
            
            if (srcProjects.Length == 1)
                return srcProjects[0];
        }

        return null;
    }

    private static string? FindPublishOutput(string projectDir, string config, string? rid)
    {
        var binDir = Path.Combine(projectDir, "bin", config);
        
        if (!Directory.Exists(binDir))
            return null;

        // Look for publish folder
        var publishDirs = Directory.GetDirectories(binDir, "publish", SearchOption.AllDirectories);
        
        if (rid != null)
        {
            var ridPublish = publishDirs.FirstOrDefault(d => d.Contains(rid));
            if (ridPublish != null) return ridPublish;
        }

        return publishDirs.FirstOrDefault();
    }
}
