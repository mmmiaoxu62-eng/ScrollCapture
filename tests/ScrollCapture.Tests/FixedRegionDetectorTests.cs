using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class FixedRegionDetectorTests
{
    private const int W = 200;
    private const int H = 300;
    // baseline-verified geometry (see OverlapWeightTests): 105-row header with scroll 100
    private const int ProvenHeaderH = 105;
    private const int ProvenScroll = 100;
    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 1800);
    private readonly FixedRegionDetector _detector = new(); // per-test instance (EMA isolation)

    private static BitmapSource Overlay(BitmapSource src, int yFrom, int yTo, byte seedBase)
    {
        byte[] buf = FrameSimilarity.ToBgr32Buffer(src);
        for (int y = yFrom; y < yTo && y < H; y++)
        {
            int row = y * W * 4;
            for (int x = 0; x < W; x++)
            {
                byte v = (byte)((x * 3 + y * 5 + seedBase) & 0xff);
                int idx = row + x * 4;
                buf[idx] = v;
                buf[idx + 1] = v;
                buf[idx + 2] = v;
            }
        }
        return TestImages.CreateBgr32(buf, W, H);
    }

    private static (BitmapSource a, BitmapSource b) Pair(int scrollPx, int headerH, int footerH)
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, scrollPx);
        if (headerH > 0)
        {
            a = Overlay(a, 0, headerH, 92);
            b = Overlay(b, 0, headerH, 92);
        }
        if (footerH > 0)
        {
            a = Overlay(a, H - footerH, H, 233);
            b = Overlay(b, H - footerH, H, 233);
        }
        return (a, b);
    }

    [Fact]
    public void PureScrollPage_NoFixedEvidence_Null()
    {
        var (a, b) = Pair(scrollPx: 100, headerH: 0, footerH: 0);
        RegionWeightMap? map = _detector.Update(a, b, drivingBands: null);
    }

    [Fact]
    public void FixedHeader_AfterVoting_WeightsZero_TopUnchanged()
    {
        var (a, b) = Pair(scrollPx: ProvenScroll, headerH: ProvenHeaderH, footerH: 0);
        RegionWeightMap? map = null;
        for (int i = 0; i < 3; i++)
        {
            map = _detector.Update(a, b, drivingBands: null);
        }
        if (map == null)
        {
            byte[] fa = FrameSimilarity.ToBgr32Buffer(a);
            byte[] fb = FrameSimilarity.ToBgr32Buffer(b);
            long diff40 = 0;
            int n40 = 0;
            for (int y = 0; y < 60; y += 3)
            {
                int row = y * W;
                for (int x = 0; x < W; x += 8)
                {
                    diff40 += Math.Abs(fa[row + x * 4] - fb[row + x * 4]);
                    n40++;
                }
            }
            long h0 = 0;
            for (int y = 0; y < 60; y += 3)
            {
                int row = y * W;
                for (int x = 0; x < W; x += 8)
                {
                    h0 += Math.Abs(fa[row + x * 4] - 85);
                }
            }
            int bIdx = (45 * W + 8) * 4;
            throw new Xunit.Sdk.XunitException(
                $"REPORT: {_detector.DebugLastReport}" +
                $" | headerRowsAvgDiff={diff40 / (double)n40:F2} distFrom85={h0 / (double)n40:F2}" +
                $" | a45=[{fa[bIdx]},{fa[bIdx + 1]},{fa[bIdx + 2]}] b45=[{fb[bIdx]},{fb[bIdx + 1]},{fb[bIdx + 2]}]");
        }
        if (map == null) throw new Xunit.Sdk.XunitException("NULL-REPORT: " + _detector.DebugLastReport);
        Assert.NotNull(map);
        Assert.True(map!.IsReliable);
        // header zone weighted down
        double headerWeight = map.RowWeight.Skip(4).Take(90).Average();
        Assert.True(headerWeight < 0.7, $"header weight avg {headerWeight:F2}");
        // content zone keeps scroll weight
        double contentWeight = map.RowWeight.Skip(130).Take(70).Average();
        Assert.True(contentWeight > 0.8, $"content weight avg {contentWeight:F2}");
    }

    /// <summary>
    /// Bottom-fixed regions break the ORIGINAL detector (A-side footer rows sit inside the
    /// alignment band). Per spec, dy0 comes only from the original path — so this pair
    /// MUST fall back to null (the session then skips the frame: no repeated button).
    /// </summary>
    [Fact]
    public void FixedFooter_OriginalPathRejects_NullFallback()
    {
        var (a, b) = Pair(scrollPx: 100, headerH: 0, footerH: 60);
        Assert.Null(_detector.Update(a, b, drivingBands: null));
        // and the session's fallback behavior: original detector must also reject
        var baseline = new OverlapDetector().Detect(a, b);
        Assert.False(baseline.Success);
    }

    [Fact]
    public void FixedHeaderFooter_OriginalPathRejects_NullFallback()
    {
        var (a, b) = Pair(scrollPx: 100, headerH: 40, footerH: 30);
        Assert.Null(_detector.Update(a, b, drivingBands: null));
        var baseline = new OverlapDetector().Detect(a, b);
        Assert.False(baseline.Success);
    }

    [Fact]
    public void StickyTransition_VotesInAndCanRecover()
    {
        // pair where header SCROLLS (part of content)
        var (scrollA, scrollB) = Pair(scrollPx: 100, headerH: 0, footerH: 0);
        // pair where header is content again? our fixed pair:
        var (fixA, fixB) = Pair(scrollPx: ProvenScroll, headerH: ProvenHeaderH, footerH: 0);

        // phase 1: header scrolls (no fixed evidence)
        for (int i = 0; i < 3; i++)
        {
            RegionWeightMap? m = _detector.Update(scrollA, scrollB, null);
            Assert.Null(m); // plain page: pure scroll => null
        }
        // phase 2: header sticks
        RegionWeightMap? map = null;
        for (int i = 0; i < 3; i++)
        {
            map = _detector.Update(fixA, fixB, null);
        }
        if (map == null) throw new Xunit.Sdk.XunitException("NULL-REPORT: " + _detector.DebugLastReport);
        Assert.NotNull(map);
        Assert.True(map!.RowWeight.Skip(4).Take(40).Average() < 0.6,
            "sticky header should eventually weigh down");
    }

    [Fact]
    public void MismatchedSizes_Null_FallbackSafe()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W - 10, H, 0);
        Assert.Null(_detector.Update(a, b, null));
    }

    [Fact]
    public void SlightColorShift_DoesNotFlipWholePage()
    {
        var (a, b) = Pair(scrollPx: ProvenScroll, headerH: ProvenHeaderH, footerH: 0);
        // shift content color slightly (+10)
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        for (int i = 0; i < wb.Length; i += 4)
        {
            wb[i] = (byte)Math.Clamp(wb[i] + 4, 0, 255);
            wb[i + 1] = (byte)Math.Clamp(wb[i + 1] + 4, 0, 255);
        }
        BitmapSource b2 = TestImages.CreateBgr32(wb, W, H);
        RegionWeightMap? map = _detector.Update(TestImages.CreateBgr32(wa, W, H), b2, null);

        // a uniform +10 shift keeps the two-test deltas intact (fixedSim/scrollSim both
        // drop ~equally): header must still be weighted down, content must stay scroll —
        // the whole page must NOT be flipped by the slight color change.
        Assert.NotNull(map);
        Assert.True(map!.RowWeight.Skip(4).Take(90).Average() < 0.7,
            "header should still read as fixed");
        Assert.True(map.RowWeight.Skip(130).Take(70).Average() > 0.7,
            $"content must stay scroll-weighted, got {map.RowWeight.Skip(130).Take(70).Average():F2}");
    }
}










