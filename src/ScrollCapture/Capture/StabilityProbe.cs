using System.Runtime.InteropServices;
using System.Windows;
using ScrollCapture.Utils;
using ScrollCapture.Vision;

namespace ScrollCapture.Capture;

/// <summary>
/// Allocation-free screen sampler used by the scroll-stability detector.
/// Owns ONE DIB section + two pre-allocated byte buffers; every snapshot only
/// BitBlts and memcpys in place (no BitmapSource, no per-call byte arrays).
/// Compares with the same sampling logic as FrameSimilarity byte helpers.
/// </summary>
public sealed class StabilityProbe : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly byte[] _bufferA;
    private readonly byte[] _bufferB;
    private byte[] _active;
    private readonly IntPtr _dib;
    private readonly IntPtr _memDc;
    private readonly IntPtr _old;
    private readonly IntPtr _bits;
    private readonly IntPtr _screenDc;
    private bool _hasPrevious;

    public StabilityProbe(Int32Rect region)
    {
        _width = region.Width;
        _height = region.Height;
        _bufferA = new byte[_width * _height * 4];
        _bufferB = new byte[_width * _height * 4];
        _active = _bufferA;

        _screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (_screenDc == IntPtr.Zero)
        {
            throw new InvalidOperationException("GetDC failed");
        }
        _memDc = NativeMethods.CreateCompatibleDC(_screenDc);
        var bmi = new NativeMethods.BITMAPINFO
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = _width,
                biHeight = -_height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB,
            }
        };
        _dib = NativeMethods.CreateDIBSection(_memDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero)
        {
            throw new InvalidOperationException("CreateDIBSection failed");
        }
        _old = NativeMethods.SelectObject(_memDc, _dib);
    }

    /// <summary>Captures the strip; returns true when it changed since the previous snapshot.</summary>
    public bool Snapshot(Int32Rect region)
    {
        if (!NativeMethods.BitBlt(_memDc, 0, 0, _width, _height, _screenDc,
                region.X, region.Y, NativeMethods.SRCCOPY))
        {
            throw new InvalidOperationException("BitBlt failed in stability probe");
        }

        byte[] target = ReferenceEquals(_active, _bufferA) ? _bufferB : _bufferA;
        Marshal.Copy(_bits, target, 0, target.Length);

        bool changed;
        if (_hasPrevious)
        {
            changed = !FrameSimilarity.IsNearlyIdentical(_bufferA, _bufferB, _width, _height, _width, _height);
        }
        else
        {
            changed = true;
        }
        _active = target;
        _hasPrevious = true;
        return changed;
    }

    public void Dispose()
    {
        NativeMethods.SelectObject(_memDc, _old);
        NativeMethods.DeleteObject(_dib);
        NativeMethods.DeleteDC(_memDc);
        NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
    }
}
