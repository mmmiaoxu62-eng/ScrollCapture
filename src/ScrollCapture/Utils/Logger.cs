using System.Globalization;
using System.IO;
using System.Text;

namespace ScrollCapture.Utils;

/// <summary>
/// Minimal file logger. Never throws; failures are silently swallowed.
/// Location: %AppData%\ScrollCapture\logs\app.log
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();

    public static string CurrentLogFilePath { get; } = Path.Combine(AppPaths.DataDir, "logs", "app.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? exception = null)
    {
        Write("ERROR", message);
        if (exception != null)
        {
            Write("ERROR", exception.ToString());
        }
    }

    private static void Write(string level, string message)
    {
        try
        {
            string line = string.Concat(
                "[", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), "] ",
                level, " ", message);
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CurrentLogFilePath)!);
                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
