using Atlantis.Bridge;

namespace Atlantis;

/// <summary>
/// Entry point for an Atlantis application. Owns the native webview host so apps
/// stay free of any platform webview type (WKWebView on macOS and iOS). The concrete
/// host is selected at build time per <c>RuntimeIdentifier</c>, which keeps any
/// platform-specific interop out of builds that don't target that platform.
/// </summary>
public static class AtlantisApp
{
    /// <summary>
    /// Create the webview, load <paramref name="html"/>, and run until the window
    /// closes. When <paramref name="configure"/> is supplied, a <see cref="BridgeHost"/>
    /// is attached to the webview before it is shown so the app can register the
    /// handlers backing its <c>[AtlExport]</c> methods.
    /// </summary>
    /// <param name="title">Window title (desktop only; ignored on platforms without a title bar).</param>
    /// <param name="html">The HTML document to display.</param>
    /// <param name="configure">Optional callback to register bridge handlers.</param>
    public static void Run(string title, string html, Action<BridgeHost>? configure = null)
    {
#if ATLANTIS_IOS
        // iOS currently displays the document via WKWebView; the JS bridge transport
        // for iOS is not wired yet, so configure is not invoked on this platform.
        Platforms.iOS.iOSApp.Run(html);
#elif ATLANTIS_DESKTOP
        DesktopHost.Run(title, html, configure);
#else
#error Unsupported platform
#endif
    }
}
