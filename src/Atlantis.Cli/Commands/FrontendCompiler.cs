using System.Diagnostics;

namespace Atlantis.Cli.Commands;

/// <summary>
/// Compiles the generated TypeScript bridge (frontend/atlantis.ts) into the
/// browser-loadable runtime (atlantis.js) and ambient editor types (atlantis.d.ts)
/// using tsc. The Photino webview can only execute JavaScript, so the .ts is a
/// source-of-truth that must be transpiled before it is embedded and served.
/// </summary>
internal static class FrontendCompiler
{
    public const string SourceFileName = "atlantis.ts";
    public const string OutputFileName = "atlantis.js";

    /// <summary>
    /// Compile <paramref name="frontendDir"/>/atlantis.ts with tsc, emitting
    /// atlantis.js and atlantis.d.ts alongside it. Returns false (with a message
    /// written to stderr) if compilation fails or the toolchain is unavailable.
    /// </summary>
    public static async Task<bool> CompileAsync(string frontendDir)
    {
        var sourcePath = Path.Combine(frontendDir, SourceFileName);
        if (!File.Exists(sourcePath))
        {
            Console.Error.WriteLine($"Error: {SourceFileName} not found in '{frontendDir}'.");
            return false;
        }

        // Pin the major version and use --package so npx runs typescript's tsc
        // rather than the unrelated 'tsc' squatting package.
        var tscArgs = new[]
        {
            "--yes", "--package", "typescript@5", "tsc",
            SourceFileName,
            "--declaration",
            "--target", "ES2020",
            "--skipLibCheck",
        };

        var psi = CreateNpxStartInfo(tscArgs, frontendDir);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                "Error: failed to launch the TypeScript compiler. Node.js and npx must be installed to build the frontend bridge.");
            Console.Error.WriteLine($"  {ex.Message}");
            return false;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
            Console.Error.WriteLine($"Error: tsc failed to compile {SourceFileName}.");
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (!string.IsNullOrWhiteSpace(details))
                Console.Error.WriteLine(details.TrimEnd());
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensure atlantis.js is up to date with atlantis.ts. Recompiles only when the
    /// output is missing or older than the source. Used by 'atl run'/'atl build' so
    /// the embedded runtime is current. A failure is non-fatal when atlantis.js
    /// already exists (the stale copy is used with a warning).
    /// </summary>
    public static async Task EnsureCompiledAsync(string frontendDir)
    {
        var sourcePath = Path.Combine(frontendDir, SourceFileName);
        if (!File.Exists(sourcePath))
            return;

        var outputPath = Path.Combine(frontendDir, OutputFileName);
        var outputExists = File.Exists(outputPath);
        if (outputExists && File.GetLastWriteTimeUtc(outputPath) >= File.GetLastWriteTimeUtc(sourcePath))
            return;

        if (await CompileAsync(frontendDir))
            return;

        if (outputExists)
            Console.Error.WriteLine($"warning: using existing {OutputFileName}; it may be out of date.");
    }

    private static ProcessStartInfo CreateNpxStartInfo(IReadOnlyList<string> npxArgs, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        // On Windows npx is a .cmd shim that must be invoked through cmd.exe.
        if (OperatingSystem.IsWindows())
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add("npx");
        }
        else
        {
            psi.FileName = "npx";
        }

        foreach (var arg in npxArgs)
            psi.ArgumentList.Add(arg);

        return psi;
    }
}
