using System.Net;
using System.Text;

namespace Atlantis;

public sealed class WebServer
{
    private readonly HttpListener _listener = new();

    public const string HelloPageHtml =
        """
        <!DOCTYPE html>
        <html>
        <head><title>Atlantis</title></head>
        <body>
            <h1>Hello, World!</h1>
        </body>
        </html>
        """;

    public WebServer(string prefix)
    {
        _listener.Prefixes.Add(prefix);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _listener.Start();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
                await HandleRequestAsync(context);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown requested
        }
        finally
        {
            _listener.Stop();
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext context)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(HelloPageHtml);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        await context.Response.OutputStream.WriteAsync(buffer);
        context.Response.Close();
    }
}
