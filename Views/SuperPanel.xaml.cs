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
using LTools.Features;
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

    // 空白处左键拖动窗口（照 Quicker：不只顶栏可以拖）
    private Point _blankDragStart;
    private bool _blankDragArmed;

    // 拖动排序（两个区各一个：互不跨区移动）
    private readonly TileDragSort _globalDrag;
    private readonly TileDragSort _contextDrag;

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

        SetPinned(false);
        RebuildAll();

        Loaded += (_, _) =>
        {
            PlaceAtCursor(_invokeX, _invokeY);
            FocusSearch();
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

    // ============ 构建 ============

    private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    private void RebuildAll()
    {
        _tiles.Clear();
        _selected = -1;
        BuildSceneTabs();
        BuildGlobal();
        BuildContext();
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void BuildSceneTabs()
    {
        GlobalSceneTabs.Children.Clear();
        var active = PanelLibrary.ActiveGlobalScene();

        foreach (var scene in PanelLibrary.Current.Global.Scenes)
        {
            var s = scene;
            GlobalSceneTabs.Children.Add(BuildTab(
                s.Name,
                ReferenceEquals(s, active),
                s.Name,
                () =>
                {
                    PanelLibrary.Current.Global.ActiveSceneId = s.Id;
                    PanelLibrary.Save();
                    BuildSceneTabs();
                    BuildGlobal();
                    GlobalScroll.ScrollToTop();
                },
                BuildGlobalSceneMenu(s)));
        }

        if (GlobalSceneTabs.Children.Count == 0)
            GlobalSceneTabs.Children.Add(EmptyHint("还没有全局场景，请在设置「面板管理」里新建。"));
    }

    /// <summary>
    /// 一个场景标签：左侧竖条 + 名称。选中 = 浅底 + 描边 + 主色竖条（照 Quicker 的区头标签）。
    /// </summary>
    private Button BuildTab(string text, bool active, string tooltip, Action onClick,
                            ContextMenu? menu = null)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new Border
        {
            Width = 3,
            Height = 13,
            CornerRadius = new CornerRadius(1.5),
            Background = active ? Res("PrimaryBrush") : Brushes.Transparent,
            Margin = new Thickness(0, 0, 6, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = active ? Res("TextBrush") : Res("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 160,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var b = new Button
        {
            Style = (Style)FindResource("SceneTabBtn"),
            Content = content,
            ToolTip = tooltip,
            Background = active ? Res("SurfaceAltBrush") : Brushes.Transparent,
            BorderBrush = active ? Res("BorderBrush") : Brushes.Transparent,
            BorderThickness = active ? new Thickness(1) : new Thickness(0),
        };
        b.Click += (_, _) => onClick();
        if (menu != null) b.ContextMenu = menu;
        return b;
    }

    private void BuildGlobal()
    {
        GlobalGroups.Children.Clear();
        GlobalGroupRail.Children.Clear();
        _globalRailBars.Clear();
        _globalAnchors.Clear();
        _globalPad = null;

        var scene = PanelLibrary.ActiveGlobalScene();
        if (scene == null)
        {
            GlobalGroupRail.Visibility = Visibility.Collapsed;
            GlobalGroups.Children.Add(EmptyHint("还没有全局场景，请在设置「面板管理」里新建。"));
            return;
        }

        FillZone(GlobalGroupRail, GlobalGroups, GlobalScroll, scene.Groups, _globalDrag,
                 global: true, _globalRailBars, _globalAnchors);
    }

    private void BuildContext()
    {
        ContextGroups.Children.Clear();
        ContextGroupRail.Children.Clear();
        _contextRailBars.Clear();
        _contextAnchors.Clear();
        _contextPad = null;
        BuildContextTabs();

        var scene = GetContextScene();
        var groups = scene?.Groups ?? PanelLibrary.Current.Context.Common;
        FillZone(ContextGroupRail, ContextGroups, ContextScroll, groups, _contextDrag,
                 global: false, _contextRailBars, _contextAnchors);

        // 手动切到别的场景时不显示「未配置」提示（那说的是自动匹配结果）
        bool manual = _contextSceneId != _resolved.Scene?.Id;
        if (!manual && _resolved.ShowHint && _foreground != null)
        {
            ContextHintBar.Visibility = Visibility.Visible;
            ContextHintText.Text = HintTextFor(_foreground.ExeName);
        }
        else
        {
            ContextHintBar.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 未配置提示的文案（照 Quicker：`尚未为“OpenChamber”添加专用设置`，进程名去掉 .exe）。
    /// </summary>
    private static string HintTextFor(string? exeName)
    {
        string name = (exeName ?? "").Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return name.Length == 0
            ? "尚未为此程序添加专用设置"
            : $"尚未为“{name}”添加专用设置";
    }

    /// <summary>
    /// 画一个区：所有分组照常从上到下平铺（一个分组 = 标题 + 磁贴网格），
    /// 左边那条导航只是**滚动锚点**——悬停哪一条就把视图滚到那个分组（照 Quicker 的分组导航）。
    /// 搜索时导航条让位，因为筛选后分组位置会变。
    /// </summary>
    private void FillZone(StackPanel rail, StackPanel host, ScrollViewer scroll,
                          List<PanelGroupDto> groups, TileDragSort drag, bool global,
                          List<Button> bars, List<FrameworkElement> anchors)
    {
        string query = SearchBox.Text?.Trim() ?? "";
        bars.Clear();
        anchors.Clear();
        rail.Children.Clear();

        anchors.AddRange(AddGroups(host, groups, drag, global));

        if (query.Length > 0 || groups.Count == 0 || anchors.Count != groups.Count)
        {
            rail.Visibility = Visibility.Collapsed;
            if (global) _globalPad = null;
            else _contextPad = null;
            return;
        }

        rail.Visibility = Visibility.Visible;

        // 末尾留白：让「最后一个分组的标题」也能贴到视图顶部（Quicker 的效果）。
        // 高度由 UpdateZonePad 按视口算；搜索 / 单分组时不会加。
        var pad = new Border { Height = 0 };
        host.Children.Add(pad);
        if (global) _globalPad = pad;
        else _contextPad = pad;

        for (int i = 0; i < groups.Count; i++)
        {
            int index = i;
            var bar = BuildGroupBar(groups[i], global, active: false);
            bar.MouseEnter += (_, _) =>
            {
                if (DateTime.UtcNow < _railHoverIgnoreUntil) return;
                ScrollToGroup(scroll, host, anchors, index);
            };
            bar.Click += (_, _) => ScrollToGroup(scroll, host, anchors, index);
            rail.Children.Add(bar);
            bars.Add(bar);
        }

        rail.Children.Add(BuildGroupAddBar(groups, global));

        // 重建后光标可能正好落在新的一条上（典型的：点完「＋」导航条整体下移），
        // 这时的 MouseEnter 不是用户主动悬停，忽略一小会儿
        _railHoverIgnoreUntil = DateTime.UtcNow.AddMilliseconds(250);

        scroll.UpdateLayout();
        UpdateZonePad(scroll, host, anchors, pad);
        SyncRail(scroll, host, bars, anchors);
    }

    private Button BuildGroupBar(PanelGroupDto group, bool global, bool active)
    {
        var bar = new Button
        {
            Style = (Style)FindResource("GroupBarBtn"),
            Tag = active ? "Active" : null,
            ToolTip = group.Name + "\n右键：分组菜单",
        };
        AutomationProperties.SetName(bar, group.Name);
        bar.ContextMenu = BuildGroupMenu(group, global);
        return bar;
    }

    /// <summary>在当前场景里新建一个空分组，并滚到它。</summary>
    private void AddGroupToZone(bool global)
    {
        var groups = ZoneGroups(global);
        groups.Add(PanelLibrary.NewGroup("新分组"));
        PanelLibrary.Save();

        if (global)
        {
            BuildGlobal();
            if (_globalAnchors.Count > 0)
                ScrollToGroup(GlobalScroll, GlobalGroups, _globalAnchors, _globalAnchors.Count - 1);
        }
        else
        {
            BuildContext();
            if (_contextAnchors.Count > 0)
                ScrollToGroup(ContextScroll, ContextGroups, _contextAnchors, _contextAnchors.Count - 1);
        }
    }

    private List<PanelGroupDto> ZoneGroups(bool global)
    {
        if (!global) return GetContextScene()?.Groups ?? PanelLibrary.Current.Context.Common;

        var scene = PanelLibrary.ActiveGlobalScene();
        if (scene == null)
        {
            // 没有全局场景时右键「新建分组」：顺手建一个默认场景，别让操作静默失败
            scene = PanelLibrary.NewScene("默认");
            PanelLibrary.Current.Global.Scenes.Add(scene);
            PanelLibrary.Current.Global.ActiveSceneId = scene.Id;
        }
        return scene.Groups;
    }

    /// <summary>分组导航末尾的「＋」：在当前场景里新建一个空分组，并滚到它。</summary>
    private Button BuildGroupAddBar(List<PanelGroupDto> groups, bool global)
    {
        var add = new Button
        {
            Style = (Style)FindResource("GroupAddBtn"),
            ToolTip = "新建分组（右键空白处也有菜单）",
        };
        AutomationProperties.SetName(add, "新建分组");
        add.Click += (_, _) => AddGroupToZone(global);
        return add;
    }

    /// <summary>把视图滚到第 <paramref name="index"/> 个分组的顶部（带 180ms 缓出，照 Quicker 的悬停滚动）。</summary>
    private void ScrollToGroup(ScrollViewer scroll, FrameworkElement host,
                               List<FrameworkElement> anchors, int index, bool allowRetry = true)
    {
        if (index < 0 || index >= anchors.Count) return;

        double target;
        try { target = anchors[index].TranslatePoint(new Point(0, 0), host).Y - 2; }
        catch (InvalidOperationException) { return; }

        // 先把末尾留白算好再读可滚距离：否则最后一个分组永远滚不到顶部
        UpdateZonePad(scroll, host, anchors, PadOf(scroll));
        scroll.UpdateLayout();
        double max = Math.Max(0, scroll.ScrollableHeight);

        // 没有可滚的距离（内容装得下，或刚新建的分组还没量好高度）：
        // 等一小会儿重试一次就够了。**不能无条件 BeginInvoke 重试**——内容一直装得下时
        // 会把自己无限排回队列，把 UI 线程堵死。
        if (target > 0.5 && max <= 0.5)
        {
            if (!allowRetry || _scrollRetryScheduled) return;

            _scrollRetryScheduled = true;
            var retry = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            retry.Tick += (_, _) =>
            {
                retry.Stop();
                _scrollRetryScheduled = false;
                ScrollToGroup(scroll, host, anchors, index, allowRetry: false);
            };
            retry.Start();
            return;
        }

        AnimateScroll(scroll, Math.Clamp(target, 0, max));
    }

    private DispatcherTimer? _scrollAnim;

    /// <summary>滚到某个区时找它自己的末尾留白。</summary>
    private Border? PadOf(ScrollViewer scroll)
        => ReferenceEquals(scroll, GlobalScroll) ? _globalPad : _contextPad;

    /// <summary>
    /// 维护末尾留白：高度 = 视口高度 - 最后一个分组块的高度（不足则 0）。
    /// 有了这块空高，最后一个分组也能把标题滚到视图顶部（Quicker 的效果）；
    /// 单分组 / 搜索时不加，免得平白多一条滚动条。
    /// </summary>
    private static void UpdateZonePad(ScrollViewer scroll, FrameworkElement host,
                                      List<FrameworkElement> anchors, Border? pad)
    {
        if (pad == null) return;

        if (anchors.Count < 2)
        {
            if (pad.Height != 0) pad.Height = 0;
            return;
        }

        if (scroll.ViewportHeight <= 0) return;

        double lastTop;
        try { lastTop = anchors[^1].TranslatePoint(new Point(0, 0), host).Y; }
        catch (InvalidOperationException) { return; }

        // 增量修正：目标 = ExtentHeight 刚好等于「最后一块的顶部 + 一屏」
        double next = pad.Height + lastTop + scroll.ViewportHeight - scroll.ExtentHeight;
        next = Math.Max(0, next);
        if (Math.Abs(next - pad.Height) > 0.5) pad.Height = next;
    }

    /// <summary>「等布局再滚」只重试一次，避免在不可滚动的区域打转。</summary>
    private bool _scrollRetryScheduled;

    /// <summary>导航条刚重建完的一小段时间里，忽略 MouseEnter（光标被动落在新条目上）。</summary>
    private DateTime _railHoverIgnoreUntil;

    private void AnimateScroll(ScrollViewer scroll, double target)
    {
        _scrollAnim?.Stop();
        _scrollAnim = null;

        double from = scroll.VerticalOffset;
        if (Math.Abs(target - from) < 1)
        {
            scroll.ScrollToVerticalOffset(target);
            return;
        }

        double elapsed = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _scrollAnim = timer;
        timer.Tick += (_, _) =>
        {
            elapsed += 16;
            double p = Math.Min(1, elapsed / 180.0);
            double eased = 1 - Math.Pow(1 - p, 3);
            scroll.ScrollToVerticalOffset(from + (target - from) * eased);
            if (p >= 1)
            {
                timer.Stop();
                if (ReferenceEquals(_scrollAnim, timer)) _scrollAnim = null;
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 滚动时同步导航条的高亮：视口顶部所在的分组 = 当前分组。
    /// 高亮只是「你在哪一段」，不改内容，也不写盘。
    /// </summary>
    private static void SyncRail(ScrollViewer scroll, FrameworkElement host,
                                 List<Button> bars, List<FrameworkElement> anchors)
    {
        if (bars.Count == 0 || anchors.Count != bars.Count) return;

        int active = 0;
        for (int i = 0; i < anchors.Count; i++)
        {
            double top;
            try { top = anchors[i].TranslatePoint(new Point(0, 0), host).Y; }
            catch (InvalidOperationException) { return; }

            // 8px 容差：滚过一点点就算到了这个分组
            if (top - scroll.VerticalOffset <= 8) active = i;
            else break;
        }

        // 最后一个分组一般没法定到顶部（内容不够高）：滚到底就算它
        if (scroll.ScrollableHeight > 0 && scroll.VerticalOffset >= scroll.ScrollableHeight - 1)
            active = anchors.Count - 1;

        for (int i = 0; i < bars.Count; i++)
            bars[i].Tag = i == active ? "Active" : null;
    }

    /// <summary>上下文区当前场景；null = 通用兜底。</summary>
    private PanelSceneDto? GetContextScene()
    {
        if (_contextSceneId == null) return null;
        return PanelLibrary.Current.Context.Scenes
            .FirstOrDefault(s => s.Id == _contextSceneId && s.Enabled);
    }

    private void BuildContextTabs()
    {
        ContextSceneTabs.Children.Clear();

        ContextSceneTabs.Children.Add(BuildTab(
            "通用",
            _contextSceneId == null,
            "没有匹配到程序场景时显示",
            () =>
            {
                _contextSceneId = null;
                BuildContext();
                ContextScroll.ScrollToTop();
            },
            BuildContextSceneMenu(null)));

        var current = GetContextScene();
        var scenes = PanelLibrary.Current.Context.Scenes
            .Where(s => s.Enabled && string.IsNullOrEmpty(s.ParentId))
            .ToList();

        // 命中的窗口子场景不在根列表里：补一个标签，否则当前场景没有选中项
        if (current != null && !scenes.Contains(current)) scenes.Insert(0, current);

        foreach (var scene in scenes)
        {
            var s = scene;
            string tip = PanelSceneResolver.DescribeMatch(s)
                         + (string.IsNullOrEmpty(s.ParentId) ? "" : "（窗口子场景）");
            ContextSceneTabs.Children.Add(BuildTab(
                s.Name,
                _contextSceneId == s.Id,
                tip,
                () =>
                {
                    _contextSceneId = s.Id;
                    BuildContext();
                    ContextScroll.ScrollToTop();
                },
                BuildContextSceneMenu(s)));
        }
    }

    /// <summary>
    /// 把所有分组铺进 <paramref name="host"/>：一个分组 = 一个小块（标题 + 磁贴网格）。
    /// 返回每个分组的块，供左缘导航条当滚动锚点（块数 = 分组数，搜索过滤时可能更少）。
    /// </summary>
    private List<FrameworkElement> AddGroups(StackPanel host, List<PanelGroupDto> groups, TileDragSort drag,
                                             bool global)
    {
        var anchors = new List<FrameworkElement>();
        string query = SearchBox.Text?.Trim() ?? "";
        var prefs = PanelLibrary.Current.Preferences;

        foreach (var group in groups)
        {
            var ids = group.Actions
                .Where(id => MatchesQuery(id, query))
                .ToList();
            if (ids.Count == 0 && query.Length > 0) continue;

            var block = new StackPanel { Tag = group };

            if (prefs.ShowGroupTitles)
            {
                var title = new TextBlock
                {
                    Text = group.Name,
                    FontSize = (double)FindResource("FontSizeHint"),
                    Foreground = Res("SecondaryTextBrush"),
                    Margin = new Thickness(2, 4, 0, 2),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    ToolTip = "双击重命名；右键管理分组",
                };
                title.MouseLeftButtonDown += (_, e) =>
                {
                    if (e.ClickCount != 2) return;
                    BeginRenameGroup(group, global);
                    e.Handled = true;
                };
                block.Children.Add(title);
            }

            var grid = new TileGridPanel
            {
                MinTileWidth = 72,
                GapX = 4,
                GapY = 2,
                TileHeight = prefs.CompactTiles ? 56 : 72,
                Margin = new Thickness(0, 0, 0, 4),
                Tag = group,
            };

            foreach (string id in ids)
            {
                int index = group.Actions.IndexOf(id);
                if (index >= 0) grid.Children.Add(BuildTile(group, index, id, drag));
            }

            if (query.Length == 0) grid.Children.Add(BuildAddTile(group));

            block.Children.Add(grid);
            host.Children.Add(block);
            anchors.Add(block);
        }

        if (host.Children.Count == 0)
            host.Children.Add(EmptyHint(query.Length > 0 ? "没有匹配的动作" : "这个区域还没有动作"));

        return anchors;
    }

    /// <summary>
    /// 搜索只匹配动作名称（忽略大小写）。**不匹配内部 Id**——否则搜 "1" 会命中 a1 之类的编号；
    /// 缺失动作没有名称，只在未搜索时显示。
    /// </summary>
    private bool MatchesQuery(string id, string query)
    {
        if (query.Length == 0) return true;
        return ActionRegistry.FindById(id) is { } a
               && a.Name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 磁贴内容（图标 chip + 标题）。磁贴与拖动幽灵共用一份，保证幽灵和磁贴长得一模一样。
    /// </summary>
    private static StackPanel TileContent(ActionDto? a, bool compact)
    {
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new Border
        {
            Width = compact ? 26 : 36,
            Height = compact ? 26 : 36,
            CornerRadius = new CornerRadius(compact ? 6 : 8),
            Background = Res("SurfaceAltBrush"),
            BorderBrush = Res("BorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = a != null ? IconCatalog.GetGlyph(a.Icon) : "\uE783",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = compact ? 15 : 19,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res(a != null ? "TextBrush" : "DangerBrush"),
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = a != null ? a.Name : "缺失",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = compact ? 12 : 11,
            MaxWidth = 100,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res(a != null ? "SecondaryTextBrush" : "DangerBrush"),
        });
        return content;
    }

    /// <summary>拖动幽灵的内容：按拖动中的磁贴临时造一份同样的内容（不依赖截图快照）。</summary>
    private static FrameworkElement? TileGhostBody(PanelTileRef tref)
    {
        if (tref.ActionId == null) return null;
        return TileContent(
            ActionRegistry.FindById(tref.ActionId),
            PanelLibrary.Current.Preferences.CompactTiles);
    }

    private Button BuildTile(PanelGroupDto group, int index, string id, TileDragSort drag)
    {
        var a = ActionRegistry.FindById(id);
        bool compact = PanelLibrary.Current.Preferences.CompactTiles;

        var btn = new Button
        {
            Style = (Style)FindResource("TileBtn"),
            Height = compact ? 56 : 72,
            Margin = new Thickness(0),
            Tag = new PanelTileRef { Group = group, Index = index, ActionId = id },
        };

        // 照 Quicker：36px 图标 chip（淡底 + 1px 内描边）+ 11px 次要色标题，垂直居中
        var content = TileContent(a, compact);
        btn.Content = content;

        var tref = (PanelTileRef)btn.Tag;
        drag.Attach(btn);
        btn.Click += (_, _) =>
        {
            if (tref.ActionId != null) ExecuteAction(tref.ActionId);
        };

        var menu = a != null ? BuildFilledMenu(tref) : BuildMissingMenu(tref);
        TrackInteract(menu);
        btn.ContextMenu = menu;

        _tiles.Add(btn);
        return btn;
    }

    private Button BuildAddTile(PanelGroupDto group)
    {
        bool compact = PanelLibrary.Current.Preferences.CompactTiles;
        var btn = new Button
        {
            Style = (Style)FindResource("TileBtn"),
            Height = compact ? 56 : 72,
            Margin = new Thickness(0),
            Tag = new PanelTileRef { Group = group, Index = group.Actions.Count, ActionId = null },
            Opacity = 0.75,
        };
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new TextBlock
        {
            Text = "\uE710",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = compact ? 16 : 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res("HintTextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = "添加",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = compact ? 11 : 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res("HintTextBrush"),
        });
        btn.Content = content;
        btn.Click += (_, _) => ShowAddMenu(group, btn);
        return btn;
    }

    private static TextBlock EmptyHint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Margin = new Thickness(4, 6, 4, 4),
        Foreground = Res("HintTextBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    // ============ 搜索 / 选中 ============

    private void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
        RebuildAll();
        GlobalScroll.ScrollToTop();
        ContextScroll.ScrollToTop();
        if (SearchBox.Text?.Length > 0 && _tiles.Count > 0) SelectTile(0);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                RequestEscape();
                e.Handled = true;
                break;
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Tab:
                // 输入框里 Tab 让出焦点给磁贴，保持可键盘操作
                break;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { RequestEscape(); e.Handled = true; return; }
        if (e.Key == Key.Down) { MoveSelection(1); e.Handled = true; return; }
        if (e.Key == Key.Up) { MoveSelection(-1); e.Handled = true; return; }
        if (e.Key == Key.Enter) { ExecuteSelected(); e.Handled = true; }
    }

    private void MoveSelection(int delta)
    {
        if (_tiles.Count == 0) return;
        int next = _selected < 0
            ? (delta > 0 ? 0 : _tiles.Count - 1)
            : Math.Clamp(_selected + delta, 0, _tiles.Count - 1);
        SelectTile(next);
    }

    private void SelectTile(int index)
    {
        if (index < 0 || index >= _tiles.Count) return;
        if (_selected >= 0 && _selected < _tiles.Count)
        {
            var old = _tiles[_selected];
            old.BorderBrush = Res("BorderBrush");
            old.BorderThickness = new Thickness(1);
        }
        _selected = index;
        var b = _tiles[index];
        b.BorderBrush = Res("PrimaryBrush");
        b.BorderThickness = new Thickness(2);
        try { b.BringIntoView(); } catch { /* 布局尚未完成时忽略 */ }
    }

    private void ExecuteSelected()
    {
        if (_selected < 0 || _selected >= _tiles.Count) return;
        if (_tiles[_selected].Tag is not PanelTileRef tref || tref.ActionId == null) return;
        ExecuteAction(tref.ActionId);
    }

    /// <summary>Esc：拖动中先取消，否则关闭面板。</summary>
    internal void RequestEscape()
    {
        if (_globalDrag.IsActive) { _globalDrag.Cancel(); return; }
        if (_contextDrag.IsActive) { _contextDrag.Cancel(); return; }
        Features.SuperPanel.Close();
    }

    private void ExecuteAction(string id)
    {
        var action = ActionRegistry.FindById(id);
        if (action == null)
        {
            ToastService.Show("动作不存在：" + id);
            return;
        }
        Features.SuperPanel.Close();
        ActionExecutor.Run(action, _invokeX, _invokeY);
    }

    // ============ 磁贴拖动（实现见 Views/Controls/TileDragSort.cs） ============

    /// <summary>坐标是否落在磁贴上（菜单在场时吞掉这次点击用）。</summary>
    private bool IsOverTile(Point windowPos)
    {
        if (InputHitTest(windowPos) is not DependencyObject d) return false;
        while (d != null)
        {
            if (d is Button b && b.Tag is PanelTileRef) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    // ============ 菜单 ============

    private ContextMenu BuildFilledMenu(PanelTileRef tref)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "编辑…", () => EditAction(tref));
        AddMenuItem(menu, "复制", () => DuplicateAction(tref));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "从此组移除", () => RemoveFromGroup(tref));
        AddMenuItem(menu, "删除动作…", () => DeleteAction(tref.ActionId!));
        return menu;
    }

    private ContextMenu BuildMissingMenu(PanelTileRef tref)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "从此组移除", () => RemoveFromGroup(tref));
        return menu;
    }

    private void ShowAddMenu(PanelGroupDto group, UIElement? target)
    {
        var menu = new ContextMenu();
        AddMenuItem(menu, "新建动作…", () => NewActionFor(group, null));
        AddMenuItem(menu, "新建组合动作…", () => NewActionFor(group, ActionType.composite));
        AddMenuItem(menu, "从动作池选择…", () => PickExistingFor(group));
        TrackInteract(menu);
        menu.PlacementTarget = target;
        menu.Placement = target != null ? PlacementMode.Bottom : PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private ContextMenu NewTrackedMenu()
    {
        var menu = new ContextMenu();
        TrackInteract(menu);
        return menu;
    }

    /// <summary>分组菜单（分组标题 / 左缘导航条）：新建动作、重命名、删除、去设置里管理。</summary>
    private ContextMenu BuildGroupMenu(PanelGroupDto group, bool global)
    {
        var menu = NewTrackedMenu();
        AddMenuItem(menu, "在此组新建动作…", () => NewActionFor(group, null));
        AddMenuItem(menu, "新建组合动作…", () => NewActionFor(group, ActionType.composite));
        AddMenuItem(menu, "从动作池选择…", () => PickExistingFor(group));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "重命名分组…", () => BeginRenameGroup(group, global));
        AddMenuItem(menu, "删除分组…", () => DeleteGroupFromPanel(group, global));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "在设置中管理…", () => OpenSceneSettings(group, global));
        return menu;
    }

    /// <summary>
    /// 面板空白处菜单（照 Quicker）：新建动作 / 新建分组 / 管理场景，外加钉住、设置、关闭。
    /// 光标在某个分组里时，新建动作直接落到那个分组。
    /// </summary>
    private void ShowZoneMenu(bool global, PanelGroupDto? group)
    {
        var menu = NewTrackedMenu();
        var target = group ?? ZoneGroups(global).FirstOrDefault();
        if (target != null)
        {
            AddMenuItem(menu, "在此组新建动作…", () => NewActionFor(target, null));
            AddMenuItem(menu, "新建组合动作…", () => NewActionFor(target, ActionType.composite));
            AddMenuItem(menu, "从动作池选择…", () => PickExistingFor(target));
            menu.Items.Add(new Separator());
        }

        if (group != null)
        {
            AddMenuItem(menu, "重命名分组…", () => BeginRenameGroup(group, global));
            AddMenuItem(menu, "删除分组…", () => DeleteGroupFromPanel(group, global));
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, "新建分组", () => AddGroupToZone(global));
        AddMenuItem(menu, "在设置中管理…", () => OpenSceneSettings(group, global));
        menu.Items.Add(new Separator());
        AddPanelCommands(menu);

        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    /// <summary>标题栏 / 左栏空白：只放面板级操作。</summary>
    private void ShowPanelMenu()
    {
        var menu = NewTrackedMenu();
        AddPanelCommands(menu);
        menu.Placement = PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void AddPanelCommands(ContextMenu menu)
    {
        var pin = new MenuItem
        {
            Header = "钉住面板",
            IsCheckable = true,
            IsChecked = _pinned,
            ToolTip = "钉住后点击外部不关闭",
        };
        pin.Click += (_, _) => Defer(() => SetPinned(!_pinned));
        menu.Items.Add(pin);
        AddMenuItem(menu, "面板设置…", () =>
        {
            Features.SuperPanel.Close();
            ConfigHelper.OpenPanelScene(PanelLibrary.ActiveGlobalScene()?.Id);
        });
        AddMenuItem(menu, "关闭面板", () => Features.SuperPanel.Close());
    }

    private ContextMenu BuildGlobalSceneMenu(PanelSceneDto scene)
    {
        var menu = NewTrackedMenu();
        AddMenuItem(menu, "编辑场景…", () =>
        {
            Features.SuperPanel.Close();
            ConfigHelper.OpenPanelScene(scene.Id);
        });
        AddMenuItem(menu, "新建全局场景", NewGlobalSceneFromPanel);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "删除场景…", () => DeleteSceneFromPanel(scene));
        return menu;
    }

    private ContextMenu BuildContextSceneMenu(PanelSceneDto? scene)
    {
        var menu = NewTrackedMenu();
        if (scene == null)
        {
            AddMenuItem(menu, "编辑通用分组…", () =>
            {
                Features.SuperPanel.Close();
                ConfigHelper.OpenPanelScene(PanelManageView.CommonKey);
            });
            AddMenuItem(menu, "新建上下文场景", NewContextSceneFromPanel);
            return menu;
        }

        AddMenuItem(menu, "编辑场景…", () =>
        {
            Features.SuperPanel.Close();
            ConfigHelper.OpenPanelScene(scene.Id);
        });
        AddMenuItem(menu, "新建上下文场景", NewContextSceneFromPanel);
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "改为显示通用", () =>
        {
            _contextSceneId = null;
            BuildContext();
            ContextScroll.ScrollToTop();
        });
        AddMenuItem(menu, "删除场景…", () => DeleteSceneFromPanel(scene));
        return menu;
    }

    private void NewGlobalSceneFromPanel()
    {
        var scene = PanelLibrary.NewScene("新场景");
        PanelLibrary.Current.Global.Scenes.Add(scene);
        PanelLibrary.Current.Global.ActiveSceneId = scene.Id;
        PanelLibrary.Save();
        RebuildAll();
    }

    private void NewContextSceneFromPanel()
    {
        var scene = _foreground != null
            ? PanelLibrary.CreateContextScene(_foreground.ExeName, _foreground.ExeName, _foreground.ExePath)
            : PanelLibrary.CreateContextScene("新程序", null, null);
        _contextSceneId = scene.Id;
        RebuildAll();
    }

    private void DeleteSceneFromPanel(PanelSceneDto scene)
    {
        bool children = PanelLibrary.ChildrenOf(scene.Id).Any();
        string extra = children ? "\n它的窗口子场景也会一并删除。" : "";
        if (!ConfirmDialog.Confirm(this, "删除场景",
                $"确定删除场景「{scene.Name}」吗？{extra}\n动作本体仍保留在动作池。",
                danger: true))
            return;

        PanelLibrary.DeleteScene(scene);
        if (_contextSceneId == scene.Id) _contextSceneId = null;
        RebuildAll();
    }

    private void DeleteGroupFromPanel(PanelGroupDto group, bool global)
    {
        var groups = ZoneGroups(global);
        if (groups.Count <= 1)
        {
            ToastService.Show("每个场景至少保留一个分组");
            return;
        }
        if (!ConfirmDialog.Confirm(this, "删除分组",
                $"确定删除分组「{group.Name}」吗？\n里面的动作只是从分组移除，不会删除动作本体。",
                danger: true))
            return;

        groups.Remove(group);
        PanelLibrary.Save();
        RebuildAll();
    }

    private static PanelSceneDto? SceneOfGroup(PanelGroupDto group) =>
        PanelLibrary.Current.Global.Scenes.Concat(PanelLibrary.Current.Context.Scenes)
            .FirstOrDefault(s => s.Groups.Contains(group));

    /// <summary>去设置里管理：优先定位到分组所属场景；通用 / 找不到场景时定位到「通用」。</summary>
    private void OpenSceneSettings(PanelGroupDto? group, bool global)
    {
        Features.SuperPanel.Close();
        var scene = group != null ? SceneOfGroup(group)
            : global ? PanelLibrary.ActiveGlobalScene() : GetContextScene();
        ConfigHelper.OpenPanelScene(scene?.Id ?? PanelManageView.CommonKey);
    }

    /// <summary>分组名原地改名：临时插一个输入框，Enter 保存、Esc 取消、失焦也保存。</summary>
    private void BeginRenameGroup(PanelGroupDto group, bool global)
    {
        var host = global ? GlobalGroups : ContextGroups;
        var block = host.Children.OfType<StackPanel>()
            .FirstOrDefault(p => ReferenceEquals(p.Tag, group));
        if (block == null) return;

        var title = block.Children.OfType<TextBlock>().FirstOrDefault();
        if (title != null) title.Visibility = Visibility.Collapsed;

        var box = new TextBox
        {
            Text = group.Name,
            MinHeight = 26,
            FontSize = (double)FindResource("FontSizeSecondary"),
            Margin = new Thickness(0, 2, 0, 2),
            Tag = "GroupRename",
        };
        block.Children.Insert(0, box);

        bool done = false;
        void Commit()
        {
            if (done) return;
            done = true;
            string v = box.Text.Trim();
            if (v.Length > 0 && v != group.Name)
            {
                group.Name = v;
                PanelLibrary.Save();
            }
            RebuildAll();
        }
        void Cancel()
        {
            if (done) return;
            done = true;
            RebuildAll();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancel(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => Commit();
        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
    }

    private void AddMenuItem(ContextMenu menu, string header, Action invoke)
    {
        var mi = new MenuItem { Header = header };
        // 菜单项 Click 内同步打开模态框会与菜单关闭冲突，推迟到下一 Dispatcher 周期
        mi.Click += (_, _) => Defer(invoke);
        menu.Items.Add(mi);
    }

    private void TrackInteract(ContextMenu menu)
    {
        menu.Opened += (_, _) => { _openMenu = menu; _interactCount++; };
        menu.Closed += (_, _) =>
        {
            if (_openMenu == menu) _openMenu = null;
            if (_interactCount > 0) _interactCount--;
        };
    }

    /// <summary>菜单在场时点击面板其它位置：只关菜单，磁贴上的这次点击吞掉。</summary>
    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        bool hadMenu = _openMenu != null;
        if (hadMenu)
        {
            var m = _openMenu!;
            _openMenu = null;
            if (m.IsOpen) m.IsOpen = false;
            if (IsOverTile(e.GetPosition(this))) e.Handled = true;
        }

        // 空白处按下：记住起点，移动超过 4px 才拖窗口（磁贴 / 按钮等交互元素不参与）
        if (!hadMenu && e.ChangedButton == MouseButton.Left && !IsInteractive(e.OriginalSource as DependencyObject))
        {
            _blankDragStart = e.GetPosition(this);
            _blankDragArmed = true;
        }

        // 照 Quicker：中键点空白处关闭面板
        if (!hadMenu && e.ChangedButton == MouseButton.Middle && !IsInteractive(e.OriginalSource as DependencyObject))
        {
            Features.SuperPanel.Close();
            e.Handled = true;
        }
    }

    /// <summary>交互控件（按钮 / 输入框 / 滚动条 / 分割条）上的拖动归控件自己。</summary>
    private static bool IsInteractive(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase or TextBoxBase or ScrollBar or GridSplitter or Thumb
                or ComboBox or Slider or MenuItem)
                return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void Defer(Action action) => Dispatcher.InvokeAsync(action);

    // ============ 磁贴 CRUD（全部先关面板再开对话框） ============

    private void NewActionFor(PanelGroupDto group, ActionType? presetType)
    {
        Features.SuperPanel.Close();
        PanelActionOps.AddNew(null, group, presetType);
    }

    private void PickExistingFor(PanelGroupDto group)
    {
        Features.SuperPanel.Close();
        PanelActionOps.AddExisting(null, group);
    }

    private void EditAction(PanelTileRef tref)
    {
        if (tref.ActionId == null) return;
        Features.SuperPanel.Close();
        if (PanelActionOps.Edit(null, tref.ActionId)) RebuildAll();
    }

    private void DuplicateAction(PanelTileRef tref)
    {
        if (tref.ActionId == null) return;
        var copy = ConfigStore.DuplicateAction(tref.ActionId);
        if (copy == null) return;
        ConfigStore.Save();
        int insert = Math.Min(tref.Index + 1, tref.Group.Actions.Count);
        tref.Group.Actions.Insert(insert, copy.Id);
        PanelLibrary.Save();
        RebuildAll();
    }

    private void RemoveFromGroup(PanelTileRef tref)
    {
        PanelActionOps.RemoveFromGroup(tref.Group, tref.Index);
        RebuildAll();
    }

    private void DeleteAction(string id)
    {
        Features.SuperPanel.Close();
        PanelActionOps.Delete(null, id);
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

    // ============ 窗口事件 ============

    private void NavActions_Click(object sender, RoutedEventArgs e) => FocusSearch();

    private void NavStash_Click(object sender, RoutedEventArgs e)
        => ToastService.Show("暂存区还在做，先把常用内容放成动作磁贴吧。", "暂存区（即将推出）");

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // 搜索框 / 按钮自行处理，不拖窗口
        if (e.OriginalSource is DependencyObject d
            && (FindAncestor<Button>(d) != null || FindAncestor<TextBox>(d) != null)) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* 极快点击：按下时鼠标已抬起 */ }
        e.Handled = true;
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => SetPinned(!_pinned);

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        PinIcon.Text = pinned ? "\uE842" : "\uE840";
        PinBtn.Foreground = Res(pinned ? "PrimaryBrush" : "SecondaryTextBrush");
        PinBtn.ToolTip = pinned ? "取消钉住（当前：点击外部不关闭）" : "钉住：点击外部不关闭";
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Features.SuperPanel.Close();
        ConfigHelper.OpenPanelScene(PanelLibrary.ActiveGlobalScene()?.Id);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Features.SuperPanel.Close();

    protected override void OnClosed(EventArgs e)
    {
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

