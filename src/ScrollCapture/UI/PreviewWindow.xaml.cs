using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using ScrollCapture.Capture;
using ScrollCapture.Settings;
using ScrollCapture.Utils;

namespace ScrollCapture.UI;

public partial class PreviewWindow : Window
{
    public event Action? RetakeRequested;

    private readonly AppSettings _settings;
    private readonly string? _saveInfo;

    private string? _savedFilePath;

    public PreviewWindow(BitmapSource bitmap, AppSettings settings, string? saveInfo = null, string? savedFilePath = null)
    {
        InitializeComponent();
        _settings = settings;
        _saveInfo = saveInfo;
        _savedFilePath = savedFilePath;

        PreviewImage.Source = bitmap;
        InfoText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px" + (saveInfo != null ? $" · {saveInfo}" : "");
        Loaded += (_, _) =>
        {
            if (!double.IsNaN(_rememberedZoom) && _rememberedZoom is > 0.05 and < 20)
            {
                ApplyZoom(_rememberedZoom);
            }
            else
            {
                FitToWindow();
            }
        };
    }

    private void FitToWindow()
    {
        if (PreviewImage.Source is not BitmapSource bmp || bmp.PixelWidth == 0 || bmp.PixelHeight == 0)
        {
            return;
        }
        double windowW = Viewport.ActualWidth > 0 ? Viewport.ActualWidth : 800;
        double windowH = Viewport.ActualHeight > 0 ? Viewport.ActualHeight : 540;
        double scale = Math.Min(1.0, Math.Min(windowW / bmp.PixelWidth, windowH / bmp.PixelHeight));
        ApplyZoom(scale);
    }

    private void ApplyZoom(double scale)
    {
        if (scale <= 0.05) return;
        PreviewImage.LayoutTransform = new ScaleTransform(scale, scale);
        _zoom = scale;
        _rememberedZoom = scale;
    }

    private double _zoom = 1.0;
    private static double _rememberedZoom = double.NaN;

    private BitmapSource? _sourceAfterCrop;
    private bool _cropActive;
    private Point _cropStart;

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        ApplyZoom(_zoom * delta);
        e.Handled = true;
    }

    private Rect _cropRect;

    private void OnCropClick(object sender, RoutedEventArgs e) => EnterCropMode();

    private void EnterCropMode()
    {
        if (_cropActive || PreviewImage.Source is not BitmapSource || PreviewImage.ActualWidth < 2)
        {
            return;
        }
        _cropActive = true;
        CropOverlay.Width = PreviewImage.ActualWidth;
        CropOverlay.Height = PreviewImage.ActualHeight;
        CropOverlay.Visibility = Visibility.Visible;

        // initial frame = full image (adjustable)
        _cropRect = new Rect(0, 0, CropOverlay.Width, CropOverlay.Height);
        UpdateCropVisuals();

        CropOverlay.MouseLeftButtonDown += OnCropMouseDown;
        CropOverlay.MouseMove += OnCropMouseMove;
        CropOverlay.MouseLeftButtonUp += OnCropMouseUp;

        CropButton.Visibility = Visibility.Collapsed;
        CallbackCrop.Visibility = Visibility.Visible;
        CancelCrop.Visibility = Visibility.Visible;
        SetStatus("拖动边框/角调整裁剪区域，确认后生效");
    }

    private void ExitCropMode()
    {
        if (!_cropActive)
        {
            return;
        }
        _cropActive = false;
        CropOverlay.MouseLeftButtonDown -= OnCropMouseDown;
        CropOverlay.MouseMove -= OnCropMouseMove;
        CropOverlay.MouseLeftButtonUp -= OnCropMouseUp;
        CropOverlay.Visibility = Visibility.Collapsed;
        CropButton.Visibility = Visibility.Visible;
        CallbackCrop.Visibility = Visibility.Collapsed;
        CancelCrop.Visibility = Visibility.Collapsed;
    }

    private void UpdateCropVisuals()
    {
        if (CropOverlay.Width <= 0)
        {
            return;
        }
        CropRect.Width = _cropRect.Width;
        CropRect.Height = _cropRect.Height;
        Canvas.SetLeft(CropRect, _cropRect.X);
        Canvas.SetTop(CropRect, _cropRect.Y);

        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(new Rect(0, 0, CropOverlay.Width, CropOverlay.Height)));
        if (_cropRect.Width > 1 && _cropRect.Height > 1)
        {
            dim.Children.Add(new RectangleGeometry(_cropRect));
        }
        CropDimPath.Data = dim;

        const double h = 5;
        foreach (var (handle, x, y, cursor) in new[]
        {
            (HandleTL, _cropRect.X - h, _cropRect.Y - h, Cursors.SizeNWSE),
            (HandleTR, _cropRect.Right - h, _cropRect.Y - h, Cursors.SizeNESW),
            (HandleBL, _cropRect.X - h, _cropRect.Bottom - h, Cursors.SizeNESW),
            (HandleBR, _cropRect.Right - h, _cropRect.Bottom - h, Cursors.SizeNWSE),
        })
        {
            Canvas.SetLeft(handle, x);
            Canvas.SetTop(handle, y);
            handle.Cursor = cursor;
        }
    }

    private const double HandleHit = 12;
    private enum DragMode { None, Move, NW, NE, SW, SE }
    private DragMode _drag = DragMode.None;
    private Rect _dragStartRect;
    private Point _dragStartPoint;

    private DragMode HitTestCrop(Point p)
    {
        var r = _cropRect;
        bool corner(double x, double y) => Math.Abs(p.X - x) < HandleHit && Math.Abs(p.Y - y) < HandleHit;
        if (corner(r.X, r.Y)) return DragMode.NW;
        if (corner(r.Right, r.Y)) return DragMode.NE;
        if (corner(r.X, r.Bottom)) return DragMode.SW;
        if (corner(r.Right, r.Bottom)) return DragMode.SE;
        if (p.X >= r.X - HandleHit && p.X <= r.Right + HandleHit
            && p.Y >= r.Y - HandleHit && p.Y <= r.Bottom + HandleHit)
        {
            return DragMode.Move;
        }
        return DragMode.None;
    }

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(CropOverlay);
        _drag = HitTestCrop(p);
        if (_drag != DragMode.None)
        {
            _dragStartRect = _cropRect;
            _dragStartPoint = p;
            CropOverlay.CaptureMouse();
        }
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        if (_drag == DragMode.None)
        {
            return;
        }
        var p = e.GetPosition(CropOverlay);
        double dx = p.X - _dragStartPoint.X;
        double dy = p.Y - _dragStartPoint.Y;
        double minX = 0, minY = 0, maxX = CropOverlay.Width, maxY = CropOverlay.Height;

        switch (_drag)
        {
            case DragMode.Move:
                double nx = Math.Clamp(_dragStartRect.X + dx, minX, maxX - _dragStartRect.Width);
                double ny = Math.Clamp(_dragStartRect.Y + dy, minY, maxY - _dragStartRect.Height);
                _cropRect = new Rect(nx, ny, _dragStartRect.Width, _dragStartRect.Height);
                break;
            case DragMode.NW: _cropRect = new Rect(_dragStartRect.Left + dx, _dragStartRect.Top + dy, _dragStartRect.Right - (_dragStartRect.Left + dx), _dragStartRect.Bottom - (_dragStartRect.Top + dy)); break;
            case DragMode.SE: _cropRect = new Rect(_dragStartRect.Left, _dragStartRect.Top, p.X - _dragStartRect.Left, p.Y - _dragStartRect.Top); break;
            case DragMode.NE: _cropRect = new Rect(_dragStartRect.Left, _dragStartRect.Top + dy, p.X - _dragStartRect.Left, _dragStartRect.Bottom - (_dragStartRect.Top + dy)); break;
            case DragMode.SW: _cropRect = new Rect(_dragStartRect.Left + dx, _dragStartRect.Top, _dragStartRect.Right - (_dragStartRect.Left + dx), p.Y - _dragStartRect.Top); break;
        }
        _cropRect = NormalizeRect(_cropRect, minX, minY, maxX, maxY);
        UpdateCropVisuals();
    }

    private static Rect NormalizeRect(Rect r, double minX, double minY, double maxX, double maxY)
    {
        double x0 = Math.Min(r.Left, r.Right);
        double y0 = Math.Min(r.Top, r.Bottom);
        double w = Math.Abs(r.Width);
        double h = Math.Abs(r.Height);
        w = Math.Max(8, Math.Min(w, maxX - x0));
        h = Math.Max(8, Math.Min(h, maxY - y0));
        return new Rect(Math.Clamp(x0, minX, maxX - w), Math.Clamp(y0, minY, maxY - h), w, h);
    }

    private void OnCropMouseUp(object sender, MouseButtonEventArgs e)
    {
        CropOverlay.ReleaseMouseCapture();
        _drag = DragMode.None;
        UpdateCropVisuals();
    }

    private void OnConfirmCropClick(object sender, RoutedEventArgs e)
    {
        try
        {
            ExecuteCrop(_cropRect);
        }
        catch (Exception ex)
        {
            Logger.Error("Crop failed", ex);
            SetStatus($"裁剪失败：{ex.Message}");
        }
        finally
        {
            ExitCropMode();
        }
    }

    private void OnCancelCropClick(object sender, RoutedEventArgs e) => ExitCropMode();

    private void ExecuteCrop(Rect sel)
    {
        var bmp = (BitmapSource)PreviewImage.Source;
        double scale = bmp.PixelWidth / PreviewImage.ActualWidth;
        var px = new Int32Rect(
            DpiMath.SafeRound(sel.X * scale),
            DpiMath.SafeRound(sel.Y * scale),
            Math.Max(1, DpiMath.SafeRound(sel.Width * scale)),
            Math.Max(1, DpiMath.SafeRound(sel.Height * scale)));
        _sourceAfterCrop ??= (BitmapSource)PreviewImage.Source;
        var cropped = new CroppedBitmap(_sourceAfterCrop, px);
        cropped.Freeze();
        PreviewImage.Source = cropped;
        RestoreButton.Visibility = Visibility.Visible;
        InfoText.Text = $"已裁剪 {cropped.PixelWidth} × {cropped.PixelHeight} px（原 {bmp.PixelWidth} × {bmp.PixelHeight}）";
        FitToWindow();
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (_sourceAfterCrop != null)
        {
            PreviewImage.Source = _sourceAfterCrop;
            RestoreButton.Visibility = Visibility.Collapsed;
            FitToWindow();
        }
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource bmp)
        {
            return;
        }
        try
        {
            Clipboard.SetImage(bmp);
            SetStatus("已复制到剪贴板");
        }
        catch (Exception ex)
        {
            Logger.Error("Clipboard copy failed", ex);
            SetStatus($"复制失败：{ex.Message}");
        }
    }

    private void OnSavePngClick(object sender, RoutedEventArgs e) => SaveAs("PNG", "PNG 图片|*.png", "png");

    private void OnSaveJpgClick(object sender, RoutedEventArgs e) => SaveAs("JPG", "JPG 图片|*.jpg;*.jpeg", "jpg");

    private void SaveAs(string title, string filter, string extension)
    {
        if (PreviewImage.Source is not BitmapSource bmp)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = $"保存 {title}",
            Filter = filter,
            DefaultExt = extension,
            FileName = $"scrollcapture_{DateTime.Now:yyyyMMdd_HHmmss}.{extension}",
            InitialDirectory = _settings.SaveDirectory,
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dialog.FileName)!);
            if (extension == "jpg")
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var stream = new FileStream(dialog.FileName, FileMode.Create);
                encoder.Save(stream);
            }
            else
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var stream = new FileStream(dialog.FileName, FileMode.Create);
                encoder.Save(stream);
            }
            SetStatus($"已保存：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Logger.Error("Save failed", ex);
            SetStatus($"保存失败：{ex.Message}");
        }
    }

    private void OnExportPdfClick(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource bmp)
        {
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "导出 PDF",
            Filter = "PDF 文档|*.pdf",
            DefaultExt = "pdf",
            FileName = $"scrollcapture_{DateTime.Now:yyyyMMdd_HHmmss}.pdf",
            InitialDirectory = _settings.SaveDirectory,
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }
        try
        {
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            byte[] jpeg;
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                jpeg = ms.ToArray();
            }
            PdfBuilder.Build(dialog.FileName, jpeg, bmp.PixelWidth, bmp.PixelHeight);
            SetStatus($"已导出 PDF：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            Logger.Error("PDF export failed", ex);
            SetStatus($"导出失败：{ex.Message}");
        }
    }

    private void OnRetakeClick(object sender, RoutedEventArgs e)
    {
        RetakeRequested?.Invoke();
        Close();
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (_savedFilePath == null)
        {
            SetStatus("没有可删除的已保存文件。");
            return;
        }
        var answer = MessageBox.Show(this,
            $"删除本次截取的长图？\n\n{_savedFilePath}\n\n（此操作不可撤销；之前另存的文件不受影响）",
            "删除截取的图", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }
        try
        {
            if (File.Exists(_savedFilePath))
            {
                File.Delete(_savedFilePath);
            }
            SetStatus("已删除，可关闭此窗口。");
            Logger.Info($"Preview delete: {_savedFilePath}");
        }
        catch (Exception ex)
        {
            Logger.Error("Delete failed", ex);
            SetStatus($"删除失败：{ex.Message}");
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_settings.SaveDirectory);
            System.Diagnostics.Process.Start("explorer.exe", _settings.SaveDirectory);
        }
        catch (Exception ex)
        {
            Logger.Error("Open folder failed", ex);
        }
    }

    private void SetStatus(string text) => InfoText.Text = $"{((BitmapSource)PreviewImage.Source).PixelWidth} × {((BitmapSource)PreviewImage.Source).PixelHeight} px · {text}";
}
