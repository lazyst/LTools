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

    /// <summary>
    /// 把一次选择结果追加成名单的一行（去重、保留已有内容）。
    /// 供触发规则「作用范围」、快捷键「生效范围 / 全局默认名单」共用，别各写一份。
    /// </summary>
    public static void AppendLine(System.Windows.Controls.TextBox box, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var lines = box.Text.Replace("\r\n", "\n").Split('\n')
            .Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        if (!lines.Contains(value, StringComparer.OrdinalIgnoreCase)) lines.Add(value);
        box.Text = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// 挑一个窗口：默认**点击窗口选取**（边缘渐变高亮，见 <see cref="WindowPickOverlay"/>）；
    /// 用户点「从列表选…」时才退回下面的可搜索列表。取消返回 null。
    /// </summary>
    public static WindowInfo? PickWindow(Window? owner)
    {
        var (info, wantsList) = WindowPickOverlay.Pick(owner);
        if (info != null) return info;
        return wantsList ? PickWindowFromList(owner) : null;
    }

    /// <summary>原「从列表挑窗口」实现：点选器的次要入口（无窗口 / 精确挑窗口时用）。</summary>
    private static WindowInfo? PickWindowFromList(Window? owner)
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
            "选中后按窗口标题填入（按「包含」匹配）；要更严格时可再填窗口类名。",
            "按窗口标题 / 进程 / 类名筛选",
            rows, owner);
        return dialog.ShowDialog() == true && dialog.SelectedRow is { } row
            ? new WindowInfo(row.Value ?? "", row.Extra ?? "", "")
            : null;
    }

    /// <summary>
    /// 挑一个进程名：默认点击窗口选取，用选中窗口的 PID 解析进程名；
    /// 没有可见窗口的后台进程走「从列表选…」或手填。取消返回 null。
    /// </summary>
    public static string? PickProcess(Window? owner)
    {
        var (info, wantsList) = WindowPickOverlay.Pick(owner);
        if (info != null) return string.IsNullOrWhiteSpace(info.ProcessName) ? null : info.ProcessName;
        return wantsList ? PickProcessFromList(owner) : null;
    }

    /// <summary>原「从列表挑进程」实现：点选器的次要入口（后台进程时用）。</summary>
    private static string? PickProcessFromList(Window? owner)
    {
        // 先枚举一次可见窗口，得到「进程 → 主窗口标题」。
        // 不要逐个 Process.MainWindowTitle：那会对每个进程都做一次窗口枚举，
        // 遇到无响应的窗口时还可能卡住 UI 线程（表现为「打开选择器后整机很卡」）。
        var titles = WindowTitlesByProcess();

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
                if (windowTitle == null && titles.TryGetValue((uint)process.Id, out string? title))
                    windowTitle = title;
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

    /// <summary>当前可见顶层窗口的标题（按进程 Id 取第一条）。</summary>
    private static Dictionary<uint, string> WindowTitlesByProcess()
    {
        var map = new Dictionary<uint, string>();
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
            map.TryAdd(pid, title);
            return true;
        }, IntPtr.Zero);
        return map;
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
