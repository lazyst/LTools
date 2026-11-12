using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Panel;

namespace LTools.Views.Controls;

/// <summary>
/// 设置「子动作库」：全局子动作（<see cref="SubActionStore"/>，数据根 <c>subactions/</c>）的列表与增删改。
/// </summary>
/// <remarks>
/// 编辑复用 <see cref="ActionEditorWindow.ForSubAction"/>——子动作与动作同构，
/// 只多一列「方向」（输入 / 本地 / 输出），所以没必要再写一个编辑器。
/// 试运行走 <see cref="SubActionRunner"/>，输入值由 <see cref="SubActionInputDialog"/> 收集。
/// </remarks>
public partial class SubActionLibraryView : UserControl
{
    private IReadOnlyList<SubActionRow> _rows = Array.Empty<SubActionRow>();

    private bool _loaded;

    public SubActionLibraryView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_loaded) Reload();
        };
    }

    /// <summary>重新读取 <c>subactions/</c> 并刷新列表。</summary>
    public void Reload()
    {
        _loaded = true;
        string? keep = SelectedRow()?.Id;

        // 设置页是「刷新」入口：用户在资源管理器里改过文件也能看到。
        SubActionStore.Invalidate();

        var references = SubActionStore.ReferenceIndex();
        _rows = SubActionStore.LoadAll()
            .Select(d => new SubActionRow(d,
                references.TryGetValue(d.Id, out var list) ? list : Array.Empty<SubActionReference>()))
            .ToList();

        ApplyFilter(keep);
        ShowBrokenFiles();
    }

    /// <summary>
    /// 切到本页时调用：已建页且数据没变就不重建（首次建页时 Loaded 与 ApplyNavPage 只让
    /// 第一次真正 Reload）；数据真变时（导入 / 编辑 / 删除）调用方仍直接调 <see cref="Reload"/>。
    /// </summary>
    public void RefreshIfNeeded()
    {
        if (!_loaded) Reload();
    }

    private SubActionRow? SelectedRow() => List.SelectedItem as SubActionRow;

    // ============ 筛选 ============

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter(SelectedRow()?.Id);

    private void ApplyFilter(string? keep)
    {
        string filter = (SearchBox.Text ?? "").Trim();

        var visible = filter.Length == 0
            ? _rows.ToList()
            : _rows.Where(r => r.Matches(filter)).ToList();

        List.ItemsSource = visible;

        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoMatchHint.Visibility = _rows.Count > 0 && visible.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (keep == null) return;
        var target = visible.FirstOrDefault(r =>
            string.Equals(r.Id, keep, StringComparison.OrdinalIgnoreCase));
        if (target != null) List.SelectedItem = target;
    }

    private void ShowBrokenFiles()
    {
        var broken = SubActionStore.BrokenFiles();
        if (broken.Count == 0)
        {
            BrokenHint.Visibility = Visibility.Collapsed;
            return;
        }

        BrokenHint.Visibility = Visibility.Visible;
        BrokenHint.Text = $"有 {broken.Count} 个子动作文件读不出来（已改名成 .bad-* 留在 subactions/ 里，最多保留 3 份）："
                          + string.Join("、", broken.Take(3).Select(Path.GetFileName))
                          + "。修好内容后把文件名改回 <id>.json 即可。";
    }

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

    /// <summary>子动作 → 新动作（转过去后变量一律是「本地」，由用户在动作编辑器里再调）。</summary>
    private void ToAction_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        PanelActionOps.ToAction(Window.GetWindow(this), row.Def);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;

        // 引用清单：删除前让用户看到「哪些动作的哪一步会坏」，而不是一句通用警告。
        var refs = SubActionStore.FindReferences(row.Id);
        string detail = refs.Count == 0
            ? $"「{row.Name}」会被删除。目前没有动作在调用它。"
            : $"「{row.Name}」会被删除，下面 {refs.Count} 处调用之后会报「找不到子动作」：\n\n"
              + string.Join("\n", refs.Take(8).Select(r => $"· {r.ActionName} · {r.Where}"))
              + (refs.Count > 8 ? $"\n…还有 {refs.Count - 8} 处" : "");

        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除子动作", detail,
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

    /// <summary>
    /// 双击是否落在行右侧的小图标上。整行卡片本身也是一个 Button（<c>SubActionRowBtn</c>），
    /// 但它的 <c>CommandParameter</c> 是 null；行内图标（run / edit / copy / delete / more）都带
    /// 非 null 的 CommandParameter。只按「有没有 Button 祖先」判断会连整行卡片一起挡掉——
    /// 于是双击整行永远没反应（旧的坏实现）。
    /// </summary>
    internal static bool IsRowIconDoubleClick(Button? ancestor) => ancestor is { CommandParameter: not null };

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击整行 / 文字 = 编辑；只有点行内图标才不弹编辑器（那些图标自己有单击语义）。
        if (IsRowIconDoubleClick(FindAncestor<Button>(e.OriginalSource as DependencyObject))) return;
        if (SelectedRow() is { } row) Edit(row.Def);
    }

    /// <summary>
    /// 行右侧 5 个图标的分发器：按 <c>DataContext</c> 认行（点按钮不会自动选中这一行），
    /// 设为选中项后复用既有的试运行 / 编辑 / 复制 / 删除；第 5 个 ⋯ 弹低频 3 项。
    /// </summary>
    private void SubActionRowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not SubActionRow row) return;
        List.SelectedItem = row;
        switch ((el as Button)?.CommandParameter as string)
        {
            case "run": Run_Click(sender, e); break;
            case "edit": Edit_Click(sender, e); break;
            case "copy": Duplicate_Click(sender, e); break;
            case "delete": Delete_Click(sender, e); break;
            case "more": ShowRowMore(el, row); break;
        }
    }

    /// <summary>每行 ⋯ 的低频菜单：另存为动作 / 复制 id / 在文件夹中显示。</summary>
    private void ShowRowMore(FrameworkElement anchor, SubActionRow row)
    {
        var menu = new ContextMenu
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };
        AddRowMenuItem(menu, "另存为动作…", row, ToAction_Click);
        AddRowMenuItem(menu, "复制 id", row, CopyId_Click);
        AddRowMenuItem(menu, "在文件夹中显示", row, Reveal_Click);
        menu.IsOpen = true;
    }

    private void AddRowMenuItem(ContextMenu menu, string header, SubActionRow row,
        RoutedEventHandler handler)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) =>
        {
            List.SelectedItem = row;
            handler(this, new RoutedEventArgs());
        };
        menu.Items.Add(item);
    }

    private void Edit(SubActionDef? def)
    {
        var dlg = ActionEditorWindow.ForSubAction(
            def == null || string.IsNullOrWhiteSpace(def.Id) ? "新建子动作" : "编辑子动作", def);
        dlg.Owner = Window.GetWindow(this);

        if (dlg.ShowDialog() != true || dlg.SubResult == null) return;

        try
        {
            SubActionStore.Save(dlg.SubResult, createNew: def == null || string.IsNullOrWhiteSpace(def.Id));
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

    // ============ 右键菜单 ============

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;

        var inputs = SubActionInputDialog.Ask(Window.GetWindow(this), row.Name, row.Def.Variables);
        if (inputs == null) return;

        if (!SubActionRunner.TryRun(row.Def, inputs, result => OnRunCompleted(row, result)))
            ConfirmDialog.Info(Window.GetWindow(this), "试运行", "已有动作正在运行，先等它结束或按停止。");
    }

    /// <summary>试运行结束（后台线程）：回到 UI 线程把输出变量的值列出来。</summary>
    private void OnRunCompleted(SubActionRow row, ActionRunResult result)
    {
        if (Dispatcher.HasShutdownStarted) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsLoaded) return;
            ConfirmDialog.Info(Window.GetWindow(this), "试运行结果", DescribeResult(row, result));
        }));
    }

    private static string DescribeResult(SubActionRow row, ActionRunResult result)
    {
        if (result.Stopped) return $"已停止 · 共 {result.Steps} 步";

        if (!result.Completed)
        {
            string where = string.IsNullOrEmpty(result.FailedStepLabel) ? "" : result.FailedStepLabel + "\n";
            return where + (result.Error ?? "执行失败");
        }

        var lines = new List<string> { $"完成 · {result.Steps} 步 · {result.Elapsed.TotalMilliseconds:0} ms" };
        var outputs = row.Def.Variables.Where(v => v.Direction == VariableDirection.output).ToList();

        if (outputs.Count == 0)
        {
            lines.Add("");
            lines.Add("（这个子动作没有输出变量）");
            return string.Join("\n", lines);
        }

        lines.Add("");
        foreach (var variable in outputs)
        {
            string key = (variable.Key ?? "").Trim();
            string text = result.Variables.TryGetValue(key, out var value) ? SafeText(value) : "（未赋值）";
            lines.Add($"{key} = {text}");
        }
        return string.Join("\n", lines);
    }

    private static string SafeText(Value value)
    {
        try
        {
            string text = value.AsText();
            return text.Length > 300 ? text[..300] + "…" : text;
        }
        catch
        {
            return "(无法显示)";
        }
    }

    private void CopyId_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;
        try { Clipboard.SetText(row.Id); }
        catch (Exception ex) { CrashLog.Write("SubActionLibrary.CopyId", ex); }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRow() is not { } row) return;

        string file = Path.Combine(SubActionStore.DirectoryPath, row.Id + ".json");
        try
        {
            if (File.Exists(file))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else
                OpenFolder();
        }
        catch (Exception ex)
        {
            CrashLog.Write("SubActionLibrary.Reveal", ex);
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolder();

    private void OpenFolder()
    {
        Directory.CreateDirectory(SubActionStore.DirectoryPath);
        Process.Start(new ProcessStartInfo("explorer.exe", SubActionStore.DirectoryPath)
        {
            UseShellExecute = true,
        });
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null)
        {
            if (source is T match) return match;
            source = source is Visual || source is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return null;
    }

    /// <summary>列表行；<see cref="Def"/> 保留完整定义供编辑 / 复制 / 试运行。</summary>
    public sealed class SubActionRow
    {
        private readonly string _search;

        public SubActionRow(SubActionDef def, IReadOnlyList<SubActionReference> references)
        {
            Def = def;
            Id = def.Id;
            Name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;
            Glyph = IconCatalog.GetGlyph(def.Icon);
            Summary = Summarize(def);

            RefText = references.Count == 0 ? "" : $"被 {references.Count} 处引用";
            RefTip = references.Count == 0
                ? "还没有动作调用它"
                : string.Join("\n", references.Select(r => $"{r.ActionName} · {r.Where}"));

            _search = (Id + " " + Name + " " + string.Join(" ", def.Variables.Select(v => v.Key)))
                .ToLowerInvariant();
        }

        public SubActionDef Def { get; }
        public string Id { get; }
        public string Name { get; }
        public string Glyph { get; }
        public string Summary { get; }
        public string RefText { get; }
        public string RefTip { get; }

        /// <summary>名称 / id / 输入变量名，任一处包含筛选词就算命中（大小写不敏感）。</summary>
        public bool Matches(string filter) =>
            _search.Contains(filter.ToLowerInvariant(), StringComparison.Ordinal);

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
