using System.Reflection;
using System.Text;

namespace Atlantis.Cli.Commands;

public static class GenerateCommand
{
    public static async Task<int> RunAsync(string? source, string? output, string? path)
    {
        // Source may be given as --source, a positional target (a directory or a
        // .csproj, whose directory is used), or default to the current directory.
        var sourceDir = ResolveSourceDir(source ?? path);
        // Bindings belong to the app project's embedded web frontend, so default the
        // output to <project>/frontend rather than the project root.
        var outputDir = output ?? Path.Combine(sourceDir, "frontend");

        Console.WriteLine($"Scanning {sourceDir} for [AtlExport] methods...");

        if (!Directory.Exists(sourceDir))
        {
            Console.Error.WriteLine($"Error: Source directory not found: '{sourceDir}'.");
            return 1;
        }

        var exports = await ExportScanner.ScanAsync(sourceDir);

        // Each exported class is emitted into a bindings file named after its source
        // .cs file, so a class must live in one file and file names must be unique.
        if (TryGetExportError(exports, sourceDir, out var exportError))
        {
            Console.Error.WriteLine(exportError);
            return 1;
        }

        Directory.CreateDirectory(outputDir);

        // The bridge source is split so framework code and your generated bindings
        // never share a file: atlantis.runtime.ts is the invariant framework runtime,
        // while each <SourceFile>.bindings.ts holds only the typed bindings for the
        // classes declared in the matching .cs file. tsc compiles them together into
        // the browser-loadable atlantis.js plus ambient atlantis.d.ts editor types.
        var runtimePath = Path.Combine(outputDir, FrontendCompiler.RuntimeFileName);
        await File.WriteAllTextAsync(runtimePath, LoadTemplate("atlantis_runtime_ts.template").ReplaceLineEndings("\n"));
        Console.WriteLine($"✓ Generated {runtimePath}");

        // Drop bindings from a previous run first so renamed or deleted .cs files
        // don't leave orphaned bindings that still compile into atlantis.js.
        foreach (var stale in Directory.EnumerateFiles(outputDir, "*.bindings.ts"))
            File.Delete(stale);

        var byFile = exports
            .GroupBy(e => e.SourceFile)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (byFile.Count == 0)
        {
            // No [AtlExport] methods, but the bridge still carries the built-in
            // atlantis.dialog/on/off API from the runtime, so emit that alone.
            Console.WriteLine("No [AtlExport] methods found; generating the built-in bridge only.");
        }
        else
        {
            Console.WriteLine($"Found {exports.Count} exported method(s) in {exports.Select(e => e.ClassName).Distinct().Count()} class(es) across {byFile.Count} file(s).");
        }

        foreach (var fileGroup in byFile)
        {
            // Embed a hash of this file's export contract so 'atl run'/'atl build' can
            // detect when its bindings are stale without relying on file timestamps.
            var hash = ExportScanner.ComputeInputHash(fileGroup);
            var baseName = Path.GetFileNameWithoutExtension(fileGroup.Key);
            var bindingsPath = Path.Combine(outputDir, $"{baseName}.bindings.ts");
            await File.WriteAllTextAsync(bindingsPath, GenerateBindings(fileGroup, hash));
            Console.WriteLine($"✓ Generated {bindingsPath}");
        }

        Console.WriteLine("Compiling TypeScript bridge with tsc...");
        if (!await FrontendCompiler.CompileAsync(outputDir))
            return 1;

        Console.WriteLine($"✓ Compiled {Path.Combine(outputDir, "atlantis.js")}");
        Console.WriteLine($"✓ Compiled {Path.Combine(outputDir, "atlantis.d.ts")}");

        return 0;
    }

    // A positional target may be a .csproj (scan its directory) or a directory.
    private static string ResolveSourceDir(string? target)
    {
        if (target == null)
            return Directory.GetCurrentDirectory();

        if (File.Exists(target) && target.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.GetFullPath(target))!;

        return target;
    }

    // The framework runtime lives in Templates/atlantis_runtime_ts.template and is
    // emitted verbatim. Per-file bindings are generated here, into
    // <SourceFile>.bindings.ts: a typed interface augmentation plus a registration
    // body that attaches each class declared in that file onto the atlantis object.
    // Every bindings file flows through tsc into atlantis.js and atlantis.d.ts.
    private static string GenerateBindings(IEnumerable<ExportedMethod> fileExports, string hash)
    {
        var byClass = fileExports.GroupBy(e => e.ClassName).ToList();

        var interfaceMembers = new StringBuilder();
        var bindings = new StringBuilder();
        foreach (var group in byClass)
        {
            interfaceMembers.AppendLine($"  {group.Key}: {{");
            bindings.AppendLine($"  atlantis.{group.Key} = {{");
            foreach (var method in group)
            {
                var typedParams = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {MapToTypeScript(p.Type)}"));
                var paramNames = string.Join(", ", method.Parameters.Select(p => p.Name));
                var tsReturn = MapToTypeScript(method.ReturnType);
                interfaceMembers.AppendLine($"    {method.MethodName}({typedParams}): Promise<{tsReturn}>;");
                bindings.AppendLine($"    {method.MethodName}: ({typedParams}): Promise<{tsReturn}> => _invoke('{group.Key}.{method.MethodName}', [{paramNames}]),");
            }
            interfaceMembers.AppendLine("  };");
            bindings.AppendLine("  };");
        }

        return LoadTemplate("atlantis_bindings_ts.template")
            .Replace("{{Header}}", ExportScanner.HeaderLine(hash))
            .Replace("{{InterfaceMembers}}", interfaceMembers.ToString())
            .Replace("{{Bindings}}", bindings.ToString())
            .ReplaceLineEndings("\n");
    }

    // Bindings are emitted one file per source .cs file, so the per-file model only
    // holds if each exported class lives in a single file and no two source files
    // share a name. Both cases would otherwise collide in the merged bridge, so they
    // are reported as hard errors rather than silently producing a broken bridge.
    private static bool TryGetExportError(List<ExportedMethod> exports, string sourceDir, out string error)
    {
        var classSpanningFiles = exports
            .GroupBy(e => e.ClassName)
            .FirstOrDefault(g => g.Select(e => e.SourceFile).Distinct().Count() > 1);
        if (classSpanningFiles != null)
        {
            var files = string.Join(", ", classSpanningFiles
                .Select(e => Path.GetRelativePath(sourceDir, e.SourceFile))
                .Distinct()
                .OrderBy(f => f, StringComparer.Ordinal));
            error = $"Error: exported class '{classSpanningFiles.Key}' has [AtlExport] methods in multiple files ({files}). " +
                    "Move all of its [AtlExport] methods into a single .cs file.";
            return true;
        }

        var nameCollision = exports
            .Select(e => e.SourceFile)
            .Distinct()
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (nameCollision != null)
        {
            var files = string.Join(", ", nameCollision
                .Select(f => Path.GetRelativePath(sourceDir, f))
                .OrderBy(f => f, StringComparer.Ordinal));
            error = $"Error: source files map to the same bindings file '{nameCollision.Key}.bindings.ts' ({files}). " +
                    "Rename one so each exported file has a unique name.";
            return true;
        }

        error = "";
        return false;
    }

    private static string LoadTemplate(string templateName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Atlantis.Cli.Templates.{templateName}";

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            var matching = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(templateName));
            if (matching != null)
                stream = assembly.GetManifestResourceStream(matching);
        }

        if (stream == null)
            throw new InvalidOperationException($"Template not found: {templateName}. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");

        using (stream)
        using (var reader = new StreamReader(stream))
        {
            return reader.ReadToEnd();
        }
    }

    private static string MapToTypeScript(string csharpType)
    {
        return csharpType switch
        {
            "void" => "void",
            "bool" => "boolean",
            "string" => "string",
            "int" or "long" or "short" or "byte" or "float" or "double" or "decimal" => "number",
            "string[]" => "string[]",
            "int[]" or "long[]" or "float[]" or "double[]" => "number[]",
            "bool[]" => "boolean[]",
            _ when csharpType.EndsWith("[]") => "unknown[]",
            _ when csharpType.StartsWith("Task<") => MapToTypeScript(csharpType[5..^1]),
            "Task" => "void",
            _ => "unknown"
        };
    }
}
