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
            Console.WriteLine("No [AtlExport] methods found.");
            return 0;
        }

        Console.WriteLine($"Found {exports.Count} exported method(s) in {exports.Select(e => e.ClassName).Distinct().Count()} class(es).");

        Directory.CreateDirectory(outputDir);

        // Embed a hash of the export contract so 'atl run'/'atl build' can detect
        // when bindings are stale without relying on file timestamps.
        var hash = ExportScanner.ComputeInputHash(exports);

        var jsPath = Path.Combine(outputDir, "atlantis.js");
        var dtsPath = Path.Combine(outputDir, "atlantis.d.ts");

        await File.WriteAllTextAsync(jsPath, GenerateJavaScript(exports, hash));
        await File.WriteAllTextAsync(dtsPath, GenerateTypeScript(exports, hash));

        Console.WriteLine($"✓ Generated {jsPath}");
        Console.WriteLine($"✓ Generated {dtsPath}");
        
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

    private static string GenerateJavaScript(List<ExportedMethod> exports, string hash)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ExportScanner.HeaderLine(hash));
        sb.AppendLine();
        sb.AppendLine("const atlantis = (() => {");
        sb.AppendLine("  let _callId = 0;");
        sb.AppendLine("  const _pending = new Map();");
        sb.AppendLine("  const _subs = new Map(); // channel -> Set<callback>");
        sb.AppendLine();
        sb.AppendLine("  // Receive responses and events from the native host.");
        sb.AppendLine("  // Photino exposes window.external.receiveMessage(callback).");
        sb.AppendLine("  window.external.receiveMessage((json) => {");
        sb.AppendLine("    let msg;");
        sb.AppendLine("    try { msg = JSON.parse(json); } catch { return; }");
        sb.AppendLine("    if (msg && msg.event === true) {");
        sb.AppendLine("      const subs = _subs.get(msg.channel);");
        sb.AppendLine("      if (subs) subs.forEach((cb) => cb(msg.payload));");
        sb.AppendLine("      return;");
        sb.AppendLine("    }");
        sb.AppendLine("    if (msg && msg.callId !== undefined && _pending.has(msg.callId)) {");
        sb.AppendLine("      const { resolve, reject } = _pending.get(msg.callId);");
        sb.AppendLine("      _pending.delete(msg.callId);");
        sb.AppendLine("      if (msg.error) reject(new Error(msg.error));");
        sb.AppendLine("      else resolve(msg.result);");
        sb.AppendLine("    }");
        sb.AppendLine("  });");
        sb.AppendLine();
        sb.AppendLine("  function _invoke(className, methodName, args) {");
        sb.AppendLine("    return new Promise((resolve, reject) => {");
        sb.AppendLine("      const callId = ++_callId;");
        sb.AppendLine("      _pending.set(callId, { resolve, reject });");
        sb.AppendLine("      window.external.sendMessage(JSON.stringify({ callId, className, methodName, args }));");
        sb.AppendLine("    });");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  // Subscribe to a host event channel. Returns an unsubscribe function.");
        sb.AppendLine("  function on(channel, callback) {");
        sb.AppendLine("    let subs = _subs.get(channel);");
        sb.AppendLine("    if (!subs) { subs = new Set(); _subs.set(channel, subs); }");
        sb.AppendLine("    subs.add(callback);");
        sb.AppendLine("    return () => off(channel, callback);");
        sb.AppendLine("  }");
        sb.AppendLine();
        sb.AppendLine("  function off(channel, callback) {");
        sb.AppendLine("    const subs = _subs.get(channel);");
        sb.AppendLine("    if (subs) subs.delete(callback);");
        sb.AppendLine("  }");
        sb.AppendLine();

        // Group by class
        var byClass = exports.GroupBy(e => e.ClassName);
        foreach (var group in byClass)
        {
            sb.AppendLine($"  const {group.Key} = {{");
            foreach (var method in group)
            {
                var paramNames = string.Join(", ", method.Parameters.Select(p => p.Name));
                sb.AppendLine($"    {method.MethodName}: ({paramNames}) => _invoke('{group.Key}', '{method.MethodName}', [{paramNames}]),");
            }
            sb.AppendLine("  };");
            sb.AppendLine();
        }

        sb.AppendLine("  return {");
        foreach (var className in byClass.Select(g => g.Key))
        {
            sb.AppendLine($"    {className},");
        }
        sb.AppendLine("    on,");
        sb.AppendLine("    off,");
        sb.AppendLine("  };");
        sb.AppendLine("})();");

        return sb.ToString();
    }

    private static string GenerateTypeScript(List<ExportedMethod> exports, string hash)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(ExportScanner.HeaderLine(hash));
        sb.AppendLine();
        sb.AppendLine("declare namespace atlantis {");

        var byClass = exports.GroupBy(e => e.ClassName);
        foreach (var group in byClass)
        {
            sb.AppendLine($"  namespace {group.Key} {{");
            foreach (var method in group)
            {
                var tsParams = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {MapToTypeScript(p.Type)}"));
                var tsReturn = MapToTypeScript(method.ReturnType);
                sb.AppendLine($"    function {method.MethodName}({tsParams}): Promise<{tsReturn}>;");
            }
            sb.AppendLine("  }");
            sb.AppendLine();
        }

        sb.AppendLine("  /** Subscribe to a host event channel. Returns an unsubscribe function. */");
        sb.AppendLine("  function on(channel: string, callback: (payload: any) => void): () => void;");
        sb.AppendLine("  /** Unsubscribe a previously registered event callback. */");
        sb.AppendLine("  function off(channel: string, callback: (payload: any) => void): void;");
        sb.AppendLine();

        sb.AppendLine("}");

        return sb.ToString();
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
