using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using LTools.Native;

namespace LTools.Views;

/// <summary>
/// 从当前系统状态里挑一个窗口 / 进程，避免让用户手填标题、类名或进程名。
/// </summary>
/// <remarks>
/// 窗口列表按 Z 序枚举可见的顶层窗口（跳过 LTools 自己），显示「标题 + 进程 · 类名」；
/// 进程列表按名称聚合，显示实例数与一个主窗口标题。搜索框即时过滤，回车 / 双击直接选用。
/// </remarks>
public partial class SystemPickerDialog : Window
{
    /// <summary>选中的窗口：标题（按包含匹配）、类名（精确匹配）、进程名。</summary>
    public sealed record WindowInfo(string Title, string ClassName, string ProcessName);

    private readonly List<PickerRow> _all;
    private List<PickerRow> _rows = new();

    /// <summary>取消时为 null。</summary>
    public PickerRow? SelectedRow { get; private set; }

    private SystemPickerDialog(string title, string hint, string searchHint, List<PickerRow> rows, Window? owner)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        Title = title;
        HintText.Text = hint;
        SearchBox.ToolTip = searchHint;
        if (owner != null)
        {
            Owner = owner;
            Topmost = owner.Topmost;
        }

        _all = rows;
        ApplyFilter("");
        Loaded += (_, _) => SearchBox.Focus();
    }

    // ============ 对外入口 ============

    /// <summary>挑一个当前打开的窗口；取消返回 null。</summary>
    public static WindowInfo? PickWindow(Window? owner)
    {
        var rows = new List<PickerRow>();
        Win32.EnumWindows((hwnd, _) =>
        {
            if (!Win32.IsWindowVisible(hwnd)) return true;

            int length = Win32.GetWindowTextLength(hwnd);
            if (length <= 0) return true;
            var buffer = new StringBuilder(length + 1);
            Win32.GetWindowText(hwnd, buffer, buffer.Capacity);
            string title = buffer.ToString().Trim();
            if (title.Length == 0) return true;

            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId) return true;   // 别把 LTools 自己的窗口列进来

            var cls = new StringBuilder(256);
            Win32.GetClassName(hwnd, cls, cls.Capacity);
            string className = cls.ToString();
            if (className is "Progman" or "WorkerW") return true;   // 桌面

            string process = ProcessNameOf(pid);
            rows.Add(new PickerRow
            {
                Primary = title,
                Secondary = $"{process} · {className}",
                Value = title,
                Extra = className,
                Search = title + " " + process + " " + className,
            });
            return true;
        }, IntPtr.Zero);

        var dialog = new SystemPickerDialog(
            "选择窗口",
            "选中后会把标题与类名一起填入（标题按「包含」匹配，类名精确匹配；都可再改）。",
            "按窗口标题 / 进程 / 类名筛选",
            rows, owner);
        return dialog.ShowDialog() == true && dialog.SelectedRow is { } row
            ? new WindowInfo(row.Value ?? "", row.Extra ?? "", "")
            : null;
    }

    /// <summary>挑一个正在运行的进程名；取消返回 null。</summary>
    public static string? PickProcess(Window? owner)
    {
        var rows = new List<PickerRow>();
        var snapshot = new Dictionary<string, (int Count, string? Title)>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                string name = process.ProcessName;
                if (string.IsNullOrWhiteSpace(name)) continue;

                snapshot.TryGetValue(name, out var entry);
                string? windowTitle = entry.Title;
                if (windowTitle == null && !string.IsNullOrWhiteSpace(process.MainWindowTitle))
                    windowTitle = process.MainWindowTitle;
                snapshot[name] = (entry.Count + 1, windowTitle);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
            {
                // 进程在枚举过程中退出 / 拒绝访问：跳过
            }
            finally
            {
                process.Dispose();
            }
        }

        foreach (var (name, (count, windowTitle)) in snapshot.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            string secondary = count > 1 ? $"{count} 个实例" : "1 个实例";
            if (windowTitle != null) secondary += " · " + windowTitle;
            rows.Add(new PickerRow
            {
                Primary = name,
                Secondary = secondary,
                Value = name,
                Search = name + " " + (windowTitle ?? ""),
            });
        }

        var dialog = new SystemPickerDialog(
            "选择进程",
            "选中后把进程名（不含 .exe）填入条件；进程没在运行时可手改。",
            "按进程名 / 窗口标题筛选",
            rows, owner);
        return dialog.ShowDialog() == true ? dialog.SelectedRow?.Value : null;
    }

    // ============ 列表 ============

    private void Search_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void ApplyFilter(string keyword)
    {
        string query = (keyword ?? "").Trim();
        _rows = query.Length == 0
            ? new List<PickerRow>(_all)
            : _all.Where(r => r.Search.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        ItemList.ItemsSource = _rows;
        CountText.Text = $"{_rows.Count} 项";
        if (_rows.Count > 0) ItemList.SelectedIndex = 0;
    }

    private void List_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow != null) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not PickerRow row)
        {
            HintText.Text = "请先在列表里选一项。";
            SearchBox.Focus();
            return;
        }

        SelectedRow = row;
        DialogResult = true;
    }

    private static string ProcessNameOf(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "?";
        }
    }

    /// <summary>列表行：主标题 + 次要说明 + 选中后的值。</summary>
    public sealed class PickerRow
    {
        public string Primary { get; init; } = "";
        public string Secondary { get; init; } = "";
        public string? Value { get; init; }
        public string? Extra { get; init; }
        public string Search { get; init; } = "";
    }
}
