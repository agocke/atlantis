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
    public static void Run(string html, WindowOptions options, Action<BridgeHost>? configure)
    {
        if (OperatingSystem.IsMacOS())
        {
            MacWebViewHost.Run(options, html, configure);
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsWebViewHost.Run(options, html, configure);
        }
        else if (OperatingSystem.IsLinux())
        {
            LinuxWebViewHost.Run(options, html, configure);
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
internal sealed unsafe class MacWebViewHost : IBridgeTransport
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

    public static void Run(WindowOptions options, string html, Action<BridgeHost>? configure)
    {
        EnsureInitialized();

        IntPtr nsApp = SendPtr(Class("NSApplication"), Sel("sharedApplication"));
        SendVoid(nsApp, Sel("setActivationPolicy:"), IntPtr.Zero); // NSApplicationActivationPolicyRegular

        // App delegate so closing the last window ends the run loop.
        IntPtr appDelegate = SendPtr(SendPtr(_appDelegateClass, Sel("alloc")), Sel("init"));
        SendVoid(nsApp, Sel("setDelegate:"), appDelegate);

        // Window. macOS points are already logical units, so the options map directly.
        var frame = new CGRect(0, 0, options.Width, options.Height);
        nuint style = StyleMask(options);
        IntPtr window = SendPtr(Class("NSWindow"), Sel("alloc"));
        window = SendInitWindow(
            window, Sel("initWithContentRect:styleMask:backing:defer:"),
            frame, style, 2 /* NSBackingStoreBuffered */, false);
        WithNSString(options.Title, s => SendVoid(window, Sel("setTitle:"), s));

        // Resize constraints. Cocoa's defaults are (0,0) min and a very large max, so
        // only the supplied bounds are overridden.
        if (options.MinWidth is not null || options.MinHeight is not null)
        {
            SendVoidSize(window, Sel("setContentMinSize:"),
                new CGSize(options.MinWidth ?? 0, options.MinHeight ?? 0));
        }
        if (options.MaxWidth is not null || options.MaxHeight is not null)
        {
            SendVoidSize(window, Sel("setContentMaxSize:"),
                new CGSize(options.MaxWidth ?? float.MaxValue, options.MaxHeight ?? float.MaxValue));
        }

        if (options.AlwaysOnTop)
            SendVoid(window, Sel("setLevel:"), (IntPtr)3 /* NSFloatingWindowLevel */);

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
        using var pumpCts = new CancellationTokenSource();
        if (configure is not null)
            BridgeHost.Attach(host, configure, pumpCts.Token);

        // Load content and position the window.
        WithNSString(html, h => SendVoid(webview, Sel("loadHTMLString:baseURL:"), h, IntPtr.Zero));
        if (options.Center)
            SendVoid(window, Sel("center"));
        else if (options.X is not null || options.Y is not null)
            SendVoidPoint(window, Sel("setFrameTopLeftPoint:"), new CGPoint(options.X ?? 0, options.Y ?? 0));

        // Show (or keep hidden), then apply states that require an on-screen window.
        if (options.Visible)
        {
            SendVoid(window, Sel("makeKeyAndOrderFront:"), window);
            SendVoid(nsApp, Sel("activateIgnoringOtherApps:"), true);
            if (options.Maximized)
                SendVoid(window, Sel("zoom:"), IntPtr.Zero);
            if (options.Fullscreen)
                SendVoid(window, Sel("toggleFullScreen:"), IntPtr.Zero);
        }

        SendVoid(nsApp, Sel("run"));
        pumpCts.Cancel();
        _current = null;
    }

    // NSWindowStyleMask: Borderless=0, Titled=1, Closable=2, Miniaturizable=4, Resizable=8.
    private static nuint StyleMask(WindowOptions options)
    {
        nuint style = options.Decorations ? (nuint)(1 | 2 | 4) : 0;
        if (options.Resizable)
            style |= 8;
        return style;
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

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGSize(double width, double height)
    {
        public readonly double Width = width;
        public readonly double Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct CGPoint(double x, double y)
    {
        public readonly double X = x;
        public readonly double Y = y;
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
    private static extern void SendVoid(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg0);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg0, IntPtr arg1);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, [MarshalAs(UnmanagedType.I1)] bool arg0);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidSize(IntPtr receiver, IntPtr selector, CGSize size);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidPoint(IntPtr receiver, IntPtr selector, CGPoint point);

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
