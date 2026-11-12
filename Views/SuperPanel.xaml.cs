using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Features;
using LTools.Features.Floating;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 动作面板窗口（设计见 <c>docs/PANEL_REDESIGN.md</c>）。
/// 上下双区：全局区（可切换场景）+ 上下文区（唤起瞬间按前台窗口解析）。
/// </summary>
/// <remarks>
/// <para>
/// **用完即收**：执行动作、打开设置、新建 / 编辑 / 复制 / 删除动作等入口都先关面板再开对话框，
/// 不把隐藏的面板当 Owner（隐藏 Owner 会让模态框出现时闪黑块，还会挡住长按右键手势）。
/// </para>
/// <para>
/// 搜索框是普通 <see cref="TextBox"/>：打开即聚焦，正常 IME 输入。全局钩子只拦 Esc，
/// 数字键不再选格（见 <see cref="Features.SuperPanel.HandleKey"/>）。
/// </para>
/// </remarks>
public partial class SuperPanelWindow : Window
{
    private readonly int _invokeX;
    private readonly int _invokeY;
    private readonly ForegroundApp? _foreground;
    private readonly PanelResolveResult _resolved;

    /// <summary>上下文区当前显示的场景 Id；null = 通用。仅本次会话有效，不落盘。</summary>
    private string? _contextSceneId;

    private readonly List<Button> _tiles = new();
    private int _selected = -1;
    private bool _pinned;
    private int _interactCount;
    private ContextMenu? _openMenu;

    /// <summary>主区停在「暂存区」页（左栏第二个入口）。仅本次打开有效，不落盘。</summary>
    private bool _stashMode;

    // 空白处左键拖动窗口（照 Quicker：不只顶栏可以拖）
    private Point _blankDragStart;
    private bool _blankDragArmed;

    // 拖动排序（两个区各一个：互不跨区移动）
    private readonly TileDragSort _globalDrag;
    private readonly TileDragSort _contextDrag;
    private readonly SearchDebounce _searchDebounce;

    // 上下两区高度比例（0.15~0.85），关闭时记忆
    private double _globalRatio;

    // 分组导航（只是滚动锚点）：每区的导航条按钮 + 每个分组块的引用，用于滚动与高亮同步
    private readonly List<Button> _globalRailBars = new();
    private readonly List<Button> _contextRailBars = new();
    private readonly List<FrameworkElement> _globalAnchors = new();
    private readonly List<FrameworkElement> _contextAnchors = new();

    // 每区末尾的一块留白：让最后一个分组也能滚到视图顶部（否则内容装得下时滚不动）
    private Border? _globalPad;
    private Border? _contextPad;

    /// <summary>供 <see cref="Features.SuperPanel"/> 判断是否有菜单 / 对话框正打开。</summary>
    internal int InteractCount => _interactCount;

    /// <summary>是否已钉住（点外部不关闭）。</summary>
    internal bool IsPinned => _pinned;

    internal SuperPanelWindow(ForegroundApp? foreground, int invokeX, int invokeY)
    {
        InitializeComponent();
        _searchDebounce = new SearchDebounce(150, ApplyPanelSearch);
        Closed += (_, _) => _searchDebounce.Cancel();
        _foreground = foreground;
        _invokeX = invokeX;
        _invokeY = invokeY;

        var panel = PanelLibrary.Current;
        _resolved = PanelSceneResolver.Resolve(panel, foreground);
        _contextSceneId = _resolved.Scene?.Id;

        Width = panel.Preferences.Width;
        Height = panel.Preferences.Height;

        _globalRatio = Math.Clamp(panel.Preferences.GlobalRatio, 0.15, 0.85);
        ApplySplitRatio();

        // 上次停留的页（动作面板 / 暂存区）：别每次打开都回到动作面板。
        // XAML 默认是动作面板，暂存区在 Loaded 里再切（此时窗口还没显示，切了不闪）。
        _stashMode = string.Equals(panel.Preferences.LastPage, "stash", StringComparison.OrdinalIgnoreCase);

        _globalDrag = new TileDragSort(this, GhostLayer, GlobalGroups)
        {
            AllowDrag = () => string.IsNullOrEmpty(SearchBox.Text?.Trim()),
            GhostBody = TileGhostBody,
            Finished = RebuildAll,
        };
        _contextDrag = new TileDragSort(this, GhostLayer, ContextGroups)
        {
            AllowDrag = () => string.IsNullOrEmpty(SearchBox.Text?.Trim()),
            GhostBody = TileGhostBody,
            Finished = RebuildAll,
        };

        // 分组导航只是滚动锚点：滚动时同步高亮（视口顶部所在的分组）
        GlobalScroll.ScrollChanged += (_, _) =>
            SyncRail(GlobalScroll, GlobalGroups, _globalRailBars, _globalAnchors);
        ContextScroll.ScrollChanged += (_, _) =>
            SyncRail(ContextScroll, ContextGroups, _contextRailBars, _contextAnchors);

        // 视口变大 / 变小（拖分割条、改面板高度）后，重算末尾留白
        GlobalScroll.SizeChanged += (_, _) =>
            UpdateZonePad(GlobalScroll, GlobalGroups, _globalAnchors, _globalPad);
        ContextScroll.SizeChanged += (_, _) =>
            UpdateZonePad(ContextScroll, ContextGroups, _contextAnchors, _contextPad);

        try
        {
            var icon = App.LoadIconSource();
            if (icon != null) LogoImage.Source = icon;
            else LogoFallback.Visibility = Visibility.Visible;
        }
        catch { LogoFallback.Visibility = Visibility.Visible; }

        SetPinned(false);   // 内部会推面板镜像（构造期 _window 尚未赋值 → Empty，Show 后再推有效快照）
        RebuildAll();

        // 暂存区页（照 Quicker：AI 写好的草稿就在面板左栏，不在设置里）
        StashView.ItemsChanged += UpdateStashRail;
        StashView.Reload(force: true);   // 只为把草稿数带进左栏提示（页面还没打开）

        Loaded += (_, _) =>
        {
            PlaceAtCursor(_invokeX, _invokeY);
            // 按上次停留的页恢复（暂存区的搜索框收起、列表刷新都在 ApplyPageMode 里）
            ApplyPageMode();
        };
        Activated += (_, _) => FocusSearch();

        PreviewMouseLeftButtonDown += OnWindowMouseDown;
        PreviewMouseMove += OnWindowMouseMove;
        PreviewMouseLeftButtonUp += (_, _) => _blankDragArmed = false;
        ContextMenuOpening += OnWindowContextMenuOpening;
    }

    /// <summary>
    /// 空白处左键拖动窗口（照 Quicker）：磁贴 / 按钮 / 输入框 / 滚动条 / 分割条仍归自己处理。
    /// </summary>
    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_blankDragArmed) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _blankDragArmed = false; return; }

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _blankDragStart.X) < 4 && Math.Abs(p.Y - _blankDragStart.Y) < 4) return;

        _blankDragArmed = false;
        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标已抬起 */ }
    }

    /// <summary>右键：没有自带菜单的地方（空白 / 分组标题 / 标题栏）弹出面板菜单。</summary>
    private void OnWindowContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;
        if (FindContextMenuOwner(src) != null) return;   // 磁贴 / 场景标签 / 分组导航条自己的菜单
        if (IsInteractive(src)) return;                  // 输入框 / 滚动条 / 分割条等，别抢原生行为

        var pos = Mouse.GetPosition(this);
        if (GlobalScroll.IsMouseOver || GlobalGroups.IsMouseOver || GlobalSceneTabs.IsMouseOver)
        {
            ShowZoneMenu(global: true, GroupAt(pos));
            e.Handled = true;
        }
        else if (ContextScroll.IsMouseOver || ContextGroups.IsMouseOver || ContextSceneTabs.IsMouseOver)
        {
            ShowZoneMenu(global: false, GroupAt(pos));
            e.Handled = true;
        }
        else
        {
            ShowPanelMenu();   // 标题栏 / 左栏空白 / 腰栏
            e.Handled = true;
        }
    }

    /// <summary>沿视觉树找第一个带 ContextMenu 的元素（有就交给它自己弹）。</summary>
    private static ContextMenu? FindContextMenuOwner(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.ContextMenu != null) return fe.ContextMenu;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    /// <summary>光标下的分组（分组块 / 磁贴网格的 Tag 是 group）。</summary>
    private PanelGroupDto? GroupAt(Point pos)
    {
        if (InputHitTest(pos) is not DependencyObject d) return null;
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.Tag is PanelGroupDto g) return g;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    // ============ 未配置提示 ============

    private void CreateSceneForForeground_Click(object sender, RoutedEventArgs e)
    {
        var fg = _foreground;
        if (fg == null) return;

        Features.SuperPanel.Close();
        var scene = PanelLibrary.CreateContextScene(fg.ExeName, fg.ExeName, fg.ExePath);
        ConfigHelper.OpenPanelScene(scene.Id);
    }

    private void DismissHint_Click(object sender, RoutedEventArgs e)
    {
        PanelLibrary.Current.HintDismissed = true;
        PanelLibrary.Save();
        ContextHintBar.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosed(EventArgs e)
    {
        StashView.SetActive(false);

        var prefs = PanelLibrary.Current.Preferences;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;

        bool dirty = false;
        if (w >= 340 && h >= 320 && (Math.Abs(prefs.Width - w) > 0.5 || Math.Abs(prefs.Height - h) > 0.5))
        {
            prefs.Width = w;
            prefs.Height = h;
            dirty = true;
        }
        if (Math.Abs(prefs.GlobalRatio - _globalRatio) > 0.001)
        {
            prefs.GlobalRatio = _globalRatio;
            dirty = true;
        }

        string page = _stashMode ? "stash" : "actions";
        if (!string.Equals(prefs.LastPage, page, StringComparison.OrdinalIgnoreCase))
        {
            prefs.LastPage = page;
            dirty = true;
        }
        if (dirty) PanelLibrary.Save();

        base.OnClosed(e);
    }

    // ============ 双区高度 ============

    /// <summary>按比例设置上下两区高度（分割条拖动后调用）。</summary>
    private void ApplySplitRatio()
    {
        GlobalRow.Height = new GridLength(_globalRatio, GridUnitType.Star);
        ContextRow.Height = new GridLength(1 - _globalRatio, GridUnitType.Star);
    }

    /// <summary>
    /// 分割条拖动：不交给 GridSplitter 写死像素，而是换算成比例写回星号行，
    /// 这样窗口缩放时两区仍按比例伸缩（比例记入 Preferences.GlobalRatio）。
    /// </summary>
    private void PaneSplitter_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double total = GlobalRow.ActualHeight + ContextRow.ActualHeight;
        if (total <= 0) return;

        double min = Math.Max(96, total * 0.15);
        double max = Math.Min(total - 96, total * 0.85);
        if (max < min) { e.Handled = true; return; }

        double proposed = Math.Clamp(GlobalRow.ActualHeight + e.VerticalChange, min, max);
        double ratio = proposed / total;
        if (Math.Abs(ratio - _globalRatio) > 0.0001)
        {
            _globalRatio = ratio;
            ApplySplitRatio();
        }
        e.Handled = true;
    }

    // ============ 定位 ============

    /// <summary>以光标为中心展开（鼠标落在面板正中）；越界时整体平移到工作区内。</summary>
    private void PlaceAtCursor(int px, int py)
    {
        double m11 = 1.0, m22 = 1.0;
        var src = PresentationSource.FromVisual(this);
        if (src != null)
        {
            var tfd = src.CompositionTarget.TransformFromDevice;
            m11 = tfd.M11;
            m22 = tfd.M22;
        }

        double cx = px * m11, cy = py * m22;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;

        var wa = WorkArea(px, py);
        double sLeft = wa.Left * m11, sTop = wa.Top * m22;
        double sRight = wa.Right * m11, sBottom = wa.Bottom * m22;

        double left = cx - w / 2;
        double top = cy - h / 2;

        if (left + w > sRight) left = Math.Max(sLeft, sRight - w);
        if (left < sLeft) left = sLeft;
        if (top + h > sBottom) top = Math.Max(sTop, sBottom - h);
        if (top < sTop) top = sTop;

        Left = left;
        Top = top;
    }

    private static Win32.Rect WorkArea(int x, int y)
    {
        var mon = Win32.MonitorFromPoint(new Win32.Point { X = x, Y = y }, Win32.MonitorDefaultToNearest);
        var mi = new Win32.MonitorInfo { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MonitorInfo>() };
        if (Win32.GetMonitorInfo(mon, ref mi)) return mi.rcWork;
        var wa = SystemParameters.WorkArea;
        return new Win32.Rect
        {
            Left = (int)wa.Left,
            Top = (int)wa.Top,
            Right = (int)wa.Right,
            Bottom = (int)wa.Bottom,
        };
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : (d as FrameworkElement)?.Parent;
        }
        return null;
    }
}
