using Photino.NET;

namespace Atlantis.Bridge.Photino;

/// <summary>
/// <see cref="IBridgeTransport"/> implemented over a <see cref="PhotinoWindow"/>.
/// Photino exposes <c>window.external.sendMessage</c> / <c>receiveMessage</c> to
/// JavaScript, which map to the window's <see cref="PhotinoWindow.WebMessageReceived"/>
/// event and <see cref="PhotinoWindow.SendWebMessage"/> method.
/// </summary>
public sealed class PhotinoBridgeTransport : IBridgeTransport
{
    private readonly PhotinoWindow _window;

    public event Action<string>? MessageReceived;

    public PhotinoBridgeTransport(PhotinoWindow window)
    {
        _window = window;
        _window.WebMessageReceived += (_, message) => MessageReceived?.Invoke(message);
    }

    public void Send(string message) => _window.SendWebMessage(message);
}

/// <summary>Convenience helpers for attaching a <see cref="BridgeHost"/> to a window.</summary>
public static class PhotinoBridge
{
    /// <summary>Create a <see cref="BridgeHost"/> bound to <paramref name="window"/>.</summary>
    public static BridgeHost Attach(PhotinoWindow window)
        => new(new PhotinoBridgeTransport(window));
}
