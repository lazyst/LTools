using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>贴图管理页的一行：磁盘上的 <see cref="PinRecord"/> + 已经解码好的缩略图 + 勾选状态。</summary>
internal sealed class PinRow : INotifyPropertyChanged
{
    private bool _selected;
    private BitmapSource? _thumb;

    public PinRow(PinRecord record, bool openNow)
    {
        Record = record;
        OpenNow = openNow;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PinRecord Record { get; }

    public BitmapSource? Thumb => _thumb;

    /// <summary>缩略图按页懒加载（200 条全解码会拖慢打开这一页）。</summary>
    public void SetThumb(BitmapSource? thumb)
    {
        if (ReferenceEquals(_thumb, thumb)) return;
        _thumb = thumb;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumb)));
    }

    /// <summary>记录对应的贴图现在还开在屏幕上（窗口还在）。</summary>
    public bool OpenNow { get; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    /// <summary>上游用「最后关闭时间，没有就创建时间」排序 / 显示。</summary>
    public string TimeText => CaptureCatalog.FormatTime(
        Record.ClosedAt.Length > 0 ? Record.ClosedAt : Record.CreatedAt);

    public string SourceText => Record.SourceText;

    public string StatusText => OpenNow ? "未关闭" : "已关闭";

    public Brush StatusBrush => OpenNow
        ? (Application.Current?.TryFindResource("PrimaryBrush") as Brush ?? Brushes.SeaGreen)
        : (Application.Current?.TryFindResource("SecondaryTextBrush") as Brush ?? Brushes.Gray);

    /// <summary>副标题：分组 · 尺寸（实时贴图另标一句）。</summary>
    public string InfoText
    {
        get
        {
            string text = $"分组：{Record.GroupText} · {Record.SizeText}";
            return Record.Live ? text + " · 实时" : text;
        }
    }

    public string ShowLabel => OpenNow ? "显示" : "贴回屏幕";
}

/// <summary>
/// 贴图管理：贴图历史（<c>pins/</c>）的浏览、贴回、另存、删除与批量操作。
/// </summary>
/// <remarks>
/// <para>
/// 照 snow-shot 的 <c>PinnedWindowManagementPageWidget</c>：标题 + 计数、来源多选、日期范围、
/// 选择栏（全选本页 / 取消选择 / 批量贴到屏幕 / 删除选中）、卡片网格、分页（10 / 20 / 50）。
/// </para>
/// <para>
/// 数据全在 <see cref="PinStore"/>，这个窗口只读列表 + 调它的增删；贴「回」屏幕走
/// <see cref="CapturePinManager.Restore"/>（整条记录复用，不新建记录）。
/// 拼图 / 切图 / 图片编辑是 P6 的事，这一页先不放那些入口（不做死按钮）。
/// </para>
/// </remarks>
public partial class PinManagerWindow : Window
{
    private static PinManagerWindow? _current;

    private readonly List<PinRow> _all = new();
    private readonly List<PinRow> _filtered = new();
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BitmapSource?> _thumbs = new(StringComparer.Ordinal);

    private int _page = 1;
    private int _pageSize = 20;
    private bool _loaded;
    private bool _suppressEvents;

    public PinManagerWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    /// <summary>托盘 / 贴图右键菜单的入口：没开就开一个，开着就激活 / 还原。</summary>
    public static void Toggle()
    {
        if (_current == null)
        {
            var window = new PinManagerWindow();
            _current = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, window)) _current = null;
            };
            WindowFront.Show(window);
            return;
        }

        WindowFront.Bring(_current);
    }

    /// <summary>
    /// 配置锚点：正常运行是 <see cref="ConfigLocator.FindPath"/>；导入备份 / 冒烟会把
    /// <see cref="ConfigStore.ConfigPath"/> 指到临时目录（与 <c>ToolLayoutStore</c> 同一个写法）。
    /// </summary>
    private static string ConfigAnchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        // 每页条数照上游存在 ui.json（0 = 默认 20）
        try
        {
            int saved = UiStore.Read(ConfigAnchor).PinPageSize;
            _pageSize = saved is 10 or 20 or 50 ? saved : 20;
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinManager.LoadPageSize", "读取每页条数失败：" + ex.Message);
        }
        SelectPageSize(_pageSize);

        PinStore.Changed += Store_Changed;
        Reload();
    }

    private void OnClosed(object? sender, EventArgs e) => PinStore.Changed -= Store_Changed;

    private void Store_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Reload));
            return;
        }
        Reload();
    }

    // ============ 列表 ============

    /// <summary>重新读 <c>pins.json</c> 并重建当前页（勾选状态按 Id 保留）。</summary>
    internal void Reload()
    {
        _all.Clear();
        foreach (var record in PinStore.Load())
        {
            bool open = CapturePinManager.FindByRecord(record.Id) != null;
            _all.Add(new PinRow(record, open)
            {
                Selected = _selected.Contains(record.Id),
            });
        }

        // 记录被删了 / 被保留策略剪掉：勾选跟着失效
        _selected.RemoveWhere(id => _all.All(row => row.Record.Id != id));

        ApplyFilter(resetPage: false);
    }

    private BitmapSource? ThumbFor(string id)
    {
        if (_thumbs.TryGetValue(id, out var cached)) return cached;
        var thumb = PinStore.LoadThumb(id, 260);
        _thumbs[id] = thumb;
        return thumb;
    }

    private void ApplyFilter(bool resetPage)
    {
        _filtered.Clear();
        DateTime? start = StartDate?.SelectedDate;
        DateTime? end = EndDate?.SelectedDate;
        foreach (var row in _all)
            if (PinStore.Matches(row.Record, _sources, start, end)) _filtered.Add(row);

        if (resetPage) _page = 1;
        _page = Math.Clamp(_page, 1, PinStore.PageCount(_filtered.Count, _pageSize));
        RenderPage();
        UpdateHeader();
        UpdateSelectionBar();
    }

    private void RenderPage()
    {
        var (first, last) = PinStore.PageRange(_page, _pageSize, _filtered.Count);
        var rows = _filtered.Skip(first).Take(Math.Max(0, last - first)).ToList();
        foreach (var row in rows) row.SetThumb(ThumbFor(row.Record.Id));   // 只解码当前页
        CardList.ItemsSource = rows;

        bool empty = _filtered.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = _all.Count == 0 ? "还没有贴图" : "没有匹配的贴图";
        EmptyDescription.Text = _all.Count == 0
            ? "贴到屏幕上的图会出现在这里。"
            : "换个来源或日期范围再试试。";

        PageInfo.Text = _filtered.Count == 0
            ? "0-0 / 共 0 条"
            : $"{first + 1}-{last} / 共 {_filtered.Count} 条";

        PrevPageBtn.IsEnabled = _page > 1;
        NextPageBtn.IsEnabled = _page < PinStore.PageCount(_filtered.Count, _pageSize);
        CardScroll.ScrollToTop();
    }

    private void UpdateHeader()
    {
        CountText.Text = $"{_all.Count} 张贴图";
        DeleteAllBtn.IsEnabled = _all.Count > 0;
        UpdateSourceButton();
    }

    private void UpdateSourceButton()
    {
        if (SourceFilterBtn == null) return;
        SourceFilterBtn.Content = _sources.Count switch
        {
            0 => "全部来源",
            1 => "来源：" + PinStore.SourceLabel(_sources.First()),
            _ => $"来源：{PinStore.SourceLabel(_sources.OrderBy(x => x).First())} +{_sources.Count - 1}",
        };
    }

    private void UpdateSelectionBar()
    {
        if (SelectionBar == null) return;

        int count = _selected.Count;
        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = $"已选 {count} 项";

        // 批量贴回要求同一分组（照上游 selectedGroupId()）：混组时置灰并说明原因
        bool sameGroup = SelectedRows().Select(row => row.Record.Group).Distinct().Count() <= 1;
        BatchPinBtn.IsEnabled = count > 0 && sameGroup;
        BatchPinBtn.ToolTip = sameGroup
            ? "把选中的贴图重新贴回屏幕（按记录里的位置与尺寸）"
            : "选中的贴图不在同一分组：请只选同一组再批量贴回";
        DeleteSelectedBtn.IsEnabled = count > 0;
    }

    private List<PinRow> SelectedRows() =>
        _all.Where(row => _selected.Contains(row.Record.Id)).ToList();

    private void SetSelected(PinRow row, bool on)
    {
        row.Selected = on;
        if (on) _selected.Add(row.Record.Id);
        else _selected.Remove(row.Record.Id);
        UpdateSelectionBar();
    }

    // ============ 卡片 ============

    private static PinRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as PinRow;

    private void CardSelect_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (_selected.Contains(row.Record.Id) == row.Selected) return;   // 已经同步过
        SetSelected(row, row.Selected);
    }

    private void CardShow_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) ShowRecord(row.Record);
    }

    /// <summary>贴回屏幕 / 已经开着就把它顶到最上面（分组过滤藏起来的先露出来）。</summary>
    private void ShowRecord(PinRecord record)
    {
        if (CapturePinManager.FindByRecord(record.Id) is { } open)
        {
            if (!open.IsVisible) open.Show();
            open.RaiseToTop();
            PinStacking.Promote(open);
            return;
        }

        if (CapturePinManager.Restore(record) == null)
        {
            ToastService.Show("图片文件不见了，没法贴回屏幕", "贴图管理", 2200, ToastKind.error);
            return;
        }

        Reload();
    }

    private void CardDelete_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        if (!ConfirmDialog.Confirm(this, "删除贴图记录",
                "这条记录与它的图片会被删除，屏幕上的这张贴图也会关掉，且不可撤销。",
                danger: true, okText: "删除"))
        {
            return;
        }

        CapturePinManager.CloseRecord(row.Record.Id);
        PinStore.Remove(row.Record.Id);
        _selected.Remove(row.Record.Id);
        Reload();
    }

    private void CardMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host || RowOf(sender) is not { } row) return;

        var menu = new ContextMenu { PlacementTarget = host };

        var save = new MenuItem { Header = "另存为…" };
        save.Click += (_, _) =>
        {
            var image = PinStore.LoadAsset(row.Record.Id);
            if (image == null)
            {
                ToastService.Show("图片文件不见了", "贴图管理", 1800, ToastKind.error);
                return;
            }
            CaptureSave.TrySave(this, image);
        };
        menu.Items.Add(save);

        var copyPath = new MenuItem { Header = "复制图片路径" };
        copyPath.Click += (_, _) =>
        {
            string path = PinStore.AssetPath(row.Record.Id);
            if (!File.Exists(path))
            {
                ToastService.Show("图片文件不见了", "贴图管理", 1800, ToastKind.error);
                return;
            }
            if (NativeClipboard.SetText(path)) ToastService.Show("已复制图片路径", "贴图管理", 1400);
            else ToastService.Show("复制失败（剪贴板被占用）", "贴图管理", 0, ToastKind.error);
        };
        menu.Items.Add(copyPath);

        var reveal = new MenuItem { Header = "在文件夹中显示" };
        reveal.Click += (_, _) =>
        {
            string path = PinStore.AssetPath(row.Record.Id);
            if (!File.Exists(path))
            {
                ToastService.Show("图片文件不见了", "贴图管理", 1800, ToastKind.error);
                return;
            }
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); }
            catch (Exception ex) { AppLog.Warn("PinManager.Reveal", "打开资源管理器失败：" + ex.Message); }
        };
        menu.Items.Add(reveal);

        if (CapturePinManager.FindByRecord(row.Record.Id) != null)
        {
            menu.Items.Add(new Separator());
            var close = new MenuItem { Header = "关闭屏幕上的贴图" };
            close.Click += (_, _) =>
            {
                CapturePinManager.CloseRecord(row.Record.Id);
                Reload();
            };
            menu.Items.Add(close);
        }

        menu.IsOpen = true;
    }

    // ============ 选择栏 / 批量 ============

    private void SelectPage_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in CardList.ItemsSource as IEnumerable<PinRow> ?? Array.Empty<PinRow>())
            SetSelected(item, true);

        // 勾选框是 TwoWay 绑定：ItemsSource 里的对象已经改过，界面自己会跟着走
        UpdateSelectionBar();
    }

    private void Deselect_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _all) row.Selected = false;
        _selected.Clear();
        UpdateSelectionBar();
    }

    private void BatchPin_Click(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;

        // 整批交给协调器：中途不观测、结束统一排一次（已开着的那几张直接复用）
        int opened = CapturePinManager.RestoreBatch(rows.Select(row => row.Record).ToArray());
        if (opened == 0 && rows.All(row => row.Record.ClosedAt.Length == 0))
        {
            ToastService.Show("选中的贴图都已经在屏幕上了", "贴图管理", 2000);
        }
        else
        {
            ToastService.Show($"已贴回 {rows.Count} 张", "贴图管理", 1800);
        }

        Reload();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        int count = _selected.Count;
        if (count == 0) return;
        if (!ConfirmDialog.Confirm(this, "删除选中的贴图",
                $"选中的 {count} 条记录与图片会被删除，屏幕上的对应贴图也会关掉，且不可撤销。",
                danger: true, okText: "删除"))
        {
            return;
        }

        foreach (var row in SelectedRows())
        {
            CapturePinManager.CloseRecord(row.Record.Id);
            PinStore.Remove(row.Record.Id);
        }

        _selected.Clear();
        Reload();
    }

    // ============ 头部 / 筛选（来源多选 + 日期范围） ============

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _thumbs.Clear();
        Reload();
    }

    /// <summary>贴图文字的默认样式（只影响新贴的文字，已贴的按记录里的样式重画）。</summary>
    private void TextStyle_Click(object sender, RoutedEventArgs e) => PinTextStyleWindow.Show(this);

    private void DeleteAll_Click(object sender, RoutedEventArgs e)
    {
        int count = _all.Count;
        if (count == 0) return;
        if (!ConfirmDialog.Confirm(this, "删除全部贴图",
                $"全部 {count} 条贴图记录与图片会被删除，屏幕上的贴图也会关掉，且不可撤销。",
                danger: true, okText: "删除"))
        {
            return;
        }

        foreach (var row in _all) CapturePinManager.CloseRecord(row.Record.Id);
        PinStore.Clear();
        _selected.Clear();
        _thumbs.Clear();
        Reload();
    }

    private void SourceFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement host) return;

        var menu = new ContextMenu { PlacementTarget = host };
        menu.Items.Add(FilterItem("全部来源", checkedOn: _sources.Count == 0, key: ""));
        menu.Items.Add(new Separator());
        foreach (var (key, label) in PinStore.SourceOptions())
            menu.Items.Add(FilterItem(label, checkedOn: _sources.Contains(key), key: key));

        menu.IsOpen = true;
    }

    private MenuItem FilterItem(string label, bool checkedOn, string key)
    {
        var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = checkedOn, StaysOpenOnClick = true };
        item.Click += (_, _) =>
        {
            if (key.Length == 0)
            {
                _sources.Clear();
            }
            else if (!_sources.Remove(key))
            {
                _sources.Add(key);
            }

            _selected.Clear();
            foreach (var row in _all) row.Selected = false;
            UpdateSourceButton();
            ApplyFilter(resetPage: true);
        };
        return item;
    }

    private void Date_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressEvents) return;
        _selected.Clear();
        foreach (var row in _all) row.Selected = false;
        ApplyFilter(resetPage: true);
    }

    private void ClearDate_Click(object sender, RoutedEventArgs e)
    {
        _suppressEvents = true;
        StartDate.SelectedDate = null;
        EndDate.SelectedDate = null;
        _suppressEvents = false;
        ApplyFilter(resetPage: true);
    }

    // ============ 分页 ============

    private void SelectPageSize(int size)
    {
        if (PageSizeBox == null) return;
        for (int i = 0; i < PageSizeBox.Items.Count; i++)
        {
            if (PageSizeBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == size.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                _suppressEvents = true;
                PageSizeBox.SelectedIndex = i;
                _suppressEvents = false;
                return;
            }
        }
        PageSizeBox.SelectedIndex = 1;
    }

    private void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppressEvents) return;
        if (PageSizeBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out int size) || size is not (10 or 20 or 50)) return;

        _pageSize = size;
        _page = 1;
        RenderPage();
        UpdateSelectionBar();

        try { UiStore.Modify(ConfigAnchor, ui => ui.PinPageSize = size); }
        catch (Exception ex) { AppLog.Warn("PinManager.SavePageSize", "保存每页条数失败：" + ex.Message); }
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page <= 1) return;
        _page--;
        RenderPage();
    }

    private void NextPage_Click(object sender, RoutedEventArgs e)
    {
        if (_page >= PinStore.PageCount(_filtered.Count, _pageSize)) return;
        _page++;
        RenderPage();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
