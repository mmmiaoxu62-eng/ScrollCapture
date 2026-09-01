using System.Windows;
using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class StaticEdgeAndPriorTests
{
    private const int W = 240;
    private const int H = 340;

    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 2400);
    private static readonly OverlapDetector Detector = new();

    private static BitmapSource FrameAt(int yStart) => TestImages.Slice(LongBuffer, W, H, yStart);

    /// <summary>
    /// Overwrites the "chrome" band (row range of the frame) with a fixed deterministic
    /// pattern that is identical across frames (browser toolbar-like).
    /// </summary>
    private static BitmapSource WithChrome(BitmapSource frame, int topChrome, int bottomChrome, int seed)
    {
        byte[] buf = FrameSimilarity.ToBgr32Buffer(frame);
        var rnd = new Random(seed);
        for (int y = 0; y < topChrome || y >= H - bottomChrome; y++)
        {
            if (y >= H) break;
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                int i = row + x * 4;
                buf[i] = (byte)(x * 3 + y * 2 + 40);
                buf[i + 1] = (byte)(x + y + 60);
                buf[i + 2] = 128;
            }
        }
        return TestImages.CreateBgr32(buf, W, H);
    }

    [Fact]
    public void Detect_WithStaticTopAndBottomChrome_StillExact()
    {
        // true overlap 160px; chrome occupies top 40 and bottom 30 rows of BOTH frames
        BitmapSource a = WithChrome(FrameAt(0), 40, 30, seed: 1);
        BitmapSource b = WithChrome(FrameAt(180), 40, 30, seed: 1); // scroll 180 => overlap 160

        OverlapResult r = Detector.Detect(a, b);
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 152, 168);
    }

    [Fact]
    public void Detect_ChromeOnlyDifference_WithoutMask_WouldBiasButMaskKeepsTrue()
    {
        // chrome identical between frames at BOTH top and bottom margin (worst case bait).
        // content shift: scroll 120, overlap 220.
        BitmapSource a = WithChrome(FrameAt(0), 50, 50, seed: 77);
        BitmapSource b = WithChrome(FrameAt(120), 50, 50, seed: 77);

        OverlapResult r = Detector.Detect(a, b);
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 212, 228);
        Assert.True(r.Confidence > 0.7);
    }

    [Fact]
    public void Detect_AllStaticContent_Fails()
    {
        // nothing moves at all between frames except trivial noise: masking should
        // shrink the matchable set to nothing (returns failure, not a random peak).
        BitmapSource a = TestImages.CreateSolid(W, H, 120);
        BitmapSource b = TestImages.CreateSolid(W, H, 120);
        OverlapResult r = Detector.Detect(a, b);
        Assert.False(r.Success);
    }

    /// <summary>
    /// Floating widgets (WeChat "jump to latest", hover toolbars) sit at a FIXED absolute
    /// row range across frames, mid-frame — they offset-match at any candidate k. The
    /// whole-frame static-row mask must strip them so the true overlap stays sharp.
    /// </summary>
    [Fact]
    public void Detect_MidFrameConstantWidget_StillExact()
    {
        // durable pattern for the widget (contrasty so it is not mistaken for blank)
        BitmapSource a = WithChrome(FrameAt(0), 0, 0, seed: 5);
        BitmapSource b = WithChrome(FrameAt(180), 0, 0, seed: 5);
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        int widgetY = H / 2;
        int widgetH = 40;
        var rnd = new Random(91);
        for (int y = widgetY; y < widgetY + widgetH; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x += 2)
            {
                byte v = (byte)rnd.Next(40, 220);
                // identical widget pixels at the SAME absolute y in both frames
                for (int c = 0; c < 3; c++)
                {
                    wa[row + x * 4 + c] = v;
                    wb[row + x * 4 + c] = v;
                }
            }
        }
        BitmapSource af = TestImages.CreateBgr32(wa, W, H);
        BitmapSource bf = TestImages.CreateBgr32(wb, W, H);

        OverlapResult r = Detector.Detect(af, bf);

        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 152, 168);
        Assert.True(r.Confidence > 0.6, $"confidence degraded: {r.Confidence:F2}");
    }

    [Fact]
    public void Prior_ExactPrior_DetectsQuicklyAndExactly()
    {
        OverlapResult r = Detector.Detect(FrameAt(0), FrameAt(200), priorOverlapPx: 140);
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 134, 146);
    }

    [Fact]
    public void Prior_WrongPrior_FallsBackToGlobal()
    {
        // prior in range (220) but wrong (true 140): narrow search must miss, global must hit
        OverlapResult r = Detector.Detect(FrameAt(0), FrameAt(200), priorOverlapPx: 220);
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 134, 146);
        Assert.NotNull(r.Note);
        Assert.Contains("prior mismatch", r.Note!);
    }

    [Fact]
    public void Prior_CombinedWithChrome_StillExact()
    {
        BitmapSource a = WithChrome(FrameAt(0), 40, 30, seed: 3);
        BitmapSource b = WithChrome(FrameAt(180), 40, 30, seed: 3);
        OverlapResult r = Detector.Detect(a, b, priorOverlapPx: 160);
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 152, 168);
    }
}
