using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LTools.Features;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 控件树对话框：把窗口里的控件摊成一份可搜索的缩进列表，挑一个填回 <c>ui.uia</c>。
/// </summary>
/// <remarks>
/// 点选器悬停拿不到的控件（被遮挡、在折叠页签 / 滚动区外、或者要手动挑父级）从这里选。
/// 数据来自同一份 <see cref="ControlTargets.Snapshot"/>；「刷新」重读一次当前窗口。
/// **这个窗口是手写的「标题栏 / 搜索 / <c>*</c> 列表 / 常驻按钮栏」四行**，没套 <c>DialogShell</c>：
/// 它的内容区是 ScrollViewer，列表套进去会变成嵌套滚动。
/// </remarks>
public partial class ControlTreeDialog : Window
{
    private readonly IntPtr _hwnd;
    private readonly string _windowTitle;
    private readonly string _windowClass;
    private readonly ObservableCollection<ControlRow> _rows = new();
    private readonly SearchDebounce _search;
    private ControlTree _tree;
    private bool _refreshing;

    /// <summary>选中的控件（按下「确定」时有效）。</summary>
    public PickedControl? Result { get; private set; }

    private ControlTreeDialog(IntPtr hwnd, ControlTree tree, string windowTitle, string windowClass)
    {
        InitializeComponent();

        _hwnd = hwnd;
        _tree = tree;
        _windowTitle = windowTitle;
        _windowClass = windowClass;
        _search = new SearchDebounce(150, ApplyFilter);

        ControlList.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            Rebuild();
        };
    }

    /// <summary>列出这个窗口的控件让用户挑；取消返回 null。</summary>
    public static PickedControl? Show(Window? owner, IntPtr hwnd, ControlTree tree,
        string windowTitle, string windowClass)
    {
        var dialog = new ControlTreeDialog(hwnd, tree, windowTitle, windowClass)
        {
            Owner = owner,
            // 宿主是置顶的点选遮罩：不置顶会被它整块盖住
            Topmost = owner?.Topmost ?? true,
        };

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    // ============ 列表 ============

    private void Rebuild()
    {
        string window = _windowTitle.Length > 0 ? _windowTitle : _windowClass;
        string note = _tree.Unavailable
            ? _tree.Problem ?? ""
            : _tree.Truncated || _tree.DepthCut
                ? $"控件树被截断（depth {_tree.MaxDepth} / 最多 {_tree.MaxElements} 个），没看到的控件换一个窗口或调大参数"
                : "";
        WindowText.Text = window.Length > 0
            ? (note.Length > 0 ? $"窗口：{window} · {note}" : $"窗口：{window}")
            : note;

        ApplyFilter();
        SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        string selected = (ControlList.SelectedItem as ControlRow)?.Title ?? "";

        _rows.Clear();
        foreach (ControlTarget node in ControlTargets.Filter(_tree.Nodes, SearchBox.Text))
            _rows.Add(new ControlRow(node));

        if (_rows.Count > 0 && selected.Length > 0)
        {
            foreach (ControlRow row in _rows)
            {
                if (row.Title != selected) continue;
                ControlList.SelectedItem = row;
                break;
            }
        }

        ControlList_SelectionChanged(this, null);
        UpdateHint();
    }

    private void UpdateHint()
    {
        int total = _tree.Nodes.Count;
        int shown = _rows.Count;
        string count = SearchBox.Text.Trim().Length > 0 ? $"匹配 {shown} / 共 {total} 个" : $"共 {total} 个控件";

        if (_tree.Unavailable)
            HintText.Text = _tree.Problem ?? "读不到控件";
        else if (shown == 0)
            HintText.Text = $"{count} · 没有匹配的控件";
        else
            HintText.Text = $"{count} · 选一个控件，确定后填进「名称 / AutomationId / 类型 / 窗口类名」";
    }

    private void SearchBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        _search.Signal();

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down || e.Key == Key.Up)
        {
            _search.Flush();   // 防抖还没到点：方向键必须拿到筛选后的列表
            int index = ControlList.SelectedIndex;
            int next = e.Key == Key.Down ? index + 1 : index - 1;
            if (_rows.Count > 0)
                ControlList.SelectedIndex = Math.Clamp(next < 0 ? 0 : next, 0, _rows.Count - 1);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _search.Flush();
            Ok_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void ControlList_SelectionChanged(object? sender, System.Windows.Controls.SelectionChangedEventArgs? e)
    {
        bool can = ControlList.SelectedItem is ControlRow { CanTarget: true };
        OkBtn.IsEnabled = can;

        if (ControlList.SelectedItem is ControlRow row && !row.CanTarget)
            HintText.Text = "这个控件没有名称和 AutomationId，选不了：用「鼠标操作」点坐标，或「文字点击」按文字找";
        else
            UpdateHint();
    }

    private void ControlList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ControlList.SelectedItem is ControlRow { CanTarget: true }) Ok_Click(sender, new RoutedEventArgs());
    }

    // ============ 刷新 / 确定 ============

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshBtn.IsEnabled = false;
        HintText.Text = "重新读取控件树…";

        Task.Run(() => ControlTargets.Snapshot(_hwnd)).ContinueWith(t =>
        {
            if (Dispatcher.HasShutdownStarted) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshing = false;
                RefreshBtn.IsEnabled = true;

                if (t.Status == TaskStatus.RanToCompletion) _tree = t.Result;
                Rebuild();
            }));
        });
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (ControlList.SelectedItem is not ControlRow { CanTarget: true } row) return;

        Result = new PickedControl(row.Node.Name, row.Node.AutomationId, row.Node.ControlType,
            _windowClass, _windowTitle);

        try { DialogResult = true; }
        catch { Close(); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        try { DialogResult = false; }
        catch { Close(); }
    }

    protected override void OnClosed(EventArgs e)
    {
        _search.Cancel();
        base.OnClosed(e);
    }
}

/// <summary>控件树里的一行：缩进由层级决定，文字全部来自纯函数（同一份也给点选器的信息卡用）。</summary>
internal sealed class ControlRow
{
    public ControlRow(ControlTarget node) => Node = node;

    public ControlTarget Node { get; }

    public Thickness IndentMargin => new(Math.Min(Node.Depth, 10) * 14, 0, 0, 0);

    public string ControlType => Node.ControlType;

    public string Title => ControlTargets.TitleOf(Node);

    public string Detail => ControlTargets.DetailOf(Node);

    public bool CanTarget => ControlTargets.CanTarget(Node);
}
