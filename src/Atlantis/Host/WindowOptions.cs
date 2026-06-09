namespace Atlantis;

/// <summary>
/// Configuration for the application's main window, applied once when the window is
/// created. Sizes and positions are expressed in <em>logical</em> (DPI-independent)
/// units: each host maps them to its platform's native coordinate space (macOS points
/// and GTK are already logical; the Windows host scales by the window's DPI).
/// </summary>
/// <remarks>
/// On iOS the app is a single full-screen <c>WKWebView</c>, so these options are
/// accepted but ignored.
/// </remarks>
public sealed record WindowOptions
{
    /// <summary>Window title shown in the title bar (desktop only; ignored on iOS).</summary>
    public string Title { get; init; } = "Atlantis";

    /// <summary>
    /// Stable identifier for this window. Reserved for future multi-window addressing
    /// by the JS bridge; today there is only the single <c>"main"</c> window.
    /// </summary>
    public string Label { get; init; } = "main";

    /// <summary>Initial content width, in logical units.</summary>
    public double Width { get; init; } = 800;

    /// <summary>Initial content height, in logical units.</summary>
    public double Height { get; init; } = 600;

    /// <summary>Minimum content width the user may resize to, in logical units.</summary>
    public double? MinWidth { get; init; }

    /// <summary>Minimum content height the user may resize to, in logical units.</summary>
    public double? MinHeight { get; init; }

    /// <summary>Maximum content width the user may resize to, in logical units.</summary>
    public double? MaxWidth { get; init; }

    /// <summary>Maximum content height the user may resize to, in logical units.</summary>
    public double? MaxHeight { get; init; }

    /// <summary>
    /// Center the window on the active screen. When <see langword="true"/>,
    /// <see cref="X"/> and <see cref="Y"/> are ignored.
    /// </summary>
    public bool Center { get; init; } = true;

    /// <summary>Initial left position, in logical units (used only when <see cref="Center"/> is false).</summary>
    public double? X { get; init; }

    /// <summary>Initial top position, in logical units (used only when <see cref="Center"/> is false).</summary>
    public double? Y { get; init; }

    /// <summary>Whether the user can resize the window.</summary>
    public bool Resizable { get; init; } = true;

    /// <summary>Whether to draw the native title bar and border.</summary>
    public bool Decorations { get; init; } = true;

    /// <summary>Start the window in fullscreen mode.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>Start the window maximized (zoomed).</summary>
    public bool Maximized { get; init; }

    /// <summary>Keep the window above all other (non-topmost) windows.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Whether the window is shown on launch.</summary>
    public bool Visible { get; init; } = true;
}
