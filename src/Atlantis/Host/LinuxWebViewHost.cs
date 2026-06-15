#if ATLANTIS_DESKTOP
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;
using Atlantis.Bridge;

namespace Atlantis;

/// <summary>
/// A native Linux webview host backed by GTK 3 + WebKitGTK, driven through plain C
/// P/Invoke. GTK and WebKitGTK are OS-provided system libraries (installed via the
/// distro's package manager), so the package carries no native binaries for Linux.
/// </summary>
/// <remarks>
/// The WKWebView wiring (the <c>window.external</c> bridge init script, the
/// <c>postMessage</c> message handler, and the host-to-JS dispatch) is translated from
/// Photino's Linux host (Photino.Linux.cpp), which is MIT-licensed. Credit: Photino
/// (https://github.com/tryphotino/photino.Native).
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed unsafe class LinuxWebViewHost : IBridgeTransport, IFileDialogProvider
{
    // The WebKit script message handler name JavaScript posts to (see InitScript).
    private const string MessageHandlerName = "atlantisinterop";

    // Injected at document start so window.external exists before app scripts run.
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
    private readonly IntPtr _window;

    private static LinuxWebViewHost? _current;

    private readonly Channel<ReadOnlyMemory<byte>> _incoming =
        Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions { SingleReader = true });

    public Task<ReadOnlyMemory<byte>> ReceiveAsync(CancellationToken cancellationToken = default)
        => _incoming.Reader.ReadAsync(cancellationToken).AsTask();

    private LinuxWebViewHost(IntPtr webview, IntPtr window)
    {
        _webview = webview;
        _window = window;
    }

    public static void Run(string title, string html, Action<BridgeHost>? configure)
    {
        gtk_init(IntPtr.Zero, IntPtr.Zero);

        IntPtr window = gtk_window_new(0 /* GTK_WINDOW_TOPLEVEL */);
        gtk_window_set_title(window, title);
        gtk_window_set_default_size(window, 800, 600);
        gtk_window_set_position(window, 1 /* GTK_WIN_POS_CENTER */);

        // Quit the GTK main loop when the window is closed.
        Connect(window, "destroy", (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, void>)&OnDestroy, IntPtr.Zero);

        IntPtr contentManager = webkit_user_content_manager_new();
        IntPtr webview = webkit_web_view_new_with_user_content_manager(contentManager);
        gtk_container_add(window, webview);

        // Inject the window.external bridge before any page script runs.
        IntPtr script = webkit_user_script_new(
            InitScript,
            0 /* WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES */,
            0 /* WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START */,
            IntPtr.Zero, IntPtr.Zero);
        webkit_user_content_manager_add_script(contentManager, script);
        webkit_user_script_unref(script);

        // Route JS -> host messages. The handler name is appended to the signal as a
        // GObject signal detail ("script-message-received::<name>").
        Connect(
            contentManager, "script-message-received::" + MessageHandlerName,
            (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&OnScriptMessage, IntPtr.Zero);
        webkit_user_content_manager_register_script_message_handler(contentManager, MessageHandlerName);

        var host = new LinuxWebViewHost(webview, window);
        _current = host;
        Dialog.Provider = host;
        using var pumpCts = new CancellationTokenSource();
        BridgeHost.Attach(host, configure, pumpCts.Token);

        webkit_web_view_load_html(webview, html, IntPtr.Zero);
        gtk_widget_show_all(window);

        gtk_main();
        pumpCts.Cancel();
        Dialog.Provider = null;
        _current = null;
    }

    /// <summary>
    /// Show a GtkFileChooserNative for files or folders and return the selected paths.
    /// GTK is single-threaded, so the dialog is run on the GTK main loop while the calling
    /// (worker) thread blocks until it closes.
    /// </summary>
    public string[] ShowOpen(OpenDialogOptions options)
    {
        string[] result = [];
        using var done = new ManualResetEventSlim(false);
        var handle = GCHandle.Alloc((Action)(() =>
        {
            try { result = ShowOpenOnMain(options); }
            finally { done.Set(); }
        }));
        g_idle_add((IntPtr)(delegate* unmanaged<IntPtr, int>)&RunIdleAction, GCHandle.ToIntPtr(handle));
        done.Wait();
        return result;
    }

    private string[] ShowOpenOnMain(OpenDialogOptions options)
    {
        // GTK_FILE_CHOOSER_ACTION_OPEN == 0, GTK_FILE_CHOOSER_ACTION_SELECT_FOLDER == 2.
        int action = options.Directories ? 2 : 0;
        IntPtr dialog = gtk_file_chooser_native_new(options.Title, _window, action, "_Open", "_Cancel");
        if (options.AllowMultiple)
        {
            gtk_file_chooser_set_select_multiple(dialog, true);
        }
        if (!string.IsNullOrEmpty(options.InitialDirectory))
        {
            gtk_file_chooser_set_current_folder(dialog, options.InitialDirectory);
        }

        string[] result = [];
        // GTK_RESPONSE_ACCEPT == -3.
        if (gtk_native_dialog_run(dialog) == -3)
        {
            result = options.AllowMultiple
                ? DrainStringSList(gtk_file_chooser_get_filenames(dialog))
                : TakeSingleFilename(gtk_file_chooser_get_filename(dialog));
        }

        g_object_unref(dialog);
        return result;
    }

    private static string[] TakeSingleFilename(IntPtr path)
    {
        if (path == IntPtr.Zero)
        {
            return [];
        }
        string[] result = [Marshal.PtrToStringUTF8(path) ?? ""];
        g_free(path);
        return result;
    }

    // Walk a GSList of newly-allocated UTF-8 path strings, freeing each element's data and
    // the list itself (the GSList struct is { gpointer data; GSList* next; }).
    private static string[] DrainStringSList(IntPtr list)
    {
        var paths = new List<string>();
        for (IntPtr node = list; node != IntPtr.Zero; node = *(IntPtr*)(node + IntPtr.Size))
        {
            IntPtr data = *(IntPtr*)node;
            if (data != IntPtr.Zero)
            {
                paths.Add(Marshal.PtrToStringUTF8(data) ?? "");
                g_free(data);
            }
        }
        g_slist_free(list);
        return paths.ToArray();
    }

    /// <summary>Push a raw UTF-8 message to the webview by invoking the JS dispatch shim.</summary>
    public Task Send(ReadOnlyMemory<byte> message)
    {
        // Wrap the UTF-8 envelope as window.__dispatchMessageCallback("<escaped>"); WebKitGTK
        // is UTF-8 native, so the bytes go straight to run_javascript with no transcoding.
        byte[] js = WrapDispatch(message.Span);

        // WebKitGTK is single-threaded; marshal onto the GTK main loop and
        // complete once the dispatch has run there.
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc((Action)(() =>
        {
            try
            {
                webkit_web_view_run_javascript(_webview, js, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }));
        g_idle_add((IntPtr)(delegate* unmanaged<IntPtr, int>)&RunIdleAction, GCHandle.ToIntPtr(handle));
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

    // ---- Native callbacks ----

    [UnmanagedCallersOnly]
    private static void OnDestroy(IntPtr widget, IntPtr userData) => gtk_main_quit();

    [UnmanagedCallersOnly]
    private static void OnScriptMessage(IntPtr contentManager, IntPtr jsResult, IntPtr userData)
    {
        IntPtr jsValue = webkit_javascript_result_get_js_value(jsResult);
        if (jsc_value_is_string(jsValue) != 0)
        {
            IntPtr utf8 = jsc_value_to_string(jsValue);
            if (utf8 != IntPtr.Zero)
            {
                byte[] bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)utf8).ToArray();
                _current?.OnNativeMessage(bytes);
            }
            g_free(utf8);
        }

        webkit_javascript_result_unref(jsResult);
    }

    [UnmanagedCallersOnly]
    private static int RunIdleAction(IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var action = (Action)handle.Target!;
        handle.Free();
        action();
        return 0; // G_SOURCE_REMOVE: run once
    }

    private static void Connect(IntPtr instance, string signal, IntPtr callback, IntPtr data)
        => g_signal_connect_data(instance, signal, callback, data, IntPtr.Zero, 0);

    // ---- P/Invoke. Logical library names are mapped to versioned sonames by the
    //      resolver below so the same binary works across distros. ----

    private const string Gtk = "gtk";
    private const string WebKit = "webkit2gtk";
    private const string GObject = "gobject";
    private const string GLib = "glib";
    private const string Jsc = "jsc";

    static LinuxWebViewHost()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxWebViewHost).Assembly, Resolve);
    }

    private static IntPtr Resolve(string libraryName, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
    {
        string[] candidates = libraryName switch
        {
            Gtk => ["libgtk-3.so.0", "libgtk-3.so"],
            WebKit => ["libwebkit2gtk-4.1.so.0", "libwebkit2gtk-4.0.so.37", "libwebkit2gtk-4.0.so"],
            GObject => ["libgobject-2.0.so.0", "libgobject-2.0.so"],
            GLib => ["libglib-2.0.so.0", "libglib-2.0.so"],
            Jsc => ["libjavascriptcoregtk-4.1.so.0", "libjavascriptcoregtk-4.0.so.18", "libjavascriptcoregtk-4.0.so"],
            _ => [],
        };

        foreach (string candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out IntPtr handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    [DllImport(Gtk)] private static extern void gtk_init(IntPtr argc, IntPtr argv);
    [DllImport(Gtk)] private static extern IntPtr gtk_window_new(int type);
    [DllImport(Gtk, CharSet = CharSet.Ansi)] private static extern void gtk_window_set_title(IntPtr window, string title);
    [DllImport(Gtk)] private static extern void gtk_window_set_default_size(IntPtr window, int width, int height);
    [DllImport(Gtk)] private static extern void gtk_window_set_position(IntPtr window, int position);
    [DllImport(Gtk)] private static extern void gtk_container_add(IntPtr container, IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_main();
    [DllImport(Gtk)] private static extern void gtk_main_quit();

    [DllImport(Gtk, CharSet = CharSet.Ansi)]
    private static extern IntPtr gtk_file_chooser_native_new(string? title, IntPtr parent, int action, string? acceptLabel, string? cancelLabel);

    [DllImport(Gtk)] private static extern int gtk_native_dialog_run(IntPtr dialog);
    [DllImport(Gtk)] private static extern void gtk_file_chooser_set_select_multiple(IntPtr chooser, [MarshalAs(UnmanagedType.I1)] bool select);

    [DllImport(Gtk, CharSet = CharSet.Ansi)]
    private static extern bool gtk_file_chooser_set_current_folder(IntPtr chooser, string folder);

    [DllImport(Gtk)] private static extern IntPtr gtk_file_chooser_get_filename(IntPtr chooser);
    [DllImport(Gtk)] private static extern IntPtr gtk_file_chooser_get_filenames(IntPtr chooser);

    [DllImport(WebKit)] private static extern IntPtr webkit_user_content_manager_new();
    [DllImport(WebKit)] private static extern IntPtr webkit_web_view_new_with_user_content_manager(IntPtr contentManager);

    [DllImport(WebKit, CharSet = CharSet.Ansi)]
    private static extern IntPtr webkit_user_script_new(string source, int injectedFrames, int injectionTime, IntPtr allowList, IntPtr blockList);

    [DllImport(WebKit)] private static extern void webkit_user_content_manager_add_script(IntPtr contentManager, IntPtr script);
    [DllImport(WebKit)] private static extern void webkit_user_script_unref(IntPtr script);

    [DllImport(WebKit, CharSet = CharSet.Ansi)]
    private static extern bool webkit_user_content_manager_register_script_message_handler(IntPtr contentManager, string name);

    [DllImport(WebKit, CharSet = CharSet.Ansi)]
    private static extern void webkit_web_view_load_html(IntPtr webView, string content, IntPtr baseUri);

    [DllImport(WebKit)]
    private static extern void webkit_web_view_run_javascript(IntPtr webView, byte[] script, IntPtr cancellable, IntPtr callback, IntPtr userData);

    [DllImport(WebKit)] private static extern IntPtr webkit_javascript_result_get_js_value(IntPtr jsResult);
    [DllImport(WebKit)] private static extern void webkit_javascript_result_unref(IntPtr jsResult);

    [DllImport(Jsc)] private static extern int jsc_value_is_string(IntPtr value);
    [DllImport(Jsc)] private static extern IntPtr jsc_value_to_string(IntPtr value);

    [DllImport(GLib)] private static extern void g_free(IntPtr mem);
    [DllImport(GLib)] private static extern void g_slist_free(IntPtr list);
    [DllImport(GLib)] private static extern uint g_idle_add(IntPtr function, IntPtr data);

    [DllImport(GObject)] private static extern void g_object_unref(IntPtr obj);

    [DllImport(GObject, CharSet = CharSet.Ansi)]
    private static extern ulong g_signal_connect_data(IntPtr instance, string detailedSignal, IntPtr handler, IntPtr data, IntPtr destroyData, int connectFlags);
}
#endif
