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
