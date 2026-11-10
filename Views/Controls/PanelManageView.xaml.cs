using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>
/// 设置「面板管理」页。左侧主导航把这里拆成三个二级页（常规 / 场景 / 禁止弹出）：
/// <list type="bullet">
///   <item>常规：开关、长按阈值、视图选项；</item>
///   <item>场景：左边场景列表，右边详情——匹配条件**默认折叠成一行**，动作管理（分组 + 磁贴）常驻；</item>
///   <item>禁止弹出：禁止列表。</item>
/// </list>
/// 设计见 <c>docs/PANEL_REDESIGN.md</c> §5.1。
/// </summary>
public partial class PanelManageView : UserControl
{
    /// <summary>上下文「通用」区的键（面板跳转设置时也用它定位）。</summary>
    public const string CommonKey = "common";

    private readonly DispatcherTimer _thresholdTimer;
    private string _selectedKey = CommonKey;
    private bool _loading = true;      // 构造期间 XAML 设 Minimum/Maximum 也会触发 ValueChanged
    private bool _rulesExpanded;       // 匹配条件默认折叠（方案 3：规则折叠、动作管理常驻）
    private TileDragSort? _tileDrag;
    private TextBox? _sceneNameBox;    // 右侧场景名输入框（右键「重命名场景」用）
    private readonly Dictionary<PanelGroupDto, TextBox> _groupNameBoxes = new();

    public PanelManageView()
    {
        InitializeComponent();

        _thresholdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _thresholdTimer.Tick += (_, _) =>
        {
            _thresholdTimer.Stop();
            SaveThreshold();
        };

        // 构造时可能还没挂到窗口上（Window.GetWindow 为 null），那时拖动挂不上。
        // 首次 Loaded 再补一次：只重建详情区，空转说明已经挂好了。
        Loaded += (_, _) =>
        {
            if (_tileDrag == null) RenderDetail();
        };

        // 空白处右键（照 Quicker）：列表空白新建场景，详情空白新建分组 / 添加动作
        SceneList.ContextMenu = BuildSceneListBlankMenu();
        DetailHost.ContextMenu = BuildDetailBlankMenu();
    }

    // ============ 右键菜单 ============

    private static ContextMenu Menu(params (string Header, Action? Invoke)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (header, invoke) in items)
        {
            if (header == "-") { menu.Items.Add(new Separator()); continue; }
            var mi = new MenuItem { Header = header };
            if (invoke != null)
                mi.Click += (_, _) => Application.Current.Dispatcher.InvokeAsync(invoke);
            else
                mi.IsEnabled = false;
            menu.Items.Add(mi);
        }
        return menu;
    }

    private ContextMenu BuildSceneListBlankMenu() => Menu(
        ("新建全局场景", () => CreateScene(global: true)),
        ("新建上下文场景", () => CreateScene(global: false)),
        ("新建窗口子场景", NewChildSceneForSelection));

    private ContextMenu BuildDetailBlankMenu() => Menu(
        ("在此场景新建动作…", () => NewActionForSelectedScene()),
        ("新建分组", AddGroupToSelectedScene));

    private ContextMenu BuildSceneRowMenu(PanelSceneDto scene, bool isGlobal)
    {
        var items = new List<(string, Action?)>
        {
            ("重命名场景", () => RenameScene(scene)),
            ("新建动作…", () => NewActionForScene(scene)),
            ("新建分组", () => AddGroupToScene(scene)),
            ("-", null),
        };
        if (!isGlobal) items.Add(("新建窗口子场景", () => CreateChildScene(scene)));
        items.Add(("-", null));
        items.Add(("删除场景…", () => DeleteScene(scene)));
        return Menu(items.ToArray());
    }

    private ContextMenu BuildGroupMenu(PanelGroupDto group, List<PanelGroupDto> groups) => Menu(
        ("重命名分组", () => RenameGroup(group)),
        ("新建动作…", () => AddActionToGroup(group)),
        ("从动作池选择…", () => AddExistingToGroup(group)),
        ("-", null),
        ("删除分组…", () => DeleteGroup(groups, group)));

    private void RenameScene(PanelSceneDto scene)
    {
        _selectedKey = scene.Id;
        _rulesExpanded = false;
        BuildSceneList();
        RenderDetail();
        Dispatcher.InvokeAsync(() =>
        {
            _sceneNameBox?.Focus();
            _sceneNameBox?.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void RenameGroup(PanelGroupDto group)
    {
        if (!_groupNameBoxes.TryGetValue(group, out var box)) return;
        box.Focus();
        box.SelectAll();
    }

    private void NewChildSceneForSelection()
    {
        var selected = PanelLibrary.FindScene(_selectedKey);
        if (selected != null && !PanelLibrary.Current.Global.Scenes.Contains(selected))
            CreateChildScene(selected);
        else
            SetStatus("请先在左侧选中一个上下文场景", ok: false);
    }

    private void CreateScene(bool global)
    {
        if (global)
        {
            var scene = PanelLibrary.NewScene("新场景");
            PanelLibrary.Current.Global.Scenes.Add(scene);
            PanelLibrary.Current.Global.ActiveSceneId = scene.Id;
            PanelLibrary.Save();
            _selectedKey = scene.Id;
            _rulesExpanded = false;
            SetStatus("已新建全局场景，可在右侧改名", ok: true);
        }
        else
        {
            var scene = PanelLibrary.NewScene("新程序");
            PanelLibrary.Current.Context.Scenes.Add(scene);
            PanelLibrary.Save();
            _selectedKey = scene.Id;
            _rulesExpanded = true;
            SetStatus("已新建上下文场景，请在右侧填写匹配条件", ok: true);
        }
        BuildSceneList();
        RenderDetail();
    }

    private void CreateChildScene(PanelSceneDto parent)
    {
        if (!string.IsNullOrEmpty(parent.ParentId))
            parent = PanelLibrary.FindScene(parent.ParentId!) ?? parent;

        var child = PanelLibrary.NewScene("新窗口");
        child.ParentId = parent.Id;
        child.Groups[0].Name = "常用";
        PanelLibrary.Current.Context.Scenes.Add(child);
        PanelLibrary.Save();
        _selectedKey = child.Id;
        _rulesExpanded = true;
        BuildSceneList();
        RenderDetail();
        SetStatus($"已在「{parent.Name}」下新建窗口子场景", ok: true);
    }

    private void NewActionForSelectedScene()
    {
        var groups = GroupsOfSelectedScene();
        if (groups.Count == 0) return;
        AddActionToGroup(groups[0]);
    }

    private void NewActionForScene(PanelSceneDto scene)
    {
        if (scene.Groups.Count == 0) return;
        AddActionToGroup(scene.Groups[0]);
    }

    private void AddGroupToSelectedScene()
    {
        var groups = GroupsOfSelectedScene();
        if (groups.Count == 0) return;
        AddGroupToScene(groups);
    }

    private void AddGroupToScene(PanelSceneDto scene) => AddGroupToScene(scene.Groups);

    private void AddGroupToScene(List<PanelGroupDto> groups)
    {
        groups.Add(PanelLibrary.NewGroup("新分组"));
        PanelLibrary.Save();
        RenderDetail();
    }

    private List<PanelGroupDto> GroupsOfSelectedScene()
    {
        if (_selectedKey == CommonKey) return PanelLibrary.Current.Context.Common;
        return PanelLibrary.FindScene(_selectedKey)?.Groups ?? PanelLibrary.Current.Context.Common;
    }

    private void AddActionToGroup(PanelGroupDto group)
    {
        if (PanelActionOps.AddNew(Window.GetWindow(this), group) != null) RefreshAll();
    }

    private void AddExistingToGroup(PanelGroupDto group)
    {
        if (PanelActionOps.AddExisting(Window.GetWindow(this), group)) RefreshAll();
    }

    // ============ 对外入口 ============

    /// <summary>重新从 <see cref="PanelLibrary.Current"/> 回填整页。</summary>
    public void Reload()
    {
        _loading = true;
        var prefs = PanelLibrary.Current.Preferences;
        EnabledBox.IsChecked = AppState.IsSuperPanelEnabled;
        ThresholdSlider.Value = Math.Clamp(AppState.SuperPanelThresholdMs, 100, 800);
        ThresholdText.Text = $"{(int)ThresholdSlider.Value} ms";
        ApplyEnabledState();
        ShowGroupTitlesBox.IsChecked = prefs.ShowGroupTitles;
        CompactTilesBox.IsChecked = prefs.CompactTiles;
        var fp = FloatingLibrary.Prefs;
        FloatMiddleCloseBox.IsChecked = fp.MiddleClickClose;
        FloatTooltipBox.IsChecked = fp.ShowTooltip;
        FloatSnapBox.IsChecked = fp.SnapToEdges;
        _loading = false;

        if (_selectedKey != CommonKey && PanelLibrary.FindScene(_selectedKey) == null)
            _selectedKey = CommonKey;

        BuildBlocked();
        BuildSceneList();
        RenderDetail();
    }

    /// <summary>选中指定场景（面板「为此程序创建场景」跳转用）。</summary>
    public void SelectScene(string? sceneId)
    {
        if (!string.IsNullOrEmpty(sceneId))
        {
            _selectedKey = sceneId!;
            _rulesExpanded = false;   // 新选中的场景先看规则摘要
        }
        ShowPage("scenes");
        Reload();
    }

    /// <summary>切换二级页：general / scenes / blocked（主导航「面板管理」下拉调用）。</summary>
    public void ShowPage(string page)
    {
        bool general = page == "general";
        bool blocked = page == "blocked";
        PageGeneral.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        PageBlocked.Visibility = blocked ? Visibility.Visible : Visibility.Collapsed;
        PageScenes.Visibility = general || blocked ? Visibility.Collapsed : Visibility.Visible;
        if (PageScenes.Visibility == Visibility.Visible)
        {
            BuildSceneList();
            RenderDetail();
        }
    }

    // ============ 常规 ============

    private void EnabledBox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyEnabledState();
        if (_loading) return;
        Settings.SetSuperPanelEnabled(EnabledBox.IsChecked == true);
    }

    /// <summary>面板关掉时长按阈值没有意义，整行置灰（点不动，但仍能看到当前值）。</summary>
    private void ApplyEnabledState()
        => ThresholdRow.IsEnabled = EnabledBox.IsChecked == true;

    private void ThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ThresholdText == null) return;   // XAML 设置 Minimum/Maximum 时控件还没建好
        ThresholdText.Text = $"{(int)ThresholdSlider.Value} ms";
        if (_loading) return;
        AppState.SuperPanelThresholdMs = (int)ThresholdSlider.Value;
        _thresholdTimer.Stop();
        _thresholdTimer.Start();
    }

    private void SaveThreshold()
    {
        try
        {
            int v = (int)ThresholdSlider.Value;
            PanelStore.Modify(ConfigStore.ConfigPath, p => p.SuperPanel.LongPressThresholdMs = v);
            SetStatus($"长按阈值已保存：{v} ms", ok: true);
        }
        catch (Exception ex)
        {
            SetStatus("保存长按阈值失败：" + ex.Message, ok: false);
        }
    }

    private void Prefs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var prefs = PanelLibrary.Current.Preferences;
        prefs.ShowGroupTitles = ShowGroupTitlesBox.IsChecked == true;
        prefs.CompactTiles = CompactTilesBox.IsChecked == true;
        PanelLibrary.Save();
    }

    /// <summary>悬浮偏好：立即写盘，并重建现有悬浮窗口（悬停说明 / 吸附等要马上生效）。</summary>
    private void FloatPrefs_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var prefs = FloatingLibrary.Prefs;
        prefs.MiddleClickClose = FloatMiddleCloseBox.IsChecked == true;
        prefs.ShowTooltip = FloatTooltipBox.IsChecked == true;
        prefs.SnapToEdges = FloatSnapBox.IsChecked == true;
        FloatingLibrary.SavePreferences();
        FloatingManager.RefreshAll();
    }

    private void FloatCloseAll_Click(object sender, RoutedEventArgs e) => FloatingManager.CloseAll();

    private void ResetHint_Click(object sender, RoutedEventArgs e)
    {
        PanelLibrary.Current.HintDismissed = false;
        PanelLibrary.Save();
        SetStatus("已恢复「尚未添加专用设置」提示", ok: true);
    }

    // ============ 禁止弹出 ============

    private void AddBlocked_Click(object sender, RoutedEventArgs e) => AddBlocked();

    private void BlockedInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        AddBlocked();
        e.Handled = true;
    }

    private void AddBlocked()
    {
        string v = BlockedInput.Text.Trim();
        if (v.Length == 0) return;
        var blocked = PanelLibrary.Current.Blocked;
        if (!blocked.Any(x => x.Equals(v, StringComparison.OrdinalIgnoreCase)))
        {
            blocked.Add(v);
            PanelLibrary.Save();
        }
        BlockedInput.Text = "";
        BuildBlocked();
        SetStatus($"已禁止在 {v} 上弹出面板", ok: true);
    }

    private void BuildBlocked()
    {
        BlockedHost.Children.Clear();
        foreach (string raw in PanelLibrary.Current.Blocked.ToList())
        {
            string name = raw;
            var row = new Grid { Margin = new Thickness(0, 2, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = name,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = (double)FindResource("FontSizeHint"),
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
            };
            Grid.SetColumn(text, 0);
            row.Children.Add(text);

            var del = new Button
            {
                Style = (Style)FindResource("MiniBtn"),
                Content = "✕",
                Width = 24,
                Padding = new Thickness(0),
                ToolTip = "从禁止列表移除",
            };
            del.Click += (_, _) =>
            {
                PanelLibrary.Current.Blocked.RemoveAll(x => x.Equals(name, StringComparison.OrdinalIgnoreCase));
                PanelLibrary.Save();
                BuildBlocked();
            };
            Grid.SetColumn(del, 1);
            row.Children.Add(del);
            BlockedHost.Children.Add(row);
        }

        if (BlockedHost.Children.Count == 0)
            BlockedHost.Children.Add(new TextBlock
            {
                Text = "（没有禁止的程序）",
                FontSize = (double)FindResource("FontSizeHint"),
                Foreground = (Brush)FindResource("HintTextBrush"),
            });
    }

    // ============ 场景列表 ============

    private void BuildSceneList()
    {
        SceneList.Children.Clear();

        AddSceneGroupLabel("全局场景");
        foreach (var scene in PanelLibrary.Current.Global.Scenes)
            SceneList.Children.Add(SceneRow(scene, isGlobal: true, indent: 0));

        AddSceneGroupLabel("上下文场景");
        SceneList.Children.Add(CommonRow());
        foreach (var scene in PanelLibrary.Current.Context.Scenes.Where(s => string.IsNullOrEmpty(s.ParentId)))
        {
            SceneList.Children.Add(SceneRow(scene, isGlobal: false, indent: 0));
            foreach (var child in PanelLibrary.ChildrenOf(scene.Id))
                SceneList.Children.Add(SceneRow(child, isGlobal: false, indent: 1));
        }
    }

    private void AddSceneGroupLabel(string text) =>
        SceneList.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)FindResource("HintTextBrush"),
            Margin = new Thickness(8, 8, 0, 4),
        });

    private Button SceneRow(PanelSceneDto scene, bool isGlobal, int indent)
    {
        bool selected = _selectedKey == scene.Id;
        var btn = new Button
        {
            Style = (Style)FindResource("SceneRowBtn"),
            // 选中实底；悬停时保持实底，未选中用浅灰
            Tag = FindResource(selected ? "BorderBrush" : "HoverBgBrush"),
            Background = selected ? (Brush)FindResource("BorderBrush") : Brushes.Transparent,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(indent * 14, 1, 0, 1),
            ToolTip = (isGlobal ? "全局场景" : "上下文场景") + "\n" + PanelSceneResolver.DescribeMatch(scene),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = IconCatalog.GetGlyph(scene.Icon),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextBrush"),
            },
        };
        Grid.SetColumn(icon, 0);
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = scene.Name,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        if (!scene.Enabled)
        {
            var off = new TextBlock
            {
                Text = "已停用",
                FontSize = (double)FindResource("FontSizeHint"),
                Foreground = (Brush)FindResource("HintTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            Grid.SetColumn(off, 2);
            row.Children.Add(off);
        }

        btn.Content = row;
        btn.ContextMenu = BuildSceneRowMenu(scene, isGlobal);
        btn.Click += (_, _) =>
        {
            _selectedKey = scene.Id;
            _rulesExpanded = false;
            BuildSceneList();
            RenderDetail();
        };
        return btn;
    }

    private Button CommonRow()
    {
        bool selected = _selectedKey == CommonKey;
        var btn = new Button
        {
            Style = (Style)FindResource("SceneRowBtn"),
            Tag = FindResource(selected ? "BorderBrush" : "HoverBgBrush"),
            Background = selected ? (Brush)FindResource("BorderBrush") : Brushes.Transparent,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            ToolTip = "没有匹配到任何上下文场景时显示这里的动作",
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "\uE946",   // Info
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
            },
        });
        row.Children.Add(new TextBlock
        {
            Text = "通用（未匹配时显示）",
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        });

        btn.Content = row;
        btn.ContextMenu = Menu(
            ("新建动作…", () =>
            {
                var groups = PanelLibrary.Current.Context.Common;
                if (groups.Count > 0) AddActionToGroup(groups[0]);
            }),
            ("新建分组", () => AddGroupToScene(PanelLibrary.Current.Context.Common)));
        btn.Click += (_, _) =>
        {
            _selectedKey = CommonKey;
            _rulesExpanded = false;
            BuildSceneList();
            RenderDetail();
        };
        return btn;
    }

    private void NewSceneBtn_Click(object sender, RoutedEventArgs e)
    {
        if (NewSceneBtn.ContextMenu is not ContextMenu menu) return;
        menu.PlacementTarget = NewSceneBtn;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void NewGlobalScene_Click(object sender, RoutedEventArgs e) => CreateScene(global: true);

    private void NewContextScene_Click(object sender, RoutedEventArgs e) => CreateScene(global: false);

    private void NewChildScene_Click(object sender, RoutedEventArgs e) => NewChildSceneForSelection();

    private void DeleteScene_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedKey == CommonKey)
        {
            SetStatus("通用区不能删除；可以清空里面的动作", ok: false);
            return;
        }
        var scene = PanelLibrary.FindScene(_selectedKey);
        if (scene != null) DeleteScene(scene);
    }

    private void DeleteScene(PanelSceneDto scene)
    {
        string extra = PanelLibrary.ChildrenOf(scene.Id).Any() ? "\n它的窗口子场景也会一并删除。" : "";
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除场景",
                $"确定删除场景「{scene.Name}」吗？{extra}\n场景里的动作入口会被移除，动作本体仍保留在动作池。",
                danger: true))
            return;

        PanelLibrary.DeleteScene(scene);
        _selectedKey = CommonKey;
        BuildSceneList();
        RenderDetail();
        SetStatus("场景已删除", ok: true);
    }

    // ============ 详情 ============

    private void RenderDetail()
    {
        DetailHost.Children.Clear();
        _groupNameBoxes.Clear();
        _sceneNameBox = null;
        if (_selectedKey == CommonKey)
        {
            RenderCommon();
            return;
        }

        var scene = PanelLibrary.FindScene(_selectedKey);
        if (scene == null)
        {
            _selectedKey = CommonKey;
            RenderCommon();
            return;
        }

        bool isGlobal = PanelLibrary.Current.Global.Scenes.Contains(scene);
        RenderSceneHeader(scene, isGlobal);
        if (!isGlobal) RenderRuleSection(scene);
        RenderGroups(scene.Groups);
    }

    private void RenderCommon()
    {
        DetailHost.Children.Add(new TextBlock
        {
            Text = "通用区",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 2),
        });
        DetailHost.Children.Add(Hint("没有匹配到任何上下文场景时显示这里的动作。"));
        RenderGroups(PanelLibrary.Current.Context.Common);
    }

    private void RenderSceneHeader(PanelSceneDto scene, bool isGlobal)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBox
        {
            Text = scene.Name,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            MinWidth = 120,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,   // 悬停 / 聚焦时由样式显出边
            Background = Brushes.Transparent,
            Padding = new Thickness(2, 1, 2, 1),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "点击可直接重命名（Enter 保存，Esc 取消）",
        };
        _sceneNameBox = name;
        name.LostFocus += (_, _) =>
        {
            string v = name.Text.Trim();
            if (v.Length == 0) { name.Text = scene.Name; return; }
            if (v == scene.Name) return;
            scene.Name = v;
            PanelLibrary.Save();
            BuildSceneList();
            SetStatus("场景名已保存", ok: true);
        };
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                name.Text = scene.Name;
                name.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            name.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        };
        Grid.SetColumn(name, 0);
        row.Children.Add(name);

        var badge = new Border
        {
            Background = (Brush)FindResource("SurfaceAltBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = isGlobal ? "全局场景" : string.IsNullOrEmpty(scene.ParentId) ? "上下文场景" : "窗口子场景",
                FontSize = (double)FindResource("FontSizeHint"),
                Foreground = (Brush)FindResource("SecondaryTextBrush"),
            },
        };
        Grid.SetColumn(badge, 1);
        row.Children.Add(badge);

        var enabled = new CheckBox
        {
            Content = "启用",
            IsChecked = scene.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        enabled.Checked += (_, _) => SetSceneEnabled(scene, true);
        enabled.Unchecked += (_, _) => SetSceneEnabled(scene, false);
        Grid.SetColumn(enabled, 2);
        row.Children.Add(enabled);

        DetailHost.Children.Add(row);

        if (isGlobal)
            DetailHost.Children.Add(Hint("全局区：始终显示，不参与前台窗口匹配。可用面板上方的标签切换场景。"));
        else
        {
            var parent = string.IsNullOrEmpty(scene.ParentId) ? null : PanelLibrary.FindScene(scene.ParentId!);
            DetailHost.Children.Add(Hint(parent != null
                ? $"上下文区：父场景「{parent.Name}」命中后，再按窗口条件挑选本场景。"
                : "上下文区：前台程序命中匹配条件时显示。完整路径优先于进程名。"));
        }
    }

    private void SetSceneEnabled(PanelSceneDto scene, bool enabled)
    {
        if (scene.Enabled == enabled) return;
        scene.Enabled = enabled;
        PanelLibrary.Save();
        BuildSceneList();
        SetStatus(enabled ? "场景已启用" : "场景已停用", ok: true);
    }

    // ---- 匹配条件：默认折叠成一行，点开才出现表单 ----

    private void RenderRuleSection(PanelSceneDto scene)
    {
        if (_rulesExpanded) RenderMatchCard(scene);
        else DetailHost.Children.Add(BuildRuleSummary(scene));
    }

    private FrameworkElement BuildRuleSummary(PanelSceneDto scene)
    {
        var m = scene.Match;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(m.Exe)) parts.Add("进程 " + m.Exe!.Trim());
        if (!string.IsNullOrWhiteSpace(m.Path)) parts.Add("路径 " + m.Path!.Trim());
        if (!string.IsNullOrWhiteSpace(m.TitleContains)) parts.Add("标题含 “" + m.TitleContains!.Trim() + "”");
        if (!string.IsNullOrWhiteSpace(m.ClassName)) parts.Add("类名 " + m.ClassName!.Trim());
        bool any = parts.Count > 0;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource(any ? "SuccessBrush" : "HintTextBrush"),
        };
        Grid.SetColumn(dot, 0);
        grid.Children.Add(dot);

        var label = new TextBlock
        {
            Text = "匹配条件",
            FontSize = (double)FindResource("FontSizeSecondary"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var summary = new TextBlock
        {
            Text = any ? string.Join(" · ", parts) : "未设置（不会被自动选中）",
            FontSize = (double)FindResource("FontSizeHint"),
            Foreground = (Brush)FindResource(any ? "SecondaryTextBrush" : "DangerBrush"),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(summary, 2);
        grid.Children.Add(summary);

        var expand = new TextBlock
        {
            Text = "展开",
            FontSize = (double)FindResource("FontSizeHint"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(expand, 3);
        grid.Children.Add(expand);

        var bar = new Border
        {
            Background = (Brush)FindResource("SurfaceAltBrush"),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 8, 0, 10),
            Cursor = Cursors.Hand,
            ToolTip = "点击展开匹配条件",
            Child = grid,
        };
        bar.MouseLeftButtonUp += (_, _) =>
        {
            _rulesExpanded = true;
            RenderDetail();
        };
        return bar;
    }

    private void RenderMatchCard(PanelSceneDto scene)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("SurfaceAltBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 10),
            Margin = new Thickness(0, 8, 0, 10),
        };
        var stack = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = "匹配条件",
            FontSize = (double)FindResource("FontSizeSecondary"),
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var collapse = new Button { Style = (Style)FindResource("MiniBtn"), Content = "收起" };
        collapse.Click += (_, _) =>
        {
            _rulesExpanded = false;
            RenderDetail();
        };
        Grid.SetColumn(collapse, 1);
        head.Children.Add(collapse);
        stack.Children.Add(head);

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < 2; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var exe = AddMatchField(grid, 0, 0, "进程名", scene.Match.Exe, "如 WeChat.exe");
        var path = AddMatchField(grid, 0, 2, "完整路径", scene.Match.Path, "优先于进程名；可留空");
        var title = AddMatchField(grid, 1, 0, "标题包含", scene.Match.TitleContains, "窗口标题包含的文字");
        var cls = AddMatchField(grid, 1, 2, "窗口类名", scene.Match.ClassName, "完全相等，如 Notepad");
        stack.Children.Add(grid);

        var preview = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = (double)FindResource("FontSizeHint"),
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(preview);

        void UpdatePreview()
        {
            bool isChild = !string.IsNullOrEmpty(scene.ParentId);
            var (text, warn) = PreviewMatch(
                NullIfEmpty(exe.Text), NullIfEmpty(path.Text),
                NullIfEmpty(title.Text), NullIfEmpty(cls.Text), isChild);
            preview.Text = text;
            preview.Foreground = (Brush)FindResource(warn ? "DangerBrush" : "SecondaryTextBrush");
        }
        exe.TextChanged += (_, _) => UpdatePreview();
        path.TextChanged += (_, _) => UpdatePreview();
        title.TextChanged += (_, _) => UpdatePreview();
        cls.TextChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        var foot = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
        };
        var reset = new Button
        {
            Style = (Style)FindResource("Btn"),
            Content = "重置",
            MinWidth = 64,
        };
        reset.Click += (_, _) => RenderDetail();
        var apply = new Button
        {
            Style = (Style)FindResource("BtnPrimary"),
            Content = "应用匹配条件",
            MinWidth = 104,
            Margin = new Thickness(8, 0, 0, 0),
        };
        apply.Click += (_, _) => ApplyMatch(scene, exe.Text, path.Text, title.Text, cls.Text);
        foot.Children.Add(reset);
        foot.Children.Add(apply);
        stack.Children.Add(foot);

        card.Child = stack;
        DetailHost.Children.Add(card);
    }

    private TextBox AddMatchField(Grid grid, int row, int col, string label, string? value, string tooltip)
    {
        var l = new TextBlock
        {
            Text = label,
            FontSize = (double)FindResource("FontSizeHint"),
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(col == 0 ? 0 : 14, 4, 8, 0),
        };
        Grid.SetRow(l, row);
        Grid.SetColumn(l, col);
        grid.Children.Add(l);

        var box = new TextBox
        {
            Text = value ?? "",
            MinHeight = 26,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = tooltip,
            Margin = new Thickness(0, 4, 0, 0),
        };
        Grid.SetRow(box, row);
        Grid.SetColumn(box, col + 1);
        grid.Children.Add(box);
        return box;
    }

    private void ApplyMatch(PanelSceneDto scene, string exe, string path, string title, string cls)
    {
        scene.Match.Exe = NullIfEmpty(exe);
        scene.Match.Path = NullIfEmpty(path);
        scene.Match.TitleContains = NullIfEmpty(title);
        scene.Match.ClassName = NullIfEmpty(cls);
        PanelLibrary.Save();
        BuildSceneList();
        RenderDetail();

        string summary = PanelSceneResolver.DescribeMatch(scene);
        bool ok = summary != "未设置匹配条件";
        SetStatus(ok ? "匹配条件已保存：" + summary : "已保存：没有条件，这个场景不会被自动选中", ok);
    }

    /// <summary>
    /// 实时匹配预览：说明按哪个条件命中、窗口条件对根场景 / 窗口子场景的不同作用。
    /// </summary>
    private static (string Text, bool Warn) PreviewMatch(
        string? exe, string? path, string? title, string? cls, bool isChild)
    {
        bool hasWin = title != null || cls != null;
        bool hasApp = exe != null || path != null;
        if (!hasApp && !hasWin)
            return ("还没有可用的条件：这个场景不会被自动选中。", true);

        string basis = path != null ? "完整路径" : exe != null ? "进程名" : "窗口条件";
        string text = $"匹配预览：以「{basis}」为准";
        if (hasWin)
            text += isChild
                ? "，并同时满足标题 / 类名（父场景命中后挑选本场景）"
                : "（根场景的标题 / 类名不参与匹配，只在窗口子场景里生效）";
        return (text + "。", false);
    }

    // ---- 动作管理 ----

    private void RenderGroups(List<PanelGroupDto> groups)
    {
        int actionCount = groups.Sum(g => g.Actions.Count);
        var head = new Grid { Margin = new Thickness(0, 6, 0, 8) };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "动作管理", FontSize = 14, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = $"{groups.Count} 个分组 · {actionCount} 个动作",
            FontSize = (double)FindResource("FontSizeHint"),
            Foreground = (Brush)FindResource("HintTextBrush"),
            Margin = new Thickness(10, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(title, 0);
        head.Children.Add(title);

        var add = new Button
        {
            Style = (Style)FindResource("MiniBtn"),
            Content = "＋ 添加分组",
            ToolTip = "在面板里新建一个分组（动作按分组显示）",
        };
        add.Click += (_, _) =>
        {
            groups.Add(PanelLibrary.NewGroup("新分组"));
            PanelLibrary.Save();
            RenderDetail();
        };
        Grid.SetColumn(add, 1);
        head.Children.Add(add);
        DetailHost.Children.Add(head);

        foreach (var group in groups.ToList())
            DetailHost.Children.Add(BuildGroupCard(groups, group));
    }

    private Border BuildGroupCard(List<PanelGroupDto> groups, PanelGroupDto group)
    {
        var card = new Border
        {
            Background = (Brush)FindResource("SurfaceBrush"),
            BorderBrush = (Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
        };

        var stack = new StackPanel();

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new TextBox
        {
            Text = group.Name,
            MinWidth = 60,
            MaxWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Left,
            BorderThickness = new Thickness(1),
            BorderBrush = Brushes.Transparent,   // 悬停 / 聚焦时由样式显出边
            Background = Brushes.Transparent,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(2, 1, 2, 1),
            ToolTip = "点击可直接重命名（Enter 保存，Esc 取消）",
        };
        _groupNameBoxes[group] = name;
        void CommitName()
        {
            string v = name.Text.Trim();
            if (v.Length == 0) { name.Text = group.Name; return; }
            if (v == group.Name) return;
            group.Name = v;
            PanelLibrary.Save();
            SetStatus("分组名已保存", ok: true);
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                name.Text = group.Name;
                name.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter) return;
            CommitName();
            name.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            e.Handled = true;
        };
        // 显式的「重命名」入口：borderless 输入框太像纯文本，给个铅笔
        var rename = new Button
        {
            Style = (Style)FindResource("MiniBtn"),
            Content = new TextBlock
            {
                Text = "\uE70F",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
            },
            Width = 24,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            ToolTip = "重命名分组",
        };
        rename.Click += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };

        // 名字 + 铅笔放一起；右边留白把「删除分组」推到最右
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        nameRow.Children.Add(name);
        nameRow.Children.Add(rename);
        Grid.SetColumn(nameRow, 0);
        header.Children.Add(nameRow);

        var del = new Button
        {
            Style = (Style)FindResource("MiniBtn"),
            Content = "删除分组",
            ToolTip = "只删除分组，不删除动作",
            Margin = new Thickness(4, 0, 0, 0),
        };
        del.Click += (_, _) => DeleteGroup(groups, group);
        Grid.SetColumn(del, 2);
        header.Children.Add(del);

        stack.Children.Add(header);

        var grid = new TileGridPanel
        {
            MinTileWidth = 80,
            GapX = 4,
            GapY = 2,
            TileHeight = 64,
            Tag = group,
        };
        for (int i = 0; i < group.Actions.Count; i++)
            grid.Children.Add(BuildTile(group, i, group.Actions[i]));
        grid.Children.Add(BuildAddTile(group));
        stack.Children.Add(grid);

        card.Child = stack;
        card.ContextMenu = BuildGroupMenu(group, groups);
        return card;
    }

    private void DeleteGroup(List<PanelGroupDto> groups, PanelGroupDto group)
    {
        if (groups.Count <= 1)
        {
            SetStatus("每个场景至少保留一个分组", ok: false);
            return;
        }
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除分组",
                $"确定删除分组「{group.Name}」吗？\n里面的动作只是从分组移除，不会删除动作本体。",
                danger: true))
            return;
        groups.Remove(group);
        PanelLibrary.Save();
        RenderDetail();
        SetStatus("分组已删除", ok: true);
    }

    /// <summary>磁贴内容（图标 chip + 标题）；磁贴与拖动幽灵共用。</summary>
    private static StackPanel TileContent(string? reference)
    {
        bool exists = BindingRef.Exists(reference);
        var content = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            Background = (Brush)Application.Current.FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new TextBlock
            {
                Text = exists ? IconCatalog.GetGlyph(BindingRef.Icon(reference)) : "\uE783",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource(exists ? "TextBrush" : "DangerBrush"),
            },
        });
        content.Children.Add(new TextBlock
        {
            Text = exists ? BindingRef.Name(reference) : "缺失",
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11,
            MaxWidth = 92,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)Application.Current.FindResource(exists ? "SecondaryTextBrush" : "DangerBrush"),
        });
        return content;
    }

    private Button BuildTile(PanelGroupDto group, int index, string id)
    {
        bool feature = FeatureCatalog.IsFeatureRef(id);
        var btn = new Button
        {
            Style = (Style)FindResource("PanelTileBtn"),
            Tag = new PanelTileRef { Group = group, Index = index, ActionId = id },
            ToolTip = BindingRef.Describe(id),
        };

        var content = TileContent(id);
        btn.Content = content;

        AttachTileDrag(btn);

        var menu = new ContextMenu();
        var remove = new MenuItem { Header = "从此组移除" };
        remove.Click += (_, _) =>
        {
            PanelActionOps.RemoveFromGroup(group, index);
            RefreshAll();
        };

        if (feature)
        {
            var def = FeatureCatalog.Find(FeatureRef.IdOf(id));
            if (def is { HasParams: true })
            {
                var param = new MenuItem { Header = "参数…" };
                param.Click += (_, _) => EditFeatureParams(group, index, id);
                menu.Items.Add(param);
                menu.Items.Add(new Separator());
            }
            menu.Items.Add(remove);
            btn.ContextMenu = menu;
            btn.Click += (_, _) =>
            {
                if (def is { HasParams: true }) EditFeatureParams(group, index, id);
            };
            return btn;
        }

        var a = ActionRegistry.FindById(id);
        btn.Click += (_, _) =>
        {
            if (PanelActionOps.Edit(Window.GetWindow(this), id)) RefreshAll();
        };

        var edit = new MenuItem { Header = "编辑…" };
        edit.Click += (_, _) => { if (PanelActionOps.Edit(Window.GetWindow(this), id)) RefreshAll(); };
        menu.Items.Add(edit);
        if (a != null)
        {
            var export = new MenuItem { Header = "导出动作…" };
            export.Click += (_, _) => PanelActionOps.Export(Window.GetWindow(this), id);
            menu.Items.Add(export);

            var toSubAction = new MenuItem { Header = "另存为子动作…" };
            toSubAction.Click += (_, _) => PanelActionOps.ToSubAction(Window.GetWindow(this), id);
            menu.Items.Add(toSubAction);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(remove);
        if (a != null)
        {
            var del = new MenuItem { Header = "删除动作…" };
            del.Click += (_, _) => { if (PanelActionOps.Delete(Window.GetWindow(this), id)) RefreshAll(); };
            menu.Items.Add(del);
        }
        btn.ContextMenu = menu;
        return btn;
    }

    /// <summary>改内置功能的参数：替换分组里的那一处引用（同一个功能可以在不同位置带不同参数）。</summary>
    private void EditFeatureParams(PanelGroupDto group, int index, string reference)
    {
        string? updated = FeatureParamsDialog.Show(Window.GetWindow(this), reference);
        if (string.IsNullOrEmpty(updated) || updated == reference) return;

        if (index >= 0 && index < group.Actions.Count && group.Actions[index] == reference)
            group.Actions[index] = updated;
        else
        {
            int i = group.Actions.IndexOf(reference);
            if (i >= 0) group.Actions[i] = updated;
        }

        PanelLibrary.Save();
        RefreshAll();
    }

    private Button BuildAddTile(PanelGroupDto group)
    {
        var btn = new Button
        {
            Style = (Style)FindResource("PanelTileBtn"),
            Tag = new PanelTileRef { Group = group, Index = group.Actions.Count, ActionId = null },
            Opacity = 0.8,
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
            FontSize = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("HintTextBrush"),
        });
        content.Children.Add(new TextBlock
        {
            Text = "添加",
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("HintTextBrush"),
        });
        btn.Content = content;

        btn.Click += (_, _) =>
        {
            var menu = new ContextMenu();
            var owner = Window.GetWindow(this);
            void Add(string header, Action act)
            {
                var mi = new MenuItem { Header = header };
                mi.Click += (_, _) => Dispatcher.InvokeAsync(() => { act(); RefreshAll(); });
                menu.Items.Add(mi);
            }
            Add("新建动作…", () => PanelActionOps.AddNew(owner, group));
            Add("从动作池选择…", () => PanelActionOps.AddExisting(owner, group));
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return btn;
    }

    // ============ 辅助 ============

    /// <summary>磁贴拖动：与动作面板同一套（快照幽灵 + 虚线占位 + 实时让位 + Esc 取消）。</summary>
    private void AttachTileDrag(Button btn)
    {
        if (_tileDrag == null)
        {
            var win = Window.GetWindow(this);
            if (win == null) return;   // 还没挂到窗口上：等下一次 Reload
            _tileDrag = new TileDragSort(win, GhostLayer, DetailHost)
            {
                GhostBody = tref => tref.ActionId == null
                    ? null
                    : TileContent(tref.ActionId),
                Finished = RefreshAll,
            };
        }
        _tileDrag.Attach(btn);
    }

    private void RefreshAll()
    {
        BuildSceneList();
        RenderDetail();
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = (double)FindResource("FontSizeHint"),
        Foreground = (Brush)FindResource("HintTextBrush"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 2, 0, 8),
    };

    private void SetStatus(string text, bool ok)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource(ok ? "SuccessBrush" : "DangerBrush");
    }
}
