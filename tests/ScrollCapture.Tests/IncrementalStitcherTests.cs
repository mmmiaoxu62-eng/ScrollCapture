using System.Windows.Media.Imaging;
using ScrollCapture.Stitching;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class IncrementalStitcherTests
{
    private const int W = 200;
    private const int H = 300;
    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 1800);

    [Fact]
    public void Incremental_PixelsMatchSource()
    {
        var frames = new[] { 0, 180, 360, 540 };
        var stitcher = new IncrementalStitcher(maxImageHeight: 5000);
        for (int i = 0; i < frames.Length; i++)
        {
            BitmapSource frame = TestImages.Slice(LongBuffer, W, H, frames[i]);
            if (i == 0) stitcher.Start(frame);
            else stitcher.Add(frame, priorScrollDeltaPx: 180);
        }
        BitmapSource? image = stitcher.Finish();

        Assert.NotNull(image);
        Assert.Equal(300 + 3 * 180, image!.PixelHeight);
        Assert.Equal(0, stitcher.Warnings.Count);
        Assert.Equal(4, stitcher.Steps.Count);

        byte[] stitched = FrameSimilarity.ToBgr32Buffer(image);
        int stride = W * 4;
        for (int y = 0; y < image.PixelHeight; y += 7)
        {
            int baseIdx = y * stride;
            int srcBase = y * stride;
            for (int x = 0; x < W; x += 3)
            {
                int i = baseIdx + x * 4;
                int j = srcBase + x * 4;
                Assert.True(Math.Abs(stitched[i] - LongBuffer[j]) <= 1 &&
                            Math.Abs(stitched[i + 1] - LongBuffer[j + 1]) <= 1 &&
                            Math.Abs(stitched[i + 2] - LongBuffer[j + 2]) <= 1,
                            $"row mismatch at y={y}, x={x}");
            }
        }
    }

    [Fact]
    public void Incremental_SkipsIdenticalFrames()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        BitmapSource b = TestImages.Slice(LongBuffer, W, H, 240);
        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(a);
        stitcher.Add(a, 180);
        stitcher.Add(b, 180);

        BitmapSource? image = stitcher.Finish();
        Assert.NotNull(image);
        Assert.Equal(H + (H - 60) + 0, image!.PixelHeight); // duplicated a skipped; 240-slide => overlap 60
    }

    [Fact]
    public void Incremental_NearStaticFrames_SkippedNotPasted()
    {
        BitmapSource a = TestImages.Slice(LongBuffer, W, H, 0);
        // mutate <4% rows: a small "animated banner" like a chat emoji
        byte[] mut = FrameSimilarity.ToBgr32Buffer(a);
        var rnd = new Random(7);
        for (int y = 20; y < 24; y++)
        {
            int row = y * W;
            for (int x = 0; x < W; x++)
            {
                int idx = row + x * 4;
                mut[idx] = (byte)rnd.Next(256);
                mut[idx + 1] = (byte)rnd.Next(256);
                mut[idx + 2] = (byte)rnd.Next(256);
            }
        }
        BitmapSource b = TestImages.CreateBgr32(mut, W, H);

        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(a);
        stitcher.Add(b, null);

        BitmapSource? image = stitcher.Finish();
        Assert.NotNull(image);
        Assert.Equal(H, image!.PixelHeight);       // nothing pasted
        Assert.True(stitcher.Steps[^1].Skipped);   // static-motion skip
    }

    [Fact]
    public void Incremental_TruncatesAtMaxHeight()
    {
        var stitcher = new IncrementalStitcher(maxImageHeight: 500);
        stitcher.Start(TestImages.Slice(LongBuffer, W, H, 0));
        stitcher.Add(TestImages.Slice(LongBuffer, W, H, 180), 180);   // 300+180=480 ok
        stitcher.Add(TestImages.Slice(LongBuffer, W, H, 360), 180);   // would be 660 > 500

        BitmapSource? image = stitcher.Finish();
        Assert.NotNull(image);
        Assert.Equal(480, image!.PixelHeight);
        Assert.True(stitcher.Truncated);
    }

    [Fact]
    public void Incremental_UnmatchableFrame_IsSkippedNotEstimated()
    {
        var stitcher = new IncrementalStitcher(5000);
        stitcher.Start(TestImages.Slice(LongBuffer, W, H, 0));
        stitcher.Add(TestImages.Slice(LongBuffer, W, H, 180), 180);          // ok -> 300+180
        stitcher.Add(TestImages.CreateNoise(W, H, seed: 4242), 180);         // unmatchable

        BitmapSource? image = stitcher.Finish();

        Assert.NotNull(image);
        Assert.Equal(300 + 180, image!.PixelHeight); // nothing pasted for the disjoint frame
        Assert.True(stitcher.Warnings.Count > 0);
        Assert.Contains(stitcher.Steps, s => s.UsedFallback);
        Assert.Equal(stitcher.Steps[^1].OverlapHeight, 0);
    }
}
