using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Stitching;

public sealed record StitchStepReport(int FrameIndex, int OverlapHeight, double Confidence, bool UsedFallback, bool Skipped);

public sealed record StitchResult(
    BitmapSource? Image,
    int Height,
    IReadOnlyList<StitchStepReport> Steps,
    IReadOnlyList<string> Warnings)
{
    public int FrameCount => Steps.Count;
    public bool HasFailures => Warnings.Count > 0;
}

/// <summary>
/// Incremental vertical stitcher. Frames are matched against the PREVIOUS FRAME (never
/// against the accumulated image — avoids error drift from past seams), and appended
/// row-block by row-block into a final Bgr32 bitmap.
/// Failure policy: nearly-identical frames are skipped; un-matchable frames use the
/// previous frame's scroll delta as a fallback and are reported as warnings
/// (never silently produce a wrong seam without a trace).
/// </summary>
public static class ImageStitcher
{
    private const long MaxMemoryBytes = 700L * 1024 * 1024;

    public static StitchResult Stitch(IReadOnlyList<BitmapSource> frames, int maxImageHeight,
        IReadOnlyList<double?>? priorScrollDeltas = null)
    {
        var warnings = new List<string>();
        var steps = new List<StitchStepReport>();
        var detector = new OverlapDetector();

        if (frames.Count == 0)
        {
            warnings.Add("no frames to stitch");
            return new StitchResult(null, 0, steps, warnings);
        }

        BitmapSource first = frames[0];
        int width = first.PixelWidth;
        int height = first.PixelHeight;
        if (width <= 0 || height <= 0)
        {
            warnings.Add("invalid frame size");
            return new StitchResult(null, 0, steps, warnings);
        }
        maxImageHeight = Math.Max(height, maxImageHeight);

        var segments = new List<(byte[] Buffer, int RowOffset, int RowCount)>();
        var totalHeight = height;
        segments.Add((FrameSimilarity.ToBgr32Buffer(first), 0, height));
        steps.Add(new StitchStepReport(0, 0, 1.0, false, false));

        int lastDelta = height;
        BitmapSource lastUsable = first;
        for (int i = 1; i < frames.Count; i++)
        {
            BitmapSource current = frames[i];
            if (current.PixelWidth != width || current.PixelHeight != height)
            {
                warnings.Add($"frame {i + 1}: size changed, skipped");
                continue;
            }

            OverlapResult overlap;
            if (FrameSimilarity.IsNearlyIdentical(lastUsable, current))
            {
                // Window did not move (or reached bottom) — nothing new.
                steps.Add(new StitchStepReport(i, height, 1.0, false, true));
                lastDelta = 0;
                continue;
            }

            double? prior = priorScrollDeltas != null && i - 1 < priorScrollDeltas.Count
                ? priorScrollDeltas[i - 1]
                : null;
            double? priorOverlap = prior is double pv && pv > 0
                ? height - pv
                : null;

            overlap = detector.Detect(lastUsable, current, priorOverlap);
            int delta = 0;
            bool usedFallback = false;
            if (overlap.Success)
            {
                delta = height - Math.Clamp(overlap.OverlapHeight, 1, height - 1);
                steps.Add(new StitchStepReport(i, overlap.OverlapHeight, overlap.Confidence, false, false));
            }
            else
            {
                // Fallback: assume the same scroll distance as the previous step.
                int expected = Math.Clamp(lastDelta == 0 ? height / 2 : lastDelta, Math.Max(20, height / 4), (int)(height * 0.9));
                delta = expected;
                usedFallback = true;
                steps.Add(new StitchStepReport(i, height - delta, 0.0, usedFallback, false));
                warnings.Add($"frame {i + 1}: overlap detection failed ({overlap.Note}); used estimated delta {delta}px");
            }
            lastDelta = delta;

            if (delta <= 0)
            {
                continue;
            }

            if (totalHeight + delta > maxImageHeight)
            {
                warnings.Add($"max image height {maxImageHeight}px reached after {i + 1} frames");
                break;
            }
            if ((long)width * (totalHeight + delta) * 4 > MaxMemoryBytes)
            {
                warnings.Add("stitcher memory limit reached");
                break;
            }

            byte[] buffer = FrameSimilarity.ToBgr32Buffer(current);
            segments.Add((buffer, height - delta, delta));
            totalHeight += delta;
            lastUsable = current;
        }

        var canvas = new WriteableBitmap(width, totalHeight, 96, 96, PixelFormats.Bgr32, null);
        int paintOffset = 0;
        foreach ((byte[] buffer, int rowOffset, int rowCount) in segments)
        {
            canvas.WritePixels(new Int32Rect(0, paintOffset, width, rowCount), buffer, width * 4, rowOffset * width * 4);
            paintOffset += rowCount;
        }
        canvas.Freeze();

        return new StitchResult(canvas, totalHeight, steps, warnings);
    }
}
