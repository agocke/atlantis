using System.Reflection;

namespace HelloWorld;

public static class HelloPage
{
    public static string Html { get; } = LoadHtml();

    private static string LoadHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("HelloWorld.frontend.index.html")
            ?? throw new InvalidOperationException("Embedded resource 'HelloWorld.frontend.index.html' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
