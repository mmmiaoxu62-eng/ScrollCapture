using System.Windows.Media.Imaging;
using ScrollCapture.Stitching;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class ImageStitcherTests
{
    private const int W = 200;
    private const int H = 300;

    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 1800);

    [Fact]
    public void StitchThreeFrames_ProducesCorrectPixels()
    {
        // slices at 0, 180, 360 => overlaps of 120px each => height 300+180+180 = 660
        var frames = new List<BitmapSource>
        {
            TestImages.Slice(LongBuffer, W, H, 0),
            TestImages.Slice(LongBuffer, W, H, 180),
            TestImages.Slice(LongBuffer, W, H, 360),
        };

        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 5000);

        Assert.NotNull(result.Image);
        Assert.Equal(0, result.Warnings.Count);
        Assert.Equal(660, result.Height);
        Assert.True(result.MatchAllPixelRows(result.Image!, LongBuffer, W));
    }

    [Fact]
    public void Stitch_StartWithDifferentOverlap_StillExact()
    {
        var frames = new List<BitmapSource>
        {
            TestImages.Slice(LongBuffer, W, H, 0),
            TestImages.Slice(LongBuffer, W, H, 240), // overlap 60
            TestImages.Slice(LongBuffer, W, H, 420), // overlap 120
        };

        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 5000);

        Assert.NotNull(result.Image);
        // deltas: 300-60=240, 300-120=180  => 300+240+180
        Assert.Equal(720, result.Height);
        Assert.True(result.MatchAllPixelRows(result.Image!, LongBuffer, W));
    }

    [Fact]
    public void Stitch_TinyScrollFrames_NoWarnings()
    {
        // A smaller scroll: 30px => overlap 270 (within window)
        var frames = new List<BitmapSource>
        {
            TestImages.Slice(LongBuffer, W, H, 0),
            TestImages.Slice(LongBuffer, W, H, 30),
        };
        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 5000);
        Assert.NotNull(result.Image);
        Assert.Equal(0, result.Warnings.Count);
        Assert.Equal(330, result.Height);
    }

    [Fact]
    public void Stitch_SkipsIdenticalFrames()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, 180);
        var frames = new List<BitmapSource> { a, a, b, b };

        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 5000);

        Assert.NotNull(result.Image);
        Assert.Equal(300 + 180, result.Height); // duplicated frames skipped
        Assert.True(result.Steps[1].Skipped);
        Assert.True(result.Steps[3].Skipped);
    }

    [Fact]
    public void Stitch_UnmatchableFrame_UsesFallbackAndWarns()
    {
        var frames = new List<BitmapSource>
        {
            TestImages.Slice(LongBuffer, W, H, 0),
            TestImages.Slice(LongBuffer, W, H, 180),
            TestImages.CreateNoise(W, H, seed: 777), // disjoint
        };

        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 5000);

        Assert.NotNull(result.Image);
        Assert.True(result.HasFailures);
        Assert.Contains(result.Steps, s => s.UsedFallback);
        Assert.Equal(300 + 180 + 180, result.Height); // fallback delta = previous 180
    }

    [Fact]
    public void Stitch_RespectsMaxHeight()
    {
        var frames = new List<BitmapSource>
        {
            TestImages.Slice(LongBuffer, W, H, 0),
            TestImages.Slice(LongBuffer, W, H, 180),
            TestImages.Slice(LongBuffer, W, H, 360),
        };

        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 500);

        Assert.NotNull(result.Image);
        Assert.Equal(480, result.Height); // third frame would exceed 500 => dropped
        Assert.True(result.HasFailures);
    }

    [Fact]
    public void Stitch_FramesWithChangedSize_SkippedWithWarning()
    {
        var frames = new List<BitmapSource>
        {
            TestImages.Slice(LongBuffer, W, H, 0),
            TestImages.CreateSolid(W - 10, H, 90),
            TestImages.Slice(LongBuffer, W, H, 180),
        };

        StitchResult result = ImageStitcher.Stitch(frames, maxImageHeight: 5000);

        Assert.NotNull(result.Image);
        Assert.Equal(300 + 180, result.Height);
        Assert.True(result.HasFailures);
    }

    [Fact]
    public void Stitch_EmptyList_ReturnsNullWithWarning()
    {
        StitchResult result = ImageStitcher.Stitch(new List<BitmapSource>(), maxImageHeight: 5000);
        Assert.Null(result.Image);
        Assert.True(result.HasFailures);
    }
}

internal static class StitchAssertions
{
    public static bool MatchAllPixelRows(this StitchResult result, BitmapSource image, byte[] longBuffer, int width)
    {
        byte[] stitched = FrameSimilarity.ToBgr32Buffer(image);
        int stride = width * 4;
        int longHeight = longBuffer.Length / stride;
        for (int y = 0; y < image.PixelHeight; y += 7)
        {
            if (y >= longHeight)
            {
                return false;
            }
            int sBase = y * stride;
            int lBase = y * stride;
            for (int x = 0; x < width; x += 3)
            {
                int idx = sBase + x * 4;
                if (Math.Abs(stitched[idx] - longBuffer[lBase + x * 4]) > 1 ||
                    Math.Abs(stitched[idx + 1] - longBuffer[lBase + x * 4 + 1]) > 1 ||
                    Math.Abs(stitched[idx + 2] - longBuffer[lBase + x * 4 + 2]) > 1)
                {
                    return false;
                }
            }
        }
        return true;
    }
}
