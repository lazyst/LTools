using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>
/// 超级面板窗口（计划 §5）。3×3 格子；左键单击空格弹新建菜单 / 非空执行动作，左键按住拖动重排（§5.6，交换语义，支持跨页）；
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

    // —— 拖动重排（§5.6）——
    private int? _dragSrcPage;                  // 拖动源页（左键按下时记录）
    private int? _dragSrcSlot;                  // 拖动源格
    private Point _dragOrigin;                  // 按下时的鼠标位置（阈值判定用）
    private bool _dragging;                     // 是否已进入拖动态（位移超阈值）
    private int _hoverSlot = -1;                // 拖动中悬停的目标格（-1=无）
    private DispatcherTimer? _pageHoverTimer;   // ‹/› 悬停翻页计时
    private int _pageHoverDir;                  // 悬停方向：-1=‹，+1=›

    internal SuperPanelWindow(List<List<string?>> pages, int invokeX, int invokeY)
    {
        InitializeComponent();
        _pages = pages;
        _invokeX = invokeX;
        _invokeY = invokeY;
        if (_pages.Count == 0) _pages.Add(new List<string?>(new string?[9]));

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
        _pageHoverTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _pageHoverTimer.Tick += (_, _) => GoPage(_pageHoverDir);   // 悬停翻页，可连续翻
    }

    /// <summary>供 <see cref="SuperPanel"/> 判断是否有右键菜单 / 对话框正打开（打开时钩子不介入）。</summary>
    internal int InteractCount => _interactCount;

    private List<string?> CurrentPage => _pages[_pageIdx];

    // —— 定位（仿 MouseTipWindow.PlaceNearCursor：物理像素→DIP 换算 + 工作区避让）——

    /// <summary>把面板「可见边框」紧贴光标（形如右键菜单），避开屏幕边缘。px/py 为屏幕物理像素。</summary>
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
        double w = ActualWidth > 0 ? ActualWidth : 310;
        double h = ActualHeight > 0 ? ActualHeight : 382;

        // XAML 里 Border 有 10px Margin（阴影留白），窗口外框比可见边框大一圈。
        // 按「可见边框」定位才能紧贴光标：可见边框左上角 = 光标 + gap。
        const double margin = 10;   // 与 Views/SuperPanel.xaml 的 Border.Margin 一致
        const double gap = 2;       // 光标到可见边框的间距（略入面板，便于立即移到首格）
        double visW = w - 2 * margin, visH = h - 2 * margin;

        double visX = cx + gap, visY = cy + gap;
        var screen = GetScreenBounds(px, py);
        double sLeft = screen.Left * m11, sTop = screen.Top * m22;
        double sRight = screen.Right * m11, sBottom = screen.Bottom * m22;
        // 右/下溢出 → 翻到光标左/上方（可见边框的右/下边对齐光标）
        if (visX + visW > sRight - 8) visX = cx - gap - visW;
        if (visX < sLeft + margin) visX = sLeft + margin;   // 连同阴影留白不越出工作区
        if (visY + visH > sBottom - 8) visY = cy - gap - visH;
        if (visY < sTop + margin) visY = sTop + margin;

        Left = visX - margin;   // 窗口外框 = 可见边框 - margin
        Top = visY - margin;
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
        _hoverSlot = -1;   // 子元素已清空，悬停引用失效
        CellsHost.Children.Clear();
        for (int slot = 0; slot < 9; slot++)
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
            Height = 96,                    // 正方形：面板宽 310 → 列宽 (310-22)/3=96
            Cursor = Cursors.Hand,
            // 不覆盖 Background/Tag：与设置页槽位（BuildSlotButton）一致——透明底 + 默认悬停
            // HoverBg(#F4F4F5)。原覆盖 SurfaceAlt(#F4F4F5) 与默认悬停同色→悬停无变化；
            // 改深色悬停又太暗。保留边框仅作 3×3 格子的视觉分隔。
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
        if (slot < 0 || slot >= 9) return;

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

    private void GoPage(int delta)
    {
        int n = _pageIdx + delta;
        if (n < 0 || n >= _pages.Count) return;
        _pageIdx = n;
        Rebuild();
        UpdateHeader();
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

    private void TrackInteract(ContextMenu menu)
    {
        menu.Opened += (_, _) => _interactCount++;
        menu.Closed += (_, _) => { if (_interactCount > 0) _interactCount--; };
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
                return ActionEditorDialog.Show(this, "新建动作", null);
            string title = presetType == ActionType.composite ? "新建组合动作" : "新建动作";
            var draft = new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default, Type = presetType.Value };
            return ActionEditorDialog.Show(this, title, draft);
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

        var dto = RunDialog(() => ActionEditorDialog.Show(this, "编辑动作", cur));
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
            if (target >= 0 && target < 9 && !(sp == _pageIdx && ss == target))
            {
                (_pages[sp][ss], _pages[_pageIdx][target]) = (_pages[_pageIdx][target], _pages[sp][ss]);
                SuperPanel.SavePages(_pages);
                Rebuild();
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

    private void ShowGhost(int page, int slot)
    {
        string? id = _pages[page][slot];
        var a = id != null ? ActionRegistry.FindById(id) : null;
        GhostIcon.Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE710";
        GhostName.Text = a != null ? a.Name : "（空）";
        DragGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
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
        if (IsOver(windowPos, PrevBtn) && _pageIdx > 0) dir = -1;
        else if (IsOver(windowPos, NextBtn) && _pageIdx < _pages.Count - 1) dir = 1;

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
