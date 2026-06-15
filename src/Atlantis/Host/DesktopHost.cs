#if ATLANTIS_DESKTOP
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Atlantis.Bridge;

namespace Atlantis;

/// <summary>
/// Desktop webview host. Dispatches to a platform implementation by the current OS:
/// macOS (AppKit + WKWebView via the Objective-C runtime), Windows (Win32 + WebView2),
/// and Linux (GTK 3 + WebKitGTK).
/// </summary>
internal static class DesktopHost
{
    public static void Run(string title, string html, Action<BridgeHost>? configure)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacWebViewHost.Run(title, html, configure);
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsWebViewHost.Run(title, html, configure);
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxWebViewHost.Run(title, html, configure);
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported desktop platform.");
        }
    }
}

/// <summary>
/// A native macOS webview host backed by AppKit + WKWebView, driven entirely through
/// the Objective-C runtime (libobjc) so the package carries no native binaries and no
/// third-party dependency. Classes are resolved by name at runtime, so there is no
/// link-time framework dependency (libobjc also exists on iOS).
/// </summary>
/// <remarks>
/// The WKWebView wiring (the <c>window.external</c> bridge init script, the
/// <c>postMessage</c> message handler, and the C#-to-JS dispatch) is translated from
/// Photino's macOS host (Photino.Mac.mm / Photino.Mac.UiDelegate.mm), which is
/// MIT-licensed. Credit: Photino (https://github.com/tryphotino/photino.Native).
/// </remarks>
[SupportedOSPlatform("macos")]
internal sealed unsafe class MacWebViewHost : IBridgeTransport, IFileDialogProvider
{
    // Name of the WKScriptMessageHandler the bridge posts to. JavaScript reaches it
    // via window.webkit.messageHandlers.<name>.postMessage (see InitScript).
    private const string MessageHandlerName = "atlantisinterop";

    // Injected at document start so window.external exists before app scripts run.
    // Mirrors the contract Atlantis' generated atlantis.ts expects.
    private const string InitScript =
        $$"""
        window.__receiveMessageCallbacks = [];
        window.__dispatchMessageCallback = function(message) {
          window.__receiveMessageCallbacks.forEach(function(cb) { cb(message); });
        };
        window.external = {
          sendMessage: function(message) {
            window.webkit.messageHandlers.{{MessageHandlerName}}.postMessage(message);
          },
          receiveMessage: function(callback) {
            window.__receiveMessageCallbacks.push(callback);
          }
        };
        """;

    private readonly IntPtr _webview;

    // The host owning the live window for this process. The script-message-handler
    // trampoline (a static native callback) routes incoming messages here.
    private static MacWebViewHost? _current;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

    public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAsync(cancellationToken).AsTask();

    private MacWebViewHost(IntPtr webview) => _webview = webview;

    public static void Run(string title, string html, Action<BridgeHost>? configure)
    {
        EnsureInitialized();

        IntPtr nsApp = SendPtr(Class("NSApplication"), Sel("sharedApplication"));
        SendVoid(nsApp, Sel("setActivationPolicy:"), IntPtr.Zero); // NSApplicationActivationPolicyRegular

        // App delegate so closing the last window ends the run loop.
        IntPtr appDelegate = SendPtr(SendPtr(_appDelegateClass, Sel("alloc")), Sel("init"));
        SendVoid(nsApp, Sel("setDelegate:"), appDelegate);

        // Window.
        var frame = new CGRect(0, 0, 800, 600);
        const nuint style = 1 | 2 | 4 | 8; // Titled | Closable | Miniaturizable | Resizable
        IntPtr window = SendPtr(Class("NSWindow"), Sel("alloc"));
        window = SendInitWindow(
            window, Sel("initWithContentRect:styleMask:backing:defer:"),
            frame, style, 2 /* NSBackingStoreBuffered */, false);
        WithNSString(title, s => SendVoid(window, Sel("setTitle:"), s));

        // WebView configuration + user content controller.
        IntPtr config = SendPtr(SendPtr(Class("WKWebViewConfiguration"), Sel("alloc")), Sel("init"));
        IntPtr contentController = SendPtr(SendPtr(Class("WKUserContentController"), Sel("alloc")), Sel("init"));

        // Inject the window.external bridge before any page script runs.
        IntPtr userScript = SendPtr(Class("WKUserScript"), Sel("alloc"));
        WithNSString(InitScript, src =>
        {
            IntPtr script = SendInitUserScript(
                userScript, Sel("initWithSource:injectionTime:forMainFrameOnly:"),
                src, 0 /* WKUserScriptInjectionTimeAtDocumentStart */, false);
            SendVoid(contentController, Sel("addUserScript:"), script);
        });

        // Register the JS -> host message handler.
        IntPtr handler = SendPtr(SendPtr(_messageHandlerClass, Sel("alloc")), Sel("init"));
        WithNSString(MessageHandlerName, name =>
            SendVoid(contentController, Sel("addScriptMessageHandler:name:"), handler, name));
        SendVoid(config, Sel("setUserContentController:"), contentController);

        // WebView, sized to fill the window.
        IntPtr webview = SendPtr(Class("WKWebView"), Sel("alloc"));
        webview = SendInitWebView(webview, Sel("initWithFrame:configuration:"), frame, config);
        SendVoid(window, Sel("setContentView:"), webview);

        var host = new MacWebViewHost(webview);
        _current = host;
        Dialog.Provider = host;
        using var pumpCts = new CancellationTokenSource();
        BridgeHost.Attach(host, configure, pumpCts.Token);

        // Load content and show.
        WithNSString(html, h => SendVoid(webview, Sel("loadHTMLString:baseURL:"), h, IntPtr.Zero));
        SendVoid(window, Sel("center"));
        SendVoid(window, Sel("makeKeyAndOrderFront:"), window);
        SendVoid(nsApp, Sel("activateIgnoringOtherApps:"), true);

        SendVoid(nsApp, Sel("run"));
        pumpCts.Cancel();
        Dialog.Provider = null;
        _current = null;
    }

    /// <summary>
    /// Show an NSOpenPanel for files or folders and return the selected paths. The panel
    /// must run on the main thread; the calling (worker) thread blocks until it closes.
    /// </summary>
    public string[] ShowOpen(OpenDialogOptions options)
    {
        string[] result = [];
        using var done = new ManualResetEventSlim(false);
        RunOnMain(() =>
        {
            try { result = ShowOpenOnMain(options); }
            finally { done.Set(); }
        });
        done.Wait();
        return result;
    }

    private static string[] ShowOpenOnMain(OpenDialogOptions options)
    {
        IntPtr panel = SendPtr(Class("NSOpenPanel"), Sel("openPanel"));
        SendVoid(panel, Sel("setCanChooseFiles:"), !options.Directories);
        SendVoid(panel, Sel("setCanChooseDirectories:"), options.Directories);
        SendVoid(panel, Sel("setAllowsMultipleSelection:"), options.AllowMultiple);

        if (!string.IsNullOrEmpty(options.Title))
        {
            WithNSString(options.Title, s => SendVoid(panel, Sel("setMessage:"), s));
        }
        if (!string.IsNullOrEmpty(options.InitialDirectory))
        {
            WithNSString(options.InitialDirectory, p =>
            {
                IntPtr url = SendPtr(Class("NSURL"), Sel("fileURLWithPath:"), p);
                SendVoid(panel, Sel("setDirectoryURL:"), url);
            });
        }

        // NSModalResponseOK == 1; anything else (e.g. cancel) yields no selection.
        if (SendNInt(panel, Sel("runModal")) != 1)
        {
            return [];
        }

        IntPtr urls = SendPtr(panel, Sel("URLs"));
        long count = (long)SendPtr(urls, Sel("count"));
        var paths = new string[count];
        for (long i = 0; i < count; i++)
        {
            IntPtr url = SendPtrIndex(urls, Sel("objectAtIndex:"), (nuint)i);
            IntPtr nsPath = SendPtr(url, Sel("path"));
            IntPtr utf8 = SendPtr(nsPath, Sel("UTF8String"));
            paths[i] = utf8 != IntPtr.Zero ? Marshal.PtrToStringUTF8(utf8) ?? "" : "";
        }
        return paths;
    }

    /// <summary>Push a raw UTF-8 message to the webview by invoking the JS dispatch shim.</summary>
    public Task Send(ReadOnlyMemory<byte> message)
    {
        // Wrap the UTF-8 envelope as window.__dispatchMessageCallback("<escaped>") and
        // run it as JS, building the expression in UTF-8 so the payload never round-trips
        // through a managed UTF-16 string.
        byte[] js = WrapDispatch(message.Span);

        // WKWebView must be touched on the main thread; complete once it has run.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RunOnMain(() =>
        {
            try
            {
                fixed (byte* p = js)
                {
                    // +[NSString stringWithUTF8String:] copies the NUL-terminated bytes.
                    IntPtr nsString = SendPtr(Class("NSString"), Sel("stringWithUTF8String:"), (IntPtr)p);
                    SendVoid(_webview, Sel("evaluateJavaScript:completionHandler:"), nsString, IntPtr.Zero);
                }
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // Build a NUL-terminated UTF-8 JS expression that hands the message to the dispatch
    // shim. JsonEncodedText escapes the payload into a safe JS/JSON string literal.
    private static byte[] WrapDispatch(ReadOnlySpan<byte> message)
    {
        ReadOnlySpan<byte> prefix = "window.__dispatchMessageCallback(\""u8;
        ReadOnlySpan<byte> suffix = "\")"u8;
        ReadOnlySpan<byte> escaped = JsonEncodedText.Encode(message).EncodedUtf8Bytes;
        var js = new byte[prefix.Length + escaped.Length + suffix.Length + 1]; // +1: NUL
        prefix.CopyTo(js);
        escaped.CopyTo(js.AsSpan(prefix.Length));
        suffix.CopyTo(js.AsSpan(prefix.Length + escaped.Length));
        return js;
    }

    private void OnNativeMessage(ReadOnlyMemory<byte> message) => _incoming.Writer.TryWrite(message);

    // ---- Objective-C runtime classes we synthesize once at startup ----

    private static IntPtr _messageHandlerClass;
    private static IntPtr _appDelegateClass;
    private static bool _initialized;

    /// <summary>
    /// Load the AppKit/WebKit frameworks and synthesize our helper Objective-C classes,
    /// exactly once. Ordering matters: the frameworks must be loaded (so their classes
    /// and the WKScriptMessageHandler protocol resolve by name) before we register the
    /// classes or look up NSApplication/WKWebView. Called on the main thread from Run.
    /// </summary>
    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        // dlopen registers each framework's Objective-C classes with the runtime; no
        // link-time dependency is created (libobjc resolves everything by name).
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        NativeLibrary.Load("/System/Library/Frameworks/WebKit.framework/WebKit");

        _messageHandlerClass = RegisterMessageHandlerClass();
        _appDelegateClass = RegisterAppDelegateClass();
        _initialized = true;
    }

    private static IntPtr RegisterMessageHandlerClass()
    {
        IntPtr cls = objc_allocateClassPair(Class("NSObject"), "AtlantisScriptMessageHandler", UIntPtr.Zero);
        IntPtr protocol = objc_getProtocol("WKScriptMessageHandler");
        if (protocol != IntPtr.Zero)
        {
            class_addProtocol(cls, protocol);
        }

        // -(void)userContentController:(WKUserContentController*)c didReceiveScriptMessage:(WKScriptMessage*)m
        class_addMethod(
            cls, Sel("userContentController:didReceiveScriptMessage:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)&OnScriptMessage,
            "v@:@@");
        objc_registerClassPair(cls);
        return cls;
    }

    private static IntPtr RegisterAppDelegateClass()
    {
        IntPtr cls = objc_allocateClassPair(Class("NSObject"), "AtlantisAppDelegate", UIntPtr.Zero);
        // -(BOOL)applicationShouldTerminateAfterLastWindowClosed:(NSApplication*)sender
        class_addMethod(
            cls, Sel("applicationShouldTerminateAfterLastWindowClosed:"),
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, byte>)&AppShouldTerminateAfterLastWindowClosed,
            "c@:@");
        objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly]
    private static void OnScriptMessage(IntPtr self, IntPtr cmd, IntPtr contentController, IntPtr message)
    {
        IntPtr body = SendPtr(message, Sel("body"));
        IntPtr utf8 = SendPtr(body, Sel("UTF8String"));
        if (utf8 != IntPtr.Zero)
        {
            byte[] bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)utf8).ToArray();
            _current?.OnNativeMessage(bytes);
        }
    }

    [UnmanagedCallersOnly]
    private static byte AppShouldTerminateAfterLastWindowClosed(IntPtr self, IntPtr cmd, IntPtr sender) => 1;

    // ---- Main-thread marshalling (libdispatch) ----

    private static readonly IntPtr MainQueue =
        NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_dispatch_main_q");

    private static void RunOnMain(Action action)
    {
        bool onMain = SendPtr(Class("NSThread"), Sel("isMainThread")) != IntPtr.Zero;
        if (onMain)
        {
            action();
            return;
        }

        var handle = GCHandle.Alloc(action);
        dispatch_async_f(
            MainQueue, GCHandle.ToIntPtr(handle),
            (IntPtr)(delegate* unmanaged<IntPtr, void>)&DispatchTrampoline);
    }

    [UnmanagedCallersOnly]
    private static void DispatchTrampoline(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target!;
        handle.Free();
        action();
    }

    // ---- Small helpers over the Objective-C runtime ----

    private static IntPtr Class(string name) => objc_getClass(name);

    private static IntPtr Sel(string name) => sel_registerName(name);

    /// <summary>Create a transient NSString, run an action with it, then free the buffer.</summary>
    private static void WithNSString(string value, Action<IntPtr> action)
    {
        IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            // +[NSString stringWithUTF8String:] copies the bytes, so the buffer is
            // safe to free once the call that consumes the NSString returns.
            IntPtr nsString = SendPtr(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
            action(nsString);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGRect(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    // ---- Objective-C runtime P/Invokes (libobjc is present on macOS and iOS) ----

    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    [DllImport(LibObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getProtocol(string name);

    [DllImport(LibObjC, CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, UIntPtr extraBytes);

    [DllImport(LibObjC)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(LibObjC, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(LibObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addProtocol(IntPtr cls, IntPtr protocol);

    [DllImport(LibSystem)]
    private static extern void dispatch_async_f(IntPtr queue, IntPtr context, IntPtr work);

    // objc_msgSend overloads, one per native signature we invoke.

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr arg0);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendPtrIndex(IntPtr receiver, IntPtr selector, nuint arg0);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendNInt(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg0);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg0, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg0);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendInitWindow(
        IntPtr receiver, IntPtr selector, CGRect contentRect, nuint styleMask, nuint backing,
        [MarshalAs(UnmanagedType.I1)] bool defer);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendInitWebView(IntPtr receiver, IntPtr selector, CGRect frame, IntPtr configuration);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendInitUserScript(
        IntPtr receiver, IntPtr selector, IntPtr source, nint injectionTime,
        [MarshalAs(UnmanagedType.I1)] bool forMainFrameOnly);
}
#endif
