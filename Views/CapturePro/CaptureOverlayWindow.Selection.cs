using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
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

    private enum DragMode { None, New, Move, Resize, Draw, Erase, Mark, MarkResize, Lasso }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 完成动作栏 / 浮动工具栏 / 选区形状条都是普通控件：点它们不能当成开始框选或画标注
        if (IsOnActionBar(e.OriginalSource as DependencyObject)) return;
        if (IsOnToolbar(e.OriginalSource as DependencyObject)) return;
        if (IsOnShapeBar(e.OriginalSource as DependencyObject)) return;

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

        // 折线选区正在逐点单击：这一下只补一个点（再点一下角落也不会把它变成「拖动选区」）
        if (_selPolyActive)
        {
            AddSelectionPoint(p, e.ClickCount);
            e.Handled = true;
            return;
        }

        FrameHandle hit = HitTest(p);
        // 自由 / 折线选区按点列判「在不在里面」：外接框的四个角是空的，点在那里等于点在外面（重新框）
        bool inside = _sel is Rect area && IsValid(area) && area.Contains(p) &&
                      (_polyDip is not { Length: >= SelectionShape.MinPoints } poly || SelectionShape.Contains(poly, p));

        _pressAt = p;
        _pressMoved = false;
        _pressTarget = null;
        _origin = p;

        // 0. 选中标注的把手优先：用户刚把它选上，这一下的意图就是改它的大小
        //    （框选的八个把手仍在，只是让位给「正在编辑的那一条」）
        FrameHandle markHandle = MarkHandleAt(p);
        if (markHandle is not (FrameHandle.None or FrameHandle.Move))
        {
            BeginMarkResize(p, markHandle);
            StartDrag(e, CursorFor(markHandle));
            return;
        }

        // 1. 八个把手优先：任何工具下都能改框。
        //    这里**不能**再要求 `inside`：把手的命中半径是 8 DIP，用户常常从框**外侧**那圈
        //    抓边（光标已经变成 SizeWE / SizeNS），这时 `Rect.Contains` 是 false，会被当成
        //    「选区外 = 重新框选」——松手时框太小被清掉，看着就是「按住拖动退出了截图」。
        if (hit is not (FrameHandle.None or FrameHandle.Move))
        {
            _originRect = _sel!.Value;
            _originPoly = _polyDip;
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
                _originPoly = _polyDip;
                _drag = DragMode.Move;
                _handle = FrameHandle.Move;
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

        // 3. 选区外 / 还没选区：按当前形状重新框选；松手时几乎没动就选中光标下的窗口 / 控件
        _pressTarget = _hoverTarget;
        _handle = FrameHandle.None;
        _originRect = _sel ?? default;   // Esc 放弃这次拖动时还原成原来的选区
        _originPoly = _polyDip;

        if (_shape == SelectionMode.Polyline)
        {
            // 折线：这一下只是第一个点，收尾靠双击 / Enter（_selPolyActive 之后走上面那个早退）
            SetHover(null);
            AddSelectionPoint(p, e.ClickCount);
            e.Handled = true;
            return;
        }

        if (_shape == SelectionMode.Lasso)
        {
            // 自由：按住拖一圈，松手就闭合（不吸附：形状是随手画的，吸附反而拽手）
            _lasso.Clear();
            SelectionShape.Append(_lasso, p);
            _polyDip = null;
            _sel = new Rect(p, p);
            _drag = DragMode.Lasso;
            SetHover(null);
            StartDrag(e, Cursors.Cross);
            return;
        }

        _drag = DragMode.New;
        _polyDip = null;
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

            // 折线选区正在逐点单击：只让最后一段虚线跟着光标
            if (_selPolyActive)
            {
                _selPolyCursor = p;
                Surface.PendingPolygon = PendingShape();
                Surface.InvalidateVisual();
                return;
            }

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
            case DragMode.Lasso:
                // 自由选区：补点 + 外接框跟着走（画面上画的是那条虚线，不是这个框）
                SelectionShape.Append(_lasso, p);
                _sel = SelectionShape.Bounds(_lasso);
                break;
            case DragMode.Move:
                _sel = SnapMove(MoveTo(_originRect, p - _origin));
                if (_originPoly != null && _sel is Rect moved)
                    _polyDip = SelectionShape.Offset(_originPoly, new Vector(moved.X - _originRect.X, moved.Y - _originRect.Y));
                break;
            case DragMode.Resize:
                _sel = SnapResize(ResizeFrom(_originRect, _handle, p - _origin), _handle);
                if (_originPoly != null && _sel is Rect scaled)
                    _polyDip = SelectionShape.Scale(_originPoly, _originRect, scaled);
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
            case DragMode.MarkResize:
                UpdateMarkResize(p);
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
        _handle = FrameHandle.None;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }

        switch (mode)
        {
            case DragMode.New:
                // 几乎没移动 = 单击：选中光标下的窗口 / 控件；拖动了就是自由框选，不抢
                if (!_pressMoved && _pressTarget is SnapBox target) _sel = ToDipRect(target.Bounds);
                else if (!IsValid(_sel)) _sel = null;   // 误点 / 太小：清掉重来，不关闭窗口
                break;

            case DragMode.Lasso:
                CommitLasso();
                break;

            case DragMode.Draw:
                FinishToolDrag(p);
                break;

            case DragMode.Mark:
                CommitMarkDrag(p);
                break;

            case DragMode.MarkResize:
                CommitMarkResize(p);
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
        bool wasFrame = _drag is DragMode.New or DragMode.Move or DragMode.Resize or DragMode.Lasso;

        if (_drag == DragMode.Mark) CancelMarkDrag();
        else if (_drag == DragMode.MarkResize) CancelMarkResize();
        else if (_drag is DragMode.Draw or DragMode.Erase) Surface.Preview = null;
        else if (_drag == DragMode.Lasso) _lasso.Clear();

        _drag = DragMode.None;
        _handle = FrameHandle.None;
        _pressTarget = null;

        if (wasFrame)
        {
            _sel = _originRect != default ? _originRect : null;
            _polyDip = _originPoly;
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

        FrameHandle hit = HitTest(p);
        FrameHandle markHit = MarkHandleAt(p);

        Cursor = _tool switch
        {
            // 选中标注的把手比框选的把手更「当前」：先把光标摆对，不然看不出能拉
            CaptureTool.Select when markHit is not (FrameHandle.None or FrameHandle.Move) => CursorFor(markHit),
            CaptureTool.Select when hit != FrameHandle.None => CursorFor(hit),
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

    /// <summary>事件源是否来自「选区形状」那条（点它不能当成开始框选）。</summary>
    private bool IsOnShapeBar(DependencyObject? source)
    {
        while (source != null)
        {
            if (ReferenceEquals(source, ShapeBar)) return true;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    // ============ 选区形状：矩形 / 自由 / 折线 ============

    /// <summary>形状条上的一下：换形状 = 从头框（旧选区的点列对不上新形状）。</summary>
    private void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (!Enum.TryParse<SelectionMode>(tag, out var mode)) return;
        SetSelectionShape(mode);
    }

    private void SetSelectionShape(SelectionMode mode)
    {
        if (_shape == mode) return;
        _shape = mode;
        CancelSelectionPolyline();
        if (IsValid(_sel)) ClearSelection();
        UpdateChrome();
    }

    /// <summary>折线选区正在逐点单击（这时还没形成选区，工具栏 / 动作栏都不该出现）。</summary>
    private bool SelectionPending => _selPolyActive;

    /// <summary>画面上要画的那条「还没闭合」的虚线：折线的已落点 + 跟手的那一段（套索由多边形代画）。</summary>
    private Point[]? PendingShape()
    {
        if (!_selPolyActive) return null;

        var list = new List<Point>(_selPoly);
        if (_selPolyCursor is Point cursor) list.Add(cursor);
        return list.Count >= 1 ? list.ToArray() : null;
    }

    /// <summary>
    /// 画面要按形状画的那份选区：自由 / 折线选区给点列，矩形给 null。
    /// 套索拖动中也给已落的点——不然压暗挖的洞是个跟着手变大的方框。
    /// </summary>
    private Point[]? ClosedPolygon()
    {
        if (_polyDip is { Length: >= SelectionShape.MinPoints } poly) return poly;
        if (_drag == DragMode.Lasso && _lasso.Count >= SelectionShape.MinPoints) return _lasso.ToArray();
        return null;
    }

    /// <summary>形状条上点亮当前形状（程序化改 IsChecked 不会触发 Click）。</summary>
    private void SyncShapeButtons()
    {
        ShapeRect.IsChecked = _shape == SelectionMode.Rect;
        ShapeLasso.IsChecked = _shape == SelectionMode.Lasso;
        ShapePolyline.IsChecked = _shape == SelectionMode.Polyline;
    }

    /// <summary>
    /// 折线选区：单击补一个点。双击（第二下的 <c>ClickCount == 2</c>）或点回起点附近就收尾，
    /// 与标注的折线手势同一套习惯。
    /// </summary>
    private void AddSelectionPoint(Point p, int clickCount)
    {
        if (clickCount >= 2 && _selPoly.Count >= 1)
        {
            CompleteSelectionPolyline();
            return;
        }

        if (!_selPolyActive)
        {
            _selPoly.Clear();
            _polyDip = null;
        }

        // 点回第一个点附近 = 闭合（截图 / 画图工具的习惯，省得去双击）
        if (_selPoly.Count >= SelectionShape.MinPoints && (p - _selPoly[0]).Length <= 6)
        {
            CompleteSelectionPolyline();
            return;
        }

        _selPoly.Add(p);
        _selPolyCursor = null;
        _selPolyActive = true;
        _sel = SelectionShape.Bounds(_selPoly);
        UpdateChrome();
    }

    private void CompleteSelectionPolyline()
    {
        var points = _selPoly.ToArray();
        bool ok = SelectionShape.IsUsable(points, MinSize);

        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;

        if (ok)
        {
            _polyDip = points;
            _sel = SelectionShape.Bounds(points);
        }
        else
        {
            _polyDip = null;
            _sel = null;
            ToastService.Show("折线选区至少要围出一小块：多点几个点再双击结束", null, 2000,
                ToastKind.info, key: "capture-shape-small");
        }

        UpdateChrome();
    }

    /// <summary>丢掉正在点的折线选区（Esc / 换形状 / 翻历史）。</summary>
    private void CancelSelectionPolyline()
    {
        if (!_selPolyActive) return;
        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        _sel = null;
        Surface.PendingPolygon = null;
        Surface.InvalidateVisual();
    }

    /// <summary>套索松手：够大就闭合（形状按点列裁），太小就当没框过。</summary>
    private void CommitLasso()
    {
        var points = _lasso.ToArray();
        _lasso.Clear();

        if (SelectionShape.IsUsable(points, MinSize))
        {
            _polyDip = points;
            _sel = SelectionShape.Bounds(points);
        }
        else
        {
            _polyDip = null;
            _sel = null;
        }
    }

    // ============ 选区计算 ============

    private bool IsValid(Rect? r) => r is Rect s && s.Width >= MinSize && s.Height >= MinSize;

    /// <summary>
    /// 把窗口 DIP 选区同步成「屏幕物理像素」写进文档（导出按它裁）。
    /// 自由 / 折线选区同时把点列换成图像像素写进 <see cref="CaptureDocument.Polygon"/>——
    /// 外接框与点列必须是**同一次换算**的产物，否则裁出来的边会差一两个像素。
    /// </summary>
    private void SyncArea()
    {
        if (_sel is not Rect s) return;

        if (_polyDip is { Length: >= SelectionShape.MinPoints } poly)
        {
            var image = SelectionShape.ToImage(poly, _doc.Origin, _doc.Scale);
            for (int i = 0; i < image.Length; i++)
                image[i] = new Point(
                    Math.Clamp(image[i].X, 0, _doc.Base.PixelWidth - 1),
                    Math.Clamp(image[i].Y, 0, _doc.Base.PixelHeight - 1));

            Rect box = SelectionShape.Bounds(image);
            _doc.Polygon = image;
            _doc.Area = new Int32Rect(_doc.Monitor.X + (int)box.X, _doc.Monitor.Y + (int)box.Y,
                Math.Max(2, (int)box.Width + 1), Math.Max(2, (int)box.Height + 1));
            return;
        }

        _doc.Polygon = null;

        var (x1, y1) = ToImage(s.TopLeft);
        var (x2, y2) = ToImage(s.BottomRight);
        int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
        int w = Math.Min(Math.Abs(x2 - x1) + 1, _doc.Base.PixelWidth - x);
        int h = Math.Min(Math.Abs(y2 - y1) + 1, _doc.Base.PixelHeight - y);
        if (w < 2 || h < 2) return;

        _doc.Area = new Int32Rect(_doc.Monitor.X + x, _doc.Monitor.Y + y, w, h);
    }

    private FrameHandle HitTest(Point p) =>
        _sel is Rect s ? MarkGeometry.HitFrame(s, p, GripHit) : FrameHandle.None;

    /// <summary>
    /// 选中标注的八个把手（只在「选择」工具下、且这一条能缩放时才给）。
    /// 框内返回 <see cref="FrameHandle.Move"/>，由调用方决定是拖动还是交给框选。
    /// </summary>
    private FrameHandle MarkHandleAt(Point p)
    {
        if (_tool != CaptureTool.Select) return FrameHandle.None;
        if (_selectedIndex < 0 || _selectedIndex >= _doc.Marks.Count) return FrameHandle.None;

        var mark = _doc.Marks[_selectedIndex];
        if (!mark.CanScale) return FrameHandle.None;

        return MarkGeometry.HitFrame(MarkGeometry.PaddedFrame(mark.Frame), p, GripHit);
    }

    private static Cursor CursorFor(FrameHandle h) => h switch
    {
        FrameHandle.TopLeft or FrameHandle.BottomRight => Cursors.SizeNWSE,
        FrameHandle.TopRight or FrameHandle.BottomLeft => Cursors.SizeNESW,
        FrameHandle.Top or FrameHandle.Bottom => Cursors.SizeNS,
        FrameHandle.Left or FrameHandle.Right => Cursors.SizeWE,
        FrameHandle.Move => Cursors.SizeAll,
        _ => Cursors.Cross,
    };

    private Rect MoveTo(Rect r, Vector delta)
    {
        double x = Math.Clamp(r.X + delta.X, 0, Math.Max(0, ActualWidth - r.Width));
        double y = Math.Clamp(r.Y + delta.Y, 0, Math.Max(0, ActualHeight - r.Height));
        return new Rect(x, y, r.Width, r.Height);
    }

    /// <summary>选区改框：纯几何在 <see cref="MarkGeometry.ResizeFrame"/>，这里只补「别跑出窗口」。</summary>
    private Rect ResizeFrom(Rect r, FrameHandle h, Vector d) =>
        MarkGeometry.ResizeFrame(r, h, d, MinSize, new Rect(0, 0, ActualWidth, ActualHeight));

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
