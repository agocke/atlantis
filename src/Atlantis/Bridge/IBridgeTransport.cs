namespace Atlantis.Bridge;

/// <summary>
/// Abstracts the underlying webview's message channel so <see cref="BridgeHost"/>
/// is independent of the host (WKWebView on macOS and iOS, etc.).
/// </summary>
public interface IBridgeTransport
{
    /// <summary>Raised when the webview posts a message to the host.</summary>
    event Action<string>? MessageReceived;

    /// <summary>Send a raw message string to the webview.</summary>
    void Send(string message);
}
