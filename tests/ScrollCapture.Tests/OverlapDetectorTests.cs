using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class OverlapDetectorTests
{
    private const int W = 200;
    private const int H = 300;

    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 1800);
    private static readonly OverlapDetector Detector = new();

    private static BitmapSource FrameAt(int yStart) => TestImages.Slice(LongBuffer, W, H, yStart);

    [Fact]
    public void DetectExactOverlap()
    {
        // frame A rows 0..299, frame B starts at row 180 => overlap = 120
        OverlapResult r = Detector.Detect(FrameAt(0), FrameAt(180));
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 114, 126);
        Assert.True(r.Confidence > 0.7, $"confidence too low: {r.Confidence:F2}");
    }

    [Fact]
    public void DetectSmallScroll()
    {
        // 60px scroll => overlap 240 (relative scroll small)
        OverlapResult r = Detector.Detect(FrameAt(0), FrameAt(60));
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 232, 248);
    }

    [Fact]
    public void DetectWithNoise()
    {
        // antialias/subpixel-level noise must be tolerated
        OverlapResult r = Detector.Detect(FrameAt(0), TestImages.AddNoise(FrameAt(180), seed: 12345, amplitude: 5));
        Assert.True(r.Success, $"failed: {r.Note}");
        Assert.InRange(r.OverlapHeight, 111, 129);
        Assert.True(r.Confidence > 0.4);
    }

    [Fact]
    public void DetectDisjointContent_Fails()
    {
        OverlapResult r = Detector.Detect(FrameAt(0), TestImages.CreateNoise(W, H, seed: 999));
        Assert.False(r.Success);
    }

    [Fact]
    public void DetectNoScrollFrames_MustFail()
    {
        // Identical frames have no legal in-window overlap (100% overlap is out of range).
        // The stitcher handles them by skipping — the detector must not fabricate a small overlap.
        OverlapResult r = Detector.Detect(FrameAt(0), FrameAt(0));
        Assert.False(r.Success);
    }

    [Fact]
    public void DetectDifferentSizes_Fails()
    {
        OverlapResult r = Detector.Detect(FrameAt(0), TestImages.CreateSolid(W, 299, 128));
        Assert.False(r.Success);
    }
}
