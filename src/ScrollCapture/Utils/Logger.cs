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
    private const long MaxLogBytes = 2L * 1024 * 1024;
    private const int MaxRotatedFiles = 5;

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

    private static void RotateIfNeeded()
    {
        try
        {
            var info = new FileInfo(CurrentLogFilePath);
            if (!info.Exists || info.Length < MaxLogBytes)
            {
                return;
            }
            string dir = Path.GetDirectoryName(CurrentLogFilePath)!;
            string rotated = Path.Combine(dir,
                $"app_{DateTime.Now:yyyyMMdd_HHmmss}.log");
            File.Move(CurrentLogFilePath, rotated, overwrite: true);

            foreach (var old in Directory.GetFiles(dir, "app_*.log")
                         .OrderByDescending(f => f))
            {
                if (Path.GetFileName(old) == Path.GetFileName(rotated))
                {
                    continue;
                }
                var files = Directory.GetFiles(dir, "app_*.log")
                    .OrderByDescending(f => f).ToList();
                for (int i = MaxRotatedFiles; i < files.Count; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                }
                break;
            }
        }
        catch
        {
            // rotation is best-effort
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
                RotateIfNeeded();
                File.AppendAllText(CurrentLogFilePath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
