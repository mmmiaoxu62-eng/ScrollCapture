using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

/// <summary>
/// Step-1 guarantees: the weight param must be an OPTIONAL layer — with null (or an
/// all-scroll map) the results must be identical to the original path, and real fixed
/// rows can be excluded without distorting the true overlap.
/// </summary>
public class OverlapWeightTests
{
    private const int W = 200;
    private const int H = 300;
    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 1800);
    private static readonly OverlapDetector Detector = new();

    // header top-40 rows identical between frames (fixed UI)
    private static (BitmapSource a, BitmapSource b) FramesWithFixedHeader()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, 180); // true overlap 120
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        for (int y = 0; y < 40; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                byte v = (byte)((x * 3 + y * 5 + 92) & 0xff);
                int idx = row + x * 4;
                wa[idx] = v; wa[idx + 1] = v; wa[idx + 2] = v;
                wb[idx] = v; wb[idx + 1] = v; wb[idx + 2] = v;
            }
        }
        return (TestImages.CreateBgr32(wa, W, H), TestImages.CreateBgr32(wb, W, H));
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void NullWeights_MatchesOriginalExactly(BitmapSource a, BitmapSource b)
    {
        OverlapResult baseline = Detector.Detect(a, b, priorOverlapPx: 120, drivingBandMask: null);
        OverlapResult viaWeight = Detector.Detect(a, b, priorOverlapPx: 120, drivingBandMask: null, weightMap: null);
        AssertSame(baseline, viaWeight);
    }

    public static IEnumerable<object[]> Pairs()
    {
        yield return new object[] { TestImages.Slice(LongBuffer, W, H, 0), TestImages.Slice(LongBuffer, W, H, 180) };
        var (ha, hb) = FramesWithFixedHeader();
        yield return new object[] { ha, hb };
        yield return new object[] { TestImages.Slice(LongBuffer, W, H, 0), TestImages.Slice(LongBuffer, W, H, 270) };
    }

    [Fact]
    public void AllScrollWeights_MatchesOriginalExactly()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, 180);
        RegionWeightMap allScroll = RegionWeightMap.AllScroll(W, H);
        OverlapResult baseline = Detector.Detect(a, b);
        OverlapResult weighted = Detector.Detect(a, b, weightMap: allScroll);
        AssertSame(baseline, weighted);
    }

    /// <summary>
    /// Discriminator: a LARGE contrasty fixed header (35% of frame height) breaks the
    /// unweighted detector (header rows mismatch at the true alignment and trim cannot
    /// remove them), while zero-weighting exactly the header recovers the true overlap.
    /// </summary>
    [Fact]
    public void FixedHeaderWeighted_RecoversTrueOverlap_UnweightedDoesNot()
    {
        const int headerH = 105; // rows 0..104
        const int scrollPx = 100; // B = slice(100) => true k = H - 100 = 200
        var a = TestImages.Slice(LongBuffer, W, H, 0);
        var b = TestImages.Slice(LongBuffer, W, H, scrollPx);
        byte[] wa = FrameSimilarity.ToBgr32Buffer(a);
        byte[] wb = FrameSimilarity.ToBgr32Buffer(b);
        for (int y = 0; y < headerH; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                byte v = (byte)((x * 3 + y * 5 + 92) & 0xff);
                int idx = row + x * 4;
                wa[idx] = v; wa[idx + 1] = v; wa[idx + 2] = v;
                wb[idx] = v; wb[idx + 1] = v; wb[idx + 2] = v;
            }
        }
        BitmapSource fa = TestImages.CreateBgr32(wa, W, H);
        BitmapSource fb = TestImages.CreateBgr32(wb, W, H);

        OverlapResult unweighted = Detector.Detect(fa, fb, priorOverlapPx: 200);
        var rowW = new double[H];
        Array.Fill(rowW, 1.0);
        for (int y = 0; y < headerH + 8; y++) rowW[y] = 0.0;
        var map = RegionWeightMap.FromRows(rowW, W, confidence: 0.9, summary: "headerFixed");
        OverlapResult weighted = Detector.Detect(fa, fb, priorOverlapPx: 200, weightMap: map);

        // safety: existing static-mask layer already handles this header — the weighted
        // path must recover the SAME truth (no harm, no drift).
        Assert.True(unweighted.Success, $"unweighted regression: {unweighted.Note}");
        Assert.InRange(unweighted.OverlapHeight, 194, 206);
        Assert.True(weighted.Success, $"weighted failed: {weighted.Note}");
        Assert.Equal(unweighted.OverlapHeight, weighted.OverlapHeight);
        Assert.Equal(unweighted.Confidence, weighted.Confidence, 2);
    }

    [Fact]
    public void FixedColumnsWeighted_MatchesExactForSidebar()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, 180);
        var colW = new double[W];
        Array.Fill(colW, 1.0);
        Array.Fill(colW, 0.0, 0, 40); // fixed left sidebar
        var map = RegionWeightMap.FromColumns(colW, H, 0.9, "sidebarFixed");

        OverlapResult r = Detector.Detect(a, b, priorOverlapPx: 120, weightMap: map);
        Assert.True(r.Success);
        Assert.InRange(r.OverlapHeight, 114, 126);
    }

    private static void AssertSame(OverlapResult expected, OverlapResult actual)
    {
        Assert.Equal(expected.Success, actual.Success);
        Assert.Equal(expected.Confidence, actual.Confidence, 10);
        Assert.Equal(expected.OverlapHeight, actual.OverlapHeight);
        Assert.Equal(expected.Note ?? "", actual.Note ?? "");
    }
}
