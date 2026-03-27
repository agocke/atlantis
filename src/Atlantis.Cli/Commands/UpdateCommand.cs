using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Atlantis.Cli.Commands;

public static class UpdateCommand
{
    private const string NuGetSearchUrl = "https://api.nuget.org/v3-flatcontainer/atl/index.json";

    public static async Task<int> RunAsync(bool check, bool verbose)
    {
        var currentVersion = GetCurrentVersion();
        
        if (verbose)
        {
            Console.WriteLine($"Current version: {currentVersion}");
        }

        // Fetch latest version from NuGet
        var latestVersion = await GetLatestVersionAsync(verbose);
        if (latestVersion == null)
        {
            Console.Error.WriteLine("Error: Could not fetch version information from NuGet.");
            return 1;
        }

        if (verbose)
        {
            Console.WriteLine($"Latest version:  {latestVersion}");
        }

        // Compare versions
        var current = ParseVersion(currentVersion);
        var latest = ParseVersion(latestVersion);

        if (CompareVersions(current, latest) >= 0)
        {
            Console.WriteLine($"atl is up to date (v{currentVersion})");
            return 0;
        }

        Console.WriteLine($"Update available: v{currentVersion} → v{latestVersion}");

        if (check)
        {
            return 0;
        }

        // Perform update using dotnet tool
        return await PerformUpdateAsync(verbose);
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
        
        // Strip any metadata (e.g., +commitsha)
        var plusIndex = version.IndexOf('+');
        if (plusIndex >= 0)
        {
            version = version[..plusIndex];
        }

        return version;
    }

    private static async Task<string?> GetLatestVersionAsync(bool verbose)
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            
            if (verbose)
            {
                Console.WriteLine($"Fetching version info from NuGet...");
            }

            var response = await client.GetFromJsonAsync(NuGetSearchUrl, UpdateJsonContext.Default.NuGetVersionIndex);
            if (response?.Versions == null || response.Versions.Length == 0)
            {
                return null;
            }

            // NuGet returns versions in ascending order, last is latest
            return response.Versions[^1];
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.Error.WriteLine($"Failed to fetch version info: {ex.Message}");
            }
            return null;
        }
    }

    private static async Task<int> PerformUpdateAsync(bool verbose)
    {
        Console.WriteLine("Updating atl...");

        // Check if running as a global tool or local tool
        var isGlobalTool = IsGlobalTool();
        
        var args = new List<string> { "tool", "update" };
        
        if (isGlobalTool)
        {
            args.Add("-g");
        }
        
        args.Add("atl");
        args.Add("--prerelease");

        if (verbose)
        {
            Console.WriteLine($"Running: dotnet {string.Join(" ", args)}");
        }

        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi);
        if (process == null)
        {
            Console.Error.WriteLine("Error: Could not start dotnet process.");
            return 1;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (verbose || process.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.WriteLine(stdout.TrimEnd());
            }
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.Error.WriteLine(stderr.TrimEnd());
            }
        }

        if (process.ExitCode == 0)
        {
            Console.WriteLine("✓ Update complete");
        }
        else
        {
            Console.Error.WriteLine("Update failed.");
        }

        return process.ExitCode;
    }

    private static bool IsGlobalTool()
    {
        // Global tools are installed in ~/.dotnet/tools
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return true; // Assume global if we can't determine
        }

        var dotnetToolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dotnet", "tools");

        return processPath.StartsWith(dotnetToolsDir, StringComparison.OrdinalIgnoreCase);
    }

    private static (int major, int minor, int patch, string? prerelease) ParseVersion(string version)
    {
        // Handle prerelease versions like "1.0.0-beta.1"
        string? prerelease = null;
        var dashIndex = version.IndexOf('-');
        if (dashIndex >= 0)
        {
            prerelease = version[(dashIndex + 1)..];
            version = version[..dashIndex];
        }

        var parts = version.Split('.');
        var major = parts.Length > 0 ? int.TryParse(parts[0], out var m) ? m : 0 : 0;
        var minor = parts.Length > 1 ? int.TryParse(parts[1], out var n) ? n : 0 : 0;
        var patch = parts.Length > 2 ? int.TryParse(parts[2], out var p) ? p : 0 : 0;

        return (major, minor, patch, prerelease);
    }

    private static int CompareVersions(
        (int major, int minor, int patch, string? prerelease) a,
        (int major, int minor, int patch, string? prerelease) b)
    {
        var majorCmp = a.major.CompareTo(b.major);
        if (majorCmp != 0) return majorCmp;

        var minorCmp = a.minor.CompareTo(b.minor);
        if (minorCmp != 0) return minorCmp;

        var patchCmp = a.patch.CompareTo(b.patch);
        if (patchCmp != 0) return patchCmp;

        // Prerelease versions are older than release versions
        if (a.prerelease == null && b.prerelease != null) return 1;
        if (a.prerelease != null && b.prerelease == null) return -1;
        if (a.prerelease != null && b.prerelease != null)
        {
            return string.Compare(a.prerelease, b.prerelease, StringComparison.Ordinal);
        }

        return 0;
    }
}

internal record NuGetVersionIndex(
    [property: JsonPropertyName("versions")] string[] Versions);

[JsonSerializable(typeof(NuGetVersionIndex))]
internal partial class UpdateJsonContext : JsonSerializerContext { }
