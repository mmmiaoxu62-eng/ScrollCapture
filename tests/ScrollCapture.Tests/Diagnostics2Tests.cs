using System.IO;
using System.Windows.Media.Imaging;
using ScrollCapture.Stitching;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class Diagnostics2Tests
{
    [Fact]
    public void ComputeSeamsForLatestSession()
    {
        if (!DiagnosticGate.Enabled) return; // diagnostic-only
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollCapture", "temp");
        var dir = Directory.GetDirectories(root).Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTime).First();
        var files = Directory.GetFiles(dir.FullName, "frame_*.png").OrderBy(f => f).ToList();
        var frames = files.Select(f => { var b = new BitmapImage(new Uri(f)); b.Freeze(); return (BitmapSource)b; }).ToList();
        if (frames.Count < 2) throw new Xunit.Sdk.XunitException("fewer than 2 frames");

        var detector = new OverlapDetector();
        var report = new System.Text.StringBuilder();
        report.AppendLine($"session={dir.Name}  frames={frames.Count}  size={frames[0].PixelWidth}x{frames[0].PixelHeight}");
        int canvasY = 0;
        var seams = new List<(int CanvasY, int Overlap)>();
        var prev = frames[0];
        seams.Add((0, 0));
        for (int i = 1; i < frames.Count; i++)
        {
            var r = detector.Detect(prev, frames[i]);
            int delta = r.Success ? frames[i].PixelHeight - r.OverlapHeight : -1;
            report.AppendLine($"frame{i}: overlap={r.OverlapHeight} conf={r.Confidence:F2} delta={delta} note={r.Note}");
            if (r.Success) canvasY += delta;
            seams.Add((canvasY, r.Success ? r.OverlapHeight : -1));
            prev = frames[i];
        }
        report.AppendLine("canvasY -> seam:" + string.Join(", ", seams.Select(s => $"{s.CanvasY}(ov={s.Overlap})")));
        throw new Xunit.Sdk.XunitException(report.ToString());
    }
}




