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
using LTools.Features;
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
/// 布局照上游：**没有顶部工具行**，绘图工具条浮在画布上（锚图片右下 4px，见
/// <see cref="PlaceOverlay"/>），缩放条贴画布底部居中；空态可点「添加图片」或拖进来。
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

        Toolbar.Init(_styles, CaptureTool.Select, CaptureToolbar.ToolbarMode.Edit);
        Toolbar.ToolChanged += SetTool;
        Toolbar.PanRequested += EnterPanMode;
        Toolbar.CropRequested += SetCropAxis;
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.ActionRequested += OnToolbarAction;
        Toolbar.StyleChanged += OnToolbarStyleChanged;
        Toolbar.AutoFilterFillRequested += OnAutoFilterFillRequested;
        Toolbar.ResetRequested += PlaceOverlay;

        Loaded += Window_Loaded;
        SizeChanged += (_, _) =>
        {
            ClampOffset();
            ApplyCamera();
            PlaceOverlay();
        };
        DpiChanged += (_, _) => { _dpi = ReadDpi(); UpdateChrome(); };

        Toolbar.SetPan(true);
        UpdateChrome();
    }

    /// <summary>还没有打开任何图片（图片编辑会话据此复用空窗）。</summary>
    internal bool IsEmpty => !_doc.HasImage;

    // ============ 打开 ============

    /// <summary>切到空态（不显示图片）。</summary>
    public void PresentEmpty()
    {
        _generation++;
        CommitText();
        CancelGesture();
        CancelCropMode();
        LeaveAutoFilter();

        _doc.Clear();
        Canvas.Document = null;
        Canvas.InvalidateVisual();

        _fitted = false;
        EmptyState.Visibility = Visibility.Visible;
        Title = "图片编辑";
        Notice("");

        _panMode = true;
        SelectMark(-1);
        Toolbar.SetPan(true);

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

        CommitText();
        CancelGesture();
        CancelCropMode();
        LeaveAutoFilter();

        _applying = true;
        _doc.Load(image, fileName ?? "", sourcePath);
        _applying = false;

        Canvas.Document = _doc.Doc;
        Canvas.Width = image.PixelWidth;
        Canvas.Height = image.PixelHeight;
        Canvas.ShowSelectionChrome = false;

        _fitted = false;
        EmptyState.Visibility = Visibility.Collapsed;
        Title = $"图片编辑 — {_doc.Title}";
        Notice("");

        // 打开一张图 = 回到上游的默认「平移」模式
        _panMode = true;
        _tool = CaptureTool.Select;
        SelectMark(-1);
        Toolbar.Sync(CaptureTool.Select);
        Toolbar.SetPan(true);

        if (IsLoaded && Surface.ActualWidth > 1) FitToWindow();
        else Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(FitIfPending));

        UpdateChrome();
        PlaceOverlay();
    }

    // ============ 镜头（缩放 / 平移） ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _dpi = ReadDpi();
        FitIfPending();
        PlaceOverlay();
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
        PlaceOverlay();
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
        PlaceOverlay();
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

        // 就地输入框跟着镜头走（缩放 / 平移时用户可能正在打字）
        if (_textEditing) PlaceTextEntry(_textOrigin);
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

    // ============ 浮层（工具条 / 缩放条）摆位 ============

    /// <summary>
    /// 照上游 <c>anchoredToolbarPlacement</c>：工具条锚图片**右下角 + 4px**，下方放不下翻到
    /// 上方、右边放不下往左挪；用户拖动过就保持用户的位置（双击工具条空白复位）。
    /// 缩放条贴画布底部居中。
    /// </summary>
    private void PlaceOverlay()
    {
        if (!_doc.HasImage || Surface.ActualWidth < 2 || Surface.ActualHeight < 2) return;

        double width = Surface.ActualWidth;
        double height = Surface.ActualHeight;

        // 工具条整条夹进画布宽度（参数条本身是 WrapPanel，会换行而不是被裁掉）
        Toolbar.FitWidth(width);

        // 缩放条：底部居中
        ZoomBar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double zw = ZoomBar.DesiredSize.Width;
        double zh = ZoomBar.DesiredSize.Height;
        System.Windows.Controls.Canvas.SetLeft(ZoomBar, Math.Max(4, (width - zw) / 2));
        System.Windows.Controls.Canvas.SetTop(ZoomBar, Math.Max(4, height - zh - 12));

        if (Toolbar.UserMoved) return;

        var image = new Rect(_offset.X, _offset.Y,
            ContentSize.Width * _zoom, ContentSize.Height * _zoom);

        Toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double tw = Toolbar.DesiredSize.Width;
        double th = Toolbar.DesiredSize.Height;

        double left = image.Right + 4;
        double top = image.Bottom + 4;

        if (top + th > height - 4) top = image.Top - th - 4;        // 下方放不下 → 翻到上方
        if (left + tw > width - 4) left = image.Right - tw;         // 右边放不下 → 往左挪

        System.Windows.Controls.Canvas.SetLeft(Toolbar, Math.Max(4, Math.Min(left, Math.Max(4, width - tw - 4))));
        System.Windows.Controls.Canvas.SetTop(Toolbar, Math.Max(4, Math.Min(top, Math.Max(4, height - th - 4))));
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
        PlaceOverlay();
        e.Handled = true;
    }

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_doc.HasImage) return;

        // 浮层上的点击（工具条 / 缩放条 / 文字输入框）不参与画布手势
        var source = e.OriginalSource as DependencyObject;
        if (IsOnOverlay(source) || IsOnTextEntry(source)) return;

        // 点画布 = 正在输入的那一段先落地
        if (_textEditing && e.ChangedButton == MouseButton.Left) CommitText();

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

        // 平移：中键任何时候都能拖；左键在平移模式（默认）下也能拖
        if (e.ChangedButton == MouseButton.Middle ||
            (e.ChangedButton == MouseButton.Left && _panMode))
        {
            _panning = true;
            _panStart = e.GetPosition(Surface);
            _panOffset = _offset;
            Surface.CaptureMouse();
            Surface.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        // 自动打码：左键 = 点亮光标下的区域（拖 = 框选一批），松手统一填一次
        if (AutoFilterActive)
        {
            BeginAutoFilterGesture(ToImage(e.GetPosition(Surface)));
            Surface.CaptureMouse();
            e.Handled = true;
            return;
        }

        if (BeginDrawGesture(ToImage(e.GetPosition(Surface)), e.ClickCount))
        {
            // 文字 / 序号 / 水印 / 折线是「点一下」类：不抓鼠标，免得输入框点不进去
            if (_drag != EditDrag.None) Surface.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_doc.HasImage) return;

        if (_panning)
        {
            var now = e.GetPosition(Surface);
            _offset = new Point(_panOffset.X + (now.X - _panStart.X),
                _panOffset.Y + (now.Y - _panStart.Y));
            ClampOffset();
            ApplyCamera();
            PlaceOverlay();
            return;
        }

        var view = e.GetPosition(Surface);

        if (_cropping && _cropAxis != null)
        {
            _cropLast = PixelAt(view);
            _cropPointerInside = true;
            UpdateCropLayer();
            return;
        }

        if (_cropAxis != null)
        {
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
            return;
        }

        if (_drag != EditDrag.None)
        {
            UpdateDraw(ToImage(view));
            return;
        }

        // 自动打码：拖 = 框选一批，悬停 = 点亮光标下那一块
        if (AutoFilterActive)
        {
            var imagePoint = ToImage(view);
            if (_autoFilter.GestureActive) UpdateAutoFilterGesture(imagePoint);
            else UpdateAutoFilterHover(imagePoint);
            return;
        }

        UpdateHoverCursor(view);
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton is MouseButton.Middle or MouseButton.Left)
        {
            _panning = false;
            Surface.ReleaseMouseCapture();
            UpdateCanvasCursor();
            return;
        }

        if (_cropping && e.ChangedButton == MouseButton.Left)
        {
            _cropping = false;
            Surface.ReleaseMouseCapture();
            CommitCrop();
            e.Handled = true;
            return;
        }

        if (_drag != EditDrag.None && e.ChangedButton == MouseButton.Left)
        {
            FinishDraw(ToImage(e.GetPosition(Surface)));
            Surface.ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        if (AutoFilterActive && _autoFilter.GestureActive && e.ChangedButton == MouseButton.Left)
        {
            FinishAutoFilterGesture(ToImage(e.GetPosition(Surface)));
            Surface.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_panning || _cropping || _drag != EditDrag.None) return;

        if (AutoFilterActive && _autoFilter.HoverId >= 0)
        {
            _autoFilter.HoverId = -1;
            Canvas.InvalidateVisual();
        }

        if (!_cropPointerInside) return;

        _cropPointerInside = false;
        UpdateCropLayer();
    }

    /// <summary>光标处的图像像素（按当前裁剪轴取行 / 列，越界夹在 [0, 边长]）。</summary>
    private int PixelAt(Point view)
    {
        var content = ContentSize;
        var point = ToImage(view);
        int extent = _cropAxis == CropAxis.Columns ? (int)content.Width : (int)content.Height;
        double value = _cropAxis == CropAxis.Columns ? point.X : point.Y;
        return Math.Clamp((int)Math.Round(value), 0, Math.Max(0, extent));
    }

    private bool IsOnOverlay(DependencyObject? source) =>
        AncestorIs(source, Toolbar) || AncestorIs(source, ZoomBar);

    private static bool AncestorIs(DependencyObject? source, DependencyObject target)
    {
        for (var node = source; node != null;)
        {
            if (ReferenceEquals(node, target)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // ============ 撤销 / 重做 ============

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();

    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        CommitText();
        if (!_doc.Undo()) return;

        SelectMark(-1);
        RefreshMarks();
        KeepCameraAfterResize();
    }

    private void Redo()
    {
        CommitText();
        if (!_doc.Redo()) return;

        SelectMark(-1);
        RefreshMarks();
        KeepCameraAfterResize();
    }

    /// <summary>文档变了：换底图尺寸、重画、修正选中（下标可能已越界）。</summary>
    private void RefreshMarks()
    {
        Canvas.Document = _doc.Doc;

        if (_doc.HasImage)
        {
            Canvas.Width = _doc.Width;
            Canvas.Height = _doc.Height;
        }

        // 选中项可能被删掉 / 撤销掉了，按现存下标重新收一遍
        SelectMarks(_selected.ToArray());
        Canvas.InvalidateVisual();

        // 自动打码：图变过（撤销 / 裁剪 / 填充）就对齐识别结果，对不上就重扫
        SyncAutoFilter(detect: true);

        UpdateChrome();
        PlaceOverlay();
    }

    // ============ 工具条 ============

    private void OnToolbarAction(CaptureToolbar.CaptureAction action)
    {
        switch (action)
        {
            case CaptureToolbar.CaptureAction.Recognize: OpenRecognition(); break;
            case CaptureToolbar.CaptureAction.Copy: CopyImage(); break;
            case CaptureToolbar.CaptureAction.Pin: PinImage(); break;
            case CaptureToolbar.CaptureAction.Print: PrintImage(); break;
            case CaptureToolbar.CaptureAction.QuickSave: QuickSave(); break;
            case CaptureToolbar.CaptureAction.Save: SaveAs(); break;
        }
    }

    private void OnToolbarStyleChanged()
    {
        CaptureStyleStore.SaveSoon();
        ApplyStyleToSelection();
    }

    /// <summary>
    /// 识别：把**整张底图**（不带标注，照上游 <c>activate()</c> 用的 <c>document.image()</c>）交给结果窗。
    /// 文字 / 表格 / 二维码 / Markdown / HTML / 文档 / LaTeX / 翻译都在那一个窗里；
    /// 上游编辑窗把它们各摆一格工具，本项目只留这一个入口（与截图覆盖层同口径）。
    /// </summary>
    private void OpenRecognition()
    {
        if (!_doc.HasImage) return;

        // 照上游 activate()：先收起编辑状态（提交文字 / 结束手势 / 退出裁剪 / 退出自动打码）并切回平移
        EnterPanMode();

        if (_doc.Image is not { } image) return;

        CaptureResultWindow.Show(image, OcrEnginePreference.DefaultForNewProbe);
    }

    /// <summary>「水印」工具点一下铺一整张（照覆盖层）。</summary>
    private Mark.Watermark WatermarkMark => new(CurrentStyle.WatermarkSpecOf());

    // ============ 界面状态 ============

    private void UpdateChrome()
    {
        bool has = _doc.HasImage;

        FitBtn.IsEnabled = has;
        ActualBtn.IsEnabled = has;

        Toolbar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        ZoomBar.Visibility = has ? Visibility.Visible : Visibility.Collapsed;

        if (!has)
        {
            _cropAxis = null;
            _cropping = false;
            _cropPointerInside = false;
            CropLayer.ImageRect = Rect.Empty;
            CropLayer.Strip = Rect.Empty;
            CropLayer.InvalidateVisual();
        }

        Toolbar.SetCrop(has ? _cropAxis : null);
        Toolbar.SetUndo(has && _doc.CanUndo, has && _doc.CanRedo);

        ZoomText.Text = has ? $"{Math.Round(_zoom * _dpi * 100)}%" : "";
        InfoText.Text = has ? $"{_doc.Width} × {_doc.Height} px · {_doc.Title}" : "";

        UpdateCanvasCursor();
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

        CommitText();
        CancelGesture();

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
        // 正在输入文字：Enter / Shift+Enter / Esc 归输入框（它在隧道更里层，先在这里让开）
        if (_textEditing) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

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

            if (_autoFilter.GestureActive || _autoFilter.Marquee != null)
            {
                CancelAutoFilterGesture();
                e.Handled = true;
                return;
            }

            if (AutoFilterActive)
            {
                SetTool(CaptureTool.Select);
                e.Handled = true;
                return;
            }

            Close();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            DeleteSelection();
            e.Handled = true;
            return;
        }

        if (ctrl)
        {
            if (e.Key == Key.Z) { Undo(); e.Handled = true; return; }
            if (e.Key == Key.Y) { Redo(); e.Handled = true; return; }
            if (e.Key == Key.C) { CopyImage(); e.Handled = true; return; }
            if (e.Key == Key.V) { PasteFromClipboard(); e.Handled = true; return; }
            if (e.Key == Key.O) { ChooseImages(); e.Handled = true; return; }
            if (e.Key == Key.P) { PrintImage(); e.Handled = true; return; }
            if (e.Key == Key.T) { PinImage(); e.Handled = true; return; }
            if (e.Key == Key.R) { OpenRecognition(); e.Handled = true; return; }

            if (e.Key == Key.S)
            {
                if (shift) SaveAs();
                else QuickSave();
                e.Handled = true;
            }

            return;
        }

        // 工具快捷键（与截图覆盖层同一份表）
        if (ToolCatalog.ForKey(e.Key) is CaptureTool tool)
        {
            SetTool(tool);
            e.Handled = true;
        }
    }
}
