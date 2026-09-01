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
        int step = 0;
        var session = new ManualCaptureSession(
            new Int32Rect(0, 0, W, H),
            maxImageHeight: 5000,
            capture: _ => TestImages.Slice(LongBuffer, W, H, step * 180 + (step++ == 0 ? 0 : 180 - 180)));

        // simulate 3 hotkey presses with the user scrolling 180px between them
        int added = 0;
        for (int i = 0; i < 3; i++)
        {
            if (session.AddFrame(out string? warning))
            {
                added++;
            }
            step++; // user scrolls forward between presses
        }

        Assert.Equal(3, added);
        Assert.Equal(3, session.FrameCount);
        BitmapSource? result = session.Finish();
        Assert.NotNull(result);
        Assert.Equal(300 + 2 * 180, result!.PixelHeight);
    }

    [Fact]
    public void Manual_RespectsHeightLimit()
    {
        var session = new ManualCaptureSession(
            new Int32Rect(0, 0, W, H),
            maxImageHeight: 460,
            capture: _ => TestImages.Slice(LongBuffer, W, H, _frameIdx++ * 180));

        while (session.AddFrame(out string? warning))
        {
            if (warning != null)
            {
                break;
            }
        }
        BitmapSource? result = session.Finish();
        Assert.NotNull(result);
        Assert.True(result!.PixelHeight <= 460, $"got {result.PixelHeight}");
        Assert.True(session.Truncated || session.Warnings.Count > 0);
    }

    private int _frameIdx = 1;
}
