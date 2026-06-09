namespace Atlantis.Bridge;

/// <summary>
/// A bidirectional message channel to the underlying webview, so
/// <see cref="BridgeHost"/> is independent of the host (WKWebView on macOS and
/// iOS, WebView2 on Windows, WebKitGTK on Linux). Messages are JSON strings.
/// </summary>
internal interface IBridgeTransport
{
    /// <summary>Await the next message posted by the webview to the host.</summary>
    Task<string> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a message to the webview. The returned task completes once the
    /// message has been handed to the webview on its UI thread.
    /// </summary>
    Task Send(string message);
}
