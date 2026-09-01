using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace ScrollCapture.Vision;

public sealed record OverlapResult(bool Success, int OverlapHeight, double Confidence, string? Note = null)
{
    public static OverlapResult Fail(string note) => new(false, 0, 0, note);
}

/// <summary>
/// Finds how many rows at the bottom of frame A are identical to the top of frame B
/// (vertical overlap for scroll stitching). Pipeline:
///   0. static-edge mask: rows at the frame top/bottom margins that never change between
///      frames (browser toolbar/sidebars, fixed bars) are excluded 鈥?they bait false peaks
///   1. BGRA->gray + 1/4 downscale (OpenCV, INTER_AREA)
///   2. coarse scan (narrow around the supplied prior if any, else the whole window):
///      per-candidate row-wise robust mean-abs-diff (worst x% rows trimmed)
///   3. full-res refinement
///   4. verification band at ~1/3 of the overlap
///   5. confidence = similarity + peak margin (+ band penalty)
/// All numeric, fully local, no ML/OCR.
/// </summary>
public sealed class OverlapDetector
{
    private const int DownscaleFactor = 4;
    private const double MinOverlapRatio = 0.12;
    private const double MaxOverlapRatio = 0.90;
    // Coarse only locates a candidate; the refinement + band check make the final call,
    // so a lenient threshold avoids false rejects on non-integer downscale offsets.
    private const double CoarseScoreThreshold = 30.0;
    private const double RefineScoreThreshold = 5.5;
    // Near-tie rejection only; real confidence comes from refinement + band check,
    // because non-integer downscale offsets naturally produce blurred peaks.
    private const double MarginRatioThreshold = 1.03;
    private const int TrimWorstRowsPercent = 8;
    private const int MinHeightForDetection = 240;

    // static-row (chrome/floating UI) detection
    private const double StaticRowDiffThreshold = 1.6;  // avg abs diff between frames (gray 0..255)
    private const double StaticRowContrastMin = 5.0;    // row std must exceed this (skip blank margins)

    /// <param name="priorOverlapPx">
    /// Optional expected overlap (px) from an offset probe (scrollbar/UIA). When supplied,
    /// detection first searches only a narrow window around it; only on failure does it
    /// fall back to a global scan.
    /// </param>
    public OverlapResult Detect(BitmapSource previous, BitmapSource next, double? priorOverlapPx = null)
    {
        if (previous.PixelWidth != next.PixelWidth || previous.PixelHeight != next.PixelHeight)
        {
            return OverlapResult.Fail("frame size mismatch");
        }
        int width = previous.PixelWidth;
        int height = previous.PixelHeight;
        if (height < MinHeightForDetection)
        {
            return OverlapResult.Fail("frame too small");
        }

        byte[] grayA = ToGrayBytes(previous);
        byte[] grayB = ToGrayBytes(next);
        if (EstimateContrast(grayA) < 2.0)
        {
            return OverlapResult.Fail("frame has no usable content (blank/uniform)");
        }
        (byte[] smallA, int sw, int sh) = Downscale(grayA, width, height);
        (byte[] smallB, _, _) = Downscale(grayB, width, height);
        bool[] staticMask = ComputeStaticMask(grayA, grayB, width, height);
        bool[] staticMaskSmall = ComputeStaticMask(smallA, smallB, sw, sh);

        if (priorOverlapPx is double prior && prior >= MinOverlapRatio * height && prior <= MaxOverlapRatio * height)
        {
            OverlapResult narrowed = Scan(smallA, smallB, grayA, grayB, sw, sh, width, height,
                staticMask, staticMaskSmall, priorMinK: (int)((prior - 12) / DownscaleFactor),
                priorMaxK: (int)((prior + 12) / DownscaleFactor));
            if (narrowed.Success)
            {
                return narrowed;
            }
            // prior was wrong or too narrow 鈥?fall through to global scan
            OverlapResult global = Scan(smallA, smallB, grayA, grayB, sw, sh, width, height, staticMask, staticMaskSmall, null, null);
            return global.Success
                ? global with { Note = (global.Note ?? "") + " | prior mismatch, global hit" }
                : global;
        }

        return Scan(smallA, smallB, grayA, grayB, sw, sh, width, height, staticMask, staticMaskSmall, null, null);
    }

    private static OverlapResult Scan(
        byte[] smallA, byte[] smallB, byte[] grayA, byte[] grayB,
        int sw, int sh, int width, int height,
        bool[] staticMask, bool[] staticMaskSmall, int? priorMinK, int? priorMaxK)
    {
        int minK = Math.Max(1, (int)(sh * MinOverlapRatio));
        int maxK = Math.Min(sh - 2, (int)(sh * MaxOverlapRatio));
        if (priorMinK is int pMin)
        {
            minK = Math.Max(minK, pMin);
        }
        if (priorMaxK is int pMax)
        {
            maxK = Math.Min(maxK, pMax);
        }
        if (maxK <= minK)
        {
            return OverlapResult.Fail("downscaled frame too small");
        }

        // ---- coarse scan ----
        double best = double.MaxValue;
        double second = double.MaxValue;
        int bestK = -1;
        // descending: on ties prefer the LARGER overlap (more conservative delta)
        for (int k = maxK; k >= minK; k--)
        {
            double score = RobustRowScore(smallA, smallB, sw, sh, sh, sh - k, 0, k, colStep: 2, TrimWorstRowsPercent, staticMaskSmall);
            if (score < best)
            {
                second = best;
                best = score;
                bestK = k;
            }
            else if (score < second && k - bestK > 4)
            {
                second = score;
            }
        }

        if (bestK < 0)
        {
            return OverlapResult.Fail("no candidates");
        }
        double marginRatio = best > 1e-6 ? second / best : 100.0;
        if (best > CoarseScoreThreshold)
        {
            return OverlapResult.Fail($"coarse best {best:F2} too high");
        }
        if (marginRatio < MarginRatioThreshold)
        {
            return OverlapResult.Fail($"unambiguous? margin {marginRatio:F2}");
        }

        // ---- full-res refinement ----
        int center = bestK * DownscaleFactor;
        int rMin = Math.Max(1, center - 12);
        int rMax = Math.Min(height - 2, center + 12);
        double bestRef = double.MaxValue;
        int bestRefK = rMin;
        for (int k = rMin; k <= rMax; k++)
        {
            double score = RobustRowScore(grayA, grayB, width, height, height, height - k, 0, k, colStep: 3, TrimWorstRowsPercent, staticMask);
            if (score < bestRef)
            {
                bestRef = score;
                bestRefK = k;
            }
        }
        if (bestRef > RefineScoreThreshold)
        {
            return OverlapResult.Fail($"refine best {bestRef:F2} too high");
        }

        // ---- verification band (~1/3 depth of overlap) ----
        int bandOffset = Math.Max(1, bestRefK / 3);
        int bandRows = Math.Min(bestRefK / 2, 120);
        double bandDiff = RobustRowScore(grayA, grayB, width, height, height,
            height - bestRefK + bandOffset, bandOffset, bandRows, colStep: 4, trimPercent: 0, staticMask);
        if (bandDiff > bestRef * 2.5 + 4)
        {
            return OverlapResult.Fail($"band mismatch {bandDiff:F2} vs {bestRef:F2}");
        }

        // ---- confidence ----
        double simPart = Math.Max(0.0, 1.0 - bestRef / 50.0);
        double marginPart = Math.Min(1.0, Math.Max(0.0, (marginRatio - 1.0) / 0.5));
        double confidence = Math.Clamp(0.6 * simPart + 0.4 * marginPart, 0.0, 1.0);
        if (bandDiff > bestRef + 2 && bandDiff > 3)
        {
            confidence *= 0.75;
        }

        return new OverlapResult(true, bestRefK, confidence);
    }

    private static double EstimateContrast(byte[] gray)
    {
        long sum = 0;
        long sq = 0;
        int n = 0;
        for (int i = 0; i < gray.Length; i += 16)
        {
            sum += gray[i];
            sq += (long)gray[i] * gray[i];
            n++;
        }
        if (n == 0) return 0;
        double mean = sum / (double)n;
        return Math.Sqrt(Math.Max(0, sq / (double)n - mean * mean));
    }

    /// <summary>
    /// Rows that (a) barely differ between the two frames at the SAME absolute position
    /// (fixed chrome, floating widgets like WeChat's "jump to latest" button) and
    /// (b) carry contrast (not blank). Unlike real overlap rows 鈥?which only match when
    /// A's bottom aligns to B's top 鈥?these match at ANY candidate offset and would
    /// otherwise flatten the score curve, so they are excluded from the scoring rows.
    /// Masks are indexed by absolute row of frame B; the caller skips those rows.
    /// </summary>
    private static bool[] ComputeStaticMask(byte[] grayA, byte[] grayB, int width, int height)
    {
        var mask = new bool[height];
        for (int y = 0; y < height; y++)
        {
            long diff = 0;
            long aSum = 0;
            int n = 0;
            long aSq = 0;
            int rowA = y * width;
            int rowB = y * width;
            for (int x = 0; x < width; x += 3)
            {
                int va = grayA[rowA + x];
                int vb = grayB[rowB + x];
                diff += Math.Abs(va - vb);
                aSum += va;
                aSq += (long)va * va;
                n++;
            }
            if (n == 0) continue;
            double meanA = aSum / (double)n;
            double stdA = Math.Sqrt(Math.Max(0, aSq / (double)n - meanA * meanA));
            if (diff / (double)n <= StaticRowDiffThreshold && stdA >= StaticRowContrastMin)
            {
                mask[y] = true;
            }
        }
        return mask;
    }

    private static byte[] ToGrayBytes(BitmapSource source)
    {
        byte[] bgr32 = FrameSimilarity.ToBgr32Buffer(source);
        using var mat = Mat.FromPixelData(source.PixelHeight, source.PixelWidth, MatType.CV_8UC4, bgr32);
        using var gray = new Mat();
        Cv2.CvtColor(mat, gray, ColorConversionCodes.BGRA2GRAY);
        var result = new byte[gray.Rows * gray.Cols];
        Marshal.Copy(gray.Data, result, 0, result.Length);
        return result;
    }

    private static (byte[] small, int w, int h) Downscale(byte[] gray, int width, int height)
    {
        int sw = Math.Max(1, width / DownscaleFactor);
        int sh = Math.Max(1, height / DownscaleFactor);
        using var src = Mat.FromPixelData(height, width, MatType.CV_8UC1, gray);
        using var dst = new Mat(sh, sw, MatType.CV_8UC1);
        Cv2.Resize(src, dst, new Size(sw, sh), interpolation: InterpolationFlags.Area);
        var result = new byte[sh * sw];
        Marshal.Copy(dst.Data, result, 0, result.Length);
        return (result, sw, sh);
    }

    /// <summary>
    /// Compares rows: [aStart, aStart+count) of A against [bStart, bStart+count) of B.
    /// Rows flagged static by the mask (absolute row of B) are excluded.
    /// Robust: worst trimPercent% rows (dynamic content) are excluded before averaging.
    /// </summary>
    private static double RobustRowScore(
        byte[] a, byte[] b, int width,
        int aHeight, int bHeight,
        int aStart, int bStart, int count,
        int colStep, int trimPercent, bool[]? staticMaskB)
    {
        if (count <= 0 || aStart < 0 || bStart < 0)
        {
            return double.MaxValue;
        }
        int rows = Math.Min(count, Math.Min(aHeight - aStart, bHeight - bStart));
        if (rows <= 0)
        {
            return double.MaxValue;
        }

        var rowDiffs = new double[rows];
        int used = 0;
        for (int j = 0; j < rows; j++)
        {
            int bAbs = bStart + j;
            if (staticMaskB != null && bAbs >= 0 && bAbs < staticMaskB.Length && staticMaskB[bAbs])
            {
                continue; // fixed chrome 鈥?skip, it matches any offset
            }
            int aBase = (aStart + j) * width;
            int bBase = bAbs * width;
            long sum = 0;
            int cols = 0;
            for (int x = 0; x < width; x += colStep)
            {
                sum += Math.Abs(a[aBase + x] - b[bBase + x]);
                cols++;
            }
            rowDiffs[used++] = sum / (double)Math.Max(1, cols);
        }

        // If everything was masked (fully static content) -> indistinguishable, fail.
        int effectiveRows = used - (trimPercent > 0 ? used * trimPercent / 100 : 0);
        if (effectiveRows <= 0)
        {
            return double.MaxValue;
        }
        if (used < rowDiffs.Length)
        {
            var compact = new double[used];
            Array.Copy(rowDiffs, compact, used);
            rowDiffs = compact;
        }
        if (trimPercent > 0 && rowDiffs.Length > 1)
        {
            Array.Sort(rowDiffs);
        }
        double total = 0;
        for (int j = 0; j < Math.Min(effectiveRows, rowDiffs.Length); j++)
        {
            total += rowDiffs[j];
        }
        return total / effectiveRows;
    }
}

