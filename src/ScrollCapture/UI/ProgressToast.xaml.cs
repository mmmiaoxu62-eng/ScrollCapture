using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScrollCapture.Capture;
using ScrollCapture.Utils;

namespace ScrollCapture.UI;

/// <summary>
/// Tiny floating indicator shown during an auto-capture session.
/// - Never activated (does not steal focus from the scrolled window).
/// - Excluded from screen captures (WDA_EXCLUDEFROMCAPTURE).
/// - Click to stop. Positioned just above the capture region when possible.
/// </summary>
public partial class ProgressToast : Window
{
    public event Action? StopRequested;

    public ProgressToast()
    {
        InitializeComponent();
        Left = 0;
        Top = 0;
    }

    public void PositionAbove(Int32Rect regionPhysical, Int32Rect virtualScreenPhysical)
    {
        bool visible = false;
        try
        {
            // Only show if we can calculate a scale for the anchor point.
            var anchor = new Point(regionPhysical.X + regionPhysical.Width / 2.0, regionPhysical.Y);
            double scale = DpiManager.GetScaleForPhysicalPoint(anchor);

            double dipWidth = Width;
            double dipHeight = Height;
            double targetX = (regionPhysical.X + regionPhysical.Width / 2.0) / scale - dipWidth / 2.0;
            double targetY = regionPhysical.Y / scale - dipHeight - 10;

            double vsLeft = virtualScreenPhysical.X / scale;
            double vsTop = virtualScreenPhysical.Y / scale;
            double vsRight = (virtualScreenPhysical.X + virtualScreenPhysical.Width) / scale;
            double vsBottom = (virtualScreenPhysical.Y + virtualScreenPhysical.Height) / scale;

            if (targetY < vsTop + 4)
            {
                // No room above — place it at the top-right inside the region's monitor,
                // kept outside the selection when possible.
                targetY = vsTop + 8;
                targetX = (virtualScreenPhysical.X + virtualScreenPhysical.Width) / scale - dipWidth - 8;
            }

            Left = Math.Max(vsLeft + 4, Math.Min(targetX, vsRight - dipWidth - 4));
            Top = Math.Max(vsTop + 4, Math.Min(targetY, vsBottom - dipHeight - 4));
            visible = true;
        }
        catch
        {
            // fall through — window stays at 0,0 (still functional as a click target)
        }
        _ = visible;

        Show();
    }

    public void Update(string text) => StatusText.Text = text;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
    }

    private void OnClickStop(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        StopRequested?.Invoke();
    }
}
