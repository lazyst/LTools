using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Views.CapturePro;

/// <summary>
/// 框选：拖新选区、拖内部移动、拖八个把手缩放、方向键微调、单击选窗口 / 控件。
/// </summary>
/// <remarks>
/// <para>
/// 全部用窗口级 <c>Preview</c> 事件：按下之后 WPF 会把捕获挂到窗口上，之后 <c>MouseMove</c>
/// 只路由到窗口，挂在 Canvas 上的处理器收不到——框选会「按下就卡住」。
/// </para>
/// <para>
/// 拖动期间只认「按下那一刻」算出来的吸附线：后台窗口的边不该把框吸走，移动中重算也会让手感发飘。
/// 按住 Ctrl 临时关闭吸附（和框选取图一致）。
/// </para>
/// </remarks>
public partial class CaptureOverlayWindow
{
    private const double SnapDistance = 8;   // 边 / 角吸附距离（DIP）
    private const double PressSlop = 3;      // 按下后位移小于此值算「单击」而不是拖动

    private Point _pressAt;
    private bool _pressMoved;
    private SnapBox? _pressTarget;

    private enum DragMode { None, New, Move, Resize }

    private enum Handle { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 完成动作栏是普通按钮：点它不能当成开始框选
        if (IsOnActionBar(e.OriginalSource as DependencyObject)) return;

        Point p = Clamp(e.GetPosition(Layer));

        // 双击选区内部 = 确定（和大多数截图工具一致）
        if (e.ClickCount == 2 && _sel is Rect dbl && dbl.Contains(p))
        {
            Accept(FinishKind.Copy);
            e.Handled = true;
            return;
        }

        Handle hit = HitTest(p);
        _pressAt = p;
        _pressMoved = false;
        _pressTarget = null;

        if (hit != Handle.None)
        {
            _drag = hit == Handle.Move ? DragMode.Move : DragMode.Resize;
            _handle = hit;
            _originRect = _sel ?? default;
        }
        else
        {
            // 空白处按下：先按自由拖动开始；松手时若几乎没动、且光标下有窗口 / 控件，就选中它
            _pressTarget = _hoverTarget;
            _drag = DragMode.New;
            _handle = Handle.None;
            _originRect = _sel ?? default;   // Esc 放弃这次拖动时还原成原来的选区
            _sel = new Rect(p, p);
            SetHover(null);
        }

        _origin = p;

        // 吸附线只取「按下时最上层那个窗口」的边，拖动期间不再重算
        RebuildSnapLines();

        Cursor = _drag == DragMode.New ? Cursors.Cross : CursorFor(_handle);
        CaptureMouse();
        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Point p = Clamp(e.GetPosition(Layer));

        if (_drag == DragMode.None)
        {
            Cursor = CursorFor(HitTest(p));
            UpdateHover(p);
            return;
        }

        if (!_pressMoved &&
            (Math.Abs(p.X - _pressAt.X) > PressSlop || Math.Abs(p.Y - _pressAt.Y) > PressSlop))
            _pressMoved = true;

        switch (_drag)
        {
            case DragMode.New:
            {
                // 拖出来的边角吸附到附近窗口 / 控件的边（Ctrl 临时关闭）；Shift 锁正方形
                Point end = SnapPoint(p);
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) end = SquareOf(_origin, end);
                _sel = RectFrom(_origin, end);
                break;
            }
            case DragMode.Move:
                _sel = SnapMove(MoveTo(_originRect, p - _origin));
                break;
            case DragMode.Resize:
                _sel = SnapResize(ResizeFrom(_originRect, _handle, p - _origin), _handle);
                break;
        }

        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        _drag = DragMode.None;
        _handle = Handle.None;
        ReleaseMouseCapture();

        // 几乎没移动 = 单击：选中光标下的窗口 / 控件；拖动了就是自由框选，不抢
        if (!_pressMoved && _pressTarget is SnapBox target) _sel = ToDipRect(target.Bounds);
        else if (!IsValid(_sel)) _sel = null;   // 误点 / 太小：清掉重来，不关闭窗口

        _pressTarget = null;
        Cursor = Cursors.Arrow;
        UpdateChrome();
        e.Handled = true;
    }

    /// <summary>放弃当前拖动：正在拖新选区就清掉，拖把手 / 移动就还原。</summary>
    private void CancelDrag()
    {
        _drag = DragMode.None;
        _handle = Handle.None;
        _pressTarget = null;
        _sel = _sel is Rect s && IsValid(s) && _originRect != default ? _originRect : null;
        if (_sel is Rect back && !IsValid(back)) _sel = null;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        Cursor = Cursors.Arrow;
        UpdateChrome();
    }

    /// <summary>事件源是否来自完成动作栏（按钮 / 它的父 Border）。</summary>
    private bool IsOnActionBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ActionBar)) return true;
            if (source is Button) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ============ 选区计算 ============

    private bool IsValid(Rect? r) => r is Rect s && s.Width >= MinSize && s.Height >= MinSize;

    /// <summary>把窗口 DIP 选区同步成「屏幕物理像素」写进文档（导出按它裁）。</summary>
    private void SyncArea()
    {
        if (_sel is not Rect s) return;

        var (x1, y1) = ToImage(s.TopLeft);
        var (x2, y2) = ToImage(s.BottomRight);
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Min(Math.Abs(x2 - x1) + 1, _shot.PixelWidth - x);
        int h = Math.Min(Math.Abs(y2 - y1) + 1, _shot.PixelHeight - y);
        if (w < 2 || h < 2) return;

        _doc.Area = new Int32Rect(_monitor.X + x, _monitor.Y + y, w, h);
    }

    private Handle HitTest(Point p)
    {
        if (_sel is not Rect s) return Handle.None;

        foreach (var h in new[]
                 {
                     Handle.TopLeft, Handle.TopRight, Handle.BottomLeft, Handle.BottomRight,
                     Handle.Top, Handle.Bottom, Handle.Left, Handle.Right,
                 })
        {
            Point at = GripPoint(s, h);
            if (Math.Abs(p.X - at.X) <= GripHit && Math.Abs(p.Y - at.Y) <= GripHit) return h;
        }

        return s.Contains(p) ? Handle.Move : Handle.None;
    }

    private static Point GripPoint(Rect r, Handle h)
    {
        double cx = r.X + r.Width / 2;
        double cy = r.Y + r.Height / 2;
        return h switch
        {
            Handle.TopLeft => new Point(r.Left, r.Top),
            Handle.TopRight => new Point(r.Right, r.Top),
            Handle.BottomLeft => new Point(r.Left, r.Bottom),
            Handle.BottomRight => new Point(r.Right, r.Bottom),
            Handle.Top => new Point(cx, r.Top),
            Handle.Bottom => new Point(cx, r.Bottom),
            Handle.Left => new Point(r.Left, cy),
            Handle.Right => new Point(r.Right, cy),
            _ => new Point(cx, cy),
        };
    }

    private static Cursor CursorFor(Handle h) => h switch
    {
        Handle.TopLeft or Handle.BottomRight => Cursors.SizeNWSE,
        Handle.TopRight or Handle.BottomLeft => Cursors.SizeNESW,
        Handle.Top or Handle.Bottom => Cursors.SizeNS,
        Handle.Left or Handle.Right => Cursors.SizeWE,
        Handle.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private Rect MoveTo(Rect r, Vector delta)
    {
        double x = Math.Clamp(r.X + delta.X, 0, Math.Max(0, ActualWidth - r.Width));
        double y = Math.Clamp(r.Y + delta.Y, 0, Math.Max(0, ActualHeight - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    private Rect ResizeFrom(Rect r, Handle h, Vector d)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;

        if (h is Handle.Left or Handle.TopLeft or Handle.BottomLeft) left += d.X;
        if (h is Handle.Right or Handle.TopRight or Handle.BottomRight) right += d.X;
        if (h is Handle.Top or Handle.TopLeft or Handle.TopRight) top += d.Y;
        if (h is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight) bottom += d.Y;

        left = Math.Clamp(left, 0, ActualWidth);
        right = Math.Clamp(right, 0, ActualWidth);
        top = Math.Clamp(top, 0, ActualHeight);
        bottom = Math.Clamp(bottom, 0, ActualHeight);

        // 小于最小尺寸时保持上一次的选区，避免框被拖成 0
        if (right - left < MinSize || bottom - top < MinSize) return r;

        return new Rect(left, top, right - left, bottom - top);
    }

    private static Rect RectFrom(Point a, Point b) => new(
        Math.Min(a.X, b.X), Math.Min(a.Y, b.Y),
        Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    /// <summary>按住 Shift 时把拖拽终点修正成正方形（保持拖拽方向）。</summary>
    private static Point SquareOf(Point origin, Point end)
    {
        double dx = end.X - origin.X;
        double dy = end.Y - origin.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        if (side < 1) return end;
        return new Point(origin.X + (dx < 0 ? -side : side), origin.Y + (dy < 0 ? -side : side));
    }
}
