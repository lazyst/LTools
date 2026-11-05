using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LTools.Views.Controls;

/// <summary>
/// 变量列表：只显示流程里由用户创建的变量（「设置变量」或左侧「取值」）。
/// 光标 / 剪贴板 / 选中文本等取值在左侧栏是独立单元，不再作为「内置变量」混进这里。
/// 每一行可点，宿主决定是插入到输入框还是复制 <c>${name}</c>。
/// </summary>
public partial class VariableList : UserControl
{
    /// <summary>点了某个变量（参数是不带 <c>${}</c> 的变量名，可能含 <c>.x</c> 属性）。</summary>
    public event Action<string>? VariableInvoked;

    public VariableList()
    {
        InitializeComponent();
        Rebuild(Array.Empty<string>());
    }

    /// <summary>设置流程里由用户创建的变量名（只有当用户真的创建过变量时才会有内容）。</summary>
    public void SetCustom(IEnumerable<string>? names) => Rebuild(names ?? Array.Empty<string>());

    private void Rebuild(IEnumerable<string> customNames)
    {
        Rows.Children.Clear();

        int count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in customNames)
        {
            string name = raw.Trim();
            if (name.Length == 0) continue;
            if (name.StartsWith("${", StringComparison.Ordinal) && name.EndsWith('}'))
                name = name[2..^1];
            if (!seen.Add(name)) continue;

            Rows.Children.Add(BuildRow(name));
            count++;
        }

        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button BuildRow(string name)
    {
        var token = new TextBlock
        {
            Text = "${" + name + "}",
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 12.5,
            Foreground = (Brush)FindResource("TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var button = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = token,
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
