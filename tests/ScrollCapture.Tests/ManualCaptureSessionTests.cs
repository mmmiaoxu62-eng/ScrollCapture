using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ScrollCapture.Scrolling;

namespace ScrollCapture.Tests;

public class ManualCaptureSessionTests
{
    private const int W = 200;
    private const int H = 300;
    private static readonly byte[] LongBuffer = TestImages.CreateLongBuffer(W, 1800);

    [Fact]
    public void Manual_AddsFramesAndProducesStitch()
    {
        var frames = new Queue<BitmapSource>();
        frames.Enqueue(TestImages.Slice(LongBuffer, W, H, 0));
        frames.Enqueue(TestImages.Slice(LongBuffer, W, H, 180));
        frames.Enqueue(TestImages.Slice(LongBuffer, W, H, 360));

        var session = new ManualCaptureSession(
            new Int32Rect(0, 0, W, H),
            maxImageHeight: 5000,
            capture: _ => frames.Dequeue());

        int added = 0;
        while (frames.Count > 0)
        {
            if (session.AddFrame(out string? warning))
            {
                added++;
            }
        }

        Assert.Equal(3, added);
        Assert.Equal(3, session.FrameCount);
        BitmapSource? result = session.Finish();
        Assert.NotNull(result);
        Assert.Equal(300 + 2 * 180, result!.PixelHeight);
        Assert.Equal(0, session.Warnings.Count);
    }

    [Fact]
    public void Manual_RespectsHeightLimit()
    {
        var frames = new Queue<BitmapSource>();
        for (int i = 0; i < 6; i++)
        {
            frames.Enqueue(TestImages.Slice(LongBuffer, W, H, (i + 1) * 180));
        }

        var session = new ManualCaptureSession(
            new Int32Rect(0, 0, W, H),
            maxImageHeight: 460,
            capture: _ => frames.Dequeue());

        bool truncated = false;
        while (frames.Count > 0)
        {
            if (!session.AddFrame(out string? warning))
            {
                truncated = true;
                break;
            }
        }
        BitmapSource? result = session.Finish();
        Assert.NotNull(result);
        Assert.True(result!.PixelHeight <= 460, $"got {result.PixelHeight}");
        Assert.True(truncated || session.Warnings.Count > 0);
    }
}
