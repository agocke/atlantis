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

        if (exports.Count == 0)
        {
            // No [AtlExport] methods, but the bridge still carries the built-in
            // atlantis.dialog/on/off API, so generate it anyway.
            Console.WriteLine("No [AtlExport] methods found; generating the built-in bridge only.");
        }
        else
        {
            Console.WriteLine($"Found {exports.Count} exported method(s) in {exports.Select(e => e.ClassName).Distinct().Count()} class(es).");
        }

        Directory.CreateDirectory(outputDir);

        // Embed a hash of the export contract so 'atl run'/'atl build' can detect
        // when bindings are stale without relying on file timestamps.
        var hash = ExportScanner.ComputeInputHash(exports);

        // atlantis.ts is the source of truth; tsc transpiles it into the
        // browser-loadable atlantis.js plus ambient atlantis.d.ts editor types.
        var tsPath = Path.Combine(outputDir, FrontendCompiler.SourceFileName);
        await File.WriteAllTextAsync(tsPath, GenerateTypeScript(exports, hash));
        Console.WriteLine($"✓ Generated {tsPath}");

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

    // The runtime scaffolding lives in Templates/atlantis.ts.template; only the
    // per-class bindings and the exported member list are generated here. The
    // typed bindings flow through tsc into atlantis.js and atlantis.d.ts.
    private static string GenerateTypeScript(List<ExportedMethod> exports, string hash)
    {
        var byClass = exports.GroupBy(e => e.ClassName).ToList();

        var bindings = new StringBuilder();
        foreach (var group in byClass)
        {
            bindings.AppendLine($"  const {group.Key} = {{");
            foreach (var method in group)
            {
                var paramList = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {MapToTypeScript(p.Type)}"));
                var paramNames = string.Join(", ", method.Parameters.Select(p => p.Name));
                var tsReturn = MapToTypeScript(method.ReturnType);
                bindings.AppendLine($"    {method.MethodName}: ({paramList}): Promise<{tsReturn}> => _invoke('{group.Key}.{method.MethodName}', [{paramNames}]),");
            }
            bindings.AppendLine("  };");
            bindings.AppendLine();
        }

        var exportList = new StringBuilder();
        foreach (var className in byClass.Select(g => g.Key))
        {
            exportList.AppendLine($"    {className},");
        }

        return LoadTemplate("atlantis_ts.template")
            .Replace("{{Header}}", ExportScanner.HeaderLine(hash))
            .Replace("{{Bindings}}", bindings.ToString())
            .Replace("{{Exports}}", exportList.ToString())
            .ReplaceLineEndings("\n");
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
