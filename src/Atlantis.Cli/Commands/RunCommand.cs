using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class RunCommand
{
    public static async Task<int> RunAsync(string? project, string? path, string? configuration, bool verbose, string[] passthroughArgs)
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
        await FrontendCompiler.EnsureCompiledAsync(Path.Combine(Path.GetDirectoryName(projectPath)!, "frontend"));

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
}
