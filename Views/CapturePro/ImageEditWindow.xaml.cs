using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.ImageEdit;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 图片编辑窗（照 snow-shot 的 <c>ImageEditingWindow</c>）：导入已有图片 → 画布标注 → 导出。
/// </summary>
/// <remarks>
/// <para>
/// 内容真源是 <see cref="ImageEditDocument"/>（底图 + 标注 + 撤销栈），画面走
/// <see cref="CaptureCanvas"/>（与截图覆盖层同一套渲染器）；缩放 / 平移只是把内容整块
/// 用 <c>RenderTransform</c> 摆过去，图像像素始终 1:1 对得上。
/// </para>
/// <para>
/// 裁剪照上游 <c>ImageEditingCanvas</c>：拖动时铺一条半透明红带 + 白字「N px」+
/// 两条蓝色虚线导引线；松手提交，进撤销栈。Esc 先退出裁剪，再按才关窗。
/// </para>
/// </remarks>
public partial class ImageEditWindow : Window
{
    private readonly ImageEditDocument _doc = new();

    private double _zoom = 1;
    private Point _offset;
    private double _dpi = 1;

    private bool _fitted;                 // 已经按窗口适过一次（之后裁剪不重置镜头）
    private bool _applying;               // 正在装图（别再触发「改过」）
    private int _generation;              // 打开图片的世代号（后一次打开作废前一次）

    private bool _panning;
    private Point _panStart;
    private Point _panOffset;

    private CropAxis? _cropAxis;
    private bool _cropping;
    private int _cropFirst;
    private int _cropLast;
    private bool _cropPointerInside;

    private bool _closing;

    public ImageEditWindow()
    {
        InitializeComponent();
        ImageEditSession.Track(this);

        Loaded += Window_Loaded;
        SizeChanged += (_, _) => { ClampOffset(); ApplyCamera(); };
        DpiChanged += (_, _) => { _dpi = ReadDpi(); UpdateChrome(); };

        UpdateChrome();
    }

    /// <summary>还没有打开任何图片（图片编辑会话据此复用空窗）。</summary>
    internal bool IsEmpty => !_doc.HasImage;

    // ============ 打开 ============

    /// <summary>切到空态（不显示图片）。</summary>
    public void PresentEmpty()
    {
        _generation++;
        _doc.Clear();
        Canvas.Document = null;
        Canvas.InvalidateVisual();

        _fitted = false;
        EmptyState.Visibility = Visibility.Visible;
        Title = "图片编辑";
        Notice("");
        UpdateChrome();
    }

    /// <summary>按文件打开（后台解码，解不出来只在状态栏说一句）。</summary>
    public void PresentFile(string path)
    {
        int generation = ++_generation;

        if (!File.Exists(path))
        {
            Notice("图片打不开：文件不存在");
            return;
        }

        try
        {
            if (new FileInfo(path).Length > PinFiles.MaximumImageBytes)
            {
                Notice("图片太大或内存不足，打不开");
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("ImageEditWindow.PresentFile", ex.Message);
            Notice("图片打开失败");
            return;
        }

        Notice("正在打开图片…");

        Task.Run(() => ImageEditSession.LoadFile(path)).ContinueWith(task =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (generation != _generation || _closing) return;

                if (task.Result is not { } image)
                {
                    Notice("图片打开失败");
                    return;
                }

                Apply(image, Path.GetFileName(path), path);
            }));
        });
    }

    /// <summary>装一张已经解码好的图（剪贴板 / 采集历史 / 贴图记录）。</summary>
    public void PresentImage(BitmapSource image, string fileName, string? sourcePath) =>
        Apply(image, fileName, sourcePath);

    private void Apply(BitmapSource image, string fileName, string? sourcePath)
    {
        _generation++;

        _applying = true;
        _doc.Load(image, fileName ?? "", sourcePath);
        _applying = false;

        Canvas.Document = _doc.Doc;
        Canvas.Width = image.PixelWidth;
        Canvas.Height = image.PixelHeight;
        Canvas.ShowSelectionChrome = false;

        _fitted = false;
        CancelCropMode();
        EmptyState.Visibility = Visibility.Collapsed;
        Title = $"图片编辑 — {_doc.Title}";
        Notice("");

        if (IsLoaded && Surface.ActualWidth > 1) FitToWindow();
        else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitIfPending));

        UpdateChrome();
    }

    // ============ 镜头（缩放 / 平移） ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _dpi = ReadDpi();
        FitIfPending();
        Surface.Focus();
    }

    private double ReadDpi()
    {
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return scale > 0 ? scale : 1;
    }

    private Size ContentSize => _doc.Image is { } image
        ? new Size(image.PixelWidth, image.PixelHeight)
        : Size.Empty;

    private Size Viewport => new(Math.Max(1, Surface.ActualWidth), Math.Max(1, Surface.ActualHeight));

    private void FitIfPending()
    {
        if (_fitted || !_doc.HasImage || Viewport.Width <= 2 || Viewport.Height <= 2) return;
        FitToWindow();
    }

    /// <summary>适应窗口（上游 <c>ImageEditingCanvas::fit</c>：四周留 48，小图也放大）。</summary>
    private void FitToWindow()
    {
        if (!_doc.HasImage) return;

        var content = ContentSize;
        var viewport = Viewport;

        _zoom = ImageEditGeometry.FitZoom(content, viewport);
        _offset = new Point((viewport.Width - content.Width * _zoom) / 2,
            (viewport.Height - content.Height * _zoom) / 2);
        _fitted = true;

        ApplyCamera();
    }

    /// <summary>实际大小（1 图像像素 = 1 物理像素）。</summary>
    private void ActualSize()
    {
        if (!_doc.HasImage) return;

        var viewport = Viewport;
        var center = new Point(viewport.Width / 2, viewport.Height / 2);
        var anchor = ToImage(center);

        _zoom = ImageEditGeometry.ClampZoom(1.0 / (_dpi > 0 ? _dpi : 1));
        _offset = new Point(center.X - anchor.X * _zoom, center.Y - anchor.Y * _zoom);
        _fitted = true;

        ClampOffset();
        ApplyCamera();
    }

    /// <summary>窗口坐标 → 图像像素坐标（DIP，1 单位 = 1 图像像素）。</summary>
    private Point ToImage(Point view) =>
        new((view.X - _offset.X) / _zoom, (view.Y - _offset.Y) / _zoom);

    private void ApplyCamera()
    {
        ZoomScale.ScaleX = _zoom;
        ZoomScale.ScaleY = _zoom;
        PanOffset.X = _offset.X;
        PanOffset.Y = _offset.Y;

        UpdateCropLayer();
        UpdateChrome();
    }

    /// <summary>把内容夹在视口里：比视口小就居中，比视口大就不许露出空白。</summary>
    private void ClampOffset()
    {
        if (!_doc.HasImage) return;

        var content = ContentSize;
        var viewport = Viewport;
        double width = content.Width * _zoom;
        double height = content.Height * _zoom;

        _offset.X = width <= viewport.Width ? (viewport.Width - width) / 2
            : Math.Min(0, Math.Max(viewport.Width - width, _offset.X));
        _offset.Y = height <= viewport.Height ? (viewport.Height - height) / 2
            : Math.Min(0, Math.Max(viewport.Height - height, _offset.Y));
    }

    /// <summary>裁剪 / 撤销改了底图尺寸之后：缩放不变，尽量保持视口中心那块内容。</summary>
    private void KeepCameraAfterResize()
    {
        var viewport = Viewport;
        var center = new Point(viewport.Width / 2, viewport.Height / 2);
        var anchor = ToImage(center);

        _offset = new Point(center.X - anchor.X * _zoom, center.Y - anchor.Y * _zoom);
        ClampOffset();
        ApplyCamera();
    }

    // ============ 鼠标 ============

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_doc.HasImage) return;

        var view = e.GetPosition(Surface);
        int steps = e.Delta / Mouse.MouseWheelDeltaForOneLine;
        if (steps == 0) steps = e.Delta > 0 ? 1 : -1;

        double next = ImageEditGeometry.Wheel(_zoom, steps);
        if (Math.Abs(next - _zoom) < 1e-9) return;

        var anchor = ToImage(view);
        _zoom = next;
        _offset = new Point(view.X - anchor.X * _zoom, view.Y - anchor.Y * _zoom);

        ClampOffset();
        ApplyCamera();
        UpdateCropLayer();
        e.Handled = true;
    }

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_doc.HasImage) return;

        // 裁剪模式：左键按下开始拉条带（起点可以先落在图外，提交时夹回去）
        if (_cropAxis is { } axis && e.ChangedButton == MouseButton.Left)
        {
            _cropping = true;
            _cropFirst = _cropLast = PixelAt(e.GetPosition(Surface));
            Surface.CaptureMouse();
            UpdateCropLayer();
            e.Handled = true;
            return;
        }

        // 平移：中键任何时候都能拖；左键在没有其它手势时也能拖
        if (e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _panning = true;
            _panStart = e.GetPosition(Surface);
            _panOffset = _offset;
            Surface.CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_doc.HasImage) return;

        if (_panning)
        {
            var now = e.GetPosition(Surface);
            _offset = new Point(_panOffset.X + (now.X - _panStart.X), _panOffset.Y + (now.Y - _panStart.Y));
            ClampOffset();
            ApplyCamera();
            return;
        }

        if (_cropAxis is null) return;

        if (_cropping)
        {
            _cropLast = PixelAt(e.GetPosition(Surface));
            _cropPointerInside = true;
            UpdateCropLayer();
            return;
        }

        var view = e.GetPosition(Surface);
        var content = ContentSize;
        bool inside = content.Width > 0 && content.Height > 0 &&
                      view.X >= _offset.X && view.Y >= _offset.Y &&
                      view.X <= _offset.X + content.Width * _zoom &&
                      view.Y <= _offset.Y + content.Height * _zoom;

        if (!inside)
        {
            if (_cropPointerInside)
            {
                _cropPointerInside = false;
                UpdateCropLayer();
            }
            return;
        }

        _cropPointerInside = true;
        _cropFirst = _cropLast = PixelAt(view);
        UpdateCropLayer();
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _panning = false;
            Surface.ReleaseMouseCapture();
            Cursor = _cropAxis != null ? Cursors.Cross : Cursors.Arrow;
            return;
        }

        if (_cropping && e.ChangedButton == MouseButton.Left)
        {
            _cropping = false;
            Surface.ReleaseMouseCapture();
            CommitCrop();
            e.Handled = true;
        }
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning || _cropping || !_cropPointerInside) return;

        _cropPointerInside = false;
        UpdateCropLayer();
    }

    /// <summary>光标处的图像像素（按内容缩放换算，越界夹在 [0, 边长]）。</summary>
    private int PixelAt(Point view)
    {
        var content = ContentSize;
        var point = ToImage(view);
        int extent = _cropAxis == CropAxis.Columns ? (int)content.Width : (int)content.Height;
        double value = _cropAxis == CropAxis.Columns ? point.X : point.Y;
        return Math.Clamp((int)Math.Round(value), 0, Math.Max(0, extent));
    }

    // ============ 撤销 / 重做 ============

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (!_doc.Undo()) return;

        Canvas.Document = _doc.Doc;
        Canvas.InvalidateVisual();
        KeepCameraAfterResize();
        UpdateChrome();
    }

    private void Redo()
    {
        if (!_doc.Redo()) return;

        Canvas.Document = _doc.Doc;
        Canvas.InvalidateVisual();
        KeepCameraAfterResize();
        UpdateChrome();
    }

    // ============ 界面状态 ============

    private void UpdateChrome()
    {
        bool has = _doc.HasImage;

        foreach (var button in new[] { FitBtn, ActualBtn, CopyBtn, QuickSaveBtn, SaveBtn, PrintBtn })
            button.IsEnabled = has;

        PinBtn.IsEnabled = has;
        CropRowsBtn.IsEnabled = has;
        CropColumnsBtn.IsEnabled = has;
        UndoBtn.IsEnabled = has && _doc.CanUndo;
        RedoBtn.IsEnabled = has && _doc.CanRedo;

        if (!has)
        {
            CropRowsBtn.IsChecked = false;
            CropColumnsBtn.IsChecked = false;
            _cropAxis = null;
            CropLayer.ImageRect = Rect.Empty;
            CropLayer.Strip = Rect.Empty;
            CropLayer.InvalidateVisual();
        }

        ZoomText.Text = has ? $"{Math.Round(_zoom * _dpi * 100)}%" : "";
        InfoText.Text = has ? $"{_doc.Width} × {_doc.Height} px · {_doc.Title}" : "";
    }

    private void Notice(string text) => NoticeText.Text = text;

    private void AddImage_Click(object sender, RoutedEventArgs e) => ChooseImages();

    private void ChooseImages()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加图片",
            Multiselect = true,
            Filter = "图片|" + string.Join(";", PinFiles.ImageExtensions.Select(ext => "*." + ext)) +
                     "|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true) return;

        // 当前窗是空的就先填它，其余各开一个（照上游 additionalFilesRequested）
        var paths = dialog.FileNames;
        if (paths.Length == 0) return;

        if (IsEmpty)
        {
            PresentFile(paths[0]);
            if (paths.Length > 1)
                ImageEditSession.OpenFiles(paths[1..]);
            return;
        }

        ImageEditSession.OpenFiles(paths);
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void Actual_Click(object sender, RoutedEventArgs e) => ActualSize();

    // ============ 关闭 ============

    private bool ConfirmReplace()
    {
        if (!_doc.Dirty || _applying) return true;

        var result = ConfirmDialog.ConfirmDiscard(this, "放弃更改？", "这个编辑窗口还有未保存的修改。");
        return result switch
        {
            SaveConfirmResult.Save => SaveAs(),
            SaveConfirmResult.Discard => true,
            _ => false,
        };
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) return;

        if (!_doc.Dirty)
        {
            _closing = true;
            return;
        }

        e.Cancel = true;

        if (!ConfirmReplace()) return;

        _closing = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
    }

    // ============ 键盘 ============

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        if (e.Key == Key.Escape)
        {
            if (_cropping)
            {
                _cropping = false;
                Surface.ReleaseMouseCapture();
                _cropPointerInside = false;
                UpdateCropLayer();
                e.Handled = true;
                return;
            }

            if (_cropAxis != null)
            {
                CancelCropMode();
                e.Handled = true;
                return;
            }

            Close();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C) { CopyImage(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.V) { PasteFromClipboard(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.O) { ChooseImages(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.P) { PrintImage(); e.Handled = true; return; }

        if (ctrl && e.Key == Key.S)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) SaveAs();
            else QuickSave();
            e.Handled = true;
        }
    }
}
