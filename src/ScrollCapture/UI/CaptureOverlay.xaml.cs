using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ScrollCapture.Capture;
using ScrollCapture.Utils;

namespace ScrollCapture.UI;

/// <summary>
/// Full virtual-desktop overlay for drag-selecting a capture region.
/// Spans the entire virtual screen in physical pixels (via SetWindowPos); all internal
/// math is done in window logical units and mapped to physical pixels with a uniform factor.
/// </summary>
public partial class CaptureOverlay : Window
{
    private readonly Int32Rect _virtualScreenPhysical;
    private double _factor = 1.0;
    private bool _selecting;
    private Point _dragStart;
    private Rect _selection;
    private bool _finished;

    public event Action<Int32Rect?>? Finished;

    public CaptureOverlay(Int32Rect virtualScreenPhysical)
    {
        InitializeComponent();
        _virtualScreenPhysical = virtualScreenPhysical;
        Left = 0;
        Top = 0;
        Width = 1280;
        Height = 720;

        PreviewKeyDown += OnPreviewKeyDown;
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(
            hwnd, new IntPtr(-1), /* HWND_TOPMOST */
            _virtualScreenPhysical.X, _virtualScreenPhysical.Y,
            _virtualScreenPhysical.Width, _virtualScreenPhysical.Height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ResizeLayer();
        Layer.Focus();
        Activate(); // needed so ESC reaches this window
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ResizeLayer();

    private void ResizeLayer()
    {
        if (!(ActualWidth > 0) || !(ActualHeight > 0))
        {
            return;
        }

        Layer.Width = ActualWidth;
        Layer.Height = ActualHeight;
        _factor = _virtualScreenPhysical.Width / ActualWidth; // uniform logical->physical factor
        UpdateDimAndSelection();
    }

    private Point ToPhysical(Point logical)
    {
        return new Point(_virtualScreenPhysical.X + logical.X * _factor,
                         _virtualScreenPhysical.Y + logical.Y * _factor);
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            Finish(null);
            return;
        }
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        _dragStart = e.GetPosition(Layer);
        _selecting = true;
        Layer.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_selecting)
        {
            return;
        }
        _selection = DpiMath.Normalize(_dragStart, e.GetPosition(Layer));
        UpdateDimAndSelection();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_selecting || e.ChangedButton != MouseButton.Left)
        {
            return;
        }
        _selecting = false;
        Layer.ReleaseMouseCapture();

        _selection = DpiMath.Normalize(_dragStart, e.GetPosition(Layer));
        UpdateDimAndSelection();

        // Tiny click without movement = cancel.
        if (_selection.Width * _factor < 4 || _selection.Height * _factor < 4)
        {
            Finish(null);
            return;
        }

        Finish(DpiMath.ToPhysicalRect(_selection, _virtualScreenPhysical, _factor));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(null);
        }
    }

    private void UpdateDimAndSelection()
    {
        double fullW = Layer.ActualWidth;
        double fullH = Layer.ActualHeight;

        var dimGeometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dimGeometry.Children.Add(new RectangleGeometry(new Rect(0, 0, fullW, fullH)));
        if (_selecting && _selection.Width > 0 && _selection.Height > 0)
        {
            dimGeometry.Children.Add(new RectangleGeometry(_selection));
        }
        DimPath.Data = dimGeometry;

        if (_selecting && _selection.Width > 0 && _selection.Height > 0)
        {
            SelRect.StrokeThickness = Math.Max(1.0 / _factor, 0.5);
            SelRect.Width = _selection.Width;
            SelRect.Height = _selection.Height;
            Canvas.SetLeft(SelRect, _selection.X);
            Canvas.SetTop(SelRect, _selection.Y);
            SelRect.Visibility = Visibility.Visible;

            int physW = DpiMath.SafeRound(_selection.Width * _factor);
            int physH = DpiMath.SafeRound(_selection.Height * _factor);
            SizeLabel.Text = $"{physW} × {physH} px";
            SizeLabel.Visibility = Visibility.Visible;
            SizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double labelW = SizeLabel.DesiredSize.Width;
            double labelH = SizeLabel.DesiredSize.Height;

            double lx = _selection.Right + 8;
            double ly = _selection.Top - labelH - 6;
            if (lx + labelW > fullW) lx = _selection.Left - labelW - 8;
            if (lx < 0) lx = 4;
            if (ly < 0) ly = _selection.Bottom + 6;
            if (ly + labelH > fullH) ly = fullH - labelH - 4;
            Canvas.SetLeft(SizeLabel, lx);
            Canvas.SetTop(SizeLabel, ly);
        }
        else
        {
            SelRect.Visibility = Visibility.Collapsed;
            SizeLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void Finish(Int32Rect? physicalRect)
    {
        if (_finished)
        {
            return;
        }
        _finished = true;
        Finished?.Invoke(physicalRect);
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // If closed by other means (Alt+F4), still deliver a cancellation to the caller.
        if (!_finished)
        {
            _finished = true;
            Finished?.Invoke(null);
        }
        base.OnClosing(e);
    }
}
