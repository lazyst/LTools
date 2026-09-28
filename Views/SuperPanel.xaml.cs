using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>
/// 超级面板窗口（计划 §5）。3×3 格子；左键非空执行 / 空弹新建菜单；右键非空编辑/删除/复制/移动、
/// 空弹 <see cref="ActionType"/> 新建菜单；滚轮翻页 + 1~9 选格 + Esc / 点外部关闭。
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
    private int? _movePage;                     // 移动模式：源页
    private int? _moveSlot;                     // 移动模式：源格

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
        double w = ActualWidth > 0 ? ActualWidth : 384;
        double h = ActualHeight > 0 ? ActualHeight : 360;

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
            Margin = new Thickness(3),
            MinHeight = 82,
            Cursor = Cursors.Hand,
            Background = (Brush)FindResource("SurfaceAltBrush"),
            // BtnTemplate 把 Tag 当「悬停背景画刷」用。格子底色 SurfaceAlt(#F4F4F5) 与默认
            // 悬停色 HoverBg(#F4F4F5) 同色→悬停无变化，故显式指定更深的 BorderStrong 作悬停色。
            Tag = (Brush)FindResource("BorderStrongBrush"),
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

        btn.Click += (_, _) => Defer(() => ExecuteSlot(slot));
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

        // 移动模式：点击目标格完成移动
        if (_movePage != null)
        {
            CompleteMove(slot);
            return;
        }

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

    /// <summary>Esc 处理：移动模式→取消移动；否则关闭面板。</summary>
    internal void RequestEscape()
    {
        if (_movePage != null) { CancelMove(); return; }
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
        GoPage(e.Delta < 0 ? 1 : -1);
        e.Handled = true;
    }

    private void UpdateHeader()
    {
        PageIndicator.Text = $"{_pageIdx + 1}/{_pages.Count}";
        if (_movePage != null)
        {
            TitleText.Text = "超级面板 · 移动";
            HintText.Text = "点击目标格子完成移动 · Esc 取消";
        }
        else
        {
            TitleText.Text = "超级面板";
            HintText.Text = "1-9 选格 · 滚轮翻页 · Esc 关闭";
        }
    }

    // —— 右键菜单（§5.4 / §5.5）——

    private ContextMenu BuildFilledMenu(int slot)
    {
        var menu = new ContextMenu();
        var miEdit = new MenuItem { Header = "编辑…" };
        miEdit.Click += (_, _) => Defer(() => EditAt(slot));
        var miCopy = new MenuItem { Header = "复制" };
        miCopy.Click += (_, _) => Defer(() => DuplicateAt(slot));
        var miMove = new MenuItem { Header = "移动到其他格子…" };
        miMove.Click += (_, _) => Defer(() => StartMove(slot));
        var miDel = new MenuItem { Header = "删除" };
        miDel.Click += (_, _) => Defer(() => DeleteAt(slot));
        menu.Items.Add(miEdit);
        menu.Items.Add(miCopy);
        menu.Items.Add(miMove);
        menu.Items.Add(new Separator());
        menu.Items.Add(miDel);
        return menu;
    }

    private ContextMenu BuildEmptyMenu(int slot)
    {
        var menu = new ContextMenu();

        var miNew = new MenuItem { Header = "新建" };
        var miAction = new MenuItem { Header = "动作…" };
        miAction.Click += (_, _) => Defer(() => NewActionAt(slot, null));
        var miComposite = new MenuItem { Header = "组合动作…" };
        miComposite.Click += (_, _) => Defer(() => NewActionAt(slot, ActionType.composite));
        miNew.Items.Add(miAction);
        miNew.Items.Add(miComposite);
        menu.Items.Add(miNew);

        var miQuick = new MenuItem { Header = "快捷新建" };
        foreach (var (label, type) in new[]
        {
            ("启动软件", ActionType.launchApp),
            ("打开文件", ActionType.openFile),
            ("打开文件夹", ActionType.openFolder),
            ("运行命令", ActionType.runCommand),
            ("打开网址", ActionType.openUrl),
        })
        {
            var mi = new MenuItem { Header = label };
            var t = type;
            mi.Click += (_, _) => Defer(() => NewActionAt(slot, t));
            miQuick.Items.Add(mi);
        }
        menu.Items.Add(miQuick);
        return menu;
    }

    /// <summary>在指定格子上弹出菜单（左键点空格走此路径；右键由 ContextMenu 自动弹出）。</summary>
    private void ShowMenu(ContextMenu menu, int slot)
    {
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

    // —— 移动模式 ——

    private void StartMove(int slot)
    {
        if (CurrentPage[slot] == null) return;
        _movePage = _pageIdx;
        _moveSlot = slot;
        UpdateHeader();
    }

    private void CompleteMove(int targetSlot)
    {
        int sp = _movePage!.Value, ss = _moveSlot!.Value;
        var srcPage = _pages[sp];
        var dstPage = _pages[_pageIdx];
        if (sp == _pageIdx && ss == targetSlot) { CancelMove(); return; }

        (srcPage[ss], dstPage[targetSlot]) = (dstPage[targetSlot], srcPage[ss]);
        _movePage = null;
        _moveSlot = null;
        SuperPanel.SavePages(_pages);
        Rebuild();
        UpdateHeader();
    }

    private void CancelMove()
    {
        _movePage = null;
        _moveSlot = null;
        UpdateHeader();
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
