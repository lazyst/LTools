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
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Features.Floating;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>PanelManageView 的「详情」部分（匹配条件 / 分组 / 磁贴）。</summary>
public partial class PanelManageView : UserControl
{
    // ============ 详情 ============

    /// <summary>
    /// 重建详情页。空白处菜单**跟着重建**：它里面有「粘贴（落到本场景第一个分组）」，
    /// 而剪贴板与当前场景都会变——构造时建一次的话会粘到别的场景的分组里。
    /// </summary>
    private void RenderDetail()
    {
        RenderDetailCore();
        DetailHost.ContextMenu = BuildDetailBlankMenu();
    }

    private void RenderDetailCore()
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
            ToolTip = BindingRef.Detail(id),
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

        var simple = SimpleActions.KindOf(a);
        var edit = new MenuItem { Header = simple != null ? "编辑简单动作…" : "编辑…" };
        edit.Click += (_, _) => { if (PanelActionOps.Edit(Window.GetWindow(this), id)) RefreshAll(); };
        menu.Items.Add(edit);
        if (simple != null)
        {
            var convert = new MenuItem { Header = "转换为组合动作…" };
            convert.Click += (_, _) =>
            {
                if (PanelActionOps.ConvertToComposite(Window.GetWindow(this), id)) RefreshAll();
            };
            menu.Items.Add(convert);
        }

        var copyRef = new MenuItem { Header = "复制引用" };
        copyRef.Click += (_, _) => PanelActionOps.CopyReference(id);
        menu.Items.Add(copyRef);
        if (a != null)
        {
            var copyCopy = new MenuItem { Header = "复制为副本" };
            copyCopy.Click += (_, _) => PanelActionOps.CopyCopy(id);
            menu.Items.Add(copyCopy);
        }
        PanelPasteMenu.Append(menu, group, PasteIntoGroup, index + 1);

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
            AddSimpleMenu(menu, group);
            Add("新建组合动作…", () => PanelActionOps.AddNew(owner, group));
            Add("从动作池选择…", () => PanelActionOps.AddExisting(owner, group));
            menu.PlacementTarget = btn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        return btn;
    }

}
