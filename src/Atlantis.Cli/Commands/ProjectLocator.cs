namespace Atlantis.Cli.Commands;

/// <summary>
/// Resolves the app project that 'atl' commands operate on. The model is "atl acts
/// on the project you point it at": by default the single .csproj in the current
/// directory, or an explicit target passed as the first argument (a directory or a
/// .csproj path). This is layout-agnostic — there is no assumption about a 'src'
/// folder or how many projects a repository contains.
/// </summary>
internal static class ProjectLocator
{
    /// <summary>
    /// Resolve a project (.csproj path) from an optional target. <paramref name="target"/>
    /// may be a .csproj file, a directory containing exactly one .csproj, or null to use
    /// the current directory. Returns null with a populated <paramref name="error"/> when
    /// it cannot be resolved unambiguously.
    /// </summary>
    public static string? Resolve(string? target, out string? error)
    {
        error = null;
        var path = target ?? Directory.GetCurrentDirectory();

        if (File.Exists(path) && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(path);

        if (Directory.Exists(path))
        {
            var projects = Directory.GetFiles(path, "*.csproj");
            if (projects.Length == 1)
                return Path.GetFullPath(projects[0]);

            error = projects.Length == 0
                ? $"No .csproj found in '{Describe(path)}'. Point atl at an app project directory or a .csproj file."
                : $"Multiple .csproj files found in '{Describe(path)}'. Pass the .csproj path explicitly.";
            return null;
        }

        error = $"Path not found: '{target}'.";
        return null;
    }

    private static string Describe(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, Directory.GetCurrentDirectory(), StringComparison.Ordinal)
            ? "the current directory"
            : path;
    }
}
