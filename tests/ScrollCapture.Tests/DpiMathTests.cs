using System.Windows;
using ScrollCapture.Capture;

namespace ScrollCapture.Tests;

public class DpiMathTests
{
    public static IEnumerable<object[]> ScaleFactors =>
        new object[][] { new object[] { 1.0 }, new object[] { 1.25 }, new object[] { 1.5 }, new object[] { 1.75 }, new object[] { 2.0 }, new object[] { 3.0 } };

    [Theory]
    [MemberData(nameof(ScaleFactors))]
    public void RoundTrip_LogicalToPhysicalToLogical(double scale)
    {
        const double logical = 500.27;
        double physical = DpiMath.ScaleUp(logical, scale);
        Assert.Equal(logical, DpiMath.ScaleDown(physical, scale), 6);
    }

    [Theory]
    [MemberData(nameof(ScaleFactors))]
    public void ToPhysicalRect_RegularVirtualScreen(double scale)
    {
        var virtualScreen = new Int32Rect(0, 0, 1920, 1080);
        var rect = DpiMath.ToPhysicalRect(new Rect(100.4, 99.6, 640, 480), virtualScreen, scale);

        Assert.Equal(DpiMath.SafeRound(100.4 * scale), rect.X);
        Assert.Equal(DpiMath.SafeRound(99.6 * scale), rect.Y);
        Assert.Equal(DpiMath.SafeRound(640 * scale), rect.Width);
        Assert.Equal(DpiMath.SafeRound(480 * scale), rect.Height);
        Assert.True(rect.Width > 0 && rect.Height > 0);
    }

    [Theory]
    [MemberData(nameof(ScaleFactors))]
    public void ToPhysicalRect_SecondaryMonitorNegativeOrigin(double scale)
    {
        // A second monitor placed left of the primary => negative virtual-origin.
        var virtualScreen = new Int32Rect(-1920, 0, 3840, 1080);
        var rect = DpiMath.ToPhysicalRect(new Rect(0.16, 0.16, 100, 100), virtualScreen, scale);

        Assert.Equal(-1920, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(DpiMath.SafeRound(100 * scale), rect.Width);
        Assert.Equal(DpiMath.SafeRound(100 * scale), rect.Height);
    }

    [Fact]
    public void ToPhysicalRect_ClampsTinySelectionToOnePixel()
    {
        var virtualScreen = new Int32Rect(0, 0, 1920, 1080);
        var rect = DpiMath.ToPhysicalRect(new Rect(10, 10, 0.1, 0.1), virtualScreen, 1.25);
        Assert.True(rect.Width >= 1 && rect.Height >= 1);
    }

    [Fact]
    public void Normalize_HandlesReverseDrag()
    {
        var r = DpiMath.Normalize(new Point(300, 400), new Point(100, 200));
        Assert.Equal(100, r.X);
        Assert.Equal(200, r.Y);
        Assert.Equal(200, r.Width);
        Assert.Equal(200, r.Height);
    }

    [Fact]
    public void Normalize_HandlesOrderIndependent()
    {
        var a = DpiMath.Normalize(new Point(1, 1), new Point(9, 5));
        var b = DpiMath.Normalize(new Point(9, 5), new Point(1, 1));
        Assert.Equal(a, b);
    }
}
