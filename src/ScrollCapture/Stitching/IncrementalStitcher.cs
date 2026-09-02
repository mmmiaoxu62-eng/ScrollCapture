using System.IO;
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
    internal const double MinAcceptConfidence = 0.70;
    internal const double MaxDeltaJumpRatio = 0.40; // |delta - last| > 40% of frame height = reject
    internal const double MotionStaticThreshold = 0.04; // <4% changed rows = window did not move

    // User-requested A/B: DISABLED — fixed-region blanking caused over-deletion;
    // keep fixed UI as-is while the mixed-region policy is being reviewed.
    internal const bool BlankPostProcessEnabled = false;

    private readonly int _maxImageHeight;
    private readonly OverlapDetector _detector = new();
    private readonly FixedRegionDetector _fixedDetector = new();
    private readonly string? _debugDir;
    private int _pairIndex;

    private int _width;
    private int _height;
    private long _totalHeight;
    private int _lastDelta;
    private BitmapSource? _lastFrame;
    private bool[]? _bandMask;
    private bool[]? _blankMask;
    private readonly List<(byte[] Buffer, int RowOffset, int RowCount)> _segments = new();

    /// <summary>
    /// Sets the columns to blank below the first frame (fixed UI bands only —
    /// classified by driving-band motion AND having actual content/contrast).
    /// </summary>
    public void SetBlankMask(bool[]? blankMask) => _blankMask = blankMask;

    public IReadOnlyList<StitchStepReport> Steps { get; } = new List<StitchStepReport>();
    public IReadOnlyList<string> Warnings { get; } = new List<string>();
    public bool Truncated { get; private set; }

    public IncrementalStitcher(int maxImageHeight, string? debugDir = null)
    {
        _maxImageHeight = Math.Max(1, maxImageHeight);
        _debugDir = debugDir;
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

    public void Add(BitmapSource current, double? priorScrollDeltaPx, bool[]? drivingBandMask = null)
    {
        if (drivingBandMask != null)
        {
            _bandMask = drivingBandMask;
        }
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
        // With a driving-band mask the check runs on the moving columns ONLY, so a
        // mixed region (static sidebar + scrolling column) keeps driving the loop.
        double motion = ColumnMotion.ComputeDrivenMotionFraction(_lastFrame, current, _bandMask);
        if (motion < MotionStaticThreshold)
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

        // Fixed/Sticky region layer: purely optional input for the existing detector.
        // null (no fixed evidence / low confidence / any error) => ORIGINAL path.
        RegionWeightMap? weightMap = null;
        try
        {
            RegionWeightMap? wm = _fixedDetector.Update(_lastFrame, current, _bandMask);
            if (wm?.IsReliable == true)
            {
                weightMap = wm;
            }
        }
        catch
        {
            weightMap = null;
        }

        OverlapResult overlap = _detector.Detect(_lastFrame, current, priorOverlap, _bandMask, weightMap);

        if (_debugDir != null)
        {
            WritePairDebug(current, weightMap, overlap);
        }

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

        if (!accepted && overlap.Note is string n && n.Contains("refine best")
            && TryExtractBest(n, out double bestRef) && bestRef < 9.0
            && priorDelta is double pd2 && pd2 > 4)
        {
            // soft-accept: the peak exists but sits just above the strict threshold,
            // and the paste prior matches it closely. Confidence is visibly degraded
            // and a warning is recorded — but the session keeps moving instead of
            // accumulating rejections at well-known page modes.
            delta = Math.Clamp((int)Math.Round(pd2), 1, _height - 1);
            accepted = true;
            overlap = new OverlapResult(true, Math.Max(1, _height - delta), 0.55, "soft-accept near-miss");
            ((List<string>)Warnings).Add($"frame {Steps.Count}: soft-accept (refine {bestRef:F1}>5.5, prior {delta}px)");
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

    /// <summary>Renders the accumulated long image; safely callable once after the loop.
    /// Post-processing: when a driving-band mask exists, non-driving (fixed) columns are
    /// KEPT only for the first frame's height — everything below is blanked, because the
    /// fixed UI (sidebar) would otherwise repeat once per frame. Result matches the
    /// desired "固定区出现一次，其余留白" layout.</summary>
    private static bool TryExtractBest(string note, out double value)
    {
        value = 0;
        int idx = note.IndexOf("refine best", StringComparison.Ordinal);
        if (idx < 0) return false;
        var span = note.AsSpan(idx + "refine best".Length).TrimStart();
        int len = 0;
        while (len < span.Length && (char.IsDigit(span[len]) || span[len] == '.')) len++;
        return len > 0 && double.TryParse(span[..len], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private void WritePairDebug(BitmapSource current, RegionWeightMap? weightMap, OverlapResult overlap)
    {
        try
        {
            Directory.CreateDirectory(_debugDir!);
            int idx = _pairIndex++;
            string prefix = Path.Combine(_debugDir!, $"pair_{idx:D4}");
            var lines = new System.Text.StringBuilder();
            lines.AppendLine($"pair={idx}");
            lines.AppendLine($"dy0={_fixedDetector.LastDy0:F1}");
            lines.AppendLine($"globalDy={_fixedDetector.LastDy0:F1}");
            lines.AppendLine($"dy0Confidence={_fixedDetector.LastOverlapConfidence:F2}");
            foreach (var r in _fixedDetector.LastStrips)
            {
                lines.AppendLine($"strip={r.StripIndex} y=[{r.Y0}..{r.Y1}) fixedSim={r.FixedSim:F3} scrollSim={r.ScrollSim:F3} margin={r.Margin:F3} class={r.Classification} weight={r.EffectiveWeight:F2}");
            }
            lines.AppendLine($"overlapHeight={overlap.OverlapHeight}");
            lines.AppendLine($"overlapConfidence={overlap.Confidence:F2}");
            string wmText = weightMap == null
                ? "null"
                : weightMap.Summary + " conf=" + weightMap.Confidence.ToString("F2");
            lines.AppendLine("weightMap=" + wmText);
            File.WriteAllText(prefix + ".txt", lines.ToString(), System.Text.Encoding.UTF8);

            byte[] src = FrameSimilarity.ToBgr32Buffer(current);
            int w = current.PixelWidth;
            int h = current.PixelHeight;
            int sw = Math.Max(1, w / 4);
            int sh = Math.Max(1, h / 4);
            var small = new byte[sw * sh * 4];
            Array.Fill(small, (byte)0);
            for (int y = 0; y < sh; y++)
            {
                int row = (y * 4) * w * 4;
                for (int x = 0; x < sw; x++)
                {
                    int si = y * sw * 4 + x * 4;
                    int di = row + x * 4 * 4;
                    double rw = weightMap != null ? weightMap.RowWeight[Math.Min(h - 1, y * 4)] : 1.0;
                    double cw = weightMap != null ? weightMap.ColWeight[Math.Min(w - 1, x * 4)] : 1.0;
                    double eff = Math.Min(rw, cw);
                    small[si] = src[di];
                    small[si + 1] = src[di + 1];
                    small[si + 2] = src[di + 2];
                    small[si + 3] = 255;
                    if (eff < 0.35)
                    {
                        small[si] = 235; small[si + 1] = 60; small[si + 2] = 60;   // red
                    }
                    else if (eff < 0.75)
                    {
                        small[si] = 60; small[si + 1] = 120; small[si + 2] = 235; // blue
                    }
                }
            }
            var bmp = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgr32, null, small, sw * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(prefix + ".png");
            encoder.Save(fs);
        }
        catch
        {
            // debug output must never break a capture
        }
    }

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

        if (BlankPostProcessEnabled && _blankMask != null && _blankMask.Any(m => !m) && _totalHeight > _height)
        {
            BlankStaticColumns(canvas);
        }

        canvas.Freeze();
        return canvas;
    }

    private void BlankStaticColumns(WriteableBitmap canvas)
    {
        // _blankMask: TRUE = blank this column below the first frame
        bool[] colMask = ColumnMotion.ToColumnMask(_blankMask, _width);
        int blankFromY = _height; // first frame keeps the fixed UI at the top
        int blankBottom = (int)_totalHeight;

        int runStart = -1;
        for (int x = 0; x <= _width; x++)
        {
            bool shouldBlank = x < _width && colMask[x];
            if (shouldBlank)
            {
                if (runStart < 0) runStart = x;
            }
            else if (runStart >= 0)
            {
                int runW = x - runStart;
                BlankRun(canvas, runStart, runW, blankFromY, blankBottom);
                runStart = -1;
            }
        }
    }

    private static void BlankRun(WriteableBitmap canvas, int x, int width, int fromY, int bottom)
    {
        var white = new byte[width * (bottom - fromY) * 4];
        for (int i = 3; i < white.Length; i += 4)
        {
            white[i] = 255; // Bgr32 with white RGB (alpha unused)
        }
        for (int i = 0; i < white.Length; i += 4)
        {
            white[i] = 255;
            white[i + 1] = 255;
            white[i + 2] = 255;
        }
        canvas.WritePixels(new Int32Rect(x, fromY, width, bottom - fromY), white, width * 4, 0);
    }
}
