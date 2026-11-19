using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 完成动作布局编辑器（设置「通用 → 截图 Pro · 界面与参考线」里的「编辑…」）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot_toolbar/action_tools_layout</c>（<c>ScreenshotToolbarLayout</c> 的
/// <c>positions</c> + <c>hidden</c>）：每行一个完成动作，勾选框决定显不显示，「新组」决定
/// 它前面要不要另起一组（工具条上画一条竖线），▲▼ 换顺序。编辑的是本地草稿，点「保存」才写
/// <c>configs/ui.json</c>（见 <see cref="CaptureActionLayoutStore"/>）。
/// </para>
/// <para>
/// 上游按工具栏种类各存一份（截图 / 贴图 / 录屏 / 悬浮 / 绘制），本项目的贴图编辑与截图编辑是
/// **同一个覆盖层**，所以只存一份；图片编辑窗里用不到的动作由
/// <see cref="CaptureToolbar.ActionAvailableIn"/> 自动跳过，这里照实说明。
/// </para>
/// </remarks>
public partial class CaptureActionLayoutWindow : Window
{
    private sealed class Row
    {
        public CaptureActionTool Tool;
        public bool Visible;
        public bool GroupStart;
        public Border Host = null!;
        public CheckBox VisibleBox = null!;
        public TextBlock Label = null!;
        public CheckBox GroupBox = null!;
        public Button Up = null!;
        public Button Down = null!;
    }

    private readonly List<Row> _rows = new();
    private bool _syncing;

    private CaptureActionLayoutWindow()
    {
        InitializeComponent();

        LeadText.Text = "工具条右半边那些按钮的顺序、分组与显隐（勾掉 = 从工具条上收起来）。";
        HintText.Text = "「新组」= 这一项前面画一条竖线，与上一项分开。图片编辑窗里用不到的动作"
            + "（长截图 / 录屏 / 实时贴图 / 更多 / 取消）会自动跳过。";

        BuildRows(CaptureActionLayoutStore.Current);
        Render();
    }

    /// <summary>打开编辑器；返回 <c>true</c> = 已保存。</summary>
    internal static bool Show(Window? owner)
    {
        var window = new CaptureActionLayoutWindow
        {
            Owner = owner is { IsLoaded: true } ? owner : null,
            Topmost = owner?.Topmost ?? true,
        };

        return window.ShowDialog() == true;
    }

    /// <summary>当前草稿（冒烟直接断言，不用点按钮）。</summary>
    internal CaptureActionLayout CurrentLayout => ToLayout();

    /// <summary>按一份布局重建全部行（第一次打开与「恢复默认」共用）。</summary>
    private void BuildRows(CaptureActionLayout layout)
    {
        _rows.Clear();

        foreach (var group in layout.Groups)
            for (int i = 0; i < group.Count; i++)
                _rows.Add(MakeRow(group[i], visible: true, groupStart: i == 0));

        // 第一行前面没有东西，不可能「另起一组」。
        if (_rows.Count > 0) _rows[0].GroupStart = false;

        foreach (var tool in CaptureActionLayout.Catalog)
            if (layout.Hidden.Contains(tool))
                _rows.Add(MakeRow(tool, visible: false, groupStart: false));
    }

    private Row MakeRow(CaptureActionTool tool, bool visible, bool groupStart)
    {
        var row = new Row { Tool = tool, Visible = visible, GroupStart = groupStart };

        row.VisibleBox = new CheckBox
        {
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "在工具条上显示这一项",
        };
        row.VisibleBox.Checked += (_, _) => OnVisibleChanged(row);
        row.VisibleBox.Unchecked += (_, _) => OnVisibleChanged(row);

        row.Label = new TextBlock
        {
            Text = CaptureActionLayout.LabelOf(tool),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };

        row.GroupBox = new CheckBox
        {
            Content = "新组",
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "从这一项开始新的一组（工具条上前面画一条竖线）",
        };
        row.GroupBox.Checked += (_, _) => OnGroupChanged(row);
        row.GroupBox.Unchecked += (_, _) => OnGroupChanged(row);

        row.Up = SmallButton("▲", "上移", () => Move(row, -1));
        row.Down = SmallButton("▼", "下移", () => Move(row, 1));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(row.VisibleBox, 0);
        Grid.SetColumn(row.Label, 1);
        Grid.SetColumn(row.GroupBox, 2);
        Grid.SetColumn(row.Up, 3);
        Grid.SetColumn(row.Down, 4);
        grid.Children.Add(row.VisibleBox);
        grid.Children.Add(row.Label);
        grid.Children.Add(row.GroupBox);
        grid.Children.Add(row.Up);
        grid.Children.Add(row.Down);

        row.Host = new Border
        {
            Child = grid,
            Padding = new Thickness(0, 5, 0, 5),
            BorderBrush = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Tag = CaptureActionLayout.KeyOf(tool),
        };

        return row;
    }

    private Button SmallButton(string glyph, string tip, Action click)
    {
        var button = new Button
        {
            Content = glyph,
            ToolTip = tip,
            Style = (Style)FindResource("BtnGhost"),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            Width = 28,
            Height = 26,
            Padding = new Thickness(0),
            Margin = new Thickness(4, 0, 0, 0),
            Focusable = false,
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>把行按当前顺序摆出来（勾选态 / 可用态 / 分组线一起刷新）。</summary>
    private void Render()
    {
        _syncing = true;
        try
        {
            RowHost.Children.Clear();
            for (int i = 0; i < _rows.Count; i++)
            {
                var row = _rows[i];
                bool breaks = row.GroupStart && i > 0;

                row.Label.Text = CaptureActionLayout.LabelOf(row.Tool);
                row.Host.Tag = CaptureActionLayout.KeyOf(row.Tool);
                row.VisibleBox.IsChecked = row.Visible;
                row.GroupBox.IsChecked = row.GroupStart;
                row.GroupBox.IsEnabled = i > 0;
                row.Label.Foreground = (Brush)FindResource(row.Visible ? "TextBrush" : "HintTextBrush");

                row.Up.IsEnabled = i > 0;
                row.Down.IsEnabled = i < _rows.Count - 1;

                row.Host.BorderBrush = breaks ? (Brush)FindResource("BorderStrongBrush") : Brushes.Transparent;
                row.Host.BorderThickness = new Thickness(0, breaks ? 1 : 0, 0, 0);

                RowHost.Children.Add(row.Host);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnVisibleChanged(Row row)
    {
        if (_syncing) return;
        row.Visible = row.VisibleBox.IsChecked == true;
        Render();
    }

    private void OnGroupChanged(Row row)
    {
        if (_syncing) return;
        row.GroupStart = row.GroupBox.IsChecked == true;
        Render();
    }

    private void Move(Row row, int delta)
    {
        int index = _rows.IndexOf(row);
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _rows.Count) return;

        // 只换「这一行是哪个动作」（以及它显不显示），**不动「新组」标记**：
        // 分组线是位置上的边界（照上游把 positions 当一组一组的列表），
        // 挪走一项不该把边界也跟着搬走——否则把「复制」上移一格会把最后一组拆成两段。
        var other = _rows[target];
        (row.Tool, other.Tool) = (other.Tool, row.Tool);
        (row.Visible, other.Visible) = (other.Visible, row.Visible);
        Render();
    }

    /// <summary>草稿 → 布局（隐藏的进 hidden，可见的按「新组」切段）。</summary>
    private CaptureActionLayout ToLayout()
    {
        var groups = new List<List<CaptureActionTool>>();
        var hidden = new List<CaptureActionTool>();
        var current = new List<CaptureActionTool>();

        foreach (var row in _rows)
        {
            if (!row.Visible)
            {
                hidden.Add(row.Tool);
                continue;
            }

            if (row.GroupStart && current.Count > 0)
            {
                groups.Add(current);
                current = new List<CaptureActionTool>();
            }

            current.Add(row.Tool);
        }

        if (current.Count > 0) groups.Add(current);
        return CaptureActionLayout.FromGroups(groups, hidden);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        BuildRows(CaptureActionLayout.Default());
        Render();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>把草稿写盘（「保存」按钮与冒烟共用；冒烟是 <c>Show()</c> 出来的窗口，设不了 DialogResult）。</summary>
    internal void Save() => CaptureActionLayoutStore.Save(ToLayout());

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Save();
        DialogResult = true;
    }
}
