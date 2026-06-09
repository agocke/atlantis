#if ATLANTIS_DESKTOP
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading.Channels;
using Atlantis.Bridge;

namespace Atlantis;

/// <summary>
/// A native Windows webview host backed by a Win32 top-level window and the WebView2
/// (Edge Chromium) control. WebView2 is reached through manual COM — vtable function
/// pointers for calls into the control (RCW), and hand-built vtables with
/// <see cref="UnmanagedCallersOnly"/> thunks for the completion/event callbacks WebView2
/// invokes (CCW) — because NativeAOT does not support the built-in COM marshaller.
/// </summary>
/// <remarks>
/// The bootstrap sequence (environment -> controller -> webview), the
/// <c>window.external</c> bridge init script over <c>chrome.webview</c>, and the
/// host/JS message plumbing are translated from Photino's Windows host
/// (Photino.Windows.cpp), which is MIT-licensed. Credit: Photino
/// (https://github.com/tryphotino/photino.Native).
///
/// The only native binary the package ships is <c>WebView2Loader.dll</c> (placed under
/// <c>runtimes/win-*/native</c>); the Edge WebView2 runtime itself is provided by the OS.
///
/// COM vtable slot numbers and interface IIDs below are taken verbatim from the WebView2
/// SDK header (WebView2.h, Microsoft.Web.WebView2).
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed unsafe class WindowsWebViewHost : IBridgeTransport
{
    // Bridge init script. Windows uses the chrome.webview transport rather than the
    // WKWebView/WebKitGTK postMessage shape, but exposes the same window.external API
    // that Atlantis' generated atlantis.ts consumes.
    private const string InitScript =
        """
        window.external = {
          sendMessage: function(message) {
            window.chrome.webview.postMessage(message);
          },
          receiveMessage: function(callback) {
            window.chrome.webview.addEventListener('message', function(e) { callback(e.data); });
          }
        };
        """;

    private static WindowsWebViewHost? _current;

    private static IntPtr _hwnd;
    private static IntPtr _controller;
    private static IntPtr _webview;

    private static string _html = string.Empty;

    // Callback objects (CCWs) handed to WebView2; kept alive for the process lifetime.
    private static IntPtr _envHandler;
    private static IntPtr _controllerHandler;
    private static IntPtr _webMessageHandler;

    // Pinned strings handed to native APIs that must outlive the call.
    private static IntPtr _classNamePtr;
    private static IntPtr _userDataFolderPtr;

    // Outbound messages marshalled onto the UI thread via WM_APP_SEND.
    private static readonly ConcurrentQueue<(string Message, TaskCompletionSource Tcs)> s_sendQueue = new();

    private readonly Channel<byte[]> _incoming =
        Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });

    public Task<byte[]> ReceiveAsync(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAsync(cancellationToken).AsTask();

    public static void Run(string title, string html, Action<BridgeHost>? configure)
    {
        _html = html;

        IntPtr hInstance = GetModuleHandleW(null);
        _classNamePtr = Marshal.StringToHGlobalUni("AtlantisWindow");

        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)sizeof(WNDCLASSEXW),
            lpfnWndProc = (IntPtr)(delegate* unmanaged<IntPtr, uint, UIntPtr, IntPtr, IntPtr>)&WndProc,
            hInstance = hInstance,
            hCursor = LoadCursorW(IntPtr.Zero, 32512 /* IDC_ARROW */),
            lpszClassName = _classNamePtr,
        };
        RegisterClassExW(ref wndClass);

        _hwnd = CreateWindowExW(
            0, _classNamePtr, title,
            0x00CF0000 /* WS_OVERLAPPEDWINDOW */,
            unchecked((int)0x80000000), unchecked((int)0x80000000), // CW_USEDEFAULT x, y
            800, 600,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        var host = new WindowsWebViewHost();
        _current = host;
        using var pumpCts = new CancellationTokenSource();
        if (configure is not null)
            BridgeHost.Attach(host, configure, pumpCts.Token);

        ShowWindow(_hwnd, 5 /* SW_SHOW */);
        UpdateWindow(_hwnd);

        // Build the WebView2 callback objects, then kick off async environment creation.
        // The completion handlers fire on this (UI) thread while the message loop runs.
        _envHandler = BuildHandler((IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr, int>)&InvokeEnvironmentCompleted);
        _controllerHandler = BuildHandler((IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr, int>)&InvokeControllerCompleted);
        _webMessageHandler = BuildHandler((IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)&InvokeWebMessageReceived);

        string userDataFolder = Path.Combine(Path.GetTempPath(), "Atlantis.WebView2");
        _userDataFolderPtr = Marshal.StringToHGlobalUni(userDataFolder);
        int hr = CreateCoreWebView2EnvironmentWithOptions(
            IntPtr.Zero, _userDataFolderPtr, IntPtr.Zero, _envHandler);
        if (hr < 0)
        {
            throw new InvalidOperationException(
                $"Failed to create the WebView2 environment (0x{hr:X8}). " +
                "Ensure the Microsoft Edge WebView2 Runtime is installed.");
        }

        // Win32 message pump; returns when the window posts WM_QUIT.
        while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        pumpCts.Cancel();
        _current = null;
    }

    /// <summary>Push a raw UTF-8 message to the webview via WebView2's PostWebMessageAsString.</summary>
    public Task Send(ReadOnlyMemory<byte> message)
    {
        // WebView2's message API is UTF-16 (LPCWSTR) only, so decode the UTF-8 envelope to
        // a string here - the one transcode the platform forces. WebView2 is thread-affine;
        // hand off to the UI thread's message loop and complete once it has been posted.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        s_sendQueue.Enqueue((Encoding.UTF8.GetString(message.Span), tcs));
        PostMessageW(_hwnd, WM_APP_SEND, UIntPtr.Zero, IntPtr.Zero);
        return tcs.Task;
    }

    private void OnNativeMessage(byte[] message) => _incoming.Writer.TryWrite(message);

    // ---- Win32 window procedure ----

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_APP_SEND = 0x8000; // WM_APP

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                if (_controller != IntPtr.Zero)
                {
                    GetClientRect(hwnd, out RECT rc);
                    var putBounds = (delegate* unmanaged<IntPtr, RECT, int>)Vtbl(_controller, 6);
                    putBounds(_controller, rc);
                }
                return IntPtr.Zero;

            case WM_APP_SEND:
                if (_webview != IntPtr.Zero)
                {
                    while (s_sendQueue.TryDequeue(out var item))
                    {
                        try
                        {
                            CallString(_webview, 33 /* PostWebMessageAsString */, item.Message);
                            item.Tcs.SetResult();
                        }
                        catch (Exception ex)
                        {
                            item.Tcs.SetException(ex);
                        }
                    }
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    // ---- WebView2 completion / event handlers (CCW Invoke thunks) ----

    [UnmanagedCallersOnly]
    private static int InvokeEnvironmentCompleted(IntPtr self, int errorCode, IntPtr environment)
    {
        if (errorCode < 0 || environment == IntPtr.Zero)
        {
            return 0;
        }

        // ICoreWebView2Environment::CreateCoreWebView2Controller(HWND, handler) — slot 3.
        var createController = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Vtbl(environment, 3);
        createController(environment, _hwnd, _controllerHandler);
        return 0;
    }

    [UnmanagedCallersOnly]
    private static int InvokeControllerCompleted(IntPtr self, int errorCode, IntPtr controller)
    {
        if (errorCode < 0 || controller == IntPtr.Zero)
        {
            return 0;
        }

        _controller = controller;

        // ICoreWebView2Controller::get_CoreWebView2(out webview) — slot 25.
        IntPtr webview;
        var getWebView = (delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(controller, 25);
        getWebView(controller, &webview);
        _webview = webview;

        // ICoreWebView2::AddScriptToExecuteOnDocumentCreated(script, handler=null) — slot 27.
        IntPtr scriptPtr = Marshal.StringToHGlobalUni(InitScript);
        try
        {
            var addScript = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, int>)Vtbl(webview, 27);
            addScript(webview, scriptPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(scriptPtr);
        }

        // ICoreWebView2::add_WebMessageReceived(handler, out token) — slot 34.
        long token;
        var addWebMessage = (delegate* unmanaged<IntPtr, IntPtr, long*, int>)Vtbl(webview, 34);
        addWebMessage(webview, _webMessageHandler, &token);

        // ICoreWebView2::NavigateToString(html) — slot 6.
        CallString(webview, 6, _html);

        // Size the control to the client area and show it.
        GetClientRect(_hwnd, out RECT rc);
        var putBounds = (delegate* unmanaged<IntPtr, RECT, int>)Vtbl(controller, 6);
        putBounds(controller, rc);
        var putVisible = (delegate* unmanaged<IntPtr, int, int>)Vtbl(controller, 4);
        putVisible(controller, 1 /* TRUE */);

        return 0;
    }

    [UnmanagedCallersOnly]
    private static int InvokeWebMessageReceived(IntPtr self, IntPtr sender, IntPtr args)
    {
        // ICoreWebView2WebMessageReceivedEventArgs::TryGetWebMessageAsString(out str) — slot 5.
        IntPtr strPtr;
        var tryGet = (delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(args, 5);
        tryGet(args, &strPtr);

        string? text = Marshal.PtrToStringUni(strPtr);
        Marshal.FreeCoTaskMem(strPtr); // ownership transfers to the caller
        if (text is not null)
        {
            _current?.OnNativeMessage(Encoding.UTF8.GetBytes(text));
        }

        return 0;
    }

    // ---- Manual COM plumbing ----

    /// <summary>Read the function pointer at <paramref name="slot"/> of a COM object's vtable.</summary>
    private static IntPtr Vtbl(IntPtr comObject, int slot)
    {
        IntPtr vtable = *(IntPtr*)comObject;
        return *(IntPtr*)(vtable + slot * IntPtr.Size);
    }

    /// <summary>Invoke a COM method whose only argument is an [in] LPCWSTR.</summary>
    private static void CallString(IntPtr comObject, int slot, string value)
    {
        IntPtr p = Marshal.StringToHGlobalUni(value);
        try
        {
            var fn = (delegate* unmanaged<IntPtr, IntPtr, int>)Vtbl(comObject, slot);
            fn(comObject, p);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// Allocate a minimal COM callback object: a native block whose first word points to a
    /// 4-slot vtable [QueryInterface, AddRef, Release, Invoke]. The object is intentionally
    /// leaked for the process lifetime, so AddRef/Release are no-ops.
    /// </summary>
    private static IntPtr BuildHandler(IntPtr invokeThunk)
    {
        IntPtr* vtable = (IntPtr*)Marshal.AllocHGlobal(IntPtr.Size * 4);
        vtable[0] = (IntPtr)(delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtable[1] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddRefRelease;
        vtable[2] = (IntPtr)(delegate* unmanaged<IntPtr, uint>)&AddRefRelease;
        vtable[3] = invokeThunk;

        IntPtr* obj = (IntPtr*)Marshal.AllocHGlobal(IntPtr.Size);
        obj[0] = (IntPtr)vtable;
        return (IntPtr)obj;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(IntPtr self, Guid* riid, IntPtr* ppvObject)
    {
        // These handlers are only ever queried for their own IID, IUnknown, or the
        // marker IAgileObject — all of which we satisfy by returning the object itself.
        *ppvObject = self;
        return 0; // S_OK
    }

    [UnmanagedCallersOnly]
    private static uint AddRefRelease(IntPtr self) => 1;

    // ---- Win32 / WebView2 loader P/Invokes ----

    [DllImport("kernel32", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wndClass);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, IntPtr lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32")] private static extern IntPtr LoadCursorW(IntPtr hInstance, int lpCursorName);
    [DllImport("user32")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32")] private static extern bool UpdateWindow(IntPtr hWnd);
    [DllImport("user32")] private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32")] private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32")]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32")] private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("WebView2Loader.dll", CharSet = CharSet.Unicode)]
    private static extern int CreateCoreWebView2EnvironmentWithOptions(
        IntPtr browserExecutableFolder, IntPtr userDataFolder, IntPtr environmentOptions, IntPtr handler);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
#endif
