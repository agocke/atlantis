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
internal sealed unsafe class WindowsWebViewHost : IBridgeTransport, IFileDialogProvider
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
    private static WindowOptions _options = new();

    // Callback objects (CCWs) handed to WebView2; kept alive for the process lifetime.
    private static IntPtr _envHandler;
    private static IntPtr _controllerHandler;
    private static IntPtr _webMessageHandler;

    // Pinned strings handed to native APIs that must outlive the call.
    private static IntPtr _classNamePtr;
    private static IntPtr _userDataFolderPtr;

    // Outbound messages marshalled onto the UI thread via WM_APP_SEND.
    private static readonly ConcurrentQueue<(string Message, TaskCompletionSource Tcs)> s_sendQueue = new();

    // Arbitrary work (e.g. showing a modal file dialog) marshalled onto the UI thread.
    private static readonly ConcurrentQueue<Action> s_invokeQueue = new();

    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

    public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAsync(cancellationToken).AsTask();

    public static void Run(WindowOptions options, string html, Action<BridgeHost>? configure)
    {
        _html = html;
        _options = options;

        // Become per-monitor DPI aware so the client area maps 1:1 to physical pixels
        // and WebView2 rasterizes crisply. Best-effort: ignored on older Windows, where
        // GetDpiForWindow then reports 96 and logical units pass through unscaled.
        SetProcessDpiAwarenessContext((IntPtr)(-4) /* PER_MONITOR_AWARE_V2 */);

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

        uint style = WindowStyle(options);

        // Create at a default spot first so the window picks up its monitor's DPI, then
        // size and position it from the logical options scaled to that DPI.
        _hwnd = CreateWindowExW(
            0, _classNamePtr, options.Title, style,
            unchecked((int)0x80000000), unchecked((int)0x80000000), // CW_USEDEFAULT x, y
            unchecked((int)0x80000000), unchecked((int)0x80000000), // CW_USEDEFAULT w, h
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        uint dpi = GetDpiForWindow(_hwnd);
        if (dpi == 0) dpi = 96;

        int x, y, winW, winH;
        if (options.Fullscreen)
        {
            // Borderless full-screen cover of the primary monitor.
            x = 0;
            y = 0;
            winW = GetSystemMetrics(0 /* SM_CXSCREEN */);
            winH = GetSystemMetrics(1 /* SM_CYSCREEN */);
        }
        else
        {
            (winW, winH) = ClientToWindow(options.Width, options.Height, dpi, style);
            if (options.Center || (options.X is null && options.Y is null))
            {
                RECT work = GetWorkArea();
                x = work.Left + ((work.Right - work.Left) - winW) / 2;
                y = work.Top + ((work.Bottom - work.Top) - winH) / 2;
            }
            else
            {
                x = Scale(options.X ?? 0, dpi);
                y = Scale(options.Y ?? 0, dpi);
            }
        }

        IntPtr insertAfter = options.AlwaysOnTop ? (IntPtr)(-1) /* HWND_TOPMOST */ : (IntPtr)0 /* HWND_TOP */;
        SetWindowPos(_hwnd, insertAfter, x, y, winW, winH, 0x0010 /* SWP_NOACTIVATE */);

        var host = new WindowsWebViewHost();
        _current = host;
        Dialog.Provider = host;
        using var pumpCts = new CancellationTokenSource();
        BridgeHost.Attach(host, configure, pumpCts.Token);

        int showCmd = (options.Maximized && !options.Fullscreen) ? 3 /* SW_MAXIMIZE */
            : options.Visible ? 5 /* SW_SHOW */
            : 0 /* SW_HIDE */;
        ShowWindow(_hwnd, showCmd);
        if (options.Visible)
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
        Dialog.Provider = null;
        _current = null;
    }

    /// <summary>
    /// Show an IFileOpenDialog for files or folders and return the selected paths. The
    /// dialog is shown on the UI thread; the calling (worker) thread blocks until it closes.
    /// </summary>
    public string[] ShowOpen(OpenDialogOptions options)
    {
        string[] result = [];
        using var done = new ManualResetEventSlim(false);
        s_invokeQueue.Enqueue(() =>
        {
            try { result = ShowOpenOnUi(options); }
            finally { done.Set(); }
        });
        PostMessageW(_hwnd, WM_APP_INVOKE, UIntPtr.Zero, IntPtr.Zero);
        done.Wait();
        return result;
    }

    private static string[] ShowOpenOnUi(OpenDialogOptions options)
    {
        // The UI thread hosts WebView2 (STA); make sure COM is initialized for it. A second
        // call is harmless (returns S_FALSE), so the result is intentionally ignored.
        CoInitializeEx(IntPtr.Zero, 2 /* COINIT_APARTMENTTHREADED */);

        Guid clsid = CLSID_FileOpenDialog;
        Guid iid = IID_IFileOpenDialog;
        if (CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out IntPtr dialog) < 0
            || dialog == IntPtr.Zero)
        {
            return [];
        }

        try
        {
            // IFileDialog::GetOptions(out) — slot 10; SetOptions — slot 9.
            uint opts;
            var getOptions = (delegate* unmanaged<IntPtr, uint*, int>)Vtbl(dialog, 10);
            getOptions(dialog, &opts);
            opts |= FOS_FORCEFILESYSTEM;
            if (options.Directories) opts |= FOS_PICKFOLDERS;
            if (options.AllowMultiple) opts |= FOS_ALLOWMULTISELECT;
            var setOptions = (delegate* unmanaged<IntPtr, uint, int>)Vtbl(dialog, 9);
            setOptions(dialog, opts);

            if (!string.IsNullOrEmpty(options.Title))
            {
                CallString(dialog, 17 /* IFileDialog::SetTitle */, options.Title);
            }
            if (!string.IsNullOrEmpty(options.InitialDirectory) && Directory.Exists(options.InitialDirectory))
            {
                TrySetFolder(dialog, options.InitialDirectory);
            }

            // IModalWindow::Show(HWND) — slot 3. A non-zero (negative) HRESULT means the
            // user cancelled (HRESULT_FROM_WIN32(ERROR_CANCELLED)) or the call failed.
            var show = (delegate* unmanaged<IntPtr, IntPtr, int>)Vtbl(dialog, 3);
            if (show(dialog, _hwnd) < 0)
            {
                return [];
            }

            return options.AllowMultiple ? GetResults(dialog) : GetSingleResult(dialog);
        }
        finally
        {
            Release(dialog);
        }
    }

    private static string[] GetSingleResult(IntPtr dialog)
    {
        // IFileDialog::GetResult(out IShellItem) — slot 20.
        IntPtr item;
        var getResult = (delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(dialog, 20);
        if (getResult(dialog, &item) < 0 || item == IntPtr.Zero)
        {
            return [];
        }
        try
        {
            string? path = GetItemPath(item);
            return path is null ? [] : [path];
        }
        finally
        {
            Release(item);
        }
    }

    private static string[] GetResults(IntPtr dialog)
    {
        // IFileOpenDialog::GetResults(out IShellItemArray) — slot 27.
        IntPtr array;
        var getResults = (delegate* unmanaged<IntPtr, IntPtr*, int>)Vtbl(dialog, 27);
        if (getResults(dialog, &array) < 0 || array == IntPtr.Zero)
        {
            return [];
        }
        try
        {
            // IShellItemArray::GetCount(out DWORD) — slot 7; GetItemAt(DWORD, out) — slot 8.
            uint count;
            var getCount = (delegate* unmanaged<IntPtr, uint*, int>)Vtbl(array, 7);
            getCount(array, &count);

            var paths = new List<string>((int)count);
            var getItemAt = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(array, 8);
            for (uint i = 0; i < count; i++)
            {
                IntPtr item;
                if (getItemAt(array, i, &item) < 0 || item == IntPtr.Zero)
                {
                    continue;
                }
                try
                {
                    string? path = GetItemPath(item);
                    if (path is not null) paths.Add(path);
                }
                finally
                {
                    Release(item);
                }
            }
            return paths.ToArray();
        }
        finally
        {
            Release(array);
        }
    }

    private static string? GetItemPath(IntPtr item)
    {
        // IShellItem::GetDisplayName(SIGDN_FILESYSPATH, out LPWSTR) — slot 5.
        IntPtr str;
        var getName = (delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Vtbl(item, 5);
        if (getName(item, SIGDN_FILESYSPATH, &str) < 0 || str == IntPtr.Zero)
        {
            return null;
        }
        string? path = Marshal.PtrToStringUni(str);
        Marshal.FreeCoTaskMem(str); // GetDisplayName allocates with CoTaskMemAlloc
        return path;
    }

    private static void TrySetFolder(IntPtr dialog, string folder)
    {
        Guid iid = IID_IShellItem;
        if (SHCreateItemFromParsingName(folder, IntPtr.Zero, ref iid, out IntPtr item) < 0 || item == IntPtr.Zero)
        {
            return;
        }
        try
        {
            // IFileDialog::SetFolder(IShellItem) — slot 12.
            var setFolder = (delegate* unmanaged<IntPtr, IntPtr, int>)Vtbl(dialog, 12);
            setFolder(dialog, item);
        }
        finally
        {
            Release(item);
        }
    }

    /// <summary>Invoke IUnknown::Release (vtable slot 2) on a COM object.</summary>
    private static void Release(IntPtr comObject)
    {
        var release = (delegate* unmanaged<IntPtr, uint>)Vtbl(comObject, 2);
        release(comObject);
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

    private void OnNativeMessage(ReadOnlyMemory<byte> message) => _incoming.Writer.TryWrite(message);

    // ---- Win32 window procedure ----

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_SIZE = 0x0005;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_APP_SEND = 0x8000; // WM_APP
    private const uint WM_APP_INVOKE = 0x8001; // WM_APP + 1

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                if (ApplyMinMax(hwnd, lParam))
                    return IntPtr.Zero;
                return DefWindowProcW(hwnd, msg, wParam, lParam);

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

            case WM_APP_INVOKE:
                while (s_invokeQueue.TryDequeue(out var work))
                {
                    work();
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

    // ---- Window option mapping ----

    // Win32 window styles.
    private const uint WS_OVERLAPPEDWINDOW = 0x00CF0000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint WS_POPUP = 0x80000000;

    private static uint WindowStyle(WindowOptions options)
    {
        if (options.Fullscreen || !options.Decorations)
            return WS_POPUP | (options.Resizable && !options.Fullscreen ? WS_THICKFRAME : 0);

        uint style = WS_OVERLAPPEDWINDOW;
        if (!options.Resizable)
            style &= ~(WS_THICKFRAME | WS_MAXIMIZEBOX);
        return style;
    }

    // Scale a logical length to physical pixels at the given DPI.
    private static int Scale(double logical, uint dpi) => (int)Math.Round(logical * dpi / 96.0);

    // Convert a logical client size to the physical outer window size (including the
    // frame) at the given DPI, so the content area ends up the requested logical size.
    private static (int Width, int Height) ClientToWindow(double logicalW, double logicalH, uint dpi, uint style)
    {
        var rc = new RECT { Left = 0, Top = 0, Right = Scale(logicalW, dpi), Bottom = Scale(logicalH, dpi) };
        AdjustWindowRectExForDpi(ref rc, style, false, 0, dpi);
        return (rc.Right - rc.Left, rc.Bottom - rc.Top);
    }

    private static RECT GetWorkArea()
    {
        RECT work;
        // SPI_GETWORKAREA = 0x0030; falls back to the full primary screen on failure.
        if (!SystemParametersInfoW(0x0030, 0, out work, 0))
            work = new RECT { Left = 0, Top = 0, Right = GetSystemMetrics(0), Bottom = GetSystemMetrics(1) };
        return work;
    }

    // Apply the configured min/max content sizes to a WM_GETMINMAXINFO request. Returns
    // false when no constraint is set, so the caller can defer to DefWindowProc.
    private static bool ApplyMinMax(IntPtr hwnd, IntPtr lParam)
    {
        var o = _options;
        if (o.MinWidth is null && o.MinHeight is null && o.MaxWidth is null && o.MaxHeight is null)
            return false;

        uint dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        uint style = (uint)GetWindowLongPtrW(hwnd, -16 /* GWL_STYLE */).ToInt64();

        MINMAXINFO* mmi = (MINMAXINFO*)lParam;
        if (o.MinWidth is double minW) mmi->ptMinTrackSize.X = ClientToWindow(minW, 0, dpi, style).Width;
        if (o.MinHeight is double minH) mmi->ptMinTrackSize.Y = ClientToWindow(0, minH, dpi, style).Height;
        if (o.MaxWidth is double maxW) mmi->ptMaxTrackSize.X = ClientToWindow(maxW, 0, dpi, style).Width;
        if (o.MaxHeight is double maxH) mmi->ptMaxTrackSize.Y = ClientToWindow(0, maxH, dpi, style).Height;
        return true;
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

    [DllImport("user32")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32")] private static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32")] private static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);

    [DllImport("user32")]
    private static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, uint dwStyle, bool bMenu, uint dwExStyle, uint dpi);

    [DllImport("user32")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32")]
    private static extern bool SystemParametersInfoW(uint uiAction, uint uiParam, out RECT pvParam, uint fWinIni);

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

    // ---- Common Item Dialog (IFileOpenDialog) for the open file/folder dialogs ----

    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
    private static readonly Guid IID_IFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const uint FOS_PICKFOLDERS = 0x20;
    private const uint FOS_FORCEFILESYSTEM = 0x40;
    private const uint FOS_ALLOWMULTISELECT = 0x200;

    [DllImport("ole32")]
    private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr outer, uint clsContext, ref Guid riid, out IntPtr ppv);

    [DllImport("shell32", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindCtx, ref Guid riid, out IntPtr item);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
#endif
