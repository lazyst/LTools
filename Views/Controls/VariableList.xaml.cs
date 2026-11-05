using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features;

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
        Rebuild(Array.Empty<VariableInfo>());
    }

    /// <summary>设置流程里由用户创建的变量名（只有当用户真的创建过变量时才会有内容）。</summary>
    public void SetCustom(IEnumerable<string>? names) =>
        Rebuild((names ?? Array.Empty<string>())
            .Select(n => new VariableInfo(n, Array.Empty<string>()))
            .ToList());

    /// <summary>设置变量及其可用属性；属性会作为独立行列出（如图像匹配的 <c>m.found</c> / <c>m.score</c>）。</summary>
    internal void SetVariables(IEnumerable<VariableInfo>? infos) =>
        Rebuild(infos?.ToList() ?? new List<VariableInfo>());

    private void Rebuild(IReadOnlyList<VariableInfo> variables)
    {
        Rows.Children.Clear();

        int count = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in variables)
        {
            string name = Clean(info.Name);
            if (name.Length == 0 || !seen.Add(name)) continue;

            Rows.Children.Add(BuildRow(name));
            count++;

            foreach (string prop in info.Properties)
            {
                string path = name + "." + prop;
                if (!seen.Add(path)) continue;
                Rows.Children.Add(BuildRow(path, isProperty: true));
                count++;
            }
        }

        EmptyHint.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Button BuildRow(string name, bool isProperty = false)
    {
        var token = new TextBlock
        {
            Text = "${" + name + "}",
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = isProperty ? 12 : 12.5,
            Foreground = (Brush)FindResource(isProperty ? "SecondaryTextBrush" : "TextBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var button = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            Content = token,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(isProperty ? 16 : 7, 3, 7, 3),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "插入 / 复制 ${" + name + "}",
        };
        button.Click += (_, _) => VariableInvoked?.Invoke(name);
        return button;
    }

    /// <summary>去掉可能的 <c>${…}</c> 外壳与首尾空白；空名字返回空串。</summary>
    private static string Clean(string? raw)
    {
        string name = (raw ?? "").Trim();
        if (name.StartsWith("${", StringComparison.Ordinal) && name.EndsWith('}'))
            name = name[2..^1].Trim();
        return name;
    }
}
