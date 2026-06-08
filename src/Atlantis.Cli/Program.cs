using Serde;
using Serde.CmdLine;
using Spectre.Console;
using Atlantis.Cli.Commands;

var console = AnsiConsole.Console;

// Split off application passthrough arguments after a "--" separator (as with
// `dotnet run <project> -- <app args>`). Everything before "--" is parsed as atl
// arguments; everything after is forwarded to the launched application by `run`.
var separator = Array.IndexOf(args, "--");
var atlArgs = separator >= 0 ? args[..separator] : args;
var forwardArgs = separator >= 0 ? args[(separator + 1)..] : [];

if (!CmdLine.TryParse<AtlCommand>(atlArgs, console, out var cmd))
{
    return 1;
}

return await cmd.RunAsync(forwardArgs);

[GenerateDeserialize]
[Command("atl", Summary = "Atlantis CLI - Tools for Atlantis development")]
public partial record AtlCommand
{
    [CommandGroup("command")]
    public AtlSubCommand? SubCommand { get; init; }

    public Task<int> RunAsync(string[] forwardArgs)
    {
        if (SubCommand is AtlSubCommand.Init init) return init.RunAsync();
        if (SubCommand is AtlSubCommand.Bindgen bindgen) return bindgen.RunAsync();
        if (SubCommand is AtlSubCommand.Build build) return build.RunAsync();
        if (SubCommand is AtlSubCommand.Run run) return run.RunAsync(forwardArgs);
        if (SubCommand is AtlSubCommand.Fix fix) return fix.RunAsync();
        if (SubCommand is AtlSubCommand.Update update) return update.RunAsync();
        
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
        public string? Name { get; init; }

        [CommandOption("-o|--output", Description = "Output directory (defaults to current directory)")]
        public string? Output { get; init; }

        public Task<int> RunAsync() => InitCommand.RunAsync(Name, Output);
    }

    [Command("bindgen", Summary = "Generate JavaScript/TypeScript bindings from [JSExport] methods")]
    public sealed partial record Bindgen : AtlSubCommand
    {
        [CommandParameter(0, "path", Description = "App project directory or .csproj to scan (defaults to the current directory)")]
        public string? Path { get; init; }

        [CommandOption("-s|--source", Description = "Source directory containing C# files")]
        public string? Source { get; init; }

        [CommandOption("-o|--output", Description = "Output directory for generated files")]
        public string? Output { get; init; }

        public Task<int> RunAsync() => GenerateCommand.RunAsync(Source, Output, Path);
    }

    [Command("build", Summary = "Build the Atlantis project for deployment")]
    public sealed partial record Build : AtlSubCommand
    {
        [CommandParameter(0, "path", Description = "App project directory or .csproj (defaults to the current directory)")]
        public string? Path { get; init; }

        [CommandOption("-p|--project", Description = "Path to the .csproj file (auto-detected if not specified)")]
        public string? Project { get; init; }

        [CommandOption("-r|--rid", Description = "Runtime identifier for cross-compilation (e.g., win-x64, linux-x64, osx-arm64)")]
        public string? Rid { get; init; }

        [CommandOption("-c|--configuration", Description = "Build configuration (default: Release)")]
        public string? Configuration { get; init; }

        [CommandOption("-v|--verbose", Description = "Show verbose output")]
        public bool? Verbose { get; init; }

        public Task<int> RunAsync() => BuildCommand.RunAsync(Project, Path, Rid, Configuration ?? "Release", Verbose ?? false);
    }

    [Command("update", Summary = "Update atl to the latest version")]
    public sealed partial record Update : AtlSubCommand
    {
        [CommandOption("--check", Description = "Check for updates without installing")]
        public bool? Check { get; init; }

        [CommandOption("-v|--verbose", Description = "Show verbose output")]
        public bool? Verbose { get; init; }

        public Task<int> RunAsync() => UpdateCommand.RunAsync(Check ?? false, Verbose ?? false);
    }

    [Command("run", Summary = "Run the Atlantis application")]
    public sealed partial record Run : AtlSubCommand
    {
        [CommandParameter(0, "path", Description = "App project directory or .csproj (defaults to the current directory)")]
        public string? Path { get; init; }

        [CommandOption("-p|--project", Description = "Path to the .csproj file (auto-detected if not specified)")]
        public string? Project { get; init; }

        [CommandOption("-c|--configuration", Description = "Build configuration (default: Debug)")]
        public string? Configuration { get; init; }

        [CommandOption("-v|--verbose", Description = "Show verbose output")]
        public bool? Verbose { get; init; }

        // Application passthrough arguments (after "--") are supplied by Program.cs.
        public Task<int> RunAsync(string[] forwardArgs) =>
            RunCommand.RunAsync(Project, Path, Configuration, Verbose ?? false, forwardArgs);
    }

    [Command("fix", Summary = "Apply code fixes for Atlantis migration issues")]
    public sealed partial record Fix : AtlSubCommand
    {
        [CommandOption("-p|--project", Description = "Path to the .csproj file (auto-detected if not specified)")]
        public string? Project { get; init; }

        [CommandOption("--dry-run", Description = "Show what would be fixed without making changes")]
        public bool? DryRun { get; init; }

        [CommandOption("-v|--verbose", Description = "Show verbose output")]
        public bool? Verbose { get; init; }

        public Task<int> RunAsync() => FixCommand.RunAsync(Project, DryRun ?? false, Verbose ?? false);
    }
}
