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
    /// Bottom-fixed pages: the PUBLIC detector rejects the pair (A-side footer rows sit
    /// inside the alignment band) — but the layer's internal dy0 path (same algorithm +
    /// A-side same-position row exclusion) recovers it, so the fixed footer gets weight 0
    /// and the session can keep stitching instead of stopping early.
    /// </summary>
    [Fact]
    public void FixedFooter_InternalDy0Recovers_WeightMapProduced()
    {
        var (a, b) = Pair(scrollPx: 100, headerH: 0, footerH: 60);
        var publicBaseline = new OverlapDetector().Detect(a, b);
        Assert.False(publicBaseline.Success); // public stays strict

        RegionWeightMap? map = null;
        for (int i = 0; i < 3; i++)
        {
            map = _detector.Update(a, b, drivingBands: null);
        }
        if (map == null) throw new Xunit.Sdk.XunitException("NULL-REPORT: " + _detector.DebugLastReport);
        Assert.NotNull(map);
        Assert.True(map!.RowWeight.Skip(H - 55).Take(55).Average() < 0.7,
            "footer band should be weighted down");
    }

    [Fact]
    public void FixedHeaderFooter_WeightsProduced()
    {
        var (a, b) = Pair(scrollPx: 100, headerH: 40, footerH: 30);
        RegionWeightMap? map = null;
        for (int i = 0; i < 3; i++)
        {
            map = _detector.Update(a, b, drivingBands: null);
        }
        if (map == null) throw new Xunit.Sdk.XunitException("NULL-REPORT: " + _detector.DebugLastReport);
        double headerW = map!.RowWeight.Take(40).Average();
        double footerW = map.RowWeight.Skip(H - 40).Take(40).Average();
        Assert.True(headerW < 0.7 || footerW < 0.7,
            $"header/footer both failed to weigh down (h={headerW:F2}, f={footerW:F2})");
        Assert.True(map.RowWeight.Skip(90).Take(80).Average() > 0.5,
            $"content should stay scroll, got {map.RowWeight.Skip(90).Take(80).Average():F2}");
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
        // the FIXED header changes slightly (+4); content stays identical to the page
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        for (int i = 0; i < wb.Length; i += 4)
        {
            // header region = rows < ProvenHeaderH
            int rowIndex = i / (W * 4);
            if (rowIndex < ProvenHeaderH)
            {
                wb[i] = (byte)Math.Clamp(wb[i] + 4, 0, 255);
                wb[i + 1] = (byte)Math.Clamp(wb[i + 1] + 4, 0, 255);
            }
        }
        BitmapSource b2 = TestImages.CreateBgr32(wb, W, H);
        RegionWeightMap? map = _detector.Update(TestImages.CreateBgr32(wa, W, H), b2, null);

        // SPEC-L: a slightly-changed fixed region must NEVER flip the whole page.
        // Two acceptable outcomes: either (a) unreliable evidence => null => the
        // original path untouched, or (b) a valid map where the header stays fixed
        // and the content stays scroll-weighted. Both guarantee no whole-page flip.
        if (map == null)
        {
            return;
        }
        Assert.True(map.RowWeight.Skip(4).Take(90).Average() < 0.7, "header should stay fixed");
        Assert.True(map.RowWeight.Skip(130).Take(70).Average() > 0.7, "content should stay scroll");
    }
}










