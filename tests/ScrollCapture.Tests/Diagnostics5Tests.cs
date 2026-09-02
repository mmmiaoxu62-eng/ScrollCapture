using System.IO;
using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class Diagnostics5Tests
{
    [Fact]
    public void DumpMixedMaskPerPair()
    {
        if (!DiagnosticGate.Enabled) return;

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScrollCapture", "temp");
        var dir = Directory.GetDirectories(root).Select(d => new DirectoryInfo(d))
            .OrderByDescending(d => d.LastWriteTime).First();
        var files = Directory.GetFiles(dir.FullName, "frame_*.png").OrderBy(f => f).Take(6).ToList();
        var frames = files.Select(f => { var b = new BitmapImage(new Uri(f)); b.Freeze(); return b; }).ToList();

        var sb = new System.Text.StringBuilder();
        for (int i = 1; i < frames.Count; i++)
        {
            double[] motion = ColumnMotion.ComputeBandMotion(frames[i - 1], frames[i]);
            bool[] mask = ColumnMotion.ClassifyDrivingBands(frames[i - 1], frames[i]);
            sb.AppendLine($"pair{i}: mask={string.Concat(mask.Select(m => m ? '1' : '0'))} motion={string.Join(",", motion.Select(m => m.ToString("F2")))}");
        }
        throw new Xunit.Sdk.XunitException(sb.ToString());
    }
}
