using System.IO;

namespace ScrollCapture.Utils;

/// <summary>
/// Keeps the temp frame folders from piling up (they were previously never
/// cleaned: hundreds of MB of PNGs + disk scans by Defender).
/// </summary>
public static class TempSessionCleaner
{
    public static string TempRoot => Path.Combine(AppPaths.DataDir, "temp");

    public static void CleanupKeepLatest(int keep = 1)
    {
        try
        {
            if (!Directory.Exists(TempRoot))
            {
                return;
            }
            var dirs = Directory.GetDirectories(TempRoot)
                .Select(d => new DirectoryInfo(d))
                .OrderByDescending(d => d.LastWriteTime)
                .ToList();
            for (int i = keep; i < dirs.Count; i++)
            {
                TryDelete(dirs[i].FullName);
            }
        }
        catch
        {
            // best effort
        }
    }

    public static void PruneOlderThan(int days = 14)
    {
        try
        {
            if (!Directory.Exists(TempRoot))
            {
                return;
            }
            foreach (string dir in Directory.GetDirectories(TempRoot))
            {
                if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-days))
                {
                    TryDelete(dir);
                }
            }
        }
        catch
        {
            // best effort
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
