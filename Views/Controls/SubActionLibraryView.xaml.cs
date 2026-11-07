using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Views.Controls;

/// <summary>
/// 设置「子动作库」：全局子动作（<see cref="SubActionStore"/>，数据根 <c>subactions/</c>）的列表与增删改。
/// </summary>
/// <remarks>
/// 编辑复用 <see cref="ActionEditorWindow.ForSubAction"/>——子动作与动作同构，
/// 只多一列「方向」（输入 / 本地 / 输出），所以没必要再写一个编辑器。
/// </remarks>
public partial class SubActionLibraryView : UserControl
{
    private IReadOnlyList<SubActionRow> _rows = Array.Empty<SubActionRow>();

    public SubActionLibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    /// <summary>重新读取 <c>subactions/</c> 并刷新列表。</summary>
    public void Reload()
    {
        string? keep = SelectedRow()?.Id;

        _rows = SubActionStore.LoadAll()
            .OrderBy(d => d.Name, StringComparer.CurrentCulture)
            .ThenBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .Select(d => new SubActionRow(d))
            .ToList();

        List.ItemsSource = _rows;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (keep != null)
        {
            var target = _rows.FirstOrDefault(r =>
                string.Equals(r.Id, keep, StringComparison.OrdinalIgnoreCase));
            if (target != null) List.SelectedItem = target;
        }
    }

    private SubActionRow? SelectedRow() => List.SelectedItem as SubActionRow;

    // ============ 增删改 ============

    private void New_Click(object sender, RoutedEventArgs e) =>
        Edit(null);

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row)
        {
            ConfirmDialog.Info(Window.GetWindow(this), "子动作库", "先选一个子动作。");
            return;
        }
        Edit(row.Def);
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;

        var copy = row.Def.Clone();
        copy.Id = "";
        copy.Name = row.Def.Name + " 副本";
        Edit(copy);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;

        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除子动作",
                $"「{row.Name}」会被删除。调用它的动作之后会提示找不到子动作。",
                danger: true, okText: "删除"))
            return;

        SubActionStore.Delete(row.Id);
        Reload();
    }

    private void Row_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is SubActionRow row)
            List.SelectedItem = row;
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow() is { } row) Edit(row.Def);
    }

    private void Edit(SubActionDef? def)
    {
        var dlg = ActionEditorWindow.ForSubAction(
            def == null || string.IsNullOrWhiteSpace(def.Id) ? "新建子动作" : "编辑子动作", def);
        dlg.Owner = Window.GetWindow(this);

        if (dlg.ShowDialog() != true || dlg.SubResult == null) return;

        try
        {
            SubActionStore.Save(dlg.SubResult);
        }
        catch (ActionException ex)
        {
            ConfirmDialog.Info(Window.GetWindow(this), "子动作库", ex.Message);
            return;
        }

        Reload();

        var saved = _rows.FirstOrDefault(r => r.Id == dlg.SubResult.Id);
        if (saved != null) List.SelectedItem = saved;
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(SubActionStore.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", SubActionStore.DirectoryPath)
        {
            UseShellExecute = true,
        });
    }

    /// <summary>列表行；<see cref="Def"/> 保留完整定义供编辑 / 复制。</summary>
    public sealed class SubActionRow
    {
        public SubActionRow(SubActionDef def)
        {
            Def = def;
            Id = def.Id;
            Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;
            Summary = Summarize(def);
        }

        public SubActionDef Def { get; }
        public string Id { get; }
        public string Name { get; }
        public string Summary { get; }

        private static string Summarize(SubActionDef def)
        {
            var inputs = def.Variables
                .Where(v => v.Direction == VariableDirection.input)
                .Select(v => v.Key).ToList();
            var outputs = def.Variables
                .Where(v => v.Direction == VariableDirection.output)
                .Select(v => v.Key).ToList();
            int steps = def.Steps.Sum(s => s.SelfAndDescendants().Count());

            var parts = new List<string>
            {
                inputs.Count == 0 ? "无输入" : "输入 " + string.Join("、", inputs),
                steps == 0 ? "空子动作" : $"{steps} 步",
            };
            if (outputs.Count > 0) parts.Add("输出 " + string.Join("、", outputs));

            return string.Join(" · ", parts);
        }
    }
}
