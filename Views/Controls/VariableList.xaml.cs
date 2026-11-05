using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LTools.Views.Controls;

/// <summary>
/// 可用变量列表：内置值（光标 / 剪贴板 / 日期 …）+ 流程里用「设置变量」创建的自定义变量。
/// 每一行可点，宿主决定是插入到输入框还是复制 <c>${name}</c>。
/// </summary>
public partial class VariableList : UserControl
{
    /// <summary>点了某个变量（参数是不带 <c>${}</c> 的变量名，可能含 <c>.x</c> 属性）。</summary>
    public event Action<string>? VariableInvoked;

    private static readonly (string Name, string Hint)[] BuiltIn =
    {
        ("cursor.x", "光标 X"),
        ("cursor.y", "光标 Y"),
        ("clipboard", "剪贴板文本"),
        ("selection", "选中文本"),
        ("date", "今天日期"),
        ("time", "当前时间"),
        ("loop.i", "循环下标（循环内）"),
        ("run.name", "组合动作名"),
        ("lastError", "上一步的错误"),
    };

    public VariableList()
    {
        InitializeComponent();
        Rebuild(Array.Empty<string>());
    }

    /// <summary>设置流程里自定义的变量名（会与内置变量一起显示）。</summary>
    public void SetCustom(IEnumerable<string>? names) => Rebuild(names ?? Array.Empty<string>());

    private void Rebuild(IEnumerable<string> customNames)
    {
        Rows.Children.Clear();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, hint) in BuiltIn)
        {
            if (seen.Add(name)) Rows.Children.Add(BuildRow(name, hint));
        }

        foreach (string raw in customNames)
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            if (name.StartsWith("${", StringComparison.Ordinal) && name.EndsWith('}'))
                name = name[2..^1];
            if (seen.Add(name)) Rows.Children.Add(BuildRow(name, "自定义变量"));
        }
    }

    private Button BuildRow(string name, string hint)
    {
        var token = new TextBlock
        {
            Text = "${" + name + "}",
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var label = new TextBlock
        {
            Text = hint,
            FontSize = (double)FindResource("FontSizeHint"),
            Foreground = (Brush)FindResource("HintTextBrush"),
            Margin = new Thickness(0, 1, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel();
        content.Children.Add(token);
        content.Children.Add(label);

        var button = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(7, 3, 7, 3),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "插入 / 复制 ${" + name + "}",
        };
        button.Click += (_, _) => VariableInvoked?.Invoke(name);
        return button;
    }
}
