using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 框选与标注的鼠标总入口：拖新选区、拖内部移动、拖八个把手缩放、画标注、拖已有标注。
/// </summary>
/// <remarks>
/// <para>
/// 全部用窗口级 <c>Preview</c> 事件：按下之后 WPF 会把捕获挂到窗口上，之后 <c>MouseMove</c>
/// 只路由到窗口，挂在 Canvas 上的处理器收不到——框选会「按下就卡住」。
/// </para>
/// <para>
/// 按下时的判断顺序固定为：工具栏 / 动作栏 / 文字输入框让路 → 把手（任何工具都能改框）
/// → 选区内（选择工具拖标注，其余工具画标注）→ 选区外（重新框选）。
/// 具体怎么画在 <c>.Annotate.cs</c>，工具状态在 <c>.Tools.cs</c>。
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
    private const double MarkHitSlop = 4;    // 点中已有标注的宽容距离（DIP）

    private Point _pressAt;
    private bool _pressMoved;
    private SnapBox? _pressTarget;

    private enum DragMode { None, New, Move, Resize, Draw, Erase, Mark }

    private enum Handle { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 完成动作栏 / 浮动工具栏都是普通控件：点它们不能当成开始框选或画标注
        if (IsOnActionBar(e.OriginalSource as DependencyObject)) return;
        if (IsOnToolbar(e.OriginalSource as DependencyObject)) return;

        // 点在文字输入框里 = 只是挪光标
        if (IsOnTextEntry(e.OriginalSource as DependencyObject)) return;

        // 正在打字：先在原地提交，再按常规处理这一下点击
        CommitText();

        Point p = Clamp(e.GetPosition(Layer));

        // 取色：单击就把光标下的颜色复制走，不框选、不落标注、不关覆盖层
        if (_tool == CaptureTool.Picker)
        {
            PickColor(p);
            e.Handled = true;
            return;
        }

        Handle hit = HitTest(p);
        bool inside = _sel is Rect area && IsValid(area) && area.Contains(p);

        _pressAt = p;
        _pressMoved = false;
        _pressTarget = null;
        _origin = p;

        // 1. 八个把手优先：任何工具下都能改框
        if (inside && hit is not (Handle.None or Handle.Move))
        {
            _originRect = _sel!.Value;
            _drag = DragMode.Resize;
            _handle = hit;
            RebuildSnapLines();
            StartDrag(e, CursorFor(hit));
            return;
        }

        // 2. 选区内：选择工具拖标注，其余工具画标注
        if (inside)
        {
            if (_tool == CaptureTool.Select)
            {
                // 双击（和框选取图一致）= 确定
                if (e.ClickCount == 2)
                {
                    Accept(FinishKind.Copy);
                    e.Handled = true;
                    return;
                }

                int mark = _doc.HitTest(p, MarkHitSlop);
                if (mark >= 0)
                {
                    BeginMarkDrag(p, mark);
                    StartDrag(e, Cursors.SizeAll);
                    return;
                }

                _originRect = _sel!.Value;
                _drag = DragMode.Move;
                _handle = Handle.Move;
                RebuildSnapLines();
                StartDrag(e, Cursors.SizeAll);
                return;
            }

            BeginToolGesture(p, e.ClickCount);
            bool dragging = _drag is DragMode.Draw or DragMode.Erase;
            Cursor = _tool == CaptureTool.Text ? Cursors.IBeam : Cursors.Cross;
            if (dragging) CaptureMouse();
            UpdateChrome();
            e.Handled = true;
            return;
        }

        // 3. 选区外 / 还没选区：重新框选；松手时几乎没动就选中光标下的窗口 / 控件
        _pressTarget = _hoverTarget;
        _drag = DragMode.New;
        _handle = Handle.None;
        _originRect = _sel ?? default;   // Esc 放弃这次拖动时还原成原来的选区
        _sel = new Rect(p, p);
        SetHover(null);

        RebuildSnapLines();   // 用按下那一刻的命中做吸附，拖动期间不再重算
        StartDrag(e, Cursors.Cross);
    }

    /// <summary>按下之后共用的收尾：改光标、捕获鼠标、刷界面。</summary>
    private void StartDrag(MouseButtonEventArgs e, Cursor cursor)
    {
        Cursor = cursor;
        CaptureMouse();
        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Point p = Clamp(e.GetPosition(Layer));

        if (_drag == DragMode.None)
        {
            // 光标停在工具栏 / 输入框上时，别去动悬停候选和光标形状
            if (_textEditing || IsOverToolbar) return;

            // 取色：光标旁跟一块色卡，不参与吸附 / 悬停探测
            if (_tool == CaptureTool.Picker)
            {
                Cursor = Cursors.Cross;
                ShowPickerAt(p);
                return;
            }
            HidePicker();

            RefreshCursor(p);
            if (!IsValid(_sel)) UpdateHover(p);
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
            case DragMode.Draw:
                UpdateToolDrag(p);
                break;
            case DragMode.Erase:
                EraseAt(p);
                break;
            case DragMode.Mark:
                UpdateMarkDrag(p);
                break;
        }

        UpdateChrome();
        e.Handled = true;
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        Point p = Clamp(e.GetPosition(Layer));
        DragMode mode = _drag;

        _drag = DragMode.None;
        _handle = Handle.None;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }

        switch (mode)
        {
            case DragMode.New:
                // 几乎没移动 = 单击：选中光标下的窗口 / 控件；拖动了就是自由框选，不抢
                if (!_pressMoved && _pressTarget is SnapBox target) _sel = ToDipRect(target.Bounds);
                else if (!IsValid(_sel)) _sel = null;   // 误点 / 太小：清掉重来，不关闭窗口
                break;

            case DragMode.Draw:
                FinishToolDrag(p);
                break;

            case DragMode.Mark:
                CommitMarkDrag(p);
                break;
        }

        _pressTarget = null;
        RefreshCursor(p);
        UpdateChrome();
        e.Handled = true;
    }

    /// <summary>放弃当前手势：画到一半的丢掉，改框 / 移动就还原成按下前的选区。</summary>
    private void CancelDrag()
    {
        bool wasFrame = _drag is DragMode.New or DragMode.Move or DragMode.Resize;

        if (_drag == DragMode.Mark) CancelMarkDrag();
        else if (_drag is DragMode.Draw or DragMode.Erase) Surface.Preview = null;

        _drag = DragMode.None;
        _handle = Handle.None;
        _pressTarget = null;

        if (wasFrame)
        {
            _sel = _originRect != default ? _originRect : null;
            if (_sel is Rect back && !IsValid(back)) _sel = null;
        }

        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        UpdateCursorForTool();
        UpdateChrome();
    }

    /// <summary>光标形状：选择工具看把手 / 标注，文字是 I 形，其余是十字。</summary>
    private void RefreshCursor(Point p)
    {
        if (_textEditing) return;

        Handle hit = HitTest(p);
        Cursor = _tool switch
        {
            CaptureTool.Select when hit != Handle.None => CursorFor(hit),
            CaptureTool.Select when !IsValid(_sel) => Cursors.Cross,
            CaptureTool.Select => _doc.HitTest(p, MarkHitSlop) >= 0 ? Cursors.SizeAll : Cursors.Arrow,
            CaptureTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
    }

    /// <summary>事件源是否来自完成动作栏（按钮 / 它的父 Border）。</summary>
    private bool IsOnActionBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ActionBar)) return true;
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
