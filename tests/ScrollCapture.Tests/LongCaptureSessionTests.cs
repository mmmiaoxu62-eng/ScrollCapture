using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Scrolling;

namespace ScrollCapture.Tests;

public class LongCaptureSessionTests
{
    private const int FrameWidth = 64;
    private const int FrameHeight = 64;

    private static BitmapSource MakeFrame(byte gray)
    {
        var buffer = new byte[FrameWidth * FrameHeight * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = gray;
            buffer[i + 1] = gray;
            buffer[i + 2] = gray;
            buffer[i + 3] = 255;
        }
        var source = BitmapSource.Create(FrameWidth, FrameHeight, 96, 96, PixelFormats.Bgr32, null, buffer, FrameWidth * 4);
        source.Freeze();
        return source;
    }

    private static readonly ScrollOptions FastOptions = new() { DelayPerScrollMs = 0 };

    [Fact]
    public async Task StopsAfterTwoIdenticalFrames_ReachedBottom()
    {
        byte[] grays = { 10, 20, 30, 40, 40, 40 }; // 4 progressive frames, then no more change
        int index = 0;

        var session = new LongCaptureSession(
            new Int32Rect(0, 0, FrameWidth, FrameHeight),
            options: FastOptions,
            maxFrames: 100,
            framesDirectory: Path.Combine(Path.GetTempPath(), $"sc_session_{Guid.NewGuid():N}"),
            capture: _ => MakeFrame(grays[Math.Min(index, grays.Length - 1)]),
            scrollOnce: () => index++);

        SessionResult result = await session.RunAsync();

        Assert.Equal(SessionStopReason.ReachedBottom, result.Reason);
        Assert.Equal(6, result.FrameCount);
        Assert.NotNull(result.StitchedImage);
        // tiny synthetic frames cannot be vision-matched: every additional frame is
        // duplicate-safety-skipped (never estimated) => canvas == first frame only.
        Assert.Equal(FrameHeight, result.StitchedImage!.PixelHeight);
        Assert.All(result.StitchSteps!.Skip(1), s => Assert.True(s.UsedFallback));
    }

    [Fact]
    public async Task Cancellation_StopsPrematurely()
    {
        int index = 0;
        var cts = new CancellationTokenSource();

        var session = new LongCaptureSession(
            new Int32Rect(0, 0, FrameWidth, FrameHeight),
            options: FastOptions,
            maxFrames: 100,
            framesDirectory: Path.Combine(Path.GetTempPath(), $"sc_session_{Guid.NewGuid():N}"),
            capture: _ => MakeFrame((byte)(index * 20)),
            scrollOnce: () =>
            {
                index++;
                if (index >= 3)
                {
                    cts.Cancel();
                }
            },
            token: cts.Token);

        SessionResult result = await session.RunAsync();

        Assert.Equal(SessionStopReason.Cancelled, result.Reason);
        Assert.Equal(3, result.FrameCount);
    }

    [Fact]
    public async Task FrameLimitIsEnforced()
    {
        int index = 0;
        var session = new LongCaptureSession(
            new Int32Rect(0, 0, FrameWidth, FrameHeight),
            options: FastOptions,
            maxFrames: 4,
            framesDirectory: Path.Combine(Path.GetTempPath(), $"sc_session_{Guid.NewGuid():N}"),
            capture: _ => MakeFrame((byte)(index * 10)),
            scrollOnce: () => index++);

        SessionResult result = await session.RunAsync();

        Assert.Equal(SessionStopReason.LimitReached, result.Reason);
        Assert.Equal(4, result.FrameCount);
    }
}
