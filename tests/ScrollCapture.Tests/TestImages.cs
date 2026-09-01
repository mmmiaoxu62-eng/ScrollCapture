using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Vision;

namespace ScrollCapture.Tests;

/// <summary>
/// Deterministic synthetic image factory: builds a tall "virtual page" (unique per-row pattern),
/// frames are slices of it with known offsets => known overlaps. No real screenshots needed.
/// </summary>
internal static class TestImages
{
    public static byte[] CreateLongBuffer(int width, int height)
    {
        // Each row gets its OWN seeded RNG => rows are vertically uncorrelated
        // (no accidental periodicity; any false alignment produces a large diff).
        var buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            var random = new Random(y * 7919 + 17);
            int rowBase = y * width;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x * 4;
                buffer[idx] = (byte)random.Next(256);     // B
                buffer[idx + 1] = (byte)random.Next(256); // G
                buffer[idx + 2] = (byte)random.Next(256); // R
                buffer[idx + 3] = 255;
            }
        }
        return buffer;
    }

    public static BitmapSource Slice(byte[] longBuffer, int width, int frameHeight, int yStart)
    {
        if (yStart + frameHeight > longBuffer.Length / (width * 4))
        {
            throw new ArgumentOutOfRangeException(nameof(yStart));
        }
        int stride = width * 4;
        var frame = new byte[stride * frameHeight];
        Buffer.BlockCopy(longBuffer, yStart * stride, frame, 0, stride * frameHeight);
        return CreateBgr32(frame, width, frameHeight);
    }

    public static BitmapSource CreateBgr32(byte[] bgr32, int width, int height)
    {
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgr32, null, bgr32, width * 4);
        source.Freeze();
        return source;
    }

    public static BitmapSource CreateSolid(int width, int height, byte gray)
    {
        var buffer = new byte[width * height * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = gray;
            buffer[i + 1] = gray;
            buffer[i + 2] = gray;
            buffer[i + 3] = 255;
        }
        return CreateBgr32(buffer, width, height);
    }

    public static BitmapSource CreateNoise(int width, int height, int seed)
    {
        var buffer = new byte[width * height * 4];
        var random = new Random(seed);
        random.NextBytes(buffer);
        return CreateBgr32(buffer, width, height);
    }

    public static BitmapSource AddNoise(BitmapSource source, int seed, int amplitude)
    {
        byte[] buffer = FrameSimilarity.ToBgr32Buffer(source);
        var random = new Random(seed);
        for (int i = 0; i < buffer.Length; i += 4)
        {
            int n = random.Next(-amplitude, amplitude + 1);
            buffer[i] = (byte)Math.Clamp(buffer[i] + n, 0, 255);
            buffer[i + 1] = (byte)Math.Clamp(buffer[i + 1] + n, 0, 255);
            buffer[i + 2] = (byte)Math.Clamp(buffer[i + 2] + n, 0, 255);
        }
        return CreateBgr32(buffer, source.PixelWidth, source.PixelHeight);
    }

}
