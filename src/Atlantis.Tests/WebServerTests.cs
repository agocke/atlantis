using System.Net;
using Atlantis;

namespace Atlantis.Tests;

public class WebServerTests : IAsyncLifetime
{
    private WebServer _server = null!;
    private CancellationTokenSource _cts = null!;
    private Task _serverTask = null!;
    private HttpClient _client = null!;
    private string _prefix = null!;

    public Task InitializeAsync()
    {
        // Use a random available port to avoid conflicts in parallel test runs
        var port = GetAvailablePort();
        _prefix = $"http://localhost:{port}/";
        _server = new WebServer(_prefix);
        _cts = new CancellationTokenSource();
        _serverTask = _server.RunAsync(_cts.Token);
        _client = new HttpClient { BaseAddress = new Uri(_prefix) };
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _cts.CancelAsync();
        await _serverTask;
        _cts.Dispose();
    }

    [Fact]
    public async Task RootReturnsHelloWorldHtml()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("<h1>Hello, World!</h1>", content);
    }

    [Fact]
    public async Task RootReturnsHtmlContentType()
    {
        var response = await _client.GetAsync("/");

        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task RootReturnsValidHtmlDocument()
    {
        var response = await _client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        Assert.StartsWith("<!DOCTYPE html>", content.TrimStart());
        Assert.Contains("<html>", content);
        Assert.Contains("</html>", content);
    }

    private static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
