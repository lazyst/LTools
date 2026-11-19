using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 采集历史的「查看大图」窗：图片 + 右侧识别文字面板。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 page 里的 <c>AdImageViewer</c>：点卡片预览就进这里看全图（卡片上只有 260 × 156 的缩略图）。
/// 交互与图片编辑窗同一套直觉：滚轮缩放（按光标位置）、拖动平移、双击在「适应窗口」与「100%」
/// 之间切、Esc 关闭；右侧面板只在有识别文字时出现。
/// </para>
/// <para>
/// 窗口是单例：再点另一条就地在同一个窗里换内容，不会堆出一屏查看器。录屏没有静态图，
/// 这里给一块「没有封面帧」的说明和一个「播放录屏」按钮，而不是假装有图。
/// </para>
/// </remarks>
public partial class CaptureViewerWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 8.0;

    private static CaptureViewerWindow? _current;

    private CaptureEntry? _entry;
    private BitmapSource? _image;

    private double _zoom = 1.0;
    private bool _fit = true;

    private bool _dragging;
    private Point _dragOrigin;
    private double _dragOffsetX;
    private double _dragOffsetY;

    public CaptureViewerWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => ApplyFit();
        SizeChanged += (_, _) => { if (_fit) ApplyFit(); };
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_current, this)) _current = null;
            if (Picture != null) Picture.Source = null;
        };
    }

    /// <summary>打开 / 复用查看器看这一条（<paramref name="owner"/> 为空时居中到屏幕）。</summary>
    internal static void Show(Window? owner, CaptureEntry entry)
    {
        if (_current == null)
        {
            var window = new CaptureViewerWindow();
            _current = window;
            if (owner != null && owner.IsVisible) window.Owner = owner;
            else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            window.Show();
        }

        _current.Present(entry);
        if (_current.WindowState == WindowState.Minimized) _current.WindowState = WindowState.Normal;
        _current.Activate();
    }

    /// <summary>换成这一条（同一窗口复用）。</summary>
    private void Present(CaptureEntry entry)
    {
        _entry = entry;

        string path = PathOf(entry);
        long bytes = 0;
        try
        {
            if (path.Length > 0 && File.Exists(path)) bytes = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureViewer.Bytes", "读文件大小失败：" + ex.Message);
        }

        Title = $"{entry.KindLabel} · {entry.TimeText}";
        TitleText.Text = $"{entry.KindLabel} · {entry.TimeText}";

        string size = $"{entry.Width} × {entry.Height} px";
        string file = CaptureCatalog.FormatBytes(bytes);
        InfoText.Text = entry.IsRecord
            ? $"{size} · {entry.DurationText} · {entry.FormatText}" + (file.Length > 0 ? " · " + file : "")
            : $"{size} · {CaptureHistorySource.LabelOf(CaptureHistorySource.KeyOf(entry))}"
              + (file.Length > 0 ? " · " + file : "");

        _image = entry.IsRecord ? null : CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(entry.Id), 0);
        Picture.Source = _image;

        bool hasImage = _image != null;
        NoImagePanel.Visibility = hasImage ? Visibility.Collapsed : Visibility.Visible;
        NoImageText.Text = entry.IsRecord
            ? "录屏没有静态画面。点下面的「播放录屏」用系统播放器打开它。"
            : "成品图不在了（可能被清理或移走）。";
        NoImagePlayBtn.Visibility = entry.IsRecord ? Visibility.Visible : Visibility.Collapsed;

        CopyImageBtn.IsEnabled = hasImage;
        SaveAsBtn.IsEnabled = hasImage || entry.IsRecord;
        RevealBtn.IsEnabled = path.Length > 0 && File.Exists(path);
        ZoomInBtn.IsEnabled = hasImage;
        ZoomOutBtn.IsEnabled = hasImage;
        FitBtn.IsEnabled = hasImage;
        ActualBtn.IsEnabled = hasImage;

        string text = entry.IsRecord ? "" : entry.Text;
        TextPanel.Visibility = text.Trim().Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TextBody.Text = text;
        CopyTextBtn.IsEnabled = text.Trim().Length > 0;

        _fit = true;
        ApplyFit();
    }

    // ============ 缩放 / 平移 ============

    private void ApplyFit()
    {
        if (_image == null || !IsLoaded) return;

        double viewport = Math.Max(40, ImageScroll.ActualWidth - 4);
        double height = Math.Max(40, ImageScroll.ActualHeight - 4);

        double fit = Math.Min(viewport / _image.PixelWidth, height / _image.PixelHeight);
        if (fit <= 0 || double.IsInfinity(fit)) fit = 1.0;

        _fit = true;
        SetZoom(Math.Clamp(fit, MinZoom, MaxZoom), anchor: null);
    }

    private void SetZoom(double zoom, Point? anchor)
    {
        double old = _zoom;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        Point viewportPoint = anchor ?? new Point(ImageScroll.ActualWidth / 2, ImageScroll.ActualHeight / 2);
        double imageX = (_image == null || old <= 0) ? 0 : (ImageScroll.HorizontalOffset + viewportPoint.X) / old;
        double imageY = (_image == null || old <= 0) ? 0 : (ImageScroll.VerticalOffset + viewportPoint.Y) / old;

        Picture.LayoutTransform = new ScaleTransform(_zoom, _zoom);

        // 布局同步一次，ScrollViewer 的 Extent 才是新值（否则滚不到位、缩放时画面乱跳）
        UpdateLayout();

        ImageScroll.ScrollToHorizontalOffset(imageX * _zoom - viewportPoint.X);
        ImageScroll.ScrollToVerticalOffset(imageY * _zoom - viewportPoint.Y);

        ZoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private void ImageScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_image == null) return;

        e.Handled = true;
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        _fit = false;
        SetZoom(_zoom * factor, e.GetPosition(ImageScroll));
    }

    private void Picture_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_image == null) return;

        if (e.ClickCount == 2)
        {
            e.Handled = true;
            if (_fit)
            {
                _fit = false;
                SetZoom(1.0, anchor: null);
            }
            else
            {
                ApplyFit();
            }
            return;
        }

        _dragging = true;
        _dragOrigin = e.GetPosition(ImageScroll);
        _dragOffsetX = ImageScroll.HorizontalOffset;
        _dragOffsetY = ImageScroll.VerticalOffset;
        Picture.CaptureMouse();
        Picture.Cursor = Cursors.SizeAll;
    }

    private void Picture_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(ImageScroll);
        ImageScroll.ScrollToHorizontalOffset(_dragOffsetX - (now.X - _dragOrigin.X));
        ImageScroll.ScrollToVerticalOffset(_dragOffsetY - (now.Y - _dragOrigin.Y));
    }

    private void Picture_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrag();

    private void Picture_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton != MouseButtonState.Pressed) EndDrag();
    }

    private void EndDrag()
    {
        if (!_dragging) return;

        _dragging = false;
        Picture.ReleaseMouseCapture();
        Picture.Cursor = Cursors.Arrow;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        SetZoom(_zoom * 1.25, null);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        _fit = false;
        SetZoom(_zoom / 1.25, null);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => ApplyFit();

    private void Actual_Click(object sender, RoutedEventArgs e) => SetZoom(1.0, anchor: null);

    // ============ 命令 ============

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.D0 when Keyboard.Modifiers == ModifierKeys.Control:
                ApplyFit();
                e.Handled = true;
                break;
            case Key.D1 when Keyboard.Modifiers == ModifierKeys.Control:
                SetZoom(1.0, anchor: null);
                e.Handled = true;
                break;
            case Key.OemPlus or Key.Add:
                ZoomIn_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
            case Key.OemMinus or Key.Subtract:
                ZoomOut_Click(this, new RoutedEventArgs());
                e.Handled = true;
                break;
        }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e)
    {
        if (_image == null) return;

        if (NativeClipboard.SetImage(_image)) ToastService.Show("已复制图片", "查看大图", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "查看大图", 0, ToastKind.error);
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        string text = _entry?.Text ?? "";
        if (text.Trim().Length == 0)
        {
            ToastService.Show("没有识别文字", "查看大图", 1600);
            return;
        }

        if (NativeClipboard.SetText(text)) ToastService.Show("已复制识别文字", "查看大图", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "查看大图", 0, ToastKind.error);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        string path = _entry == null ? "" : PathOf(_entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("文件不见了", "查看大图", 1800, ToastKind.error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToastService.Show("打开资源管理器失败：" + ex.Message, "查看大图", 0, ToastKind.error);
        }
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_entry == null) return;

        if (_entry.IsRecord)
        {
            string path = PathOf(_entry);
            if (path.Length == 0 || !File.Exists(path))
            {
                ToastService.Show("录制文件不见了", "查看大图", 2200, ToastKind.error);
                return;
            }

            CaptureSave.TrySaveFile(this, path, CaptureCatalog.ExtensionOf(path));
            return;
        }

        if (_image == null)
        {
            ToastService.Show("图片文件不见了", "查看大图", 1800, ToastKind.error);
            return;
        }

        CaptureSave.TrySave(this, _image);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_entry == null) return;

        string path = PathOf(_entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("录制文件不见了", "查看大图", 2200, ToastKind.error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.Show("打不开录制文件：" + ex.Message, "查看大图", 0, ToastKind.error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static string PathOf(CaptureEntry entry) => entry.IsRecord
        ? CaptureCatalog.FilePath(entry)
        : CaptureCatalog.AssetPath(entry.Id);
}
