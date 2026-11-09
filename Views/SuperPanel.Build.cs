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
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 面板渲染：场景标签、上下两区、分组导航、磁贴与搜索过滤。
/// </summary>
public partial class SuperPanelWindow
{
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
    /// 搜索只匹配名称（动作名 / 内置功能名，忽略大小写）。**不匹配内部 Id**——否则搜 "1" 会命中 a1 之类的编号；
    /// 缺失引用没有名称，只在未搜索时显示。
    /// </summary>
    private bool MatchesQuery(string id, string query)
    {
        if (query.Length == 0) return true;
        return BindingRef.Exists(id)
               && BindingRef.Name(id).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 磁贴内容（图标 chip + 标题）。磁贴与拖动幽灵共用一份，保证幽灵和磁贴长得一模一样。
    /// </summary>
    private static StackPanel TileContent(string? reference, bool compact)
    {
        bool exists = BindingRef.Exists(reference);
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
                Text = exists ? IconCatalog.GetGlyph(BindingRef.Icon(reference)) : "\uE783",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = compact ? 15 : 19,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Res(exists ? "TextBrush" : "DangerBrush"),
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = exists ? BindingRef.Name(reference) : "缺失",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = compact ? 12 : 11,
            MaxWidth = 100,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Res(exists ? "SecondaryTextBrush" : "DangerBrush"),
        });
        return content;
    }

    /// <summary>拖动幽灵的内容：按拖动中的磁贴临时造一份同样的内容（不依赖截图快照）。</summary>
    private static FrameworkElement? TileGhostBody(PanelTileRef tref)
    {
        if (tref.ActionId == null) return null;
        return TileContent(tref.ActionId, PanelLibrary.Current.Preferences.CompactTiles);
    }

    private Button BuildTile(PanelGroupDto group, int index, string id, TileDragSort drag)
    {
        bool exists = BindingRef.Exists(id);
        bool compact = PanelLibrary.Current.Preferences.CompactTiles;

        var btn = new Button
        {
            Style = (Style)FindResource("TileBtn"),
            Height = compact ? 56 : 72,
            Margin = new Thickness(0),
            Tag = new PanelTileRef { Group = group, Index = index, ActionId = id },
        };

        // 照 Quicker：36px 图标 chip（淡底 + 1px 内描边）+ 11px 次要色标题，垂直居中
        var content = TileContent(id, compact);
        btn.Content = content;
        btn.ToolTip = BindingRef.Describe(id);

        var tref = (PanelTileRef)btn.Tag;
        drag.Attach(btn);
        btn.Click += (_, _) =>
        {
            if (tref.ActionId != null) ExecuteAction(tref.ActionId);
        };

        var menu = exists ? BuildFilledMenu(tref) : BuildMissingMenu(tref);
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
}
