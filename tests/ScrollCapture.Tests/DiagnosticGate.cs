using System.IO;

namespace ScrollCapture.Tests;

internal static class DiagnosticGate
{
    // Flip by creating %TEMP%\sc_diag.enable — no environment carryover between sessions.
    public static bool Enabled => File.Exists(Path.Combine(Path.GetTempPath(), "sc_diag.enable"));
}
