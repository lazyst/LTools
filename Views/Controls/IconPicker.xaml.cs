using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using CapsLockPro.Features;

namespace CapsLockPro.Views.Controls;

/// <summary>
/// 内置图标网格选择器（计划 §3.1 / §10.1）：40 个 Segoe MDL2 字形铺成网格，
/// 点击选中，「不使用图标」清空选择。选中值通过 <see cref="SelectedIcon"/> 属性双向绑定。
/// </summary>
public partial class IconPicker : UserControl
{
    /// <summary>当前选中的图标名称；null = 无图标（渲染时回退默认图标）。</summary>
    public static readonly DependencyProperty SelectedIconProperty =
        DependencyProperty.Register(
            nameof(SelectedIcon), typeof(string), typeof(IconPicker),
            new FrameworkPropertyMetadata(default, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedIconChanged));

    public string? SelectedIcon
    {
        get => (string?)GetValue(SelectedIconProperty);
        set => SetValue(SelectedIconProperty, value);
    }

    private readonly List<RadioButton> _cells = new();
    private readonly List<string> _names = new();
    private bool _loading;

    public IconPicker()
    {
        InitializeComponent();
        BuildCells();
        SyncCheck();
    }

    private void BuildCells()
    {
        foreach (var name in IconCatalog.Names)
        {
            _names.Add(name);
            var rb = new RadioButton
            {
                Style = (Style)Resources["IconCell"],
                Tag = IconCatalog.GetGlyph(name),
                ToolTip = $"图标: {name}",
                Margin = new Thickness(1),
                Width = 34,
                Height = 34,
            };
            var captured = name;
            rb.Checked += (_, _) =>
            {
                if (_loading) return;
                // 单选：先取消其它格子
                _loading = true;
                try
                {
                    foreach (var other in _cells)
                        if (other != rb) other.IsChecked = false;
                }
                finally { _loading = false; }
                SelectedIcon = captured;
            };
            _cells.Add(rb);
        }

        // WrapPanel 按可用宽度自动换行：窄容器（如组合动作窗口右栏 ~244px）多排几行，
        // 不再用固定 10 列 UniformGrid——后者在窄容器里会把 34px 格子塞进 ~24px 槽导致相邻重叠。
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var c in _cells) panel.Children.Add(c);
        GridPanel.Child = panel;
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => SelectedIcon = null;

    private void SyncCheck()
    {
        _loading = true;
        try
        {
            for (int i = 0; i < _cells.Count; i++)
                _cells[i].IsChecked = (_names[i] == SelectedIcon);
            HintText.Text = SelectedIcon == null
                ? "未选图标（将使用默认「应用」图标）"
                : $"已选图标：{SelectedIcon}";
        }
        finally { _loading = false; }
    }

    private static void OnSelectedIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((IconPicker)d).SyncCheck();
}
