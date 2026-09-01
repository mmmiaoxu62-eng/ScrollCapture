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
}
