using Microsoft.Win32;

namespace ScrollCapture.Utils;

/// <summary>
/// HKCU Run key — per-user autostart for the current executable.
/// </summary>
public static class AutoStartManager
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ScrollCapture";

    public static void Apply(bool enabled, string? exePath = null)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enabled)
            {
                exePath ??= Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath))
                {
                    return;
                }
                key?.SetValue(ValueName, "\"" + exePath + "\"");
            }
            else
            {
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // best effort — a failed registry write must not crash the app
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }
}
