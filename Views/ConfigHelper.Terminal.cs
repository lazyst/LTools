using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Text;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Core.Ai;
using LTools.Core.Storage;
using LTools.Features;
using LTools.Features.Panel;
using LTools.Views.Controls;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace LTools.Views;

/// <summary>ConfigHelperWindow 的「终端路径」部分。</summary>
public partial class ConfigHelperWindow
{
    // ============ 终端路径 ============

    /// <summary>
    /// 终端路径表格的滚轮：内层 ScrollViewer 还有可滚空间就让它自己滚（内容看得全），滚到头
    /// 才把滚轮交还给外层 Page_Terminals。与 AiView.AuditGrid 同源，但 PathsGrid 有固定 220 高、
    /// 可能真的需要自己滚，所以不设 VerticalScrollBarVisibility=Disabled，也不能无条件转发
    /// （否则超出的行永远看不到）。
    /// </summary>
    private void PathsGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        if (FindDescendant<ScrollViewer>(PathsGrid) is { ScrollableHeight: > 0 } inner)
        {
            bool canScroll = e.Delta < 0
                ? inner.VerticalOffset < inner.ScrollableHeight - 0.5
                : inner.VerticalOffset > 0.5;
            if (canScroll) return;   // 交给内层自己滚，不吞
        }
        e.Handled = true;
        Page_Terminals.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = Page_Terminals,
        });
    }

    private void PopulateTerminals()
    {
        var rows = new List<PathRow>();
        foreach (var key in TerminalLauncher.ResolvableTerminals)
            rows.Add(new PathRow(key, TerminalLauncher.DisplayLabel(key)));

        PathsGrid.ItemsSource = rows;
        foreach (var row in rows) row.Refresh();
    }

    /// <summary>
    /// DataGridCell 在「自己还没有键盘焦点」时会把这一下 MouseDown 标记成已处理（先给自己抢焦点），
    /// 单元格里的输入框 / 「浏览…」第一下就会落空。预览阶段先把单元格焦点给上，它就不再吞。
    /// </summary>
    private void PathsGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = VisualTree.FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell != null && !cell.IsKeyboardFocusWithin) cell.Focus();
    }

    /// <summary>
    /// 终端路径表双击判定：命中一行、且没点「浏览…」按钮才进「覆盖」编辑。
    /// 抽成纯函数便于无头冒烟直接断言（照 TriggersView.DoubleClickEditRow 的做法）。
    /// </summary>
    internal static bool PathsDoubleClickEditsOverride(Button? hitButton, object? rowDataContext) =>
        hitButton == null && rowDataContext is PathRow;

    /// <summary>双击整行 = 把焦点给这一行的「覆盖」输入框并全选，直接开始改路径。</summary>
    private void PathsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        var row = VisualTree.FindAncestor<DataGridRow>(source);
        if (!PathsDoubleClickEditsOverride(VisualTree.FindAncestor<Button>(source), row?.DataContext)) return;
        if (row?.DataContext is not PathRow path) return;

        PathsGrid.SelectedItem = path;
        var col = PathsGrid.Columns.FirstOrDefault(c => Equals(c.Header, "覆盖"));
        if (col?.GetCellContent(path) is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
        e.Handled = true;
    }

    /// <summary>输入框失焦：保存这一行的覆盖（空 = 恢复自动探测）。</summary>
    private void TerminalOverride_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box && box.DataContext is PathRow row) SaveTerminalRow(row, box.Text);
    }

    /// <summary>回车：同样保存，然后把焦点交出去（不然改了看不到「已保存」）。</summary>
    private void TerminalOverride_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || box.DataContext is not PathRow row) return;
        e.Handled = true;
        SaveTerminalRow(row, box.Text);
        box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
    }

    /// <summary>挑一个 exe 当这一行的覆盖路径，选完即保存。</summary>
    private void BrowseTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not PathRow row) return;

        var ofd = new OpenFileDialog
        {
            Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = $"选择「{row.Label}」的可执行文件",
            FileName = row.Override.Length > 0 ? row.Override : row.Path,
        };
        if (ofd.ShowDialog(this) != true) return;

        SaveTerminalRow(row, ofd.FileName);
    }

    /// <summary>
    /// 保存一行的覆盖并刷新「实际路径」。和已存的值一样就不写盘——
    /// 绑定的 <c>UpdateSourceTrigger=PropertyChanged</c> 已经把文字写进 <see cref="PathRow.Override"/>，
    /// 所以比较的是**配置里**的值，不是行对象上的值。
    /// </summary>
    private void SaveTerminalRow(PathRow row, string? text)
    {
        string value = (text ?? "").Trim();
        string saved = TerminalLauncher.GetOverride(row.Key) ?? "";

        if (!string.Equals(saved, value, StringComparison.OrdinalIgnoreCase))
        {
            row.Override = value;
            TerminalLauncher.SavePath(row.Key, value);
            ShowStatus($"{row.Label} 路径已保存", ok: true);
        }

        row.Refresh();
    }

}
