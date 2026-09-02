using System.IO;
using System.Windows.Media.Imaging;
using ScrollCapture.Stitching;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class FixedRegionDebugTests
{
    private const int W = 200;
    private const int H = 300;
    private const int ScrollPx = 100;
    private const int HeaderH = 105;
    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 5000);

    private static (BitmapSource a, BitmapSource b) HeaderPair()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, ScrollPx);
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        for (int y = 0; y < HeaderH; y++)
        {
            int row = y * W * 4;
            for (int x = 0; x < W * 4; x++)
            {
                byte v = (byte)((x / 4 * 3 + y * 5 + 92) & 0xff);
                wa[row + x] = v;
                wb[row + x] = v;
            }
        }
        return (TestImages.CreateBgr32(wa, W, H), TestImages.CreateBgr32(wb, W, H));
    }

    [Fact]
    public void DebugWrites_TxtAndOverlayPerPair()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sc_fixdebug_{Guid.NewGuid():N}");
        var (a, b) = HeaderPair();
        var stitcher = new IncrementalStitcher(5000, debugDir: dir);
        stitcher.Start(a);
        stitcher.Add(b, priorScrollDeltaPx: ScrollPx);

        string txt = Path.Combine(dir, "pair_0000.txt");
        string png = Path.Combine(dir, "pair_0000.png");
        Assert.True(File.Exists(txt), "debug txt missing");
        Assert.True(File.Exists(png), "debug overlay png missing");
        string content = File.ReadAllText(txt);
        Assert.Contains("dy0=", content);
        Assert.Contains("fixedSim=", content);
        Assert.Contains("overlapConfidence=", content);
    }
}
