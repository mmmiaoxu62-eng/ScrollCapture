using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Scrolling;

namespace ScrollCapture.Tests;

public class OffsetProbeAndSessionTests
{
    private const int W = 64;
    private const int H = 64;

    private static BitmapSource MakeFrame(byte gray)
    {
        var buffer = new byte[W * H * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = gray; buffer[i + 1] = gray; buffer[i + 2] = gray; buffer[i + 3] = 255;
        }
        var source = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgr32, null, buffer, W * 4);
        source.Freeze();
        return source;
    }

    private static readonly ScrollOptions FastOptions = new() { DelayPerScrollMs = 0 };

    [Fact]
    public void EstimateDeltaPx_UsesScrollPos()
    {
        var a = new OffsetSnapshot(ScrollPos: 0, ScrollMax: 10000, ScrollPage: 800, ScrollTrackPos: null, UiaPercent: null, UiaViewSize: null);
        var b = new OffsetSnapshot(ScrollPos: 240, ScrollMax: 10000, ScrollPage: 800, ScrollTrackPos: null, UiaPercent: null, UiaViewSize: null);
        Assert.Equal(240, a.EstimateDeltaPx(b));
    }

    [Fact]
    public void EstimateDeltaPx_UsesUiaPercentWhenNoScrollbar()
    {
        var a = new OffsetSnapshot(null, null, null, null, UiaPercent: 5.0, UiaViewSize: 20.0, ClientHeightPx: 800);
        var b = new OffsetSnapshot(null, null, null, null, UiaPercent: 17.0, UiaViewSize: 20.0, ClientHeightPx: 800);
        double? d = a.EstimateDeltaPx(b);
        Assert.NotNull(d);
        // |17-5|% * usable(=800*(100-20)/20=3200)/100 = 384px
        Assert.Equal(384.0, d!.Value, 2);
    }

    [Fact]
    public async Task ProbeSmallMovementTwice_ReachesBottom()
    {
        // probe says deltas 4px, 4px (< static threshold) => bottom after second
        var positions = new Queue<int>(new[] { 0, 4, 8, 12 });
        int captured = 0;
        var session = new LongCaptureSession(
            new Int32Rect(0, 0, W, H),
            options: FastOptions,
            maxFrames: 100,
            framesDirectory: Path.Combine(Path.GetTempPath(), $"sc_session_{Guid.NewGuid():N}"),
            capture: _ => MakeFrame((byte)(10 + captured++)),
            scrollOnce: () => { },
            probeGetter: () =>
            {
                if (positions.Count < 2) return null;
                int p = positions.Dequeue();
                return new OffsetSnapshot(p, 10000, 800, null, null, null);
            });

        SessionResult r = await session.RunAsync();
        Assert.Equal(SessionStopReason.ReachedBottom, r.Reason);
        Assert.True(r.FrameCount <= 5);
    }

    [Fact]
    public async Task ZeroMovement_ReachesBottom()
    {
        // probe never advances: injected scrollOnce (no real wheel => no step-down),
        // zero-move x3 => bottom without hanging
        int captured = 0;
        var session = new LongCaptureSession(
            new Int32Rect(0, 0, W, H),
            options: FastOptions,
            capture: _ => MakeFrame((byte)(20 + captured)),
            scrollOnce: () => captured++,
            probeGetter: () => new OffsetSnapshot(0, 20000, 900, null, null, null));

        SessionResult r = await session.RunAsync();
        Assert.Equal(SessionStopReason.ReachedBottom, r.Reason);
        Assert.True(r.FrameCount <= 6, "must stop after a few zero-move frames");
    }

    [Fact]
    public async Task MovesPlenty_NoFalseStop()
    {
        int captured = 0;
        var session = new LongCaptureSession(
            new Int32Rect(0, 0, W, H),
            options: FastOptions,
            maxFrames: 6,
            framesDirectory: Path.Combine(Path.GetTempPath(), $"sc_session_{Guid.NewGuid():N}"),
            capture: _ => MakeFrame((byte)(30 + captured * 30)), // big steps: never "nearly identical"
            scrollOnce: () => captured++,
            probeGetter: () => new OffsetSnapshot(captured * 250, 100000, 900, null, null, null));

        SessionResult r = await session.RunAsync();
        Assert.Equal(SessionStopReason.LimitReached, r.Reason); // would reach bottom only if frames freeze
        Assert.Equal(6, r.FrameCount);
    }
}
