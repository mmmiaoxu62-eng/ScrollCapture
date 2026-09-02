using System.Windows.Media.Imaging;

namespace ScrollCapture.Vision;

/// <summary>
/// Vertical "column band" motion analysis. A captured region may mix fixed UI
/// (static sidebar, non-scrolling panel) with a genuinely scrolling column.
/// Whole-frame statistics get diluted by the static part — so the page "looks"
/// stopped while the scrollable column is still moving. These helpers classify
/// which horizontal bands actually move, so scroll/stop decisions can be driven
/// by the moving part only. All math is on raw Bgr32 byte buffers.
/// </summary>
public static class ColumnMotion
{
    public const int BandCount = 8;

    public static bool[] EmptyMask => new bool[BandCount];

    /// <summary>Changed-row fraction a band needs to count as scroll-driving.</summary>
    public const double BandDrivingThreshold = 0.05;

    // --- byte[] API (primary, fully deterministic) ---

    public static double[] ComputeBandMotion(byte[] a, byte[] b, int width, int height)
    {
        if (a.Length < width * height * 4 || b.Length < width * height * 4)
        {
            throw new ArgumentException("buffer too small");
        }
        int stride = width * 4;
        int bandWidth = Math.Max(1, width / BandCount);
        var changed = new int[BandCount];
        var total = new int[BandCount];

        for (int y = 0; y < height; y += 2)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += 8)
            {
                int band = Math.Min(BandCount - 1, x / bandWidth);
                int idx = row + x * 4;
                long sum = Math.Abs(a[idx] - b[idx])
                         + Math.Abs(a[idx + 1] - b[idx + 1])
                         + Math.Abs(a[idx + 2] - b[idx + 2]);
                total[band]++;
                if (sum / 3.0 > 4.0)
                {
                    changed[band]++;
                }
            }
        }

        var result = new double[BandCount];
        for (int i = 0; i < BandCount; i++)
        {
            result[i] = total[i] == 0 ? 0 : changed[i] / (double)total[i];
        }
        return result;
    }

    public static bool[] ClassifyDrivingBands(byte[] a, byte[] b, int width, int height)
    {
        double[] motion = ComputeBandMotion(a, b, width, height);
        var mask = new bool[BandCount];
        for (int i = 0; i < BandCount; i++)
        {
            mask[i] = motion[i] >= BandDrivingThreshold;
        }
        return mask;
    }

    /// <summary>
    /// True when a band carries actual UI content (contrast), as opposed to plain
    /// empty background. Only contrasty static bands should be blanked post-stitch —
    /// background strips stay untouched (they repeat naturally & harmlessly).
    /// </summary>
    public static bool[] BandHasContent(byte[] a, int width, int height)
    {
        int bandWidth = Math.Max(1, width / BandCount);
        int stride = width * 4;
        var result = new bool[BandCount];
        for (int b = 0; b < BandCount; b++)
        {
            int x0 = b * bandWidth;
            int x1 = Math.Min(width, x0 + bandWidth);
            long sum = 0;
            long sq = 0;
            long n = 0;
            for (int y = 0; y < height; y += 4)
            {
                int row = y * stride;
                for (int x = x0; x < x1; x += 4)
                {
                    // luminance of the first channel triple as a proxy sample
                    int v = a[row + x * 4];
                    sum += v;
                    sq += (long)v * v;
                    n++;
                }
            }
            if (n == 0)
            {
                result[b] = false;
                continue;
            }
            double mean = sum / (double)n;
            double std = Math.Sqrt(Math.Max(0, sq / (double)n - mean * mean));
            result[b] = std >= 6.0;
        }
        return result;
    }

    public static bool[] BandHasContent(BitmapSource a)
    {
        byte[] ba = FrameSimilarity.ToBgr32Buffer(a);
        return BandHasContent(ba, a.PixelWidth, a.PixelHeight);
    }

    public static double ComputeDrivenMotionFraction(byte[] a, byte[] b, int width, int height, bool[]? bandMask)
    {
        if (bandMask == null)
        {
            return ComputeWholeMotion(a, b, width, height);
        }
        int stride = width * 4;
        int bandWidth = Math.Max(1, width / BandCount);
        int changedRows = 0;
        int totalRows = 0;
        for (int y = 0; y < height; y += 2)
        {
            long sum = 0;
            int n = 0;
            int row = y * stride;
            for (int x = 0; x < width; x += 8)
            {
                int band = Math.Min(BandCount - 1, x / bandWidth);
                if (!bandMask[band])
                {
                    continue;
                }
                int idx = row + x * 4;
                sum += Math.Abs(a[idx] - b[idx])
                     + Math.Abs(a[idx + 1] - b[idx + 1])
                     + Math.Abs(a[idx + 2] - b[idx + 2]);
                n++;
            }
            if (n > 0)
            {
                totalRows++;
                if (sum / (double)(n * 3) > 4.0)
                {
                    changedRows++;
                }
            }
        }
        return totalRows == 0 ? 0 : changedRows / (double)totalRows;
    }

    private static double ComputeWholeMotion(byte[] a, byte[] b, int width, int height)
    {
        int stride = width * 4;
        int changedRows = 0;
        int totalRows = 0;
        for (int y = 0; y < height; y += 2)
        {
            long sum = 0;
            int n = 0;
            int row = y * stride;
            for (int x = 0; x < width; x += 8)
            {
                int idx = row + x * 4;
                sum += Math.Abs(a[idx] - b[idx])
                     + Math.Abs(a[idx + 1] - b[idx + 1])
                     + Math.Abs(a[idx + 2] - b[idx + 2]);
                n++;
            }
            if (n > 0)
            {
                totalRows++;
                if (sum / (double)(n * 3) > 4.0)
                {
                    changedRows++;
                }
            }
        }
        return totalRows == 0 ? 0 : changedRows / (double)totalRows;
    }

    // --- BitmapSource adapters ---

    public static double[] ComputeBandMotion(BitmapSource a, BitmapSource b)
    {
        byte[] ba = FrameSimilarity.ToBgr32Buffer(a);
        byte[] bb = FrameSimilarity.ToBgr32Buffer(b);
        return ComputeBandMotion(ba, bb, a.PixelWidth, a.PixelHeight);
    }

    public static bool[] ClassifyDrivingBands(BitmapSource a, BitmapSource b)
    {
        byte[] ba = FrameSimilarity.ToBgr32Buffer(a);
        byte[] bb = FrameSimilarity.ToBgr32Buffer(b);
        return ClassifyDrivingBands(ba, bb, a.PixelWidth, a.PixelHeight);
    }

    public static double ComputeDrivenMotionFraction(BitmapSource a, BitmapSource b, bool[]? bandMask)
    {
        byte[] ba = FrameSimilarity.ToBgr32Buffer(a);
        byte[] bb = FrameSimilarity.ToBgr32Buffer(b);
        return ComputeDrivenMotionFraction(ba, bb, a.PixelWidth, a.PixelHeight, bandMask);
    }

    /// <summary>Expands a band mask to a per-column mask indexed by x (x in 0..width).</summary>
    public static bool[] ToColumnMask(bool[]? bandMask, int width)
    {
        var columns = new bool[width];
        if (bandMask == null)
        {
            Array.Fill(columns, true);
            return columns;
        }
        int bandWidth = Math.Max(1, width / BandCount);
        for (int x = 0; x < width; x++)
        {
            int band = Math.Min(BandCount - 1, x / bandWidth);
            columns[x] = bandMask[band];
        }
        return columns;
    }
}
