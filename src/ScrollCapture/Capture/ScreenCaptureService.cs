using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Utils;

namespace ScrollCapture.Capture;

/// <summary>
/// Captures a physical-pixel rectangle of the screen via GDI (DC + CreateDIBSection + BitBlt).
/// Coordinates must be physical pixels of the virtual desktop (process is PerMonitorV2 aware).
/// </summary>
public static class ScreenCaptureService
{
    private const int MaxDimensionPx = 60000;

    public static BitmapSource Capture(Int32Rect physicalRect)
    {
        int width = physicalRect.Width;
        int height = physicalRect.Height;
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException("Capture rect has non-positive size", nameof(physicalRect));
        }
        if (width > MaxDimensionPx || height > MaxDimensionPx)
        {
            throw new ArgumentException($"Capture rect too large ({width}x{height})", nameof(physicalRect));
        }

        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC(0) failed — cannot access screen.");
        }

        IntPtr memDc = IntPtr.Zero;
        IntPtr bitmap = IntPtr.Zero;
        IntPtr old = IntPtr.Zero;
        IntPtr bits = IntPtr.Zero;
        try
        {
            memDc = NativeMethods.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateCompatibleDC failed.");
            }

            var bmi = new NativeMethods.BITMAPINFO
            {
                bmiHeader = new NativeMethods.BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // negative => top-down rows
                    biPlanes = 1,
                    biBitCount = 32,    // BgrX
                    biCompression = NativeMethods.BI_RGB,
                }
            };

            bitmap = NativeMethods.CreateDIBSection(memDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                throw new InvalidOperationException("CreateDIBSection failed.");
            }

            old = NativeMethods.SelectObject(memDc, bitmap);

            // CAPTUREBLT so layered/composited windows appear as on screen.
            bool ok = NativeMethods.BitBlt(memDc, 0, 0, width, height, screenDc,
                physicalRect.X, physicalRect.Y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);
            if (!ok)
            {
                throw new InvalidOperationException($"BitBlt failed (rect {physicalRect}).");
            }

            int stride = width * 4;
            var pixels = new byte[stride * height];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            var source = BitmapSource.Create(width, height, 96.0, 96.0, PixelFormats.Bgr32, null, pixels, stride);
            source.Freeze(); // safe for cross-thread use
            return source;
        }
        finally
        {
            if (memDc != IntPtr.Zero && old != IntPtr.Zero)
            {
                NativeMethods.SelectObject(memDc, old);
            }
            if (bitmap != IntPtr.Zero)
            {
                NativeMethods.DeleteObject(bitmap);
            }
            if (memDc != IntPtr.Zero)
            {
                NativeMethods.DeleteDC(memDc);
            }
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
