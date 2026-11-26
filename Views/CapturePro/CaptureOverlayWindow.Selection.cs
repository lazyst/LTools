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

    // 框选标注（Shift + 空白拖动）：起点 + 按下时已有的选区（加选）
    private Point _marqueeStart;
    private int[]? _marqueeBase;

    private enum DragMode { None, New, Move, MoveRegion, Resize, Draw, Erase, Mark, MarkResize, MagnifierLens, Lasso, Marquee, SmartPress }

    /// <summary>拖动中命中的是第几块（<see cref="DragMode.MoveRegion"/> 只挪这一块；-1 = 没有）。</summary>
    private int _dragRegion = -1;

    /// <summary>按下那一刻「移动整个选区」（默认 Space）是不是按着（上游 <c>m_moveEntireSelectionShortcut</c>）。</summary>
    private bool _moveEntire;

    /// <summary>
    /// 中键：按设置完成当前选区（照上游 <c>handleUnhandledMiddleClick</c>）。
    /// </summary>
    /// <remarks>
    /// 三种不接的情况与上游一致：手势 / 打字进行中不抢；落在工具条、信息条、比例菜单上
    /// 交给它们自己的控件；没有选区时什么都不做（中键不是框选的起手式）。
    /// 智能预选态先看点击点是否落在预选块里，在就落定它再执行（默认「贴到屏幕」）。
    /// </remarks>
    private void Window_MiddleDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        if (_scrollMode) { e.Handled = true; return; }

        if (IsOnToolbar(e.OriginalSource as DependencyObject)) return;
        if (IsOnSelectionBar(e.OriginalSource as DependencyObject)) return;
        if (IsOnAspectMenu(e.OriginalSource as DependencyObject)) return;
        if (IsOnQrBubble(e.OriginalSource as DependencyObject) ||
            IsOnQr(e.OriginalSource as DependencyObject)) return;

        e.Handled = true;

        if (_drag != DragMode.None || _textEditing) return;

        OverlayPointerAction action = PointerSettings.OverlayMiddleClick;
        if (action == OverlayPointerAction.None) return;

        if (SmartPreviewActive)
        {
            if (action == OverlayPointerAction.Cancel) { CompleteFromPointer(action); return; }
            if (!_smart.HasCurrentSelection) return;

            Rect preview = _smart.CurrentSelection;
            if (!preview.Contains(Clamp(e.GetPosition(Layer)))) return;   // 点不在这一块上：不动
            ConfirmSmartSelection(preview);
        }
        else if (_sel is not Rect sel || !IsValid(sel))
        {
            return;
        }

        CompleteFromPointer(action);
    }

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

        // 二维码气泡 / 标记：点气泡外先收气泡（这一下继续按普通点击处理），
        // 点气泡或标记本身则让控件自己处理（按钮 Click / 文本框选字）。
        if (_qrBubbleOpen &&
            !IsOnQrBubble(e.OriginalSource as DependencyObject) &&
            !IsOnQr(e.OriginalSource as DependencyObject))
        {
            HideQrBubble();
        }

        if (IsOnQrBubble(e.OriginalSource as DependencyObject) ||
            IsOnQr(e.OriginalSource as DependencyObject))
        {
            return;
        }

        // 比例下拉开着时：点在信息条与下拉以外的地方 = 先收起菜单（这一下不再做别的，
        // 与菜单惯例一致——点外面是「关掉」，不是「关掉并执行」）
        if (IsAspectMenuOpen &&
            !IsOnSelectionBar(e.OriginalSource as DependencyObject) &&
            !IsOnAspectMenu(e.OriginalSource as DependencyObject))
        {
            CloseAspectMenu();
            e.Handled = true;
            return;
        }

        // 点下拉里的条目：交给条目自己的 MouseLeftButtonUp，别当成开始框选
        if (IsOnAspectMenu(e.OriginalSource as DependencyObject)) return;

        // 选区信息条：字段点击 / 滚轮是改参数，别当成开始框选。
        // PreviewMouseLeftButtonDown 是 **Direct** 事件（只在本元素上被 WPF 提升），
        // 所以分发必须在这里做，别指望冒泡到 SelBar 再加一层。
        if (IsOnSelectionBar(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            HandleSelectionBarClick(e.OriginalSource as DependencyObject);
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

        // 这一下点击用掉了手上的修饰键（减区 / 加区 / 锁正方形）：抬起时别当「轻按」
        ConsumeModifiers((Keyboard.Modifiers & ModifierKeys.Alt) != 0,
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0);

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

        // 智能选区预选态（上游 handleIntelligentSelectionPress）：按下只记账——
        // 松手还在这一块里 = 确认；拖动超过阈值 = 从按下点开始手拖框选。
        if (_smartPreviewActive)
        {
            _pressAt = p;
            _pressMoved = false;
            _pressTarget = null;
            _origin = p;
            _smart.BeginPress(p, _smart.CurrentSelection);
            _drag = DragMode.SmartPress;
            StartDrag(e, Cursors.Cross);
            return;
        }

        FrameHandle hit = HitTest(p);

        // 在不在选区里按**合起来的几何**判：外接框的四个角、挖掉的洞都是空的，
        // 点在那里等于点在外面（加区 / 减区 / 整块重框）。
        bool inside = IsValid(_sel) && SelectionSet.Contains(_regions, p, _selStyle.CornerRadius);

        _pressAt = p;
        _pressMoved = false;
        _pressTarget = null;
        _origin = p;

        bool shiftToggle = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 && AllowsShiftToggle(_tool);

        // 0. 选中标注的把手优先：用户刚把它选上，这一下的意图就是改它的大小
        //    （框选的八个把手仍在，只是让位给「正在编辑的那一条」）
        FrameHandle markHandle = MarkHandleAt(p);
        if (markHandle is not (FrameHandle.None or FrameHandle.Move))
        {
            BeginMarkResize(p, markHandle);
            StartDrag(e, CursorFor(markHandle));
            return;
        }

        // 0.5 放大镜的镜头：镜头与源**分开定位**，按在镜头本体（源区域之外）或下方的移动手柄
        //     上只挪镜头；按在源区域里 = 整块挪动（交给下面的 BeginMarkDrag，MoveBy 两个中心一起走）。
        //     上游命中顺序：移动手柄 → 源区域（= 元素） → 镜头（= 只动镜头）。
        if (BeginMagnifierLensDrag(p))
        {
            StartDrag(e, Cursors.SizeAll);
            return;
        }

        // 1. 八个把手优先：任何工具下都能改框。
        //    这里**不能**再要求 `inside`：把手的命中半径是 8 DIP，用户常常从框**外侧**那圈
        //    抓边（光标已经变成 SizeWE / SizeNS），这时 `Rect.Contains` 是 false，会被当成
        //    「选区外 = 重新框选」——松手时框太小被清掉，看着就是「按住拖动退出了截图」。
        //    例外：设置里关掉「快速选择修改」时，非「选择」工具不再改框（照上游
        //    dragModeForVirtualPosition：activeTool != Move 且 quickSelectionModification 关 = None）。
        if (CanDragFrameHandle(hit))
        {
            Rect frame = _sel!.Value;

            // 尺寸方式 = 「贴到指针」：起拖就把被抓的那条边放到指针上（上游
            // grabAdjustedScreenshotSelectionRect），之后的位移从指针算
            if (ScreenshotBehavior.SelectionResizeMode == ScreenshotBehavior.ResizeFollowPosition)
                frame = GrabAdjusted(frame, hit, p, new Rect(0, 0, ActualWidth, ActualHeight), MinSize);

            BeginFrameChange(frame);
            if (frame != _sel!.Value) ResizeFrame(frame);
            _drag = DragMode.Resize;
            _handle = hit;
            RebuildSnapLines();
            StartDrag(e, CursorFor(hit));
            return;
        }

        // 2. 选区内：选择工具拖标注，其余工具画标注
        if (inside)
        {
            int markHit = _doc.HitTest(p, MarkHitSlop);

            // 2a. Shift+点选标注 = 加入 / 移出选区（上游 toggle_selection；橡皮 / 水印 / 自动打码不让）
            if (markHit >= 0 && shiftToggle)
            {
                ToggleMarkSelection(markHit);
                e.Handled = true;
                return;
            }

            if (_tool == CaptureTool.Select)
            {
                // 双击 = 完成：快捷入口（截图并贴图 / 识别…）优先走它自己的默认出口；
                // 普通截图按设置（默认「复制到剪贴板」，照上游 handleUnhandledLeftDoubleClick）。
                if (e.ClickCount == 2)
                {
                    if (HasPending) Accept(DefaultFinish);
                    else CompleteFromPointer(PointerSettings.OverlayDoubleClick);
                    e.Handled = true;
                    return;
                }

                if (markHit >= 0)
                {
                    BeginMarkDrag(p, markHit);
                    StartDrag(e, Cursors.SizeAll);
                    return;
                }

                // Shift + 空白拖动 = 框选标注（上游「Select 工具空白拖 = MarqueeSelect」）。
                // LTools 的「选择」还兼着「拖整个选区窗口」，那一个留给不带修饰键的拖动。
                if (shiftToggle)
                {
                    BeginMarkMarquee(p);
                    StartDrag(e, Cursors.Cross);
                    return;
                }

                BeginFrameChange(_sel!.Value);
                _handle = FrameHandle.Move;
                RebuildSnapLines();

                // 多块选区：默认只挪**命中的那一块**（照上游 beginMoveDrag → beginRegionMove，
                // regionAt 取最上面那块）；按住「移动整个选区」（默认 Space）才整体挪。
                // 单块选区没有「哪一块」之分，整体动也就是拖这一块。
                _dragRegion = SelectionSet.TopmostAt(_regions, p, _selStyle.CornerRadius);
                _moveEntire = CaptureShortcutTable.Held(CaptureShortcutScope.Screenshot,
                    "move_entire_selection");
                _drag = SelectionSet.MovesSingleRegion(_regions.Count, _dragRegion, _moveEntire)
                    ? DragMode.MoveRegion
                    : DragMode.Move;
                StartDrag(e, Cursors.SizeAll);
                return;
            }

            // 2b. 快速选择（上游 quick selection）：手上是绘图工具、点在**已有同类元素**上 = 选中并拖它
            //    （不想这样就在设置里把该工具列进「禁用快速选择的工具」）。文字工具点击已有文字也走这里。
            if (e.ClickCount == 1 && TryQuickSelect(p))
            {
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
            _dragRegion = -1;
            _moveEntire = true;
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
        // 锁正方形的键可以改（截图快捷键表），按住的是它才算。
        _squareDrag = false;
        if (_regions.Count == 0)
        {
            _mode = RegionMode.Add;
            _squareDrag = CaptureShortcutTable.Held(CaptureShortcutScope.Screenshot,
                "keep_selection_width_and_height_consistent");
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

        if (_shape is SelectionMode.Polyline or SelectionMode.Curve)
        {
            // 折线 / 曲线：这一下只是第一个点，收尾靠双击 / Enter（_selPolyActive 之后走上面那个早退）
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

    /// <summary>
    /// 上游 <c>ToolPolicy.allow_shift_toggle</c>：这几个工具上 Shift 另有含义（橡皮的吸附 / 水印的配置 /
    /// 自动打码的整批选择），不给「Shift 加减选区」。internal 是为了让自检直接断言这张表。
    /// </summary>
    internal static bool AllowsShiftToggle(CaptureTool tool) =>
        tool is not (CaptureTool.AutoFilter or CaptureTool.RectEraser or CaptureTool.BrushEraser
            or CaptureTool.Watermark or CaptureTool.Eraser);

    /// <summary>
    /// 按下之后共用的收尾：改光标、捕获鼠标、刷界面。</summary>
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

}
