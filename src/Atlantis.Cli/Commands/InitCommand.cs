using System.Reflection;

namespace Atlantis.Cli.Commands;

public static class InitCommand
{
    public static async Task<int> RunAsync(string? name, string? output)
    {
        var targetDir = output ?? Directory.GetCurrentDirectory();
        
        // If no name provided, initialize in current directory using folder name
        string projectDir;
        string projectName;
        if (string.IsNullOrWhiteSpace(name))
        {
            projectDir = targetDir;
            projectName = Path.GetFileName(Path.GetFullPath(targetDir));
        }
        else
        {
            projectDir = Path.Combine(targetDir, name);
            projectName = name;
        }

        // Check if already initialized (only for new subdirectory case)
        if (name != null && Directory.Exists(projectDir))
        {
            Console.Error.WriteLine($"Error: Directory '{projectDir}' already exists.");
            return 1;
        }

        // Check if current directory already has Atlantis files
        if (name == null && File.Exists(Path.Combine(projectDir, "Directory.Build.props")))
        {
            Console.Error.WriteLine("Error: Current directory appears to already be an Atlantis project.");
            return 1;
        }

        Console.WriteLine($"Creating Atlantis project '{projectName}'...");

        // Create directory structure
        Directory.CreateDirectory(projectDir);
        Directory.CreateDirectory(Path.Combine(projectDir, "src", projectName));
        Directory.CreateDirectory(Path.Combine(projectDir, "src", "frontend"));

        // Write template files
        await WriteTemplate("Project.csproj.template", Path.Combine(projectDir, "src", projectName, $"{projectName}.csproj"), projectName);
        await WriteTemplate("Program_cs.template", Path.Combine(projectDir, "src", projectName, "Program.cs"), projectName);
        await WriteTemplate("Api_cs.template", Path.Combine(projectDir, "src", projectName, "Api.cs"), projectName);
        await WriteTemplate("index.html.template", Path.Combine(projectDir, "src", "frontend", "index.html"), projectName);
        await WriteTemplate("Solution.sln.template", Path.Combine(projectDir, $"{projectName}.sln"), projectName);
        await WriteTemplate("Directory.Build.props.template", Path.Combine(projectDir, "Directory.Build.props"), projectName);
        await WriteTemplate("gitignore.template", Path.Combine(projectDir, ".gitignore"), projectName);

        Console.WriteLine();
        Console.WriteLine($"✓ Created project at {projectDir}");
        Console.WriteLine();
        Console.WriteLine("Next steps:");
        if (name != null)
        {
            Console.WriteLine($"  cd {projectName}");
        }
        Console.WriteLine($"  dotnet build src/{projectName}");
        Console.WriteLine($"  dotnet run --project src/{projectName}");
        
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
