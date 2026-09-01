using System.Windows;
using System.Windows.Media.Imaging;
using ScrollCapture.Capture;

namespace ScrollCapture.Tests;

/// <summary>
/// Real-screen smoke tests. They run on an interactive desktop, not headless CI.
/// </summary>
public class ScreenCaptureTests
{
    [Fact]
    public void DpiManager_GetVirtualScreenPhysical_IsPositive()
    {
        Int32Rect r = DpiManager.GetVirtualScreenPhysical();
        Assert.True(r.Width > 0);
        Assert.True(r.Height > 0);
    }

    [Fact]
    public void DpiManager_GetMonitors_FindsAtLeastPrimary()
    {
        IReadOnlyList<DpiManager.MonitorInfo> monitors = DpiManager.GetMonitors();
        Assert.NotEmpty(monitors);
        Assert.Contains(monitors, m => m.IsPrimary);
        Assert.All(monitors, m =>
        {
            Assert.True(m.PhysicalBounds.Width > 0);
            Assert.True(m.PhysicalBounds.Height > 0);
            Assert.True(m.Scale >= 1.0 && m.Scale <= 4.0, $"unexpected scale {m.Scale}");
        });
    }

    [Fact]
    public void Capture_SmallRegionOfPrimaryMonitor_ReturnsRequestedSize()
    {
        DpiManager.MonitorInfo primary = DpiManager.GetMonitors().First(m => m.IsPrimary);
        int w = Math.Min(64, primary.PhysicalBounds.Width);
        int h = Math.Min(64, primary.PhysicalBounds.Height);
        Assert.True(w > 0 && h > 0);

        BitmapSource bmp = ScreenCaptureService.Capture(new Int32Rect(primary.PhysicalBounds.X, primary.PhysicalBounds.Y, w, h));
        Assert.NotNull(bmp);
        Assert.Equal(w, bmp.PixelWidth);
        Assert.Equal(h, bmp.PixelHeight);
        Assert.Equal(System.Windows.Media.PixelFormats.Bgr32, bmp.Format);
        Assert.True(bmp.IsFrozen, "bitmap must be frozen for cross-thread use");
    }

    [Fact]
    public void Capture_InvalidRect_Throws()
    {
        Assert.Throws<ArgumentException>(() => ScreenCaptureService.Capture(new Int32Rect(0, 0, 0, 10)));
        Assert.Throws<ArgumentException>(() => ScreenCaptureService.Capture(new Int32Rect(0, 0, -5, 10)));
    }
}
