namespace Atlantis.Bridge;

/// <summary>
/// A bidirectional message channel to the underlying webview, so
/// <see cref="BridgeHost"/> is independent of the host (WKWebView on macOS and
/// iOS, WebView2 on Windows, WebKitGTK on Linux). Messages are UTF-8 JSON: that
/// is what System.Text.Json and the webview engines speak natively, so the bytes
/// cross the boundary without a UTF-16 round-trip through a managed string.
/// </summary>
internal interface IBridgeTransport
{
    /// <summary>Await the next UTF-8 message posted by the webview to the host.</summary>
    Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Send a UTF-8 message to the webview. The returned task completes once the
    /// message has been handed to the webview on its UI thread.
    /// </summary>
    Task Send(ReadOnlyMemory<byte> message);
}
