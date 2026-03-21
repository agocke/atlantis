using Atlantis;

namespace Atlantis.Tests;

public class HelloPageTests
{
    [Fact]
    public void HtmlContainsHelloWorld()
    {
        Assert.Contains("<h1>Hello, World!</h1>", HelloPage.Html);
    }

    [Fact]
    public void HtmlIsValidDocument()
    {
        Assert.StartsWith("<!DOCTYPE html>", HelloPage.Html.TrimStart());
        Assert.Contains("<html>", HelloPage.Html);
        Assert.Contains("</html>", HelloPage.Html);
    }

    [Fact]
    public void HtmlContainsTitle()
    {
        Assert.Contains("<title>Atlantis</title>", HelloPage.Html);
    }
}
