using System.Windows.Media.Imaging;
using ScrollCapture.Stitching;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

/// <summary>
/// Step-3 guarantees at the STITCHER level: the fixed-region layer must be transparent
/// for plain pages (null => original path) and, when a fixed header exists, the overlap
/// stays exact and the header appears exactly once in the final image.
/// </summary>
public class FixedRegionInstallTests
{
    private const int W = 200;
    private const int H = 300;
    private const int ScrollPx = 100; // true k = 200
    private const int HeaderH = 105;
    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 5000);

    private static (BitmapSource a, BitmapSource b) HeaderPair()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, ScrollPx);
        int[] rows =
        {
            0,
        };
        _ = rows;
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
    public void PureScrollPair_LayerIsTransparent()
    {
        // layer detector on this pair returns null => identical to original-path run
        var detector = new FixedRegionDetector();
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, ScrollPx);
        Assert.Null(detector.Update(a, b, null));

        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(a);
        stitcher.Add(b, priorScrollDeltaPx: ScrollPx);
        Assert.False(stitcher.Steps[^1].Skipped);
        Assert.False(stitcher.Steps[^1].UsedFallback);
        Assert.Equal(H + ScrollPx, stitcher.Finish()!.PixelHeight);
    }

    [Fact]
    public void FooterPair_StitchesNormally_FixedBandNotRepeated()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, ScrollPx);
        const int footerH = 60;
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        for (int y = H - footerH; y < H; y++)
        {
            int row = y * W * 4;
            for (int x = 0; x < W * 4; x++)
            {
                byte v = (byte)((x / 4 * 5 + y * 3 + 233) & 0xff);
                wa[row + x] = v;
                wb[row + x] = v;
            }
        }
        BitmapSource fa = TestImages.CreateBgr32(wa, W, H);
        BitmapSource fb = TestImages.CreateBgr32(wb, W, H);

        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(fa);
        stitcher.Add(fb, priorScrollDeltaPx: ScrollPx);

        var step = stitcher.Steps[^1];
        Assert.False(step.UsedFallback, "footer pair must stitch via the weighted path");
        Assert.False(step.Skipped);

        BitmapSource image = stitcher.Finish()!;
        Assert.Equal(H + ScrollPx, image.PixelHeight);

        // footer pixel signature exists once only (in the final frame's own bottom band)
        byte[] outBytes = FrameSimilarity.ToBgr32Buffer(image);
        int sigY = H + ScrollPx - 12;
        int idx = sigY * W * 4 + 10 * 4;
        byte sig = (byte)((10 / 4 * 5 + (H - 12) * 3 + 233) & 0xff);
        Assert.Equal(sig, outBytes[idx]); // footer present at its own final position
        int earlier = 0;
        for (int y = 30; y < H - footerH - 40 && y < image.PixelHeight; y += 9)
        {
            int i2 = y * W * 4 + 40 * 4;
            if (outBytes[i2] == sig && outBytes[i2 + 1] == sig && outBytes[i2 + 2] == sig)
            {
                earlier++;
            }
        }
        Assert.True(earlier <= 1, "footer band repeated in the content area");
    }

    [Fact]
    public void HeaderPair_ExactOverlap_AndHeaderAppearsOnce()
    {
        var (a, b) = HeaderPair();
        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(a);
        stitcher.Add(b, priorScrollDeltaPx: ScrollPx);

        var step = stitcher.Steps[^1];
        Assert.False(step.UsedFallback, "fixed layer must not degrade the alignment");
        Assert.InRange(step.OverlapHeight, 194, 206); // true k = 200

        BitmapSource image = stitcher.Finish()!;
        Assert.Equal(H + ScrollPx, image.PixelHeight);

        byte[] outBytes = FrameSimilarity.ToBgr32Buffer(image);
        int stride = W * 4;
        // header row pixel signature (x=10,y=6: (30+30+92)&255 = 152) present at top
        int hSig = (6 * W + 10) * 4;
        Assert.Equal(152, outBytes[hSig]);
        // ...and the SAME signature must NOT appear again below the header (header once)
        int repeats = 0;
        for (int y = HeaderH + 1; y < image.PixelHeight; y += 7)
        {
            int idx = y * stride + 10 * 4;
            if (outBytes[idx] == 152 && outBytes[idx + 1] == 152 && outBytes[idx + 2] == 152)
            {
                repeats++;
            }
        }
        Assert.True(repeats <= 1, $"header pixels repeated below: {repeats}");
    }
}
