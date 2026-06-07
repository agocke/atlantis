using System.Diagnostics;

namespace Atlantis.Cli.Commands;

public static class RunCommand
{
    public static async Task<int> RunAsync(string? project, string? configuration, bool verbose, string[] passthroughArgs)
    {
        // Resolve the project from the current directory (atl operates on the
        // project you are standing in). --project overrides.
        var projectPath = project ?? ProjectLocator.FindInCurrentDirectory();
        if (projectPath == null)
        {
            Console.Error.WriteLine($"Error: {ProjectLocator.DescribeResolutionFailure()}");
            return 1;
        }

        await ExportScanner.WarnIfBindingsStaleAsync(Path.GetDirectoryName(Path.GetFullPath(projectPath))!);

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
}
