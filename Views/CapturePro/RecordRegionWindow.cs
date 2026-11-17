using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using LTools.Features.CapturePro;
using LTools.Native;
using ShapePath = System.Windows.Shapes.Path;

namespace LTools.Views.CapturePro;

/// <summary>
/// 「还没开录」时摆在屏幕上的录屏区域框：看得出要录哪一块，边缘能拖动改大小，右下角的圆钮能整块挪。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenrecordingareawindow.cpp</c>（空闲态 <c>InputMode::RegionEditing</c>）：
/// 蓝框 <c>#4096FF</c>（2 物理像素 + 1 像素透明缝）、边缘 6 逻辑像素可抓、右下角 32 逻辑像素的
/// 半透明黑圆钮 + 白色四向箭头（悬停变蓝、按下更深）、区域最小 10 物理像素。
/// </para>
/// <para>
/// 三条实现要点：
/// ① **分层窗口 + 像素级命中**：<c>AllowsTransparency</c> 的窗口按像素 alpha 做命中测试，
///    所以只有「边框那圈 alpha 2 的抓取带」和「把手」可点，**中间是真的点得过去**（上游靠
///    <c>inputSurfaceColor</c> alpha 2 + 动态 <c>WS_EX_TRANSPARENT</c>，这里交给系统按像素判断）；
/// ② **不抢焦点**（<c>ShowActivated=False</c> + <c>WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW</c>）：
///    摆框时用户还在别的程序里操作；位置 / 尺寸全用 <c>GetCursorPos</c> 的物理像素算，
///    再按窗口 DPI 换成 DIP；
/// ③ 开始录制时由 <see cref="CaptureRecordWindow"/> 直接关掉它——录制期间换成
///    <see cref="RecordBorderWindow"/>（红 / 琥珀两态、整窗点击全透）。
/// </para>
/// </remarks>
internal sealed class RecordRegionWindow : Window
{
    private static readonly Brush MaskBrush = Frozen(Color.FromArgb(115, 0, 0, 0));
    private static readonly Brush HoverBrush = Frozen(Color.FromRgb(0x16, 0x77, 0xFF));
    private static readonly Brush PressedBrush = Frozen(Color.FromRgb(0x09, 0x58, 0xD9));
    private static readonly Brush HitBrush = Frozen(Color.FromArgb(2, 0, 0, 0));

    private static readonly Geometry MoveIcon = FrozenGeometry(
        "M9,0 L12,3 H10 V8 H15 V6 L18,9 L15,12 V10 H10 V15 H12 L9,18 L6,15 H8 V10 H3 V12 L0,9 L3,6 V8 H8 V3 H6 Z");

    private readonly Canvas _canvas = new();
    private readonly Border _handle;
    private readonly Rectangle _frameLeft = FrameRect();
    private readonly Rectangle _frameRight = FrameRect();
    private readonly Rectangle _frameTop = FrameRect();
    private readonly Rectangle _frameBottom = FrameRect();
    private readonly Rectangle _hitLeft;
    private readonly Rectangle _hitRight;
    private readonly Rectangle _hitTop;
    private readonly Rectangle _hitBottom;

    private Int32Rect _area;
    private double _scale = 1;
    private double _dipWidth = 1;
    private double _dipHeight = 1;

    private bool _resizing;
    private bool _moving;
    private bool _handleDown;
    private Rectangle? _pressRect;
    private Int32Rect _origin;
    private ResizeEdges _pressEdges;
    private ResizeEdges _effectiveEdges;
    private Win32.Point _originPointer;

    public RecordRegionWindow(Int32Rect area)
    {
        _area = area;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        MaxWidth = double.PositiveInfinity;
        MaxHeight = double.PositiveInfinity;

        var frameBrush = Frozen(RecordBorderWindow.IdleColor);
        _frameLeft.Fill = frameBrush;
        _frameRight.Fill = frameBrush;
        _frameTop.Fill = frameBrush;
        _frameBottom.Fill = frameBrush;

        _hitLeft = HitRect();
        _hitRight = HitRect();
        _hitTop = HitRect();
        _hitBottom = HitRect();

        _handle = new Border
        {
            Background = MaskBrush,
            CornerRadius = new CornerRadius(RecordRegionGeometry.HandleSideLogical / 2),
            Cursor = Cursors.SizeAll,
            ToolTip = "移动录制区域",
        };
        _handle.Child = new ShapePath
        {
            Data = MoveIcon,
            Fill = Brushes.White,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
        };
        _handle.MouseEnter += (_, _) => SetHandleBrush(_handleDown ? PressedBrush : HoverBrush);
        _handle.MouseLeave += (_, _) => SetHandleBrush(_handleDown ? PressedBrush : MaskBrush);
        _handle.MouseLeftButtonDown += Handle_Down;

        // 拖拽期间鼠标会离开那圈窄带：统一在窗口上收移动 / 松开（CaptureMouse 也是抓在窗口上的）。
        MouseMove += Drag_Move;
        MouseLeftButtonUp += Drag_Up;

        _canvas.Children.Add(_frameLeft);
        _canvas.Children.Add(_frameRight);
        _canvas.Children.Add(_frameTop);
        _canvas.Children.Add(_frameBottom);
        _canvas.Children.Add(_hitLeft);
        _canvas.Children.Add(_hitRight);
        _canvas.Children.Add(_hitTop);
        _canvas.Children.Add(_hitBottom);
        _canvas.Children.Add(_handle);

        Content = _canvas;

        Loaded += (_, _) => ApplyPlacement();
        DpiChanged += (_, _) => ApplyPlacement();
    }

    /// <summary>选区（屏幕物理像素）变化：拖边缘 / 拖把手 / 外部设值都会走它。</summary>
    internal event Action<Int32Rect>? AreaChanged;

    /// <summary>当前选区（屏幕物理像素）。</summary>
    internal Int32Rect Area => _area;

    /// <summary>整块挪走（外部改选区，不触发 <see cref="AreaChanged"/>）。</summary>
    internal void MoveTo(Int32Rect area)
    {
        if (area == _area) return;
        _area = area;
        ApplyPlacement();
    }

    // ============ 落点与布局 ============

    private void ApplyPlacement()
    {
        var screen = CaptureRecordWindow.ScreenOf(_area);
        _scale = screen is { Scale: > 0 } ? screen.Scale : 1;

        var frame = RecordRegionGeometry.FrameBounds(_area);
        _dipWidth = Math.Max(1, frame.Width) / _scale;
        _dipHeight = Math.Max(1, frame.Height) / _scale;

        Left = frame.X / _scale;
        Top = frame.Y / _scale;
        Width = _dipWidth;
        Height = _dipHeight;

        Layout();
    }

    private void Layout()
    {
        double thickness = RecordRegionGeometry.BorderPhysical / _scale;
        double bandH = RecordRegionGeometry.HitBandDip(_area, _scale, horizontal: true);
        double bandV = RecordRegionGeometry.HitBandDip(_area, _scale, horizontal: false);

        Place(_frameTop, 0, 0, _dipWidth, thickness);
        Place(_frameBottom, 0, _dipHeight - thickness, _dipWidth, thickness);
        Place(_frameLeft, 0, 0, thickness, _dipHeight);
        Place(_frameRight, _dipWidth - thickness, 0, thickness, _dipHeight);

        Place(_hitLeft, 0, 0, bandH, _dipHeight);
        Place(_hitRight, _dipWidth - bandH, 0, bandH, _dipHeight);
        Place(_hitTop, 0, 0, _dipWidth, bandV);
        Place(_hitBottom, 0, _dipHeight - bandV, _dipWidth, bandV);

        var (side, inset) = RecordRegionGeometry.HandleMetrics(_area, _scale);
        double inner = RecordRegionGeometry.FrameEdgePhysical / _scale;
        double areaW = Math.Max(1, _area.Width) / _scale;
        double areaH = Math.Max(1, _area.Height) / _scale;

        _handle.CornerRadius = new CornerRadius(side / 2);
        Place(_handle, inner + areaW - side - inset, inner + areaH - side - inset, side, side);

        if (_handle.Child is ShapePath icon)
        {
            double glyph = side * 18 / RecordRegionGeometry.HandleSideLogical;
            icon.Width = glyph;
            icon.Height = glyph;
        }
    }

    private static void Place(FrameworkElement element, double left, double top, double width,
        double height)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = Math.Max(0, width);
        element.Height = Math.Max(0, height);
    }

    private static Rectangle FrameRect() => new() { IsHitTestVisible = false };

    private Rectangle HitRect()
    {
        var rect = new Rectangle { Fill = HitBrush };

        rect.MouseMove += (_, _) =>
        {
            if (_resizing || _moving) return;
            var (x, y) = CursorPhysical();
            rect.Cursor = CursorOf(RecordRegionGeometry.EdgesAt(_area, x, y, _scale));
        };
        rect.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left) return;
            BeginResize(rect);
            e.Handled = true;
        };
        return rect;
    }

    // ============ 拖拽 ============

    private void BeginResize(Rectangle source)
    {
        if (!Win32.GetCursorPos(out var pointer)) return;

        _resizing = true;
        _moving = false;
        _pressRect = source;
        _origin = _area;
        _originPointer = pointer;
        _pressEdges = RecordRegionGeometry.EdgesAt(_area, pointer.X, pointer.Y, _scale);
        _effectiveEdges = _pressEdges;

        source.Cursor = CursorOf(_pressEdges);
        CaptureMouse();
    }

    private void Handle_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!Win32.GetCursorPos(out var pointer)) return;

        _moving = true;
        _resizing = false;
        _pressRect = null;
        _handleDown = true;
        _origin = _area;
        _originPointer = pointer;
        SetHandleBrush(PressedBrush);
        CaptureMouse();
        e.Handled = true;
    }

    private void Drag_Move(object sender, MouseEventArgs e)
    {
        if (!_resizing && !_moving) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        if (!Win32.GetCursorPos(out var pointer)) return;

        int dx = pointer.X - _originPointer.X;
        int dy = pointer.Y - _originPointer.Y;

        if (_moving)
        {
            Apply(RecordRegionGeometry.Move(_origin, dx, dy));
            return;
        }

        var (rect, effective) =
            RecordRegionGeometry.Resize(_origin, _pressEdges, dx, dy, _effectiveEdges);
        _effectiveEdges = effective;
        if (_pressRect != null) _pressRect.Cursor = CursorOf(effective);
        Apply(rect);
    }

    private void Drag_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_resizing && !_moving) return;
        EndDrag();
        e.Handled = true;
    }

    private void EndDrag()
    {
        var press = _pressRect;
        _resizing = false;
        _moving = false;
        _pressRect = null;
        _handleDown = false;
        _handle.Background = MaskBrush;
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (press == null) return;
        var (x, y) = CursorPhysical();
        press.Cursor = CursorOf(RecordRegionGeometry.EdgesAt(_area, x, y, _scale));
    }

    /// <summary>改选区：重新摆窗口 + 通知宿主（面板跟着挪、摘要跟着刷新）。</summary>
    private void Apply(Int32Rect area)
    {
        if (area == _area) return;

        _area = area;
        ApplyPlacement();
        AreaChanged?.Invoke(area);
    }

    private (int X, int Y) CursorPhysical()
    {
        if (Win32.GetCursorPos(out var pointer)) return (pointer.X, pointer.Y);
        return (_area.X, _area.Y);
    }

    private void SetHandleBrush(Brush brush)
    {
        if (_handle.Background != brush) _handle.Background = brush;
    }

    private static Cursor CursorOf(ResizeEdges edges)
    {
        bool left = (edges & ResizeEdges.Left) != 0;
        bool right = (edges & ResizeEdges.Right) != 0;
        bool top = (edges & ResizeEdges.Top) != 0;
        bool bottom = (edges & ResizeEdges.Bottom) != 0;

        if ((left && top) || (right && bottom)) return Cursors.SizeNWSE;
        if ((right && top) || (left && bottom)) return Cursors.SizeNESW;
        if (left || right) return Cursors.SizeWE;
        if (top || bottom) return Cursors.SizeNS;
        return Cursors.Arrow;
    }

    // ============ 窗口样式 ============

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayShell.MakeNonActivating(this, excludeFromCapture: true);
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Geometry FrozenGeometry(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
