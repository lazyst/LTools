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

/// <summary>PanelManageView 的「禁止弹出」页部分。</summary>
public partial class PanelManageView : UserControl
{
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

}
