#if IOS
using System.Runtime.InteropServices;

namespace Atlantis.Platforms.iOS;

/// <summary>
/// iOS application entry point. Calls into Swift to run the SwiftUI app.
/// </summary>
public static partial class iOSApp
{
    public static void Run(string html)
    {
        atlantis_run(html);
    }

    /// <summary>
    /// Entry point defined in AtlantisApp.swift. Starts UIApplicationMain
    /// with a SwiftUI WebView displaying the provided HTML.
    /// </summary>
    [LibraryImport("__Internal")]
    private static partial void atlantis_run([MarshalAs(UnmanagedType.LPUTF8Str)] string html);
}
#endif
