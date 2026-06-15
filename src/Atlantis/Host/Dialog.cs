using System.Buffers;
using System.Text.Json;
using Atlantis.Bridge;

namespace Atlantis;

/// <summary>
/// Options for a native open dialog. The same options drive a file picker or a folder
/// picker depending on <see cref="Directories"/>.
/// </summary>
public sealed class OpenDialogOptions
{
    /// <summary>Window/title text shown on the dialog (platform dependent).</summary>
    public string? Title { get; init; }

    /// <summary>Allow selecting more than one item.</summary>
    public bool AllowMultiple { get; init; }

    /// <summary>Pick directories instead of files.</summary>
    public bool Directories { get; init; }

    /// <summary>Directory the dialog initially shows. Ignored if it does not exist.</summary>
    public string? InitialDirectory { get; init; }
}

/// <summary>
/// Native open file/folder dialogs for the active Atlantis desktop window. Backed by
/// <c>NSOpenPanel</c> on macOS, <c>IFileOpenDialog</c> on Windows, and
/// <c>GtkFileChooserNative</c> on Linux. Each call blocks the caller until the user
/// dismisses the dialog and returns the chosen filesystem path(s).
/// </summary>
/// <remarks>
/// These APIs require a running desktop window (created by <see cref="AtlantisApp.Run"/>);
/// they are not available on iOS or before the window has started. The same operations
/// are exposed to JavaScript as <c>atlantis.dialog.openFile/openFiles/openFolder</c>.
/// </remarks>
public static class Dialog
{
    // The active desktop host backs the dialogs. Set while a window is running so a call
    // from any thread (typically a bridge handler on a worker thread) is routed to it.
    internal static IFileDialogProvider? Provider { get; set; }

    /// <summary>
    /// Show an open dialog described by <paramref name="options"/> and return the selected
    /// filesystem paths, or an empty list if the user cancelled.
    /// </summary>
    /// <exception cref="InvalidOperationException">No desktop window is active.</exception>
    public static IReadOnlyList<string> ShowOpen(OpenDialogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var provider = Provider
            ?? throw new InvalidOperationException(
                "No active Atlantis window. File dialogs require a running desktop window (AtlantisApp.Run).");
        return provider.ShowOpen(options);
    }

    /// <summary>Pick a single file. Returns its path, or <c>null</c> if cancelled.</summary>
    public static string? OpenFile(string? title = null, string? initialDirectory = null)
        => ShowOpen(new OpenDialogOptions { Title = title, InitialDirectory = initialDirectory }) is [string first, ..]
            ? first
            : null;

    /// <summary>Pick one or more files. Returns an empty list if cancelled.</summary>
    public static IReadOnlyList<string> OpenFiles(string? title = null, string? initialDirectory = null)
        => ShowOpen(new OpenDialogOptions { Title = title, AllowMultiple = true, InitialDirectory = initialDirectory });

    /// <summary>Pick a single folder. Returns its path, or <c>null</c> if cancelled.</summary>
    public static string? OpenFolder(string? title = null, string? initialDirectory = null)
        => ShowOpen(new OpenDialogOptions { Title = title, Directories = true, InitialDirectory = initialDirectory }) is [string first, ..]
            ? first
            : null;

    // ---- Built-in JS bridge handlers ----

    /// <summary>
    /// Register the built-in <c>Atlantis.Dialog.*</c> bridge handlers so a webview can call
    /// <c>atlantis.dialog.openFile/openFiles/openFolder</c>. Each takes an optional title
    /// string as its first argument.
    /// </summary>
    internal static void RegisterBridge(BridgeHost bridge)
    {
        bridge.Register("Atlantis.Dialog.OpenFile", (args, _) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(SerializeSingle(OpenFile(TitleArg(args)))));

        bridge.Register("Atlantis.Dialog.OpenFiles", (args, _) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(SerializeMany(OpenFiles(TitleArg(args)))));

        bridge.Register("Atlantis.Dialog.OpenFolder", (args, _) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(SerializeSingle(OpenFolder(TitleArg(args)))));
    }

    // The first arg, when present and a JSON string, is the dialog title.
    private static string? TitleArg(JsonElement args)
        => args.ValueKind == JsonValueKind.Array && args.GetArrayLength() > 0 && args[0].ValueKind == JsonValueKind.String
            ? args[0].GetString()
            : null;

    // Serialize a nullable path as a JSON string or null, without reflection (AOT safe).
    private static ReadOnlyMemory<byte> SerializeSingle(string? path)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (path is null)
                writer.WriteNullValue();
            else
                writer.WriteStringValue(path);
        }
        return buffer.WrittenMemory;
    }

    // Serialize a path list as a JSON array of strings, without reflection (AOT safe).
    private static ReadOnlyMemory<byte> SerializeMany(IReadOnlyList<string> paths)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var path in paths)
                writer.WriteStringValue(path);
            writer.WriteEndArray();
        }
        return buffer.WrittenMemory;
    }
}

/// <summary>
/// Implemented by the active desktop host to show a native open dialog on its UI thread.
/// </summary>
internal interface IFileDialogProvider
{
    /// <summary>Show the dialog and return the selected paths (empty if cancelled).</summary>
    string[] ShowOpen(OpenDialogOptions options);
}
