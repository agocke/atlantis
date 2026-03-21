using System.Reflection;

namespace Atlantis;

public static class HelloPage
{
    public static string Html { get; } = LoadHtml();

    private static string LoadHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Atlantis.frontend.index.html")
            ?? throw new InvalidOperationException("Embedded resource 'Atlantis.frontend.index.html' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
