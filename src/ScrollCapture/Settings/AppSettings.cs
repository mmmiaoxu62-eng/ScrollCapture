using ScrollCapture.Utils;

namespace ScrollCapture.Settings;

[Serializable]
public class AppSettings
{
    // Ctrl+Shift+S conflicts with Chrome/Edge built-in screenshot; keep the browser one free.
    public string CaptureHotkey { get; set; } = "Ctrl+Alt+S";

    public string SaveDirectory { get; set; } = AppPaths.DefaultSaveDirectory;

    public int MaxImageHeight { get; set; } = 30000;

    public int MaxFrames { get; set; } = 100;

    /// <summary>Writes per-pair fixed-region debug artifacts (txt report + overlay PNG). Off by default.</summary>
    public bool FixedRegionDebug { get; set; }

    /// <summary>Scroll direction for auto capture: "Down" (newer content below) or "Up" (history above).</summary>
    public string ScrollDirection { get; set; } = "Down";

    /// <summary>Wheel notches per scroll (1..8; bigger = faster, overlap smaller).</summary>
    public int ScrollWheelStep { get; set; } = 4;

    /// <summary>Frame cadence milliseconds (200..2000).</summary>
    public int ScrollDelayMs { get; set; } = 400;

    /// <summary>Start with Windows (launches to tray).</summary>
    public bool AutoStart { get; set; }

    public static AppSettings CreateDefaults() => new();
}
