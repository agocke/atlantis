using System.Diagnostics;

namespace Atlantis.Cli.Commands;

/// <summary>
/// Compiles the generated TypeScript bridge into the browser-loadable runtime
/// (atlantis.js) and ambient editor types (atlantis.d.ts) using tsc. The source is
/// split into the invariant framework runtime (atlantis.runtime.ts) and one
/// generated bindings file per exported source file (&lt;name&gt;.bindings.ts); tsc
/// concatenates them all into a single atlantis.js via --outFile. The webview can
/// only execute JavaScript, so the .ts files are the source-of-truth that must be
/// transpiled before they are embedded and served.
/// </summary>
internal static class FrontendCompiler
{
    public const string RuntimeFileName = "atlantis.runtime.ts";
    public const string OutputFileName = "atlantis.js";
    public const string BindingsSearchPattern = "*.bindings.ts";

    // The TypeScript sources, in concatenation order: the runtime must come first so
    // its declarations (atlantis, _atlRegister) exist before any bindings use them,
    // followed by the per-file bindings sorted for deterministic output.
    private static IReadOnlyList<string> SourceFileNames(string frontendDir)
    {
        var sources = new List<string> { RuntimeFileName };
        if (Directory.Exists(frontendDir))
        {
            sources.AddRange(Directory.EnumerateFiles(frontendDir, BindingsSearchPattern)
                .Select(Path.GetFileName)
                .OfType<string>()
                .OrderBy(n => n, StringComparer.Ordinal));
        }
        return sources;
    }

    /// <summary>
    /// Compile the bridge sources in <paramref name="frontendDir"/> with tsc, emitting
    /// a single atlantis.js and atlantis.d.ts alongside them. Returns false (with a
    /// message written to stderr) if compilation fails or the toolchain is unavailable.
    /// </summary>
    public static async Task<bool> CompileAsync(string frontendDir)
    {
        if (!File.Exists(Path.Combine(frontendDir, RuntimeFileName)))
        {
            Console.Error.WriteLine($"Error: {RuntimeFileName} not found in '{frontendDir}'.");
            return false;
        }

        // Pin the major version and use --package so npx runs typescript's tsc
        // rather than the unrelated 'tsc' squatting package. --outFile concatenates
        // the (module-free) sources into one script, so --module none is required.
        var tscArgs = new List<string>
        {
            "--yes", "--package", "typescript@5", "tsc",
        };
        tscArgs.AddRange(SourceFileNames(frontendDir));
        tscArgs.AddRange(new[]
        {
            "--outFile", OutputFileName,
            "--module", "none",
            "--declaration",
            "--target", "ES2020",
            "--skipLibCheck",
        });

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
            Console.Error.WriteLine($"Error: tsc failed to compile the {OutputFileName} bridge.");
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (!string.IsNullOrWhiteSpace(details))
                Console.Error.WriteLine(details.TrimEnd());
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ensure atlantis.js is up to date with its TypeScript sources. Recompiles only
    /// when the output is missing or older than any source. Used by 'atl run'/'atl
    /// build' so the embedded runtime is current. A failure is non-fatal when
    /// atlantis.js already exists (the stale copy is used with a warning).
    /// </summary>
    public static async Task EnsureCompiledAsync(string frontendDir)
    {
        if (!File.Exists(Path.Combine(frontendDir, RuntimeFileName)))
            return;

        var sourcePaths = SourceFileNames(frontendDir).Select(s => Path.Combine(frontendDir, s)).ToArray();

        var outputPath = Path.Combine(frontendDir, OutputFileName);
        var outputExists = File.Exists(outputPath);
        if (outputExists)
        {
            var outputTime = File.GetLastWriteTimeUtc(outputPath);
            if (sourcePaths.All(p => outputTime >= File.GetLastWriteTimeUtc(p)))
                return;
        }

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
