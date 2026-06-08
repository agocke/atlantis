using Atlantis;
using HelloWorld;

// AtlantisApp owns the native webview host (WKWebView on macOS and iOS),
// so this sample never references a platform webview type directly.
AtlantisApp.Run("HelloWorld", HelloPage.Html);
