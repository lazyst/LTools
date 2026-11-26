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

/// <summary>PanelManageView 的「场景列表」部分。</summary>
public partial class PanelManageView : UserControl
{
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
        var commonMenu = new ContextMenu();
        var commonGroups = PanelLibrary.Current.Context.Common;
        if (commonGroups.Count > 0) AddSimpleMenu(commonMenu, commonGroups[0]);
        AddMenuItems(commonMenu,
            ("新建组合动作…", () =>
            {
                var groups = PanelLibrary.Current.Context.Common;
                if (groups.Count > 0) AddActionToGroup(groups[0]);
            }),
            ("新建分组", () => AddGroupToScene(PanelLibrary.Current.Context.Common)));
        btn.ContextMenu = commonMenu;
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

}
