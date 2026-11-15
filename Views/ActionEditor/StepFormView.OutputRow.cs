using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Script;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 一行输出：☑ 标签 [类型] → [变量名 ▾] + 说明行；同名不同类型当场提醒。
/// </summary>
public partial class StepFormView
{
    /// <summary>
    /// 一行输出：<c>☑ 标签 [类型] → [变量名 ▾]</c>，下一行说明「写入 ${变量}：装的是什么」。
    /// 说明行**不看勾选状态**都显示——用户要先读到「退出码是否为 0」才知道值不值得勾。
    /// </summary>
    private OutputRow AddOutputRow(string key, string label, ValueKind? type,
        string? description, string value, Panel? host = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toggle = new CheckBox
        {
            Content = label,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = ValueKindText.Of(type),
                FontSize = 11.5,
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            },
        };

        var arrow = new TextBlock
        {
            Text = "→",
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("HintTextBrush"),
        };

        var box = new TextBox
        {
            MinHeight = 28,
            Text = value,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
        };

        // 换成一个已有的变量：想复用上一步的结果时不用去变量表里抄名字。
        // 图标与下拉框同一条折线（ChevronDownGeometry），不是正文里的 ▾ 字符。
        var pick = new Button
        {
            Content = new System.Windows.Shapes.Path
            {
                Data = (System.Windows.Media.Geometry)FindResource("ChevronDownGeometry"),
                Stroke = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
                StrokeThickness = 1.4,
                StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Style = (Style)FindResource("BtnGhost"),
            Width = 26,
            MinHeight = 28,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
            ToolTip = "换成已有的变量（同名就复用，不会新建）",
        };
        pick.Click += (_, _) => ShowVariablePicker(pick, box);

        var hint = new TextBlock
        {
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };

        grid.Children.Add(toggle);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);
        Grid.SetColumn(box, 3);
        grid.Children.Add(box);
        Grid.SetColumn(pick, 4);
        grid.Children.Add(pick);
        Grid.SetRow(hint, 1);
        Grid.SetColumn(hint, 1);
        Grid.SetColumnSpan(hint, 4);
        grid.Children.Add(hint);
        (host ?? Outputs).Children.Add(grid);

        Action refresh = () =>
        {
            string name = (box.Text ?? "").Trim();
            string target = name.Length == 0 ? "（还没填变量名）" : "${" + name + "}";
            string text = toggle.IsChecked == true ? $"写入 {target}" : $"勾选后写入 {target}";
            if (!string.IsNullOrWhiteSpace(description)) text += "：" + description;

            // 同名不同类型会互相覆盖（变量表里只有一行），当场提醒而不是等运行时踩坑。
            var existing = name.Length == 0 ? null : VariableInfos().FirstOrDefault(v =>
                string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && type != null && existing.Kind != type)
            {
                string from = string.IsNullOrWhiteSpace(existing.Origin)
                    ? "" : "（" + existing.Origin + "）";
                hint.Text = $"已有变量 {name} 是「{ValueKindText.Of(existing.Kind)}」{from}；" +
                            "同名不同类型会互相覆盖，换个名字";
                hint.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
                box.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBorderBrush");
            }
            else
            {
                hint.Text = text;
                hint.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
                box.ClearValue(Control.BorderBrushProperty);
            }
        };

        return new OutputRow(key, toggle, box, pick, refresh);
    }

    /// <summary>▾：把这一行的变量名换成动作里已有的某个变量（同名即复用）。</summary>
    private void ShowVariablePicker(FrameworkElement target, TextBox box)
    {
        var infos = VariableInfos();
        var menu = new ContextMenu
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
        };

        if (infos.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "（动作里还没有变量）", IsEnabled = false });
        }
        else
        {
            foreach (var info in infos)
            {
                string name = info.Name;
                var item = new MenuItem
                {
                    Header = name + "（" + ValueKindText.Of(info.Kind) + "）",
                    ToolTip = string.IsNullOrWhiteSpace(info.Origin) ? null : info.Origin,
                };
                item.Click += (_, _) =>
                {
                    box.Text = name;
                    box.Focus();
                    box.CaretIndex = box.Text.Length;
                };
                menu.Items.Add(item);
            }
        }

        menu.IsOpen = true;
    }

    /// <summary>动作里已有的变量（带类型与来源）；宿主没传时为空。</summary>
    private IReadOnlyList<VariableInfo> VariableInfos()
    {
        try { return VariableInfoSource?.Invoke() ?? Array.Empty<VariableInfo>(); }
        catch { return Array.Empty<VariableInfo>(); }
    }

}
