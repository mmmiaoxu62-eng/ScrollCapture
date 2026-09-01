using System.Windows;

namespace ScrollCapture.Capture;

/// <summary>
/// Pure geometry/conversion helpers between OS physical pixels and logical (DIP-like) units.
/// Pure static math — fully unit-testable without touching the real screen.
/// </summary>
public static class DpiMath
{
    public static int SafeRound(double value) =>
        (int)Math.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>Logical -> physical along one axis.</summary>
    public static double ScaleUp(double logical, double scale) => logical * scale;

    /// <summary>Physical -> logical along one axis.</summary>
    public static double ScaleDown(double physical, double scale) => physical / scale;

    /// <summary>
    /// Maps a logical-space rectangle (e.g. drawn by the overlay) to the physical pixel rect to capture.
    /// </summary>
    public static System.Windows.Int32Rect ToPhysicalRect(Rect logicalRect, Int32Rect virtualScreenPhysical, double scale)
    {
        int x = SafeRound(virtualScreenPhysical.X + logicalRect.X * scale);
        int y = SafeRound(virtualScreenPhysical.Y + logicalRect.Y * scale);
        int w = SafeRound(logicalRect.Width * scale);
        int h = SafeRound(logicalRect.Height * scale);
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        return new System.Windows.Int32Rect(x, y, w, h);
    }

    /// <summary>Normalizes two points into a rectangle (handles any drag direction).</summary>
    public static Rect Normalize(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X);
        double y = Math.Min(a.Y, b.Y);
        return new Rect(x, y, Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    }
}
