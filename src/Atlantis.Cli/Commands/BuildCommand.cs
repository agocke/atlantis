using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class BuildCommand
{
    public static async Task<int> RunAsync(string? project, string? path, string? rid, string configuration, bool verbose)
    {
        // Resolve the project from an explicit --project, a positional target
        // (directory or .csproj), or the current directory.
        var projectPath = ProjectLocator.Resolve(project ?? path, out var error);
        if (projectPath == null)
        {
            Console.Error.WriteLine($"Error: {error}");
            return 1;
        }

        await ExportScanner.WarnIfBindingsStaleAsync(Path.GetDirectoryName(projectPath)!);

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
