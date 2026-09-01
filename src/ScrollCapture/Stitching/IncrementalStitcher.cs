using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Stitching;

/// <summary>
/// Stitches DURING a capture session: each frame is appended immediately, so memory
/// stays at (final image + last frame) instead of N frames. Same pairing logic as
/// ImageStitcher: previous-frame matching, identical-frame skip, expected-delta
/// fallback with warnings, height/memory limits.
/// </summary>
public sealed class IncrementalStitcher
{
    private const long MaxMemoryBytes = 700L * 1024 * 1024;

    private readonly int _maxImageHeight;
    private readonly OverlapDetector _detector = new();

    private int _width;
    private int _height;
    private long _totalHeight;
    private int _lastDelta;
    private BitmapSource? _lastFrame;
    private readonly List<(byte[] Buffer, int RowOffset, int RowCount)> _segments = new();

    public IReadOnlyList<StitchStepReport> Steps { get; } = new List<StitchStepReport>();
    public IReadOnlyList<string> Warnings { get; } = new List<string>();
    public bool Truncated { get; private set; }

    public IncrementalStitcher(int maxImageHeight)
    {
        _maxImageHeight = Math.Max(1, maxImageHeight);
    }

    public void Start(BitmapSource firstFrame)
    {
        ((List<StitchStepReport>)Steps).Clear();
        ((List<string>)Warnings).Clear();
        _segments.Clear();
        Truncated = false;

        _width = firstFrame.PixelWidth;
        _height = firstFrame.PixelHeight;
        _totalHeight = _height;
        _lastDelta = _height;
        _lastFrame = firstFrame;
        _segments.Add((FrameSimilarity.ToBgr32Buffer(firstFrame), 0, _height));
        ((List<StitchStepReport>)Steps).Add(new StitchStepReport(0, 0, 1.0, false, false));
    }

    public void Add(BitmapSource current, double? priorScrollDeltaPx)
    {
        if (_lastFrame == null || _width <= 0)
        {
            return;
        }
        if (current.PixelWidth != _width || current.PixelHeight != _height)
        {
            ((List<string>)Warnings).Add($"frame size changed, skipped");
            return;
        }

        if (FrameSimilarity.IsNearlyIdentical(_lastFrame, current))
        {
            ((List<StitchStepReport>)Steps).Add(new StitchStepReport(Steps.Count, _height, 1.0, false, true));
            _lastDelta = 0;
            return; // no new content
        }

        double? priorOverlap = priorScrollDeltaPx is double d && d > 0 ? _height - d : null;
        OverlapResult overlap = _detector.Detect(_lastFrame, current, priorOverlap);

        int delta;
        if (overlap.Success)
        {
            delta = _height - Math.Clamp(overlap.OverlapHeight, 1, _height - 1);
            ((List<StitchStepReport>)Steps).Add(new StitchStepReport(Steps.Count, overlap.OverlapHeight, overlap.Confidence, false, false));
        }
        else
        {
            int expected = Math.Clamp(_lastDelta == 0 ? _height / 2 : _lastDelta,
                Math.Max(20, _height / 4), (int)(_height * 0.9));
            delta = expected;
            ((List<StitchStepReport>)Steps).Add(new StitchStepReport(Steps.Count, _height - delta, 0.0, true, false));
            ((List<string>)Warnings).Add($"overlap detection failed ({overlap.Note}); used estimated delta {delta}px");
        }
        _lastDelta = delta;

        if (delta <= 0)
        {
            return;
        }
        if (_totalHeight + delta > _maxImageHeight)
        {
            ((List<string>)Warnings).Add($"max image height {_maxImageHeight}px reached after {Steps.Count} frames");
            Truncated = true;
            return;
        }
        if ((long)_width * (_totalHeight + delta) * 4 > MaxMemoryBytes)
        {
            ((List<string>)Warnings).Add("stitcher memory limit reached");
            Truncated = true;
            return;
        }

        byte[] buffer = FrameSimilarity.ToBgr32Buffer(current);
        _segments.Add((buffer, _height - delta, delta));
        _totalHeight += delta;
        _lastFrame = current;
    }

    /// <summary>Renders the accumulated long image; safely callable once after the loop.</summary>
    public BitmapSource? Finish()
    {
        if (_segments.Count == 0 || _totalHeight <= 0)
        {
            return null;
        }
        var canvas = new WriteableBitmap(_width, (int)_totalHeight, 96, 96, PixelFormats.Bgr32, null);
        int offset = 0;
        foreach (var (buffer, rowOffset, rowCount) in _segments)
        {
            canvas.WritePixels(new Int32Rect(0, offset, _width, rowCount), buffer, _width * 4, rowOffset * _width * 4);
            offset += rowCount;
        }
        canvas.Freeze();
        return canvas;
    }
}
