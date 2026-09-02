using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Scrolling;
using ScrollCapture.Stitching;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class ColumnMotionTests
{
    private const int W = 256;
    private const int H = 340;

    private static byte[] MakeBgr32()
    {
        var buf = new byte[W * H * 4];
        for (int i = 3; i < buf.Length; i += 4)
        {
            buf[i] = 255;
        }
        return buf;
    }

    private static void SideOverlay(byte[] buf)
    {
        // bands 0..4 (x < 160): high-contrast FIXED UI — identical across frames
        // bands 5..6 (x 160..224): plain DARK empty background (constant, no content)
        for (int y = 0; y < H; y++)
        {
            int row = y * W * 4;
            for (int x = 0; x < 160; x++)
            {
                byte v = (byte)((x * 3 + y * 5 + 60) & 0xff);
                int idx = row + x * 4;
                buf[idx] = v;
                buf[idx + 1] = v;
                buf[idx + 2] = v;
            }
        }
    }

    /// <summary>Frame A: sidebar + texture starting at content row 0.</summary>
    private static byte[] FrameA(int scrollPx)
    {
        byte[] buf = MakeBgr32();
        SideOverlay(buf);
        Texture(buf, scrollPx);
        return buf;
    }

    private static void Texture(byte[] buf, int yOffset)
    {
        // band 7 (x 224..255): pseudo-random per content row (unique rows, no periodicity)
        for (int y = 0; y < H; y++)
        {
            int row = y * W * 4;
            int contentRow = y + yOffset;
            for (int x = 224; x < W; x++)
            {
                int h = unchecked(contentRow * 7919 + 13);
                byte v = (byte)((h ^ (h >> 8) ^ (h >> 16)) & 0xff);
                int idx = row + x * 4;
                buf[idx] = v;
                buf[idx + 1] = v;
                buf[idx + 2] = v;
            }
        }
    }

    [Fact]
    public void Classify_MixedLayoutMarksOnlyMovingBands()
    {
        byte[] a = FrameA(0);
        byte[] b = FrameA(30);
        bool[] mask = ColumnMotion.ClassifyDrivingBands(a, b, W, H);
        Assert.All(Enumerable.Range(0, 7), i => Assert.False(mask[i]));
        Assert.True(mask[7]);
    }

    [Fact]
    public void DrivenMotion_SeesBandMovement()
    {
        byte[] a = FrameA(0);
        byte[] b = FrameA(30);
        bool[] mask = ColumnMotion.ClassifyDrivingBands(a, b, W, H);
        double driven = ColumnMotion.ComputeDrivenMotionFraction(a, b, W, H, mask);
        Assert.True(driven > 0.04, $"driven motion too low: {driven:F3}");
    }

    [Fact]
    public void Stitcher_WithMask_TreatsMixedAsScrolling_NotStatic()
    {
        BitmapSource a = TestImages.CreateBgr32(FrameA(0), W, H);
        BitmapSource b = TestImages.CreateBgr32(FrameA(30), W, H);
        bool[] mask = ColumnMotion.ClassifyDrivingBands(a, b);

        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(a);
        stitcher.Add(b, priorScrollDeltaPx: 30, drivingBandMask: mask);

        Assert.False(stitcher.Steps[^1].Skipped, "driven content is moving — must NOT be skipped");
        Assert.False(stitcher.Steps[^1].UsedFallback, string.Join(";", stitcher.Warnings));

        // blanking OFF (user A/B): fixed regions are kept untouched below the first frame
        stitcher.SetBlankMask(ColumnMotion.EmptyMask);

        BitmapSource? image = stitcher.Finish();
        Assert.NotNull(image);
        Assert.Equal(H + 30, image!.PixelHeight);

        byte[] outBytes = FrameSimilarity.ToBgr32Buffer(image);
        int stride = W * 4;
        // sidebar content remains present below the first frame (no blanking in this mode)
        Assert.True(outBytes[(H + 5) * stride + 40 * 4] != 255 || !IncrementalStitcher.BlankPostProcessEnabled);
        // scrolling band retains actual pixels below the first frame
        var probeRow = image.PixelHeight - 8;
        int tx = 240;
        byte px = outBytes[probeRow * stride + tx * 4 + 2];
        Assert.True(px != 255 || outBytes[probeRow * stride + tx * 4] != 255 || outBytes[probeRow * stride + tx * 4 + 1] != 255,
            "scroll band should not be blanked");
    }

    [Fact]
    public async Task Session_ProbeReportsZeroButContentMoves_Continues()
    {
        var queue = new Queue<BitmapSource>();
        for (int i = 0; i < 5; i++)
        {
            queue.Enqueue(TestImages.CreateBgr32(FrameA(i * 30), W, H));
        }
        var session = new LongCaptureSession(
            new Int32Rect(0, 0, W, H),
            options: new ScrollOptions { DelayPerScrollMs = 0 },
            maxFrames: 5,
            framesDirectory: System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sc_{Guid.NewGuid():N}"),
            capture: _ => queue.Dequeue(),
            scrollOnce: () => { },
            probeGetter: () => new OffsetSnapshot(0, 10000, 900, null, UiaPercent: 0, UiaViewSize: 5, ClientHeightPx: H));

        SessionResult result = await session.RunAsync();

        // probes pretend "zero movement", but content keeps scrolling ->
        // probe stops must be ignored; session runs to the frame limit.
        Assert.Equal(SessionStopReason.LimitReached, result.Reason);
        Assert.Equal(5, result.FrameCount);
    }
}
