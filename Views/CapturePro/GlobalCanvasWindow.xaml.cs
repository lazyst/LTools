using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Hooks;
using LTools.Features.CapturePro;
using LTools.Native;

namespace LTools.Views.CapturePro;

/// <summary>
/// 全屏画布（照 snow-shot 的 <c>GlobalCanvasController</c> / <c>Session</c>）：
/// 盖住光标所在显示器的一块透明表面，随手画标注 / 圈重点，讲课时还能开激光笔与点击穿透。
/// </summary>
/// <remarks>
/// <para>
/// 与截图覆盖层的区别：**没有冻结底图、没有选区、没有完成动作**。工具只画在透明表面上，
/// 相机（缩放 / 平移）把「画布坐标」换成窗口 DIP，标注坐标永远是画布坐标，所以缩放 / 平移
/// 不会改标注本身。视图矩阵与摆位常量在 <see cref="GlobalCanvasLayout"/>，激光笔模型在
/// <see cref="GlobalCanvasLaser"/>。
/// </para>
/// <para>
/// 键盘：画布开着时 <see cref="HotkeyRegistry.Suspended"/> 为 true（工具字母键 / IME 打字正常），
/// 唯一例外是「打开 / 切换全屏画布」那条快捷键——<see cref="GlobalCanvasSession"/> 经
/// <see cref="HotkeyRegistry.TryFireOnly"/> 接管，再按一次 = 切点击穿透。
/// </para>
/// <para>
/// 分文件：手势在 <c>.Draw.cs</c>、文字在 <c>.Text.cs</c>、激光笔在 <c>.Laser.cs</c>、
/// 取色在 <c>.Picker.cs</c>、点击穿透在 <c>.ClickThrough.cs</c>、相机 / 摆位在 <c>.View.cs</c>。
/// </para>
/// </remarks>
public partial class GlobalCanvasWindow : Window
{
    private readonly System.Drawing.Rectangle _monitor;
    private readonly double _sx, _sy;
    private readonly CaptureDocument _doc;
    private readonly ToolStyleSet _styles = CaptureStyleStore.Current;

    /// <summary>画布坐标 → 窗口 DIP 的视图矩阵（相机）。</summary>
    private Matrix _view = Matrix.Identity;

    private double _zoom = 1;
    private Point _center;

    private bool _panning;
    private Point _panLast;

    private CaptureTool _tool = CaptureTool.Select;

    private readonly List<int> _selected = new();
    private int _selectedIndex = -1;

    private IntPtr _hwnd;
    private bool _closed;

    public GlobalCanvasWindow()
    {
        InitializeComponent();

        _monitor = ScreenPicker.MonitorUnderCursor();
        (_sx, _sy) = ScreenPickerWindow.MonitorScale(_monitor);
        if (_sx <= 0) _sx = 1;
        if (_sy <= 0) _sy = 1;

        _doc = new CaptureDocument(TransparentPixel(), new Int32Rect(
            _monitor.Left, _monitor.Top, Math.Max(1, _monitor.Width), Math.Max(1, _monitor.Height)), _sx);

        Canvas.Document = _doc;
        Canvas.ShowBase = false;
        Canvas.ShowSelectionChrome = false;

        Left = _monitor.Left / _sx;
        Top = _monitor.Top / _sy;
        Width = Math.Max(1, _monitor.Width) / _sx;
        Height = Math.Max(1, _monitor.Height) / _sy;

        // 工具条：只在画布模式建一次；照上游 canvasOptions() 只收不依赖底图的工具
        // 「记住上次工具」（默认关）：开着时从上次的绘图工具开始
        _tool = CaptureToolMemory.Start(CaptureTool.Select);
        Toolbar.Init(_styles, _tool, CaptureToolbar.ToolbarMode.GlobalCanvas);
        Toolbar.ToolChanged += SetTool;
        Toolbar.UndoRequested += Undo;
        Toolbar.RedoRequested += Redo;
        Toolbar.ActionRequested += OnToolbarAction;
        Toolbar.StyleChanged += OnToolbarStyleChanged;
        Toolbar.LaserToggleRequested += () => SetLaser(!_laserOn);
        Toolbar.ClickThroughToggleRequested += ToggleClickThrough;
        Toolbar.ResetRequested += () => PlaceToolbar(initial: true);

        // 工具条的高度会随「参数行」出现 / 收起变化；它锚在右下角，所以要跟着重新贴边，
        // 否则长出第二行时会被屏幕下边缘裁掉（踩过）。
        Toolbar.SizeChanged += (_, _) =>
        {
            if (Toolbar.UserMoved) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Toolbar.UserMoved) PlaceToolbar(initial: true);
            }), DispatcherPriority.Loaded);
        };

        SourceInitialized += Window_SourceInitialized;
        UpdateCanvasCursor();
    }

    private static BitmapSource TransparentPixel()
    {
        var pixel = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        pixel.Freeze();
        return pixel;
    }

    /// <summary>面板 / 快捷键 / 悬浮都能打开它：已经开着就只切点击穿透（见 <see cref="GlobalCanvasSession"/>）。</summary>
    internal bool ClickThrough => _clickThrough;

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // 窗口正好盖住那块显示器的物理像素（WPF 的 Left/Top 是 DIP，混合 DPI 下会差几像素）
        if (_hwnd != IntPtr.Zero)
        {
            Win32.SetWindowPos(_hwnd, Win32.HwndTopmost,
                _monitor.Left, _monitor.Top,
                Math.Max(1, _monitor.Width), Math.Max(1, _monitor.Height),
                Win32.SwpNoactivate | Win32.SwpShowwindow);
        }

        _center = new Point(Math.Max(1, Surface.ActualWidth) / 2, Math.Max(1, Surface.ActualHeight) / 2);
        UpdateView();

        // 先把工具条显出来再摆位：Collapsed 的元素 Measure 出来是 (0,0)，
        // 照那个尺寸摆会把工具条顶出屏幕右边（踩过）。
        Toolbar.Visibility = Visibility.Visible;
        UpdateLayout();
        PlaceToolbar(initial: true);
        Toolbar.SetLaserState(_laserOn);
        Toolbar.SetClickThroughState(_clickThrough);
        UpdateChrome();

        // 画布开着时全局快捷键整体让路：工具字母键 / IME 打字要能落进画布
        // （只留「打开 / 切换全屏画布」这一条，再按一次 = 切点击穿透）。
        // 窗口在宿主里、钩子在提权进程，所以这条状态经 InputGuard 推过去。
        InputGuard.SetSuspended(true, GlobalCanvasSession.HotkeyRef);
        // 鼠标钩子的裸右键长按（面板）与边缘滚轮让路：画布上的右键 / 滚轮归自己
        PickerHook.SetActive(true);

        StartTimers();

        Activate();
        Surface.Focus();
        Keyboard.Focus(Surface);
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateView();
        if (e.WidthChanged)
        {
            Toolbar.FitWidth(Math.Max(1, Surface.ActualWidth));
            PlaceToolbar(initial: Toolbar.UserMoved is false);
        }
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        if (_closed) return;
        _closed = true;

        CommitText();
        CancelGesture();

        StopTimers();
        SetLaser(false);
        _clickThrough = false;

        InputGuard.SetSuspended(false, null);
        PickerHook.SetActive(false);
    }

    // ============ 键盘 ============

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 正在输入文字：Enter / Shift+Enter / Esc 归输入框
        if (_textEditing) return;

        // 取色中：Esc 只退出取色（照上游 eventFilter）
        if (_pickerSampling)
        {
            if (e.Key == Key.Escape)
            {
                EndPicker();
                if (_colorTarget != CaptureTool.Picker) SetTool(_colorTarget);
                e.Handled = true;
            }
            return;
        }

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (e.Key == Key.Escape)
        {
            CommitText();
            CancelGesture();
            Close();                 // 照上游 global-canvas.exit：Esc 直接退出
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
            if (shift && e.Key == Key.Z) { ResetCanvas(); e.Handled = true; return; }
            if (e.Key == Key.Z) { Undo(); e.Handled = true; return; }
            if (e.Key == Key.Y) { Redo(); e.Handled = true; return; }
            return;
        }

        if (alt) return;

        // 绘图工具快捷键（照上游 DrawingShortcutSettings 的默认值）
        CaptureTool? tool = e.Key switch
        {
            Key.V => CaptureTool.Select,
            Key.D1 or Key.NumPad1 => CaptureTool.Shape,
            Key.D2 or Key.NumPad2 => CaptureTool.Arrow,
            Key.D3 or Key.NumPad3 or Key.P => CaptureTool.Pen,
            Key.D4 or Key.NumPad4 or Key.H => CaptureTool.HighlightRect,
            Key.D5 or Key.NumPad5 or Key.T => CaptureTool.Text,
            Key.D6 or Key.NumPad6 or Key.N => CaptureTool.Serial,
            Key.D8 or Key.NumPad8 or Key.E => CaptureTool.Eraser,
            Key.D9 or Key.NumPad9 => CaptureTool.Watermark,
            _ => null,
        };

        if (tool is { } next)
        {
            if (ToolCatalog.SupportsGlobalCanvas(next)) SetTool(next);
            e.Handled = true;
        }
    }

    // ============ 鼠标 ============

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough) return;

        // 点画布 = 正在输入的那一段先落地
        if (_textEditing && e.ChangedButton == MouseButton.Left) CommitText();

        // 取色：左键复制色值，别的键退出（照上游 colorSampling）
        if (_pickerSampling)
        {
            if (e.ChangedButton == MouseButton.Left) CommitPick(e.GetPosition(Surface));
            else EndPicker();
            e.Handled = true;
            return;
        }

        // 中键任何时候都能拖画面（上游 handleNavigation）
        if (e.ChangedButton == MouseButton.Middle)
        {
            CancelGesture();
            _panning = true;
            _panLast = e.GetPosition(Surface);
            Surface.CaptureMouse();
            Surface.Cursor = Cursors.SizeAll;
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        if (BeginDrawGesture(ToCanvas(e.GetPosition(Surface)), e.ClickCount))
        {
            // 文字 / 序号 / 水印 / 折线是「点一下」类：不抓鼠标，免得输入框点不进去
            if (_drag != EditDrag.None) Surface.CaptureMouse();
            e.Handled = true;
        }
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        var view = e.GetPosition(Surface);

        if (_panning)
        {
            var delta = view - _panLast;
            _panLast = view;
            _center = GlobalCanvasLayout.PannedCenter(_center.X, _center.Y, delta, _zoom);
            UpdateView();
            return;
        }

        if (_pickerSampling)
        {
            ShowPickerAt(view);
            return;
        }

        if (_drag != EditDrag.None)
        {
            UpdateDraw(ToCanvas(view));
            return;
        }

        UpdateHoverCursor(view);
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panning && e.ChangedButton == MouseButton.Middle)
        {
            _panning = false;
            Surface.ReleaseMouseCapture();
            UpdateCanvasCursor();
            return;
        }

        if (_drag != EditDrag.None && e.ChangedButton == MouseButton.Left)
        {
            FinishDraw(ToCanvas(e.GetPosition(Surface)));
            Surface.ReleaseMouseCapture();
            e.Handled = true;
        }
    }

    private void Surface_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_clickThrough || _pickerSampling) return;

        var mods = Keyboard.Modifiers;
        bool ctrl = (mods & ModifierKeys.Control) != 0;
        bool shift = (mods & ModifierKeys.Shift) != 0;

        if (ctrl || shift)
        {
            // Shift = 平移（Ctrl+Shift = 纵向）；Ctrl = 以指针为锚点缩放
            if (shift)
            {
                double distance = GlobalCanvasLayout.PanDistance(e.Delta, precise: false);
                var delta = ctrl ? new Vector(0, distance) : new Vector(distance, 0);
                _center = GlobalCanvasLayout.PannedCenter(_center.X, _center.Y, -delta, _zoom);
                UpdateView();
            }
            else
            {
                var pointer = e.GetPosition(Surface);
                double next = GlobalCanvasLayout.ZoomAfterWheel(_zoom, e.Delta);
                if (Math.Abs(next - _zoom) > 1e-9)
                {
                    _center = GlobalCanvasLayout.ZoomedCenter(_view, pointer, next,
                        Math.Max(1, Surface.ActualWidth), Math.Max(1, Surface.ActualHeight));
                    _zoom = next;
                    UpdateView();
                }
            }

            e.Handled = true;
        }

        // 普通滚轮不做事（上游只在 BrushEraser / 角度工具上调参数；全屏画布没有这两种依赖）
    }

    private void Surface_MouseLeave(object sender, MouseEventArgs e) => HidePicker();

    // ============ 工具 / 撤销 ============

    private void SetTool(CaptureTool tool)
    {
        if (_tool == tool) return;

        FinishPolyline();
        CommitText();
        CancelGesture();
        EndPicker();

        // 记下「取色取到的颜色写给谁」：切进取色之前的那个绘图工具
        if (_tool != CaptureTool.Picker) _colorTarget = _tool;

        _tool = tool;
        CaptureToolMemory.Remember(tool);
        Toolbar.Sync(tool);

        if (tool != CaptureTool.Select) SelectMark(-1);

        UpdateCanvasCursor();
        UpdateChrome();

        // 取色工具一选中就进取色（上游是点调色板上的取色按钮进）
        if (tool == CaptureTool.Picker) BeginPicker();
    }

    private void Undo()
    {
        CommitText();
        CancelGesture();
        EndPicker();

        _doc.Undo();
        SelectMark(-1);
        RefreshMarks();
    }

    private void Redo()
    {
        CommitText();
        CancelGesture();
        EndPicker();

        _doc.Redo();
        SelectMark(-1);
        RefreshMarks();
    }

    /// <summary>清空画布（照上游 <c>reset_canvas</c> 快捷键 Ctrl+Shift+Z）：进撤销栈，误清还能回来。</summary>
    private void ResetCanvas()
    {
        CommitText();
        CancelGesture();
        SelectMark(-1);
        _doc.ClearMarks();
        RefreshMarks();
    }

    private void OnToolbarAction(CaptureToolbar.CaptureAction action)
    {
        if (action == CaptureToolbar.CaptureAction.Exit)
        {
            CommitText();
            CancelGesture();
            Close();
        }
    }

    private void OnToolbarStyleChanged()
    {
        CaptureStyleStore.SaveSoon();
        ApplyStyleToSelection();
    }

    /// <summary>文档变了：重画、按现存下标修正选中、刷新撤销按钮。</summary>
    private void RefreshMarks()
    {
        SelectMarks(_selected.ToArray());
        Canvas.InvalidateVisual();
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        Toolbar.SetUndo(_doc.CanUndo, _doc.CanRedo);
        UpdateCanvasCursor();
    }

    /// <summary>「水印」工具点一下铺一整张（照覆盖层 / 图片编辑窗）。</summary>
    private Mark.Watermark WatermarkMark => new(CurrentStyle.WatermarkSpecOf());

    // ============ 坐标 ============

    /// <summary>窗口 DIP → 画布坐标。</summary>
    private Point ToCanvas(Point view) => GlobalCanvasLayout.ToCanvas(_view, view);

    private static Rect EditLimit => new(-1e6, -1e6, 2e6, 2e6);
}
