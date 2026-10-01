using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 超级面板窗口（计划 §5）。4×4 格子；左键单击空格弹新建菜单 / 非空执行动作，左键按住拖动重排（§5.6，交换语义，支持跨页）；
/// 右键编辑 / 删除 / 复制；滚轮翻页 + 1~9 选格 + Esc / 点外部关闭。
/// 由 <see cref="SuperPanel"/> 控制器创建，槽位改动经 <c>SuperPanel.SavePages</c>（ConfigIO）落盘。
/// </summary>
public partial class SuperPanelWindow : Window
{
    /// <summary>当前编辑中的页（每页 9 槽，元素为动作 Id 或 null）。</summary>
    private readonly List<List<string?>> _pages;

    /// <summary>唤起瞬间的光标物理坐标（供 windowPin.toggle 等需坐标的内部动作使用）。</summary>
    private readonly int _invokeX;
    private readonly int _invokeY;

    private int _pageIdx;
    private int _interactCount;                 // >0 时（右键菜单/对话框打开）钩子不拦截外部点击与 Esc

    // —— 菜单外点击抑制（需求：菜单开着时点面板其他格子应只关菜单，不触发）——
    private ContextMenu? _openMenu;             // 当前打开的菜单（Opened/Closed 时更新）

    // —— 拖动重排（§5.6）——
    private int? _dragSrcPage;                  // 拖动源页（左键按下时记录）
    private int? _dragSrcSlot;                  // 拖动源格
    private Point _dragOrigin;                  // 按下时的鼠标位置（阈值判定用）
    private bool _dragging;                     // 是否已进入拖动态（位移超阈值）
    private int _hoverSlot = -1;                // 拖动中悬停的目标格（-1=无）
    private DispatcherTimer? _pageHoverTimer;   // ‹/› 悬停翻页计时
    private int _pageHoverDir;                  // 悬停方向：-1=‹，+1=›
    private int _pageAnimToken;                 // 翻页动画令牌（新一轮打断上一轮时自增，作废其完成回调）

    internal SuperPanelWindow(List<List<string?>> pages, int invokeX, int invokeY, int startPage = 0)
    {
        InitializeComponent();
        _pages = pages;
        _invokeX = invokeX;
        _invokeY = invokeY;
        if (_pages.Count == 0) _pages.Add(new List<string?>(new string?[16]));

        // 上次关闭页（记忆页，§12）：clamp 到有效范围
        _pageIdx = Math.Clamp(startPage, 0, _pages.Count - 1);

        Rebuild();
        UpdateHeader();

        // 无边框透明置顶窗口：淡入（对齐 MenuPopup）
        Opacity = 0;
        BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));

        // ActualWidth/Height 在加载后才可靠，此时再定位到光标处
        Loaded += (_, _) => PlaceAtCursor(_invokeX, _invokeY);

        // 拖动重排（§5.6）：窗口级接管鼠标移动/释放（捕获后拖出面板也能收到）
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragUp;
        // 菜单外点击抑制：窗口级隧道先于格子 OnCellDown 收到 down（需求：点其他格子只关菜单）
        PreviewMouseLeftButtonDown += OnWindowMouseDown;
        _pageHoverTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _pageHoverTimer.Tick += (_, _) => GoPage(_pageHoverDir);   // 悬停翻页，可连续翻
    }

    /// <summary>供 <see cref="SuperPanel"/> 判断是否有右键菜单 / 对话框正打开（打开时钩子不介入）。</summary>
    internal int InteractCount => _interactCount;

    /// <summary>当前页下标（供关闭时回写 <c>LastPage</c> 记忆页）。</summary>
    internal int PageIdx => _pageIdx;

    /// <summary>关闭时停掉悬停翻页计时器：其 Tick 闭包持有本窗口引用，
    /// 且关闭后仍会 Tick 调用 GoPage 操作已拆解的视觉树。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _pageHoverTimer?.Stop();
        base.OnClosed(e);
    }

    private List<string?> CurrentPage => _pages[_pageIdx];

    // —— 定位（仿 MouseTipWindow.PlaceNearCursor：物理像素→DIP 换算 + 工作区避让）——

    /// <summary>
    /// 唤起定位：光标落在 4×4 格子区正中（需求），越界时把窗口夹回工作区。
    /// px/py 为唤起瞬间的屏幕物理坐标。
    /// </summary>
    public void PlaceAtCursor(int px, int py)
    {
        double m11 = 1.0, m22 = 1.0;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            var tfd = src.CompositionTarget.TransformFromDevice;
            m11 = tfd.M11; m22 = tfd.M22;
        }

        double cx = px * m11, cy = py * m22;   // 光标 DIP
        double w = ActualWidth > 0 ? ActualWidth : 406;
        double h = ActualHeight > 0 ? ActualHeight : 478;

        // 窗口位置 = 光标 − 格子中心相对窗口的偏移（Loaded 时布局已完成，
        // TranslatePoint 直接取 CellsHost 实测中心，不硬编码标题栏/页脚尺寸）。
        Point center = CellsHost.ActualWidth > 0
            ? CellsHost.TranslatePoint(
                  new Point(CellsHost.ActualWidth / 2, CellsHost.ActualHeight / 2), this)
            : new Point(w / 2, h / 2);   // 兜底（理论上 Loaded 时布局已就绪）
        double left = cx - center.X;
        double top = cy - center.Y;

        // 屏幕边缘夹回：窗口外框（含 10px 阴影留白）完整保持在工作区内。
        // 贴边唤起时光标不再居中，这是让面板不出屏的必然取舍。
        var screen = GetScreenBounds(px, py);
        double sLeft = screen.Left * m11, sTop = screen.Top * m22;
        double sRight = screen.Right * m11, sBottom = screen.Bottom * m22;
        if (left < sLeft) left = sLeft;
        else if (left + w > sRight) left = Math.Max(sLeft, sRight - w);
        if (top < sTop) top = sTop;
        else if (top + h > sBottom) top = Math.Max(sTop, sBottom - h);

        Left = left;
        Top = top;
    }

    private static (double Left, double Top, double Right, double Bottom) GetScreenBounds(int x, int y)
    {
        var mon = MonitorFromPoint(new POINT { x = x, y = y }, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(mon, ref mi))
            return (mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
        var wa = SystemParameters.WorkArea;
        return (wa.Left, wa.Top, wa.Right, wa.Bottom);
    }

    // —— 格子渲染 ——

    private void Rebuild()
    {
        CancelPageFx();      // 内容变更（拖动交换 / CRUD）后确保无翻页动画残留
        _hoverSlot = -1;     // 子元素已清空，悬停引用失效
        CellsHost.Children.Clear();
        for (int slot = 0; slot < 16; slot++)   // 4×4（§12）
            CellsHost.Children.Add(BuildCell(slot));
    }

    private Button BuildCell(int slot)
    {
        string? id = CurrentPage[slot];
        var a = id != null ? ActionRegistry.FindById(id) : null;

        var btn = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Margin = new Thickness(0),     // 格子无间隙（§5.3 紧凑）
            Height = 96,                    // 正方形：面板宽 406 → 列宽 (406-22)/4=96
            Cursor = Cursors.Hand,
            // 不覆盖 Background/Tag：与设置页槽位（BuildSlotButton）一致——透明底 + 默认悬停
            // HoverBg(#F4F4F5)。原覆盖 SurfaceAlt(#F4F4F5) 与默认悬停同色→悬停无变化；
            // 改深色悬停又太暗。保留边框仅作 4×4 格子的视觉分隔。
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
        };
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE710",   // add 字形
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 22,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource(a != null ? "TextBrush" : "HintTextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = a != null ? a.Name : "（空）",
            Margin = new Thickness(0, 5, 0, 0),
            MaxWidth = 104,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource(a != null ? "TextBrush" : "HintTextBrush"),
        });
        btn.Content = content;

        btn.PreviewMouseLeftButtonDown += (_, e) => OnCellDown(slot, e);
        // 右键菜单：非空 → 编辑/删除/复制/移动；空 → §5.5 新建菜单
        var menu = a != null ? BuildFilledMenu(slot) : BuildEmptyMenu(slot);
        TrackInteract(menu);
        btn.ContextMenu = menu;
        return btn;
    }

    // —— 交互（左键 / 1~9）——

    /// <summary>选中/点击某格。非空→先关面板再执行动作（避免动作窗口被置顶面板遮挡）。</summary>
    internal void ExecuteSlot(int slot)
    {
        if (slot < 0 || slot >= 16) return;

        string? id = CurrentPage[slot];
        if (id == null)
        {
            // 空格：弹出 §5.5 菜单（无窗口焦点时用代码弹出）
            ShowMenu(BuildEmptyMenu(slot), slot);
            return;
        }

        var action = ActionRegistry.FindById(id);
        if (action == null)
        {
            ConfirmDialog.Info(this, "超级面板", $"槽位引用的动作不存在：{id}");
            return;
        }

        // 先关面板（避免动作窗口被置顶面板遮挡），再用唤起瞬间的光标坐标执行
        // （windowPin.toggle 等内部命令依赖该坐标）。
        SuperPanel.Close();
        ActionExecutor.Run(action, _invokeX, _invokeY);
    }

    /// <summary>Esc 处理：拖动中→取消拖动；否则关闭面板。</summary>
    internal void RequestEscape()
    {
        if (_dragging || _dragSrcPage != null) { CancelDrag(); return; }
        SuperPanel.Close();
    }

    // —— 页切换 ——

    private void Prev_Click(object sender, RoutedEventArgs e) => GoPage(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => GoPage(1);

    /// <summary>翻页（‹/› 按钮、滚轮、拖到 ‹/› 悬停）。**循环翻页**：末页再往后回到首页、首页再往前到末页。
    /// 带横滑过渡动画（§5.3）；仅 1 页时原地不动。</summary>
    private void GoPage(int delta)
    {
        if (_pages.Count <= 1) return;
        int n = ((_pageIdx + delta) % _pages.Count + _pages.Count) % _pages.Count;   // 环回
        _pageIdx = n;

        // 拖动中（跨页悬停翻页）只做瞬时切换：幽灵由 UpdateGhost 跟随光标，
        // 两页横滑会与幽灵叠加；刚创建 / 未加载时也无法截图。
        bool animate = !_dragging && _dragSrcPage == null && IsLoaded;
        var old = animate ? SnapshotCells() : null;   // 内部先打断上一轮，截到的是静止状态
        if (!animate) CancelPageFx();

        Rebuild();
        UpdateHeader();
        if (old != null) AnimatePageTurn(old, delta);
    }

    /// <summary>截当前格子区为位图（旧页）。</summary>
    private Image? SnapshotCells()
    {
        CancelPageFx();                      // 打断上一轮动画 → 上一新页已静止，截图即完整旧页
        double w = CellsHost.ActualWidth, h = CellsHost.ActualHeight;
        if (w < 1 || h < 1) return null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap(
                (int)Math.Ceiling(w * dpi.DpiScaleX), (int)Math.Ceiling(h * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            bmp.Render(CellsHost);
            bmp.Freeze();
            // 显式尺寸是关键：窗口 SizeToContent=Height ⇒ 测量链上「高度约束 = 无穷大」，
            // 而 Image 的 Stretch=Fill 语义是「填满约束」→ 会回报无穷高并沿
            // Image → PageFxHost → 格子区 Grid → 面板把高度撑爆（宽度受 Width=310 约束故只有高度变高）。
            // 指定 Width/Height 后期望尺寸与约束无关，且与格子区等大、竖缝严格对齐。
            return new Image
            {
                Source = bmp,
                Width = w,
                Height = h,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };
        }
        catch { return null; }   // 截图失败 → 退化为无动画翻页，绝不影响翻页本身
    }

    /// <summary>
    /// 翻页过渡：旧页（截图）与新页**同速同曲线**横滑，两者严格相邻（同一条竖缝扫过），无重叠。
    /// delta&gt;0 = 新页自右滑入、旧页向左滑出；delta&lt;0 反之。
    /// </summary>
    private void AnimatePageTurn(Image old, int dir)
    {
        int token = ++_pageAnimToken;
        double w = CellsHost.ActualWidth;
        if (w < 1) return;

        var dur = TimeSpan.FromMilliseconds(200);
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

        PageFxHost.Children.Clear();
        PageFxHost.Children.Add(old);        // 旧页截图在下层，与新页无缝衔接

        var oldT = new TranslateTransform();
        old.RenderTransform = oldT;
        var oldAnim = new DoubleAnimation(0, -dir * w, dur) { EasingFunction = ease };
        oldT.BeginAnimation(TranslateTransform.XProperty, oldAnim);

        var newT = new TranslateTransform(dir * w, 0);
        CellsHost.RenderTransform = newT;
        var newAnim = new DoubleAnimation(dir * w, 0, dur) { EasingFunction = ease };
        newAnim.Completed += (_, _) => EndPageFx(token);   // 被打断时 token 不匹配，交由打断方收尾
        newT.BeginAnimation(TranslateTransform.XProperty, newAnim);
    }

    /// <summary>翻页动画收尾：清截图、还原变换，回到静态。</summary>
    private void EndPageFx(int token)
    {
        if (token != _pageAnimToken) return;
        CancelPageFx();
    }

    /// <summary>终止翻页动画并还原静止状态（新一轮翻页开始 / 收尾 / 窗口销毁时调用）。</summary>
    private void CancelPageFx()
    {
        _pageAnimToken++;                    // 作废在跑动画的完成回调
        PageFxHost.Children.Clear();
        if (CellsHost.RenderTransform is TranslateTransform t)
            t.BeginAnimation(TranslateTransform.XProperty, null);
        CellsHost.RenderTransform = null;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_dragging) { e.Handled = true; return; }   // 拖动中不滚轮翻页，避免干扰跨页悬停
        GoPage(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void UpdateHeader()
    {
        PageIndicator.Text = $"{_pageIdx + 1}/{_pages.Count}";
        TitleText.Text = "超级面板";
        HintText.Text = _dragging
            ? "拖到目标格交换 · 拖到 ‹/› 翻页 · Esc 取消"
            : "1-9 选格 · 滚轮翻页 · Esc 关闭";
    }

    // —— 右键菜单（§5.4 / §5.5）——

    private ContextMenu BuildFilledMenu(int slot)
    {
        var menu = new ContextMenu();
        var miEdit = new MenuItem { Header = "编辑…" };
        miEdit.Click += (_, _) => Defer(() => EditAt(slot));
        var miCopy = new MenuItem { Header = "复制" };
        miCopy.Click += (_, _) => Defer(() => DuplicateAt(slot));
        var miDel = new MenuItem { Header = "删除" };
        miDel.Click += (_, _) => Defer(() => DeleteAt(slot));
        menu.Items.Add(miEdit);
        menu.Items.Add(miCopy);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDel);
        return menu;
    }

    private ContextMenu BuildEmptyMenu(int slot)
        => SlotMenu.BuildAddMenu(this, slot, NewActionAt, PickExistingAt, onClear: null);

    /// <summary>从动作池选已有动作放入本槽（补上面板此前缺失的「复用已有动作」能力）。</summary>
    private void PickExistingAt(int slot)
    {
        var picker = new ActionPoolPicker(this, "超级面板槽位 — 选择动作", CurrentPage[slot]) { Owner = this };
        RunDialog(() => { picker.ShowDialog(); });
        if (picker.Result == null) return;
        SetSlot(slot, picker.Result);
        Rebuild();
    }

    /// <summary>在指定格子上弹出菜单（左键点空格走此路径；右键由 ContextMenu 自动弹出）。</summary>
    private void ShowMenu(ContextMenu menu, int slot)
    {
        // 关键：该菜单未挂到按钮的 ContextMenu，须手动计入 IsInteracting，否则钩子在点菜单项时
        // 判定为「点面板外」→ 关面板 → 菜单（作为面板的弹出子窗）随之销毁 → 菜单项点击落空。
        TrackInteract(menu);
        menu.PlacementTarget = CellsHost.Children[slot] as UIElement;
        menu.Placement = PlacementMode.Center;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 跟踪菜单开合：计入 <c>_interactCount</c>（钩子不关面板），记录当前打开的菜单
    /// 供 <see cref="OnWindowMouseDown"/> 判定点击是否在菜单外。
    /// </summary>
    private void TrackInteract(ContextMenu menu)
    {
        menu.Opened += (_, _) => { _openMenu = menu; _interactCount++; };
        menu.Closed += (_, _) =>
        {
            if (_openMenu == menu) _openMenu = null;
            if (_interactCount > 0) _interactCount--;
        };
    }

    /// <summary>
    /// 窗口级鼠标 down（隧道，先于格子 <c>OnCellDown</c>）：菜单在场时收到的左键必在菜单外
    /// （菜单是独立 popup hwnd，其内点击不路由到本窗口）→ 只关菜单，落在格子区的本次点击
    /// 吞掉不触发；标题栏/页脚不吞（✕ 只点一次即生效）。
    /// <para><b>时序依据（冒烟实测）</b>：<c>ContextMenu</c> 关闭时 <c>IsOpen</c> 先置 false、
    /// <c>Closed</c> 事件<b>异步</b>派发——故判据只能用 <c>_openMenu != null</c>（状态对象在场，
    /// 覆盖「仍开着 / IsOpen 已 false 未派发」两个窗口期），<c>不能</c>看 <c>IsOpen</c>；
    /// 也无需时间窗兜底（down 到达时 Closed 必未派发 → <c>_openMenu</c> 必在场）。</para>
    /// 吞 down 后 <c>OnDragUp</c> 因 <c>_dragSrcPage==null</c> 安全 no-op，无需吞配对 up。
    /// </summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_openMenu == null) return;          // 无菜单在场：不干预
        var m = _openMenu;
        _openMenu = null;                       // 先自清：下面 IsOpen=false 触发的 Closed 不再重复动状态
        if (m.IsOpen) m.IsOpen = false;         // 尚未关闭则关（Closed → interact--）
        if (IsInCells(e.OriginalSource)) e.Handled = true;
    }

    /// <summary>visual tree 上溯判断某元素是否落在格子区（CellsHost 子树内）。</summary>
    private bool IsInCells(object? source)
    {
        DependencyObject? d = source as DependencyObject;
        while (d != null)
        {
            if (ReferenceEquals(d, CellsHost)) return true;
            // GetParent 对 ContentElement 等非 Visual 会抛异常，分别处理
            if (d is System.Windows.Media.Visual || d is System.Windows.Media.Media3D.Visual3D)
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            else if (d is FrameworkElement fe) d = fe.Parent;
            else return false;
        }
        return false;
    }

    /// <summary>把动作推迟到下一 Dispatcher 周期——右键菜单项 Click 内同步打开模态对话框，
    /// 会与菜单正在关闭的过程冲突（ShowDialog 可能不显示），故须延后执行。</summary>
    private void Defer(Action action) => Dispatcher.InvokeAsync(action);

    // —— 槽位 CRUD（全部经 ConfigIO 落盘）——

    private void NewActionAt(int slot, ActionType? presetType)
    {
        var dto = RunDialog(() =>
        {
            if (presetType == null)
                return ActionEditor.Show(this, "新建动作", null);
            string title = presetType == ActionType.composite ? "新建组合动作" : "新建动作";
            var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = presetType.Value };
            return ActionEditor.Show(this, title, draft);
        });
        if (dto == null) return;

        ConfigStore.AddAction(dto);
        SetSlot(slot, dto.Id);
        ConfigStore.Save();
        Rebuild();
    }

    private void EditAt(int slot)
    {
        string? id = CurrentPage[slot];
        if (id == null) return;
        var cur = ActionRegistry.FindById(id);
        if (cur == null) return;

        var dto = RunDialog(() => ActionEditor.Show(this, "编辑动作", cur));
        if (dto == null) return;
        dto.Id = cur.Id;   // 编辑不换 Id
        ConfigStore.UpdateAction(dto);
        ConfigStore.Save();
        Rebuild();
    }

    private void DuplicateAt(int slot)
    {
        string? id = CurrentPage[slot];
        if (id == null) return;
        var copy = ConfigStore.DuplicateAction(id);
        if (copy == null) return;
        ConfigStore.Save();

        int empty = CurrentPage.FindIndex(x => x == null);
        if (empty < 0)
        {
            RunDialog(() => ConfirmDialog.Info(this, "超级面板", "当前页已满，副本已加入动作池（可在设置里放入格子）。"));
            return;
        }
        SetSlot(empty, copy.Id);
        Rebuild();
    }

    private void DeleteAt(int slot)
    {
        string? id = CurrentPage[slot];
        if (id == null) return;
        string name = ActionRegistry.FindById(id)?.Name ?? "(未知动作)";
        bool ok = RunDialog(() => ConfirmDialog.Confirm(this, "删除动作",
            $"确定删除「{name}」吗？\n所有菜单项 / 超级面板槽位 / 组合步骤中的引用也会被清除。", danger: true));
        if (!ok) return;

        ConfigStore.RemoveAction(id);   // 同时清菜单项 / 组合步骤 / 所有页槽位
        ConfigStore.Save();
        // 磁盘槽位已被清除，重新加载当前页
        var reloaded = SuperPanel.LoadPages();
        _pages.Clear();
        _pages.AddRange(reloaded);
        if (_pageIdx >= _pages.Count) _pageIdx = _pages.Count - 1;
        Rebuild();
        UpdateHeader();
    }

    // —— 拖动重排（§5.6）——

    private void OnCellDown(int slot, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragSrcPage = _pageIdx;
        _dragSrcSlot = slot;
        _dragOrigin = e.GetPosition(this);
        _dragging = false;
        CaptureMouse();          // 捕获到窗口：拖出面板后 PreviewMouseMove/Up 仍路由到本窗口
        e.Handled = true;        // 吞掉 Button.Click——点击/拖动由释放时位移判定
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (_dragSrcPage is not int srcPage || _dragSrcSlot is not int srcSlot) return;
        var pos = e.GetPosition(this);

        if (!_dragging)
        {
            double dx = Math.Abs(pos.X - _dragOrigin.X);
            double dy = Math.Abs(pos.Y - _dragOrigin.Y);
            if (dx <= SystemParameters.MinimumHorizontalDragDistance
                && dy <= SystemParameters.MinimumVerticalDragDistance) return;
            _dragging = true;
            ShowGhost(srcPage, srcSlot);
            UpdateHeader();
        }

        UpdateGhost(pos);
        SetHoverSlot(HitTestSlot(pos));
        UpdatePageHover(pos);
        e.Handled = true;
    }

    private void OnDragUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || _dragSrcPage is not int sp || _dragSrcSlot is not int ss) return;
        ReleaseMouseCapture();

        bool wasDrag = _dragging;
        int target = _hoverSlot;
        EndDragVisuals();

        if (wasDrag)
        {
            // 交换：源页源格 ↔ 当前页目标格（目标无效或同位→取消）
            if (target >= 0 && target < 16 && !(sp == _pageIdx && ss == target))
            {
                bool samePage = sp == _pageIdx;
                (_pages[sp][ss], _pages[_pageIdx][target]) = (_pages[_pageIdx][target], _pages[sp][ss]);
                SuperPanel.SavePages(_pages);
                Rebuild();
                // 同页交换：两格内容从对方位置滑入（跨页交换目标格不在同 visual tree，跳过动画）
                if (samePage && ss < CellsHost.Children.Count && target < CellsHost.Children.Count
                    && CellsHost.Children[ss] is FrameworkElement a
                    && CellsHost.Children[target] is FrameworkElement b)
                {
                    Views.Controls.DragFx.AnimateSwap(a, b, this);
                }
            }
        }
        else
        {
            // 位移未超阈值→视为单击：执行原语义（空格弹菜单/非空执行）
            Defer(() => ExecuteSlot(ss));
        }
        _dragSrcPage = null;
        _dragSrcSlot = null;
        _dragging = false;
        UpdateHeader();
        e.Handled = true;
    }

    private void CancelDrag()
    {
        ReleaseMouseCapture();
        EndDragVisuals();
        _dragSrcPage = null;
        _dragSrcSlot = null;
        _dragging = false;
        UpdateHeader();
    }

    // —— 拖动视觉 ——

    private Button? _ghostSrcButton;   // 拖动源格（半透明占位，EndDragVisuals 恢复）

    private void ShowGhost(int page, int slot)
    {
        string? id = _pages[page][slot];
        var a = id != null ? ActionRegistry.FindById(id) : null;
        GhostIcon.Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE710";
        GhostName.Text = a != null ? a.Name : "（空）";
        DragGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
        // 源半透明占位（同页：跨页时源格不在当前 visual tree，无需设）
        if (page == _pageIdx && slot >= 0 && slot < CellsHost.Children.Count
            && CellsHost.Children[slot] is Button b)
        {
            b.Opacity = 0.35;
            _ghostSrcButton = b;
        }
    }

    private void UpdateGhost(Point windowPos)
    {
        var rel = TranslatePoint(windowPos, GhostLayer);   // 窗口坐标→Canvas 坐标
        double w = DragGhost.ActualWidth > 0 ? DragGhost.ActualWidth : DragGhost.Width;
        double h = DragGhost.ActualHeight > 0 ? DragGhost.ActualHeight : DragGhost.Height;
        Canvas.SetLeft(DragGhost, rel.X - w / 2);
        Canvas.SetTop(DragGhost, rel.Y - h / 2);
    }

    private void EndDragVisuals()
    {
        DragGhost.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
        if (_ghostSrcButton != null) { _ghostSrcButton.Opacity = 1; _ghostSrcButton = null; }
        SetHoverSlot(-1);
        StopPageHover();
    }

    // —— 命中测试 / 目标高亮 ——

    private int HitTestSlot(Point windowPos)
    {
        var rel = TranslatePoint(windowPos, CellsHost);
        if (rel.X < 0 || rel.Y < 0 || rel.X > CellsHost.ActualWidth || rel.Y > CellsHost.ActualHeight)
            return -1;
        if (CellsHost.InputHitTest(rel) is not DependencyObject hit) return -1;
        DependencyObject? d = hit;
        while (d != null && d != CellsHost)
        {
            if (d is Button b) return CellsHost.Children.IndexOf(b);
            d = VisualTreeHelper.GetParent(d);
        }
        return -1;
    }

    private void SetHoverSlot(int slot)
    {
        if (slot == _hoverSlot) return;
        if (_hoverSlot >= 0 && _hoverSlot < CellsHost.Children.Count)
            ClearHover((Button)CellsHost.Children[_hoverSlot]);
        _hoverSlot = slot;
        if (slot >= 0 && slot < CellsHost.Children.Count)
            ApplyHover((Button)CellsHost.Children[slot]);
    }

    private void ApplyHover(Button b)
    {
        b.BorderBrush = (Brush)FindResource("PrimaryBrush");
        b.BorderThickness = new Thickness(2);
    }

    private void ClearHover(Button b)
    {
        b.BorderBrush = (Brush)FindResource("BorderBrush");
        b.BorderThickness = new Thickness(1);
    }

    // —— 跨页：拖到 ‹/› 悬停翻页 ——

    private void UpdatePageHover(Point windowPos)
    {
        int dir = 0;
        // 循环翻页：首页悬停 ‹、末页悬停 › 同样可翻（仅 1 页时不翻）
        if (_pages.Count > 1 && IsOver(windowPos, PrevBtn)) dir = -1;
        else if (_pages.Count > 1 && IsOver(windowPos, NextBtn)) dir = 1;

        if (dir == 0) { StopPageHover(); return; }
        if (_pageHoverDir != dir || _pageHoverTimer?.IsEnabled != true)
        {
            _pageHoverDir = dir;
            _pageHoverTimer!.Stop();
            _pageHoverTimer.Start();   // 悬停 ~0.45s 后翻页（Tick 内 GoPage，可连续翻）
        }
    }

    private void StopPageHover()
    {
        if (_pageHoverTimer?.IsEnabled == true) _pageHoverTimer.Stop();
    }

    private bool IsOver(Point windowPos, FrameworkElement el)
    {
        if (el.ActualWidth <= 0) return false;
        var rel = TranslatePoint(windowPos, el);
        return rel.X >= 0 && rel.Y >= 0 && rel.X <= el.ActualWidth && rel.Y <= el.ActualHeight;
    }

    // —— 辅助 ——

    private void SetSlot(int slot, string id)
    {
        CurrentPage[slot] = id;
        SuperPanel.SavePages(_pages);
    }

    /// <summary>会话/对话框期间挂起 Topmost 并禁止钩子介入（否则点击对话框会被判为“点外部”而关面板）。</summary>
    private void RunDialog(Action fn)
    {
        Topmost = false;
        _interactCount++;
        try { fn(); }
        finally
        {
            if (_interactCount > 0) _interactCount--;
            Topmost = true;
        }
    }

    /// <summary>会话/对话框期间挂起 Topmost 并禁止钩子介入，并返回对话框结果。</summary>
    private T RunDialog<T>(Func<T> fn)
    {
        Topmost = false;
        _interactCount++;
        try { return fn(); }
        finally
        {
            if (_interactCount > 0) _interactCount--;
            Topmost = true;
        }
    }

    // —— 窗口事件 ——

    private void Close_Click(object sender, RoutedEventArgs e) => SuperPanel.Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // 钩子通常会先路由并吞掉这些键；此处为焦点落在窗口时的兜底。
        if (e.Key == Key.Escape) { RequestEscape(); e.Handled = true; return; }
        int idx = e.Key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => -1,
        };
        if (idx >= 1)
        {
            ExecuteSlot(idx - 1);   // 非空时内部已关面板
            e.Handled = true;
        }
    }

    // —— 屏幕工作区 P/Invoke（与 MouseTipWindow 同一套）——

    private struct POINT { public int x; public int y; }
    private struct RECT { public int left; public int top; public int right; public int bottom; }
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
