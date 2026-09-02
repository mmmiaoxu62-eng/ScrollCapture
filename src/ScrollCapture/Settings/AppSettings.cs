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

    public static AppSettings CreateDefaults() => new();
}
