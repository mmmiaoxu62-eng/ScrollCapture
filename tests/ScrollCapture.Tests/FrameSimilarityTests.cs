using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

public class FrameSimilarityTests
{
    private static byte[] MakeBuffer(int width, int height, byte r, byte g, byte b)
    {
        var buffer = new byte[width * height * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = b;
            buffer[i + 1] = g;
            buffer[i + 2] = r;
            buffer[i + 3] = 255;
        }
        return buffer;
    }

    private static BitmapSource ToBgr32Bitmap(byte[] buffer, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, buffer, width * 4);
        source.Freeze();
        return source;
    }

    private static byte[] ChangeOnePixel(byte[] buffer, int offset, byte r, byte g, byte b)
    {
        var copy = (byte[])buffer.Clone();
        copy[offset] = b;
        copy[offset + 1] = g;
        copy[offset + 2] = r;
        return copy;
    }

    [Fact]
    public void IdenticalArrays_AreNearlyIdentical()
    {
        byte[] a = MakeBuffer(128, 128, 100, 90, 80);
        byte[] b = MakeBuffer(128, 128, 100, 90, 80);
        Assert.True(FrameSimilarity.IsNearlyIdentical(a, b, 128, 128, 128, 128));
    }

    [Fact]
    public void SlightlyTonedColors_AreNearlyIdentical()
    {
        // antialias-level differences (diff sum <= 6) must be tolerated
        byte[] a = MakeBuffer(128, 128, 100, 90, 80);
        byte[] b = MakeBuffer(128, 128, 101, 91, 82);
        Assert.True(FrameSimilarity.IsNearlyIdentical(a, b, 128, 128, 128, 128));
    }

    [Fact]
    public void CompletelyDifferent_NotIdentical()
    {
        byte[] a = MakeBuffer(128, 128, 0, 0, 0);
        byte[] b = MakeBuffer(128, 128, 200, 200, 200);
        Assert.False(FrameSimilarity.IsNearlyIdentical(a, b, 128, 128, 128, 128));
    }

    [Fact]
    public void DifferentSizesAreNotIdentical()
    {
        byte[] a = MakeBuffer(128, 128, 5, 5, 5);
        byte[] b = MakeBuffer(128, 64, 5, 5, 5);
        Assert.False(FrameSimilarity.IsNearlyIdentical(a, b, 128, 128, 128, 64));
    }

    [Fact]
    public void BitmapSources_Identical_True()
    {
        byte[] buf = MakeBuffer(64, 64, 12, 34, 56);
        BitmapSource a = ToBgr32Bitmap(buf, 64, 64);
        BitmapSource b = ToBgr32Bitmap(buf, 64, 64);
        Assert.True(FrameSimilarity.IsNearlyIdentical(a, b));
    }

    [Fact]
    public void BitmapSources_FormatConvertBgra32_StillWorks()
    {
        byte[] buf = MakeBuffer(64, 64, 12, 34, 56);
        var a = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgr32, null, buf, 64 * 4);
        var b = BitmapSource.Create(64, 64, 96, 96, PixelFormats.Bgra32, null, buf, 64 * 4);
        a.Freeze();
        b.Freeze();
        Assert.True(FrameSimilarity.IsNearlyIdentical(a, b));
    }

    [Fact]
    public void MotionFraction_StaticVsScrolled()
    {
        byte[] buf = MakeBuffer(128, 128, 100, 100, 100);
        BitmapSource a = ToBgr32Bitmap(buf, 128, 128);
        BitmapSource b = ToBgr32Bitmap(buf, 128, 128);
        Assert.Equal(0.0, FrameSimilarity.ComputeMotionFraction(a, b), 2);

        byte[] b2 = MakeBuffer(128, 128, 100, 100, 100);
        for (int y = 0; y < 128; y += 2)
        {
            for (int x = 0; x < 128; x += 8)
            {
                int idx = (y * 128 + x) * 4;
                b2[idx] = 30; b2[idx + 1] = 40; b2[idx + 2] = 50;
            }
        }
        BitmapSource c = ToBgr32Bitmap(b2, 128, 128);
        double frac = FrameSimilarity.ComputeMotionFraction(a, c);
        Assert.True(frac > 0.9, $"scrolled-like frames should move: {frac:F2}");
    }

    [Fact]
    public void SinglePixelChangedByLargeSum_StillTolerated()
    {
        // one sampled region (8x8 area) fully recolored -> mismatched ratio ~ 1/(16*16) = 0.39% < 0.5%
        byte[] a = MakeBuffer(128, 128, 100, 100, 100);
        int k = (3 * 8 * 4) + (3 * 8 * 128 * 4); // row 24, col 24 - inside one sample cell
        byte[] b = ChangeOnePixel(a, k, 10, 10, 10);
        Assert.True(FrameSimilarity.IsNearlyIdentical(a, b, 128, 128, 128, 128));
    }
}
