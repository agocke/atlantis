using System.Reflection;

namespace Atlantis.Cli;

/// <summary>
/// Exposes the NuGet version of the running <c>atl</c> tool. Used to pin the matching
/// <c>Atlantis.Analyzers</c> package when scaffolding projects (<c>atl init</c>) and when
/// adding the reference on demand (<c>atl fix</c>), so the analyzer/fixer version always
/// tracks the tool that produced it.
/// </summary>
internal static class ToolVersion
{
    public static string Current { get; } = Resolve();

    internal static string Resolve()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
            return Normalize(informational);

        var version = assembly.GetName().Version;
        return version is null ? "0.1.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }

    /// <summary>
    /// Strips SemVer build metadata (everything from the first <c>+</c>) while preserving
    /// the version core and any prerelease label, e.g. <c>0.1.0-ci.34+abc123</c> → <c>0.1.0-ci.34</c>.
    /// </summary>
    internal static string Normalize(string informationalVersion)
    {
        var plus = informationalVersion.IndexOf('+');
        return plus >= 0 ? informationalVersion[..plus] : informationalVersion;
    }
}
