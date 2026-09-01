using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScrollCapture.Vision;

/// <summary>
/// Cheap frame comparison used for stop detection ("has the page stopped moving?").
/// Samples every Nth pixel; two frames are "nearly identical" when nearly all sampled
/// pixels match within a small tolerance (animations/GIFs aside, Phase 4 will harden this).
/// </summary>
public static class FrameSimilarity
{
    public const int SampleStep = 8;
    private const int ChannelTolerance = 6;
    private const double AllowedMismatchRatio = 0.005;

    public static bool IsNearlyIdentical(BitmapSource a, BitmapSource b)
    {
        byte[] aa = ToBgr32Buffer(a);
        byte[] bb = ToBgr32Buffer(b);
        return IsNearlyIdentical(aa, bb, a.PixelWidth, a.PixelHeight, b.PixelWidth, b.PixelHeight);
    }

    public static bool IsNearlyIdentical(byte[] a, byte[] b, int widthA, int heightA, int widthB, int heightB)
    {
        if (widthA != widthB || heightA != heightB)
        {
            return false;
        }

        int stride = widthA * 4;
        int mismatched = 0;
        long total = 0;

        for (int y = 0; y < heightA; y += SampleStep)
        {
            int rowOffset = y * stride;
            for (int x = 0; x < widthA; x += SampleStep)
            {
                int idx = rowOffset + x * 4;
                int diff = Math.Abs(a[idx] - b[idx])
                         + Math.Abs(a[idx + 1] - b[idx + 1])
                         + Math.Abs(a[idx + 2] - b[idx + 2]);
                if (diff > ChannelTolerance)
                {
                    mismatched++;
                }
                total++;
            }
        }

        if (total == 0)
        {
            return true;
        }
        return mismatched / (double)total <= AllowedMismatchRatio;
    }

    /// <summary>
    /// Fraction of rows that actually changed between two frames at the SAME absolute
    /// position (sampled). True scrolling changes most rows; a static window with only
    /// tiny animations stays near zero. Used as the "did the page move at all" gate
    /// BEFORE overlap matching — stale/static content must never be pasted.
    /// </summary>
    public static double ComputeMotionFraction(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
        {
            return 1.0;
        }
        byte[] aa = ToBgr32Buffer(a);
        byte[] bb = ToBgr32Buffer(b);
        int width = a.PixelWidth;
        int height = a.PixelHeight;
        int stride = width * 4;
        const double rowDiffThreshold = 4.0;
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
                sum += Math.Abs(aa[idx] - bb[idx])
                     + Math.Abs(aa[idx + 1] - bb[idx + 1])
                     + Math.Abs(aa[idx + 2] - bb[idx + 2]);
                n++;
            }
            double avg = sum / (double)Math.Max(1, n * 3);
            if (avg > rowDiffThreshold)
            {
                changedRows++;
            }
            totalRows++;
        }
        return totalRows == 0 ? 0 : changedRows / (double)totalRows;
    }

    public static byte[] ToBgr32Buffer(BitmapSource source)
    {
        if (source.Format != PixelFormats.Bgr32)
        {
            var converted = new FormatConvertedBitmap();
            converted.BeginInit();
            converted.Source = source;
            converted.DestinationFormat = PixelFormats.Bgr32;
            converted.EndInit();
            source = converted;
        }

        int stride = source.PixelWidth * 4;
        var buffer = new byte[stride * source.PixelHeight];
        source.CopyPixels(buffer, stride, 0);
        return buffer;
    }
}
