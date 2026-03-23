using Serde;
using Serde.CmdLine;
using Spectre.Console;
using Atlantis.Cli.Commands;

var console = AnsiConsole.Console;

if (!CmdLine.TryParse<AtlCommand>(args, console, out var cmd))
{
    return 1;
}

return await cmd.RunAsync();

[GenerateDeserialize]
[Command("atl", Summary = "Atlantis CLI - Tools for Atlantis development")]
public partial record AtlCommand
{
    [CommandGroup("command")]
    public AtlSubCommand? SubCommand { get; init; }

    public Task<int> RunAsync()
    {
        if (SubCommand is AtlSubCommand.Init init) return init.RunAsync();
        if (SubCommand is AtlSubCommand.Generate generate) return generate.RunAsync();
        if (SubCommand is AtlSubCommand.Build build) return build.RunAsync();
        
        Console.WriteLine("Use --help for usage information.");
        return Task.FromResult(1);
    }
}

[GenerateDeserialize]
public abstract partial record AtlSubCommand
{
    private AtlSubCommand() { }

    [Command("init", Summary = "Create a new Atlantis project")]
    public sealed partial record Init : AtlSubCommand
    {
        [CommandParameter(0, "name", Description = "Name of the project to create")]
        public required string Name { get; init; }

        [CommandOption("-o|--output", Description = "Output directory (defaults to current directory)")]
        public string? Output { get; init; }

        public Task<int> RunAsync() => InitCommand.RunAsync(Name, Output);
    }

    [Command("generate", Summary = "Generate JavaScript/TypeScript bindings from [JSExport] methods")]
    public sealed partial record Generate : AtlSubCommand
    {
        [CommandOption("-s|--source", Description = "Source directory containing C# files")]
        public string? Source { get; init; }

        [CommandOption("-o|--output", Description = "Output directory for generated files")]
        public string? Output { get; init; }

        public Task<int> RunAsync() => GenerateCommand.RunAsync(Source, Output);
    }

    [Command("build", Summary = "Build the Atlantis project for deployment")]
    public sealed partial record Build : AtlSubCommand
    {
        [CommandOption("-p|--project", Description = "Path to the .csproj file (auto-detected if not specified)")]
        public string? Project { get; init; }

        [CommandOption("-r|--rid", Description = "Runtime identifier for cross-compilation (e.g., win-x64, linux-x64, osx-arm64)")]
        public string? Rid { get; init; }

        [CommandOption("-c|--configuration", Description = "Build configuration (default: Release)")]
        public string? Configuration { get; init; }

        [CommandOption("-v|--verbose", Description = "Show verbose output")]
        public bool? Verbose { get; init; }

        public Task<int> RunAsync() => BuildCommand.RunAsync(Project, Rid, Configuration ?? "Release", Verbose ?? false);
    }
}
