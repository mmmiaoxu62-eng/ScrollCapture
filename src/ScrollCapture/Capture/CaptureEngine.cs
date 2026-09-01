using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScrollCapture.Utils;

namespace ScrollCapture.Capture;

public enum CaptureChannelKind
{
    BitBlt,       // screen copy (includes composited overlays: cursor, our windows)
    PrintWindow,  // window-content render (no cursor/overlay pollution) — Chromium/UWP friendly via PW_RENDERFULLCONTENT
}

/// <summary>
/// Region capture for a scrolling session, with channel strategy:
///   Auto / PrintWindow -> PrintWindow(PW_RENDERFULLCONTENT) of the target window,
///   blank/black or failure -> automatic fallback to screen BitBlt of the region.
/// The printed window bitmap is cropped region-by-window mapping and supports
/// render-vs-window scale mismatches (remote desktop virtualization etc).
/// </summary>
public sealed class CaptureEngine
{
    private readonly IntPtr _targetHwnd;
    private readonly CaptureChannelKind _mode;

    public int FallbackCount { get; private set; }
    public CaptureChannelKind LastChannel { get; private set; } = CaptureChannelKind.BitBlt;
    public IReadOnlyList<string> Notes { get; } = new List<string>();

    public CaptureEngine(IntPtr targetHwnd, CaptureChannelKind mode = CaptureChannelKind.PrintWindow)
    {
        _targetHwnd = targetHwnd;
        _mode = mode;
    }

    public BitmapSource Capture(Int32Rect regionPhysical)
    {
        if (_mode == CaptureChannelKind.PrintWindow && _targetHwnd != IntPtr.Zero)
        {
            try
            {
                BitmapSource result = CaptureViaPrintWindow(_targetHwnd, regionPhysical);
                LastChannel = CaptureChannelKind.PrintWindow;
                return result;
            }
            catch (Exception ex)
            {
                FallbackCount++;
                ((List<string>)Notes).Add($"PW failed: {ex.Message}");
                Logger.Warn($"PrintWindow capture failed ({ex.Message}); falling back to BitBlt");
            }
        }
        LastChannel = CaptureChannelKind.BitBlt;
        return ScreenCaptureService.Capture(regionPhysical);
    }

    internal static BitmapSource CaptureViaPrintWindow(IntPtr hwnd, Int32Rect regionPhysical)
    {
        NativeMethods.RECT bounds = GetExtendedFrameBounds(hwnd);
        if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top)
        {
            throw new InvalidOperationException("window bounds invalid");
        }
        int winW = bounds.Right - bounds.Left;
        int winH = bounds.Bottom - bounds.Top;
        if (winW <= 0 || winH <= 0 || winW > 20000 || winH > 20000)
        {
            throw new InvalidOperationException($"window size out of range {winW}x{winH}");
        }

        using (BitmapSurface surface = BitmapSurface.Create(winW, winH))
        {
            if (!NativeMethods.PrintWindow(hwnd, surface.Dc, NativeMethods.PW_RENDERFULLCONTENT))
            {
                throw new InvalidOperationException($"PrintWindow failed: {Marshal.GetLastWin32Error()}");
            }

            // verify content is not blank/black
            if (surface.IsBlank())
            {
                throw new InvalidOperationException("PrintWindow returned blank content (app does not support WM_PRINT)");
            }

            // crop region (physical screen coords) -> window-relative -> printed bitmap.
            // The printed surface is exactly winW x winH, so mapping is 1:1; the
            // region only needs clamping into the window bounds.
            int relX = regionPhysical.X - bounds.Left;
            int relY = regionPhysical.Y - bounds.Top;
            int cropX = Math.Clamp(relX, 0, winW - 1);
            int cropY = Math.Clamp(relY, 0, winH - 1);
            int cropW = Math.Min(regionPhysical.Width, winW - cropX);
            int cropH = Math.Min(regionPhysical.Height, winH - cropY);
            if (cropW <= 0 || cropH <= 0)
            {
                throw new InvalidOperationException("region outside window");
            }

            byte[] printed = surface.CopyToBytes();
            int stride = winW * 4;
            var cropped = new byte[cropW * cropH * 4];
            for (int y = 0; y < cropH; y++)
            {
                Buffer.BlockCopy(printed, (cropY + y) * stride + cropX * 4, cropped, y * cropW * 4, cropW * 4);
            }
            BitmapSource result = BitmapSource.Create(cropW, cropH, 96, 96, PixelFormats.Bgr32, null, cropped, cropW * 4);
            result.Freeze();
            return result;
        }
    }

    /// <summary>Blank/black-content detection (PrintWindow renders nothing for some apps).</summary>
    internal static bool IsBlankContent(byte[] bgr32)
    {
        long sum = 0;
        long sq = 0;
        int n = 0;
        for (int i = 0; i < bgr32.Length; i += 64)
        {
            sum += bgr32[i];
            sq += (long)bgr32[i] * bgr32[i];
            n++;
        }
        if (n == 0) return true;
        double mean = sum / (double)n;
        double std = Math.Sqrt(Math.Max(0, sq / (double)n - mean * mean));
        return std < 1.2;
    }

    internal static NativeMethods.RECT GetExtendedFrameBounds(IntPtr hwnd)
    {
        NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT rect,
            Marshal.SizeOf<NativeMethods.RECT>());
        return rect;
    }

    private sealed class BitmapSurface : IDisposable
    {
        private readonly IntPtr _dc;
        private readonly IntPtr _bitmap;
        private readonly IntPtr _old;
        private readonly IntPtr _bits;
        private readonly int _width;
        private readonly int _height;

        public IntPtr Dc => _dc;

        private BitmapSurface(IntPtr dc, IntPtr bitmap, IntPtr old, IntPtr bits, int width, int height)
        {
            _dc = dc;
            _bitmap = bitmap;
            _old = old;
            _bits = bits;
            _width = width;
            _height = height;
        }

        public static BitmapSurface Create(int width, int height)
        {
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            try
            {
                IntPtr dc = NativeMethods.CreateCompatibleDC(screenDc);
                if (dc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed");

                var bmi = new NativeMethods.BITMAPINFO
                {
                    bmiHeader = new NativeMethods.BITMAPINFOHEADER
                    {
                        biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = NativeMethods.BI_RGB,
                    }
                };
                IntPtr bitmap = NativeMethods.CreateDIBSection(dc, ref bmi, NativeMethods.DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
                if (bitmap == IntPtr.Zero)
                {
                    NativeMethods.DeleteDC(dc);
                    throw new InvalidOperationException("CreateDIBSection failed");
                }
                IntPtr old = NativeMethods.SelectObject(dc, bitmap);
                return new BitmapSurface(dc, bitmap, old, bits, width, height);
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        public bool IsBlank() => IsBlankContent(CopyToBytes());

        public byte[] CopyToBytes()
        {
            var buffer = new byte[_width * _height * 4];
            Marshal.Copy(_bits, buffer, 0, buffer.Length);
            return buffer;
        }

        public void Dispose()
        {
            NativeMethods.SelectObject(_dc, _old);
            NativeMethods.DeleteObject(_bitmap);
            NativeMethods.DeleteDC(_dc);
        }
    }
}
