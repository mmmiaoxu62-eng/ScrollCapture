using System.IO;
using System.Windows.Media.Imaging;
using ScrollCapture.Stitching;

namespace ScrollCapture.Tests;

public class Diagnostics4Tests
{
    [Fact]
    public void ReplayLatestSessionThroughGatedStitcher()
    {
        if (!DiagnosticGate.Enabled) return;

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollCapture", "temp");
        var dir = Directory.GetDirectories(root).Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTime).First();
        var files = Directory.GetFiles(dir.FullName, "frame_*.png").OrderBy(f => f).ToList();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"session={dir.Name} frames={files.Count}");

        var stitcher = new IncrementalStitcher(30000);
        for (int i = 0; i < files.Count; i++)
        {
            var bmp = new BitmapImage(new Uri(files[i]));
            bmp.Freeze();
            if (i == 0)
            {
                stitcher.Start(bmp);
                sb.AppendLine("f0 START");
                continue;
            }
            stitcher.Add(bmp, null); // self-prior only — mirrors session without probe
            var step = stitcher.Steps[^1];
            sb.AppendLine($"f{i}: ov={step.OverlapHeight} conf={step.Confidence:F2} failed={step.UsedFallback} skipped={step.Skipped}");
        }
        sb.AppendLine($"final height={(stitcher.Finish()?.PixelHeight ?? 0)} warnings={stitcher.Warnings.Count}");
        foreach (var w in stitcher.Warnings)
        {
            sb.AppendLine("WARN: " + w);
        }
        throw new Xunit.Sdk.XunitException(sb.ToString());
    }
}
