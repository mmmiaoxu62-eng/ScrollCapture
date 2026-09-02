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

    public PreviewWindow(BitmapSource bitmap, AppSettings settings, string? saveInfo = null)
    {
        InitializeComponent();
        _settings = settings;
        _saveInfo = saveInfo;

        PreviewImage.Source = bitmap;
        InfoText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px" + (saveInfo != null ? $" · {saveInfo}" : "");
        Loaded += (_, _) => FitToWindow();
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
    }

    private double _zoom = 1.0;

    private BitmapSource? _sourceAfterCrop;
    private bool _cropActive;
    private Point _cropStart;

    private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
    {
        double delta = e.Delta > 0 ? 1.15 : 1.0 / 1.15;
        ApplyZoom(_zoom * delta);
        e.Handled = true;
    }

    private void OnCropClick(object sender, RoutedEventArgs e)
    {
        EnterCropMode();
    }

    private void EnterCropMode()
    {
        if (_cropActive || PreviewImage.Source is not BitmapSource bmp || bmp.PixelWidth < 2)
        {
            return;
        }
        _cropActive = true;
        CropOverlay.Width = PreviewImage.ActualWidth;
        CropOverlay.Height = PreviewImage.ActualHeight;
        CropOverlay.Visibility = Visibility.Visible;
        CropOverlay.Cursor = Cursors.Cross;
        CropRect.Visibility = Visibility.Collapsed;
        CropOverlay.MouseLeftButtonDown += OnCropMouseDown;
        CropOverlay.MouseMove += OnCropMouseMove;
        CropOverlay.MouseLeftButtonUp += OnCropMouseUp;
        SetStatus("拖动选择裁剪区域");
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
    }

    private void OnCropMouseDown(object sender, MouseButtonEventArgs e)
    {
        _cropStart = e.GetPosition(CropOverlay);
        CropOverlay.CaptureMouse();
    }

    private void OnCropMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }
        var p = e.GetPosition(CropOverlay);
        var sel = DpiMath.Normalize(_cropStart, p);
        CropRect.Width = sel.Width;
        CropRect.Height = sel.Height;
        Canvas.SetLeft(CropRect, sel.X);
        Canvas.SetTop(CropRect, sel.Y);
        CropRect.Visibility = Visibility.Visible;

        // dim excluding selection
        double cw = CropOverlay.Width;
        double ch = CropOverlay.Height;
        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(new Rect(0, 0, cw, ch)));
        if (sel.Width > 0.5 && sel.Height > 0.5)
        {
            dim.Children.Add(new RectangleGeometry(sel));
        }
        CropDimPath.Data = dim;
    }

    private void OnCropMouseUp(object sender, MouseButtonEventArgs e)
    {
        CropOverlay.ReleaseMouseCapture();
        var p = e.GetPosition(CropOverlay);
        var sel = DpiMath.Normalize(_cropStart, p);
        if (sel.Width < 4 || sel.Height < 4)
        {
            ExitCropMode();
            return;
        }
        try
        {
            // maps overlay dips -> source pixel space
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
            InfoText.Text = $"已裁剪 {cropped.PixelWidth} × {cropped.PixelHeight} px（原 {bmp.PixelWidth} × …）";
            FitToWindow();
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

    private void OnRetakeClick(object sender, RoutedEventArgs e)
    {
        RetakeRequested?.Invoke();
        Close();
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
