using System.Windows;
using System.Windows.Media.Imaging;
using ScrollCapture.Capture;
using ScrollCapture.Stitching;

namespace ScrollCapture.Scrolling;

/// <summary>
/// Manual (user-driven) capture: the user scrolls the target window themselves and
/// presses the hotkey repeatedly; each press appends one frame. Works for programs
/// that cannot be driven automatically. Reuses the same gated incremental stitcher,
/// so quality/limit policies are identical.
/// </summary>
public sealed class ManualCaptureSession
{
    private readonly Int32Rect _region;
    private readonly Func<Int32Rect, BitmapSource> _capture;
    private readonly IncrementalStitcher _stitcher;
    private bool _started;
    private int _frameCount;

    public event Action<int, long>? FrameAdded;

    public IReadOnlyList<StitchStepReport> Steps => _stitcher.Steps;
    public IReadOnlyList<string> Warnings => _stitcher.Warnings;
    public bool Truncated => _stitcher.Truncated;
    public int FrameCount => _frameCount;

    public ManualCaptureSession(Int32Rect region, int maxImageHeight,
        Func<Int32Rect, BitmapSource>? capture = null)
    {
        _region = region;
        _capture = capture ?? ScreenCaptureService.Capture;
        _stitcher = new IncrementalStitcher(maxImageHeight);
    }

    /// <summary>Captures one frame on demand (from a hotkey press).</summary>
    public bool AddFrame(out string? warning)
    {
        warning = null;
        BitmapSource frame = _capture(_region);
        _frameCount++;
        if (!_started)
        {
            _stitcher.Start(frame);
            _started = true;
        }
        else
        {
            _stitcher.Add(frame, priorScrollDeltaPx: null);
        }
        FrameAdded?.Invoke(_frameCount, _stitcher.Finish()?.PixelHeight ?? 0);
        if (_stitcher.Truncated)
        {
            warning = "已达最大高度，拼接结束。";
        }
        return !_stitcher.Truncated;
    }

    public BitmapSource? Finish() => _stitcher.Finish();
}
