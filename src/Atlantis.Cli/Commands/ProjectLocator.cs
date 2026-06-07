namespace Atlantis.Cli.Commands;

/// <summary>
/// Resolves the app project that 'atl' commands operate on. The model is "atl acts
/// on the project you are standing in": you cd into the app project directory
/// (e.g. src/Bower.App) and atl uses the single .csproj there. Pass --project to
/// override. This is layout-agnostic — there is no assumption about a 'src' folder
/// or how many projects a repository contains.
/// </summary>
internal static class ProjectLocator
{
    public static string? FindInCurrentDirectory()
    {
        var projects = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj");
        return projects.Length == 1 ? projects[0] : null;
    }

    public static string DescribeResolutionFailure()
    {
        var count = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.csproj").Length;
        return count == 0
            ? "No .csproj found in the current directory. cd into your app project (e.g. src/MyApp) or pass --project."
            : "Multiple .csproj files found in the current directory. Pass --project to choose one.";
    }
}
