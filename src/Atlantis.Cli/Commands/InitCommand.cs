using System.Reflection;

namespace Atlantis.Cli.Commands;

public static class InitCommand
{
    public static async Task<int> RunAsync(string name, string? output)
    {
        var targetDir = output ?? Directory.GetCurrentDirectory();
        var projectDir = Path.Combine(targetDir, name);

        if (Directory.Exists(projectDir))
        {
            Console.Error.WriteLine($"Error: Directory '{projectDir}' already exists.");
            return 1;
        }

        Console.WriteLine($"Creating Atlantis project '{name}'...");

        // Create directory structure
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "src", name));
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "frontend"));

        // Write template files
        await WriteTemplate("Project.csproj.template", Path.Combine(projectDir, "src", name, $"{name}.csproj"), name);
        await WriteTemplate("Program_cs.template", Path.Combine(projectDir, "src", name, "Program.cs"), name);
        await WriteTemplate("Api_cs.template", Path.Combine(projectDir, "src", name, "Api.cs"), name);
        await WriteTemplate("index.html.template", Path.Combine(projectDir, "src", "frontend", "index.html"), name);
        await WriteTemplate("Solution.sln.template", Path.Combine(projectDir, $"{name}.sln"), name);
        await WriteTemplate("Directory.Build.props.template", Path.Combine(projectDir, "Directory.Build.props"), name);
        await WriteTemplate("gitignore.template", Path.Combine(projectDir, ".gitignore"), name);

        Console.WriteLine();
        Console.WriteLine($"✓ Created project at {projectDir}");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        Console.WriteLine($"  cd {name}");
        Console.WriteLine($"  dotnet build src/{name}");
        Console.WriteLine($"  dotnet run --project src/{name}");
        
        return 0;
    }

    private static async Task WriteTemplate(string templateName, string outputPath, string projectName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Atlantis.Cli.Templates.{templateName}";

        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            // Try to find a matching resource
            var resources = assembly.GetManifestResourceNames();
            var matching = resources.FirstOrDefault(r => r.EndsWith(templateName));
            if (matching != null)
            {
                stream = assembly.GetManifestResourceStream(matching);
            }
        }
        
        if (stream == null)
        {
            throw new InvalidOperationException($"Template not found: {templateName}. Available: {string.Join(", ", assembly.GetManifestResourceNames())}");
        }
        
        using (stream)
        using (var reader = new StreamReader(stream))
        {
            var content = await reader.ReadToEndAsync();

            // Replace placeholders
            content = content.Replace("{{ProjectName}}", projectName);

            await File.WriteAllTextAsync(outputPath, content);
        }
    }
}
