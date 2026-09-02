using System.Diagnostics;
using System.IO;
using System.IO.Compression;

namespace ScrollCapture.Utils;

/// <summary>
/// One-click diagnostic bundle: version + settings + recent logs + latest session frames.
/// Output: ScrollCapture_diag_yyyyMMdd_HHmmss.zip inside the save directory.
/// </summary>
public static class DiagnosticsExporter
{
    public static string? Export(string targetDirectory)
    {
        try
        {
            Directory.CreateDirectory(targetDirectory);
            string path = Path.Combine(targetDirectory, $"ScrollCapture_diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

            // version + settings
            string version = FileVersionInfo.GetVersionInfo(Environment.ProcessPath ?? "0.0").ProductVersion
                             ?? ApplicationShortVersion;
            AddEntry(zip, "version.txt", "ScrollCapture " + version + Environment.NewLine);
            string settingsPath = Settings.SettingsService.SettingsFilePath;
            if (File.Exists(settingsPath))
            {
                zip.CreateEntryFromFile(settingsPath, "settings.json");
            }

            // logs (main + rotated, newest first, cap total)
            string logDir = Path.GetDirectoryName(Logger.CurrentLogFilePath)!;
            if (Directory.Exists(logDir))
            {
                int count = 0;
                foreach (string log in Directory.GetFiles(logDir, "*.log")
                             .OrderByDescending(f => f)
                             .Take(6))
                {
                    if (count++ >= 3)
                    {
                        break;
                    }
                    var info = new FileInfo(log);
                    if (info.Length > 3L * 1024 * 1024)
                    {
                        zip.CreateEntryFromFile(log, Path.GetFileName(log));
                    }
                    else
                    {
                        zip.CreateEntryFromFile(log, Path.GetFileName(log));
                    }
                }
            }

            // latest session frames (cap 30 files / ~20MB)
            string tempRoot = TempSessionCleaner.TempRoot;
            if (Directory.Exists(tempRoot))
            {
                var latest = Directory.GetDirectories(tempRoot)
                    .Select(d => new DirectoryInfo(d))
                    .OrderByDescending(d => d.LastWriteTime)
                    .FirstOrDefault();
                if (latest != null)
                {
                    int files = 0;
                    foreach (string frame in Directory.GetFiles(latest.FullName, "*.png")
                                 .OrderBy(f => f).Reverse().Take(30))
                    {
                        var fi = new FileInfo(frame);
                        if (fi.Length > 8L * 1024 * 1024)
                        {
                            continue;
                        }
                        zip.CreateEntryFromFile(frame, $"session/{Path.GetFileName(frame)}");
                        files++;
                        if (files >= 30)
                        {
                            break;
                        }
                    }
                }
            }

            return path;
        }
        catch (Exception ex)
        {
            Logger.Error("Diagnostic export failed", ex);
            return null;
        }
    }

    private const string ApplicationShortVersion = "0.1.0";

    private static void AddEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }
}
