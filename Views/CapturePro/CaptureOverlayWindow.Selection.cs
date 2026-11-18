using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 框选与标注的鼠标总入口：拖新选区（加区 / 减区）、拖内部移动、拖八个把手缩放、画标注、拖已有标注。
/// </summary>
/// <remarks>
/// <para>
/// 全部用窗口级 <c>Preview</c> 事件：按下之后 WPF 会把捕获挂到窗口上，之后 <c>MouseMove</c>
/// 只路由到窗口，挂在 Canvas 上的处理器收不到——框选会「按下就卡住」。
/// </para>
/// <para>
/// 按下时的判断顺序固定为：工具栏 / 文字输入框让路 → 把手（任何工具都能改框）
/// → 选区内（选择工具拖标注，其余工具画标注）→ 选区外（加区 / 减区 / 整块重框）。
/// 具体怎么画在 <c>.Annotate.cs</c>，工具状态在 <c>.Tools.cs</c>。
/// </para>
/// <para>
/// 选区是**多块**的（<see cref="SelectionRegion"/> + <see cref="SelectionSet"/>）：已经有选区时
/// **Shift 拖 = 加区、Alt 拖 = 减区**，无修饰键拖到选区外 = 整块重框；还没有选区时 Shift 仍是「锁正方形」。
/// 画面上「是几块、挖了哪些洞」全按合起来的几何算，导出也一样。
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

        _moveFromShadow = false;

        // 长截图模式：只有小面板上的按钮能点，别的一律不成开始框选 / 画标注
        if (_scrollMode)
        {
            if (IsOnScrollPanel(e.OriginalSource as DependencyObject)) return;
            if (e.ClickCount == 2) FinishScroll();      // 双击 = 完成（和普通截图一致）
            e.Handled = true;
            return;
        }

        // 浮动工具栏 / 选区形状条都是普通控件：点它们不能当成开始框选或画标注
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

        // 在不在选区里按**合起来的几何**判：外接框的四个角、挖掉的洞都是空的，
        // 点在那里等于点在外面（加区 / 减区 / 整块重框）。
        bool inside = IsValid(_sel) && SelectionSet.Contains(_regions, p);

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
            BeginFrameChange(_sel!.Value);
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

                BeginFrameChange(_sel!.Value);
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

        // 3. 选区外 / 还没选区：按当前形状加区 / 减区 / 整块重框；松手时几乎没动就选中光标下的窗口 / 控件
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // 3a. 压暗的阴影上拖动 = 挪整块选区（照 Snow Shot：单块矩形 + 移动工具时，外面那圈也是抓手）。
        //     多块 / 挖洞 / 自由 / 折线仍然「外面 = 重新框选」，Shift / Alt 仍优先当加区 / 减区。
        if (SelectionSet.MovesOnOutsideDrag(_regions, _tool == CaptureTool.Select, shift, alt))
        {
            _moveFromShadow = true;
            BeginFrameChange(_sel!.Value);
            _drag = DragMode.Move;
            _handle = FrameHandle.Move;
            RebuildSnapLines();
            StartDrag(e, Cursors.SizeAll);
            return;
        }

        _pressTarget = _hoverTarget;
        _handle = FrameHandle.None;
        BeginFrameChange(_sel ?? default);

        // 还没有选区时 Shift 仍然是「锁正方形」（等价于新建）；有选区之后 Shift / Alt 才是加区 / 减区。
        _squareDrag = false;
        if (_regions.Count == 0)
        {
            _mode = RegionMode.Add;
            _squareDrag = shift;
        }
        else if (alt)
        {
            _mode = RegionMode.Subtract;
        }
        else if (shift)
        {
            _mode = RegionMode.Add;
        }
        else
        {
            // 无修饰键拖到选区外 = 整块重框：**松手才换**，中途 Esc / 右键还回得去
            _mode = RegionMode.Add;
            _replaceOnCommit = true;
        }

        _liveRect = new Rect(p, p);
        _lasso.Clear();

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
            SelectionShape.Append(_lasso, p);
            _drag = DragMode.Lasso;
            SetHover(null);
            StartDrag(e, Cursors.Cross);
            return;
        }

        _drag = DragMode.New;
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

    /// <summary>改框（移动 / 缩放）前的现场：外接框 + 每一块选区，取消时原样退回。</summary>
    private void BeginFrameChange(Rect box)
    {
        _originRect = box;
        _originRegions = new List<SelectionRegion>(_regions);
        _replaceOnCommit = false;
        _liveRect = null;
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        // 长截图模式：光标只是滚轮的位置，别改光标 / 别更新悬停候选
        if (_scrollMode) return;

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
            if (_tool == CaptureTool.AutoFilter)
            {
                UpdateAutoFilterHover(p);      // 光标下那一块点亮（上游 region_at）
                return;
            }
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
                // 拖出来的边角吸附到附近窗口 / 控件的边（Ctrl 临时关闭）；还没有选区时 Shift 锁正方形
                Point end = SnapPoint(p);
                if (_squareDrag) end = SquareOf(_origin, end);
                _liveRect = RectFrom(_origin, end);
                RefreshSelection();
                break;
            }
            case DragMode.Lasso:
                // 自由选区：补点 + 外接框跟着走（画面上画的是那条虚线，不是这个框）
                SelectionShape.Append(_lasso, p);
                RefreshSelection();
                break;
            case DragMode.Move:
                MoveFrame(SnapMove(MoveTo(_originRect, p - _origin)));
                break;
            case DragMode.Resize:
                ResizeFrame(SnapResize(ResizeFrom(_originRect, _handle, p - _origin), _handle));
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
        if (_scrollMode) return;
        if (e.ChangedButton != MouseButton.Left || _drag == DragMode.None) return;

        Point p = Clamp(e.GetPosition(Layer));
        DragMode mode = _drag;

        _drag = DragMode.None;
        _handle = FrameHandle.None;
        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }

        switch (mode)
        {
            case DragMode.New:
            {
                var box = _liveRect ?? Rect.Empty;
                _liveRect = null;

                // 几乎没移动 = 单击：选中光标下的窗口 / 控件（整块换掉）；拖动了就是框选
                if (!_pressMoved && _pressTarget is SnapBox target) SetSingleRect(ToDipRect(target.Bounds));
                else if (!CommitBox(box)) RefreshSelection();   // 太小 / 挖空 / 点空白：保留原选区
                break;
            }

            case DragMode.Lasso:
                CommitLasso();
                break;

            case DragMode.Move:
                // 从压暗的阴影上按下去、几乎没移动 = 单击：在阴影上点一下什么都别做
                // （以前这条路径走「拖出去整块重框」里的单击分支，那里也是保留原选区）。
                // 没有这句的话，按下时抖的那一两像素会留在选区上。
                if (!_pressMoved && _moveFromShadow) MoveFrame(_originRect);
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
        _moveFromShadow = false;
        _originRegions = null;
        _replaceOnCommit = false;
        if (_tool == CaptureTool.AutoFilter) SyncAutoFilter(detect: true);   // 选区变了就重新识别
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

        if (_tool == CaptureTool.AutoFilter) CancelAutoFilterGesture();

        _drag = DragMode.None;
        _handle = FrameHandle.None;
        _pressTarget = null;

        if (wasFrame) RestoreRegions();

        try { ReleaseMouseCapture(); } catch { /* 未捕获 */ }
        UpdateCursorForTool();
        UpdateChrome();
    }

    /// <summary>光标形状：选择工具看边框 / 标注，文字是 I 形，其余是十字。</summary>
    private void RefreshCursor(Point p)
    {
        if (_textEditing) return;

        FrameHandle hit = HitTest(p);
        FrameHandle markHit = MarkHandleAt(p);

        // 选中标注的把手比框选的边更「当前」：先把光标摆对，不然看不出能拉
        if (markHit is not (FrameHandle.None or FrameHandle.Move))
        {
            Cursor = CursorFor(markHit);
            return;
        }

        // **任何工具**下边框都能抓（按下就改框），光标也就跟着变——否则拿着画笔
        // 走到边上还是十字，看不出那里能拉伸。
        if (hit is not (FrameHandle.None or FrameHandle.Move))
        {
            Cursor = CursorFor(hit);
            return;
        }

        Cursor = _tool switch
        {
            CaptureTool.Select when hit == FrameHandle.Move => Cursors.SizeAll,
            // 单块矩形 + 选择工具：外面那圈阴影也是「挪整块」的抓手（见 SelectionSet.MovesOnOutsideDrag），
            // 光标跟着说清楚——否则用户只会看到箭头，不知道那里能拖
            CaptureTool.Select when SelectionSet.MovesOnOutsideDrag(_regions, true, false, false) => Cursors.SizeAll,
            CaptureTool.Select when !IsValid(_sel) => Cursors.Cross,
            CaptureTool.Select => _doc.HitTest(p, MarkHitSlop) >= 0 ? Cursors.SizeAll : Cursors.Arrow,
            CaptureTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
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

    /// <summary>形状条上的一下：换「下一块的形状」（已有选区不动，加区 / 减区可以是不同形状）。</summary>
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
        CancelSelectionPolyline();      // 正在点的折线还没成选区，跟着换形状作废
        UpdateChrome();
    }

    /// <summary>折线选区正在逐点单击（这时还没形成选区，工具栏不该出现）。</summary>
    private bool SelectionPending => _selPolyActive;

    /// <summary>画面上要画的那条「还没闭合」的虚线：折线的已落点 + 跟手的那一段（套索由多边形代画）。</summary>
    private Point[]? PendingShape()
    {
        if (!_selPolyActive) return null;

        var list = new List<Point>(_selPoly);
        if (_selPolyCursor is Point cursor) list.Add(cursor);
        return list.Count >= 1 ? list.ToArray() : null;
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

        if (!_selPolyActive) _selPoly.Clear();   // 这一串点属于同一块，加 / 减由按下时的修饰键定

        // 点回第一个点附近 = 闭合（截图 / 画图工具的习惯，省得去双击）
        if (_selPoly.Count >= SelectionShape.MinPoints && (p - _selPoly[0]).Length <= 6)
        {
            CompleteSelectionPolyline();
            return;
        }

        _selPoly.Add(p);
        _selPolyCursor = null;
        _selPolyActive = true;
        UpdateChrome();
    }

    private void CompleteSelectionPolyline()
    {
        var points = _selPoly.ToArray();
        bool ok = SelectionShape.IsUsable(points, MinSize);

        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        Surface.PendingPolygon = null;

        if (ok)
        {
            Commit(SelectionRegion.FromPoints(_mode, points));
        }
        else
        {
            _replaceOnCommit = false;      // 这一笔作废，别把「整块重框」留给下一次
            RefreshSelection();
            ToastService.Show("折线选区至少要围出一小块：多点几个点再双击结束", null, 2000,
                ToastKind.info, key: "capture-shape-small");
        }

        UpdateChrome();
    }

    /// <summary>丢掉正在点的折线选区（Esc / 换形状 / 翻历史）。</summary>
    private void CancelSelectionPolyline()
    {
        _replaceOnCommit = false;
        if (!_selPolyActive) return;
        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        Surface.PendingPolygon = null;
        Surface.InvalidateVisual();
        RefreshSelection();
    }

    /// <summary>套索松手：够大就并进选区（形状按点列裁），太小就当没框过。</summary>
    private void CommitLasso()
    {
        var points = _lasso.ToArray();
        _lasso.Clear();

        if (SelectionShape.IsUsable(points, MinSize)) Commit(SelectionRegion.FromPoints(_mode, points));
        else
        {
            _replaceOnCommit = false;
            RefreshSelection();
        }
    }

    // ============ 多区域选区：加区 / 减区 ============

    /// <summary>
    /// 画面 / 导出该按哪几块算：已确定的选区 + 正在拖的那一块（矩形 / 自由圈）。
    /// 折线还在逐点单击时不算（它画的是 <see cref="PendingShape"/> 那条虚线）；
    /// 「整块重框」的手势里先不带上旧选区，松手才换。
    /// </summary>
    private List<SelectionRegion> LiveRegions()
    {
        var list = new List<SelectionRegion>(_regions.Count + 1);

        if (!_replaceOnCommit) list.AddRange(_regions);

        if (_drag == DragMode.New && _liveRect is Rect rect && rect.Width >= 0.5 && rect.Height >= 0.5)
            list.Add(SelectionRegion.FromRect(_mode, rect));

        if (_drag == DragMode.Lasso && _lasso.Count >= SelectionShape.MinPoints)
            list.Add(SelectionRegion.FromPoints(_mode, _lasso));

        return list;
    }

    /// <summary>
    /// 选区合起来的几何（窗口 DIP，含正在拖的那块）：加区并、减区挖。
    /// 减区把选区挖空 = null（画面不再挖洞，导出也拿不到内容）。
    /// </summary>
    private Geometry? SelectionGeometry() => SelectionSet.Combine(LiveRegions());

    /// <summary>按选区重算外接框（把手 / 尺寸条都挂在它上面）。</summary>
    private void RefreshSelection()
    {
        Rect box = SelectionSet.Bounds(LiveRegions());
        _sel = box.IsEmpty || box.Width < 0.5 || box.Height < 0.5 ? null : box;
    }

    /// <summary>把选区退回按下那一刻的样子（取消拖动 / 换工具 / 翻历史）。</summary>
    private void RestoreRegions()
    {
        if (_originRegions != null)
        {
            _regions.Clear();
            _regions.AddRange(_originRegions);
        }

        _originRegions = null;
        _replaceOnCommit = false;
        _liveRect = null;
        _lasso.Clear();
        RefreshSelection();
    }

    /// <summary>
    /// 把一块并进选区。<paramref name="region"/> 是减区且会把选区挖空时**不并、返回 false**——
    /// 别留下「有选区、却什么都导不出来」的状态。
    /// </summary>
    private bool Commit(SelectionRegion region)
    {
        if (region.Mode == RegionMode.Subtract && WouldEmpty(region))
        {
            ToastService.Show("减区把选区挖空了，这一块没并进去", null, 2200, ToastKind.warning,
                key: "capture-region-empty");
            return false;
        }

        if (_replaceOnCommit) _regions.Clear();     // 无修饰键拖到选区外 = 整块重框
        _replaceOnCommit = false;
        _regions.Add(region);
        RefreshSelection();
        return true;
    }

    /// <summary>落一块矩形选区：太小 / 减区挖空都不落。</summary>
    private bool CommitBox(Rect box) => IsValid(box) && Commit(SelectionRegion.FromRect(_mode, box));

    /// <summary>这一块减区会不会把选区整个挖空。</summary>
    private bool WouldEmpty(SelectionRegion region)
    {
        var probe = new List<SelectionRegion>(_regions.Count + 1);
        probe.AddRange(_regions);
        probe.Add(region);
        return SelectionSet.Combine(probe) == null;
    }

    /// <summary>
    /// 全屏：选区换成整块画面（Ctrl+A 与工具条「全屏」共用）。
    /// 折线还没收尾的先取消，否则下一段点会接到旧折线上。
    /// </summary>
    private void SelectWholeScreen()
    {
        _drag = DragMode.None;
        CancelSelectionPolyline();
        SetSingleRect(new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
        UpdateChrome();
    }

    /// <summary>整块换成一块矩形选区（单击选中窗口 / 控件、Ctrl+A 全屏、退出历史都用它）。</summary>
    private void SetSingleRect(Rect rect)
    {
        _originRegions = null;
        _replaceOnCommit = false;
        _liveRect = null;
        _lasso.Clear();
        _selPoly.Clear();
        _selPolyCursor = null;
        _selPolyActive = false;
        Surface.PendingPolygon = null;

        _regions.Clear();
        _regions.Add(SelectionRegion.FromRect(RegionMode.Add, rect));
        RefreshSelection();
    }

    /// <summary>把每一块选区整体平移到目标框（按下那一刻的框 → 目标框）。</summary>
    private void MoveFrame(Rect target)
    {
        var delta = new Vector(target.X - _originRect.X, target.Y - _originRect.Y);
        ApplyFrame(target, region => region.Offset(delta));
    }

    /// <summary>按外接框的变形缩放每一块选区（与标注缩放同一口径）。</summary>
    private void ResizeFrame(Rect target) => ApplyFrame(target, region => region.Scale(_originRect, target));

    private void ApplyFrame(Rect target, Func<SelectionRegion, SelectionRegion> transform)
    {
        _regions.Clear();
        if (_originRegions != null)
            foreach (var region in _originRegions) _regions.Add(transform(region));
        _sel = target;      // 目标框就是新的外接框（再按几何算一次只会差浮点尾数）
    }

    // ============ 选区计算 ============

    private bool IsValid(Rect? r) => r is Rect s && s.Width >= MinSize && s.Height >= MinSize;

    /// <summary>
    /// 把窗口 DIP 选区同步成「屏幕物理像素 + 图像像素」写进文档（导出按它裁）。
    /// 一整块矩形只写 <see cref="CaptureDocument.Area"/>（位图本身就是它，不用裁）；
    /// 多块 / 自由 / 折线 / 挖洞写 <see cref="CaptureDocument.Regions"/>，外接框取**加区**的并集。
    /// 外接框与几何必须是**同一次换算**的产物，否则裁出来的边会差一两个像素。
    /// </summary>
    private void SyncArea()
    {
        if (_regions.Count == 0) return;

        // 一整块矩形：老路（包含两端像素 +1，导出不裁）
        if (_regions.Count == 1 && _regions[0] is { Mode: RegionMode.Add, IsPolygon: false } only)
        {
            var s = only.Box;
            var (x1, y1) = ToImage(s.TopLeft);
            var (x2, y2) = ToImage(s.BottomRight);
            int x = Math.Min(x1, x2), y = Math.Min(y1, y2);
            int w = Math.Min(Math.Abs(x2 - x1) + 1, _doc.Base.PixelWidth - x);
            int h = Math.Min(Math.Abs(y2 - y1) + 1, _doc.Base.PixelHeight - y);
            if (w < 2 || h < 2) return;

            _doc.Regions = null;
            _doc.Area = new Int32Rect(_doc.Monitor.X + x, _doc.Monitor.Y + y, w, h);
            return;
        }

        int width = _doc.Base.PixelWidth, height = _doc.Base.PixelHeight;
        var regions = new SelectionRegion[_regions.Count];
        Rect box = Rect.Empty;
        for (int i = 0; i < _regions.Count; i++)
        {
            var image = _regions[i].ToImage(_doc.Origin, _doc.Scale).Clamp(width, height);
            regions[i] = image;
            if (image.Mode == RegionMode.Add) box.Union(image.Bounds);
        }

        if (box.IsEmpty) return;
        int bw = Math.Max(2, (int)Math.Round(box.Width));
        int bh = Math.Max(2, (int)Math.Round(box.Height));
        int bx = Math.Min((int)Math.Round(box.X), Math.Max(0, width - bw));
        int by = Math.Min((int)Math.Round(box.Y), Math.Max(0, height - bh));

        _doc.Regions = regions;
        _doc.Area = new Int32Rect(_doc.Monitor.X + bx, _doc.Monitor.Y + by, bw, bh);
    }

    /// <summary>
    /// 选区边框命中：**整条边都能抓**（照 Snow Shot 的边带判定，见 <see cref="MarkGeometry.HitFrameEdge"/>），
    /// 不再只有八个「看不见的把手」周围那点方块。
    /// </summary>
    /// <remarks>
    /// 减区挖掉的洞、外接框里的空角不是选区（那里按下等于「框外」：加区 / 减区 / 重框），
    /// 所以命中点落在洞里的边框一律当没命中，别拿它去改框。
    /// </remarks>
    private FrameHandle HitTest(Point p)
    {
        if (_sel is not Rect s) return FrameHandle.None;

        FrameHandle hit = MarkGeometry.HitFrameEdge(s, p, GripHit);
        if (hit is not (FrameHandle.None or FrameHandle.Move) &&
            s.Contains(p) && !SelectionSet.Contains(_regions, p))
            return FrameHandle.None;

        return hit;
    }

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
