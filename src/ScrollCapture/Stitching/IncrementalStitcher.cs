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

    // True matches typically score 0.98+; self-similar content (chat lists) produces
    // junk peaks at 0.4-0.6 — anything below is NOT a match (skip, don't paste).
    internal const double MinAcceptConfidence = 0.75;
    internal const double MaxDeltaJumpRatio = 0.40; // |delta - last| > 40% of frame height = reject
    internal const double MotionStaticThreshold = 0.04; // <4% changed rows = window did not move

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
        _lastDelta = 0; // no reference step yet — jump guard & self-prior skip the first pair
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

        // Motion gate: if almost NO rows changed, the window did not move — matching next
        // would be picking a phantom peak out of static content (repeated-block bug).
        if (FrameSimilarity.ComputeMotionFraction(_lastFrame, current) < MotionStaticThreshold)
        {
            ((List<StitchStepReport>)Steps).Add(new StitchStepReport(Steps.Count, _height, 1.0, false, true));
            _lastDelta = 0;
            return; // static after all — count toward the bottom-stop
        }

        // Self-prior: when no probe gave an estimate, assume the SAME delta as the last
        // good step (chat lists scroll uniformly). Junk matches at far offsets are then
        // unreachable — only genuine peaks inside the narrow window survive.
        double? priorDelta = priorScrollDeltaPx is double pd && pd > 0
            ? pd
            : _lastDelta > 0 && _lastDelta < _height - 10 ? _lastDelta : null;
        double? priorOverlap = priorDelta is double p && p > 0 ? _height - p : null;
        OverlapResult overlap = _detector.Detect(_lastFrame, current, priorOverlap);

        int delta = _lastDelta;
        bool accepted = overlap.Success
                        && overlap.Confidence >= MinAcceptConfidence;
        if (accepted)
        {
            delta = _height - Math.Clamp(overlap.OverlapHeight, 1, _height - 1);
            if (_lastDelta > 0 && Math.Abs(delta - _lastDelta) > _height * MaxDeltaJumpRatio)
            {
                accepted = false; // physically implausible jump (100px -> 800px)
            }
        }

        if (accepted)
        {
            ((List<StitchStepReport>)Steps).Add(new StitchStepReport(Steps.Count, overlap.OverlapHeight, overlap.Confidence, false, false));
            _lastDelta = delta;
            if (overlap.Confidence < 0.9)
            {
                Utils.Logger.Info($"stitch step {Steps.Count - 1}: accepted conf={overlap.Confidence:F2} ov={overlap.OverlapHeight}");
            }
        }
        else
        {
            // NEVER paste an estimated delta: live content (chat scroll-ins, animations) can
            // move LESS than the estimate -> duplicated bands. Dropping the frame risks at
            // most a one-step gap; the session ladder reacts and usually recovers.
            string why = !overlap.Success
                ? overlap.Note ?? "detection failed"
                : overlap.Confidence < MinAcceptConfidence
                    ? $"confidence {overlap.Confidence:F2} below {MinAcceptConfidence}"
                    : $"delta jump {_height - overlap.OverlapHeight}px vs last {_lastDelta}px";
            ((List<StitchStepReport>)Steps).Add(new StitchStepReport(Steps.Count, 0, 0.0, true, false));
            ((List<string>)Warnings).Add($"overlap rejected ({why}); frame skipped (duplicate-safety)");
            Utils.Logger.Info($"stitch step {Steps.Count - 1}: REJECTED ({why})");
            return;
        }

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
