using System.IO;
using System.Windows.Media.Imaging;

namespace ScrollCapture.Tests;

/// <summary>
/// Finds vertically duplicated bands inside a stitched longshot by comparing per-row
/// luminance profiles at various vertical shifts (profile is cheap; flagged runs are
/// reported with their offset and row span).
/// </summary>
public class DiagnosticsTests
{
    [Fact]
    public void ScanNewestLongshotForDuplicates()
    {
        if (!DiagnosticGate.Enabled) return; // diagnostic-only
        string pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ScrollCapture");
        var file = Directory.GetFiles(pictures, "longshot_*.png")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .First();
        var image = new BitmapImage(new Uri(file.FullName));
        image.Freeze();
        int w = image.PixelWidth, h = image.PixelHeight;
        var buf = new byte[w * h * 4];
        image.CopyPixels(buf, w * 4, 0);

        const int colStep = 4;
        var profile = new float[h];
        for (int y = 0; y < h; y++)
        {
            long sum = 0;
            int n = 0;
            int row = y * w * 4;
            for (int x = 0; x < w; x += colStep)
            {
                int i = row + x * 4;
                sum += buf[i] + buf[i + 1] + buf[i + 2];
                n++;
            }
            profile[y] = sum / (float)(n * 3);
        }

        var reports = new List<(int Shift, int Run, int Start)>();
        for (int d = 100; d < Math.Min(h, 2500); d += 1)
        {
            int run = 0, bestRun = 0, bestStart = -1;
            int limit = h - d;
            for (int y = 0; y < limit; y++)
            {
                if (Math.Abs(profile[y] - profile[y + d]) <= 3f)
                {
                    run++;
                    if (run > bestRun) { bestRun = run; bestStart = y - run + 1; }
                }
                else
                {
                    run = 0;
                }
            }
            if (bestRun >= 30)
            {
                reports.Add((d, bestRun, bestStart));
            }
        }

        string result = $"file: {file.Name}  {w}x{h}\n" +
            (reports.Count == 0
                ? "NO horizontal-band duplicates above threshold"
                : string.Join("\n", reports.OrderByDescending(r => r.Run).Take(12)
                    .Select(r => $"shift={r.Shift}px  run={r.Run}px  y={r.Start}..{r.Start + r.Run}")));
        throw new Xunit.Sdk.XunitException(result);
    }
}




