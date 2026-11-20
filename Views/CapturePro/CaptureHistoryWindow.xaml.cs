using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>
/// 采集历史（照 Snow Shot 的 <c>screenshothistorypagewidget</c>）：页头（标题 + 计数 + 清空 / 刷新 /
/// 打开目录）→ 筛选（来源多选 + 日期范围 + 搜索）→ 选择栏（有选中才出现）→ 卡片列表 → 分页。
/// </summary>
/// <remarks>
/// <para>
/// 卡片是「左详情 + 右 260 × 156 预览」的宽布局（上游 <c>kPreviewWidth</c> / <c>kPreviewHeight</c>）；
/// 勾选框是批量操作的入口，不是行选中。双击卡片 = 编辑，中键 = 复制，点预览 = 看大图。
/// </para>
/// <para>
/// 筛选 / 分页全是 <see cref="CaptureHistoryQuery"/> / <see cref="CaptureHistoryPaging"/> 的纯函数，
/// 窗口只负责搬控件状态；数据源是 <see cref="CaptureCatalog"/>（<c>captures/catalog.json</c> + 图片），
/// 这个窗口只读它，写动作都走目录自己的方法。
/// </para>
/// </remarks>
public partial class CaptureHistoryWindow : Window
{
    private static CaptureHistoryWindow? _current;

    private readonly ObservableCollection<CaptureHistoryRow> _rows = new();
    private readonly List<CaptureHistoryRow> _pageRows = new();
    private readonly Dictionary<string, BitmapSource> _thumbs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _bytes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistorySlide>> _slides = new(StringComparer.Ordinal);

    /// <summary>勾选中的记录 id（跨页保留，照上游 <c>m_selectedRecordIds</c>）。</summary>
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);

    private readonly HashSet<string> _sources = new(StringComparer.Ordinal);
    private readonly SearchDebounce _debounce;

    private List<CaptureEntry> _all = new();
    private List<CaptureEntry> _filtered = new();
    private int _page = 1;
    private int _pageSize = CaptureHistoryPaging.DefaultPageSize;
    private bool _loaded;

    /// <summary>程序化改控件（回填日期 / 勾选 / 页码）时别再触发一轮筛选。</summary>
    private bool _suppress;

    public CaptureHistoryWindow()
    {
        InitializeComponent();

        CardList.ItemsSource = _rows;
        _debounce = new SearchDebounce(150, () => ApplyFilter(resetPage: true));

        BuildSourceMenu();

        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>托盘入口：没开就开一个，开着就激活 / 还原。</summary>
    public static void Toggle()
    {
        if (_current == null)
        {
            var window = new CaptureHistoryWindow();
            _current = window;
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_current, window)) _current = null;
            };
            window.Show();
            window.Activate();
            return;
        }

        if (_current.WindowState == WindowState.Minimized)
            _current.WindowState = WindowState.Normal;

        _current.Activate();
        _current.Topmost = true;
        _current.Topmost = false;
    }

    /// <summary>
    /// 配置锚点：正常运行是 <see cref="ConfigLocator.FindPath"/>；导入备份 / 冒烟会把
    /// <see cref="ConfigStore.ConfigPath"/> 指到临时目录（与 <see cref="PinManagerWindow"/> 同一个写法）。
    /// </summary>
    private static string ConfigAnchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        // 每页条数照上游存在 ui.json（0 / 认不出 = 默认 20）
        try
        {
            _pageSize = CaptureHistoryPaging.NormalizePageSize(
                UiStore.Read(ConfigAnchor).CaptureHistoryPageSize);
        }
        catch (Exception ex)
        {
            _pageSize = CaptureHistoryPaging.DefaultPageSize;
            AppLog.Warn("CaptureHistory.LoadPageSize", "读取每页条数失败：" + ex.Message);
        }
        SelectPageSize(_pageSize);

        CaptureCatalog.Changed += Catalog_Changed;
        Reload();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CaptureCatalog.Changed -= Catalog_Changed;
        _debounce.Cancel();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 在输入框里按键是打字 / 删字符 / 全选文本，不能当成列表快捷键
        if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is DatePicker) return;

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && _selected.Count > 0)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SelectCurrentPage();
            e.Handled = true;
        }
    }

    private void Catalog_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(Reload));
            return;
        }
        Reload();
    }

    // ============ 列表 ============

    /// <summary>重新读磁盘（保留筛选、页码与勾选；失效的勾选自动丢掉）。</summary>
    internal void Reload()
    {
        _all = CaptureCatalog.Load();
        ApplyFilter(resetPage: false);
    }

    /// <summary>按当前筛选条件重建本页（<paramref name="resetPage"/> = 换筛选时回第 1 页并清空勾选）。</summary>
    private void ApplyFilter(bool resetPage)
    {
        if (!_loaded) return;

        if (resetPage)
        {
            _page = 1;
            ClearSelection(repaint: false);
        }

        var query = new CaptureHistoryQuery
        {
            Sources = _sources.ToArray(),
            Range = CurrentRange(),
            Text = SearchBox.Text ?? "",
        };

        _filtered = query.Apply(_all);

        // 记录被删掉（别处清空 / 超过保留期）后，勾选里可能挂着不存在的 id
        var live = new HashSet<string>(_all.Select(entry => entry.Id), StringComparer.Ordinal);
        _selected.RemoveWhere(id => !live.Contains(id));

        BuildPage();
        UpdateHeader();
        UpdateEmpty();
        UpdatePager();
        UpdateSelectionBar();
        UpdateDateState();
    }

    private CaptureDateRange CurrentRange()
    {
        if (!_loaded) return CaptureDateRange.Empty;
        return new CaptureDateRange(DateStart.SelectedDate, DateEnd.SelectedDate).Ordered();
    }

    private void BuildPage()
    {
        _page = CaptureHistoryPaging.ClampPage(_page, _filtered.Count, _pageSize);
        var (first, last) = CaptureHistoryPaging.Range(_page, _filtered.Count, _pageSize);

        _pageRows.Clear();
        _rows.Clear();

        for (int index = first; index < last; index++)
        {
            var entry = _filtered[index];
            var thumb = ThumbFor(entry);
            var row = new CaptureHistoryRow(entry, thumb, BytesOf(entry), SlidesFor(entry, thumb))
            {
                IsSelected = _selected.Contains(entry.Id),
            };

            _pageRows.Add(row);
            _rows.Add(row);
        }
    }

    /// <summary>
    /// 这一条预览的轮播（成品图 + 每块屏幕）：卡片翻页与查看器共用，按条目缓存。
    /// 缩略图解码宽度给 520（260 的 2 倍，125% 屏上也清楚）。
    /// </summary>
    private List<HistorySlide> SlidesFor(CaptureEntry entry, BitmapSource? thumb)
    {
        if (_slides.TryGetValue(entry.Id, out var cached)) return cached;

        var slides = HistorySlides.Load(entry, 520, thumb);
        _slides[entry.Id] = slides;
        return slides;
    }

    private BitmapSource? ThumbFor(CaptureEntry entry)
    {
        if (_thumbs.TryGetValue(entry.Id, out var cached)) return cached;

        var thumb = CaptureCatalog.LoadImage(CaptureCatalog.ThumbPath(entry.Id), CaptureCatalog.ThumbSide);
        if (thumb != null) _thumbs[entry.Id] = thumb;
        return thumb;
    }

    /// <summary>成品文件的字节数（录屏算视频、其余算成品图）；读不到按 0（卡片只写位置）。</summary>
    private long BytesOf(CaptureEntry entry)
    {
        if (_bytes.TryGetValue(entry.Id, out long cached)) return cached;

        long bytes = 0;
        try
        {
            string path = entry.IsRecord
                ? CaptureCatalog.FilePath(entry)
                : CaptureCatalog.AssetPath(entry.Id);

            if (path.Length > 0 && File.Exists(path)) bytes = new FileInfo(path).Length;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureHistory.Bytes", "读文件大小失败：" + ex.Message);
        }

        _bytes[entry.Id] = bytes;
        return bytes;
    }

    private void UpdateHeader()
    {
        CountText.Text = _filtered.Count == _all.Count
            ? $"共 {_all.Count} 条 · 最多保留 {CaptureCatalog.MaxEntries} 条 / {CaptureCatalog.MaxAgeDays} 天"
            : $"共 {_all.Count} 条 · 筛选出 {_filtered.Count} 条";

        ClearAllBtn.IsEnabled = _all.Count > 0;
    }

    private void UpdateEmpty()
    {
        if (_filtered.Count > 0)
        {
            EmptyPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyPanel.Visibility = Visibility.Visible;
        bool empty = _all.Count == 0;
        EmptyTitle.Text = empty ? "还没有截图历史" : "没有符合条件的截图";
        EmptyText.Text = empty
            ? "复制或贴到屏幕的截图会出现在这里"
            : "换一个来源或日期范围看看";
    }

    // ============ 分页 ============

    private void UpdatePager()
    {
        PageRangeText.Text = CaptureHistoryPaging.RangeText(_page, _filtered.Count, _pageSize);

        int pages = CaptureHistoryPaging.PageCount(_filtered.Count, _pageSize);
        PrevBtn.IsEnabled = _page > 1;
        NextBtn.IsEnabled = _page < pages;

        PageHost.Children.Clear();
        foreach (int page in CaptureHistoryPaging.PageNumbers(_page, pages))
        {
            var button = new Button
            {
                Content = page.ToString(CultureInfo.InvariantCulture),
                Tag = page,
                Width = 28,
                Height = 28,
                Margin = new Thickness(2, 0, 2, 0),
                Padding = new Thickness(0),
                MinWidth = 0,
                MinHeight = 0,
                FontSize = 12,
                Style = (Style)FindResource(page == _page ? "BtnPrimary" : "BtnGhost"),
            };

            button.Click += (_, _) => GoToPage(page);
            PageHost.Children.Add(button);
        }
    }

    private void GoToPage(int page)
    {
        int target = CaptureHistoryPaging.ClampPage(page, _filtered.Count, _pageSize);
        if (target == _page) return;

        _page = target;
        BuildPage();
        UpdatePager();
        UpdateSelectionBar();
        HistoryScroll.ScrollToTop();
    }

    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(_page - 1);

    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(_page + 1);

    private void SelectPageSize(int size)
    {
        int normalized = CaptureHistoryPaging.NormalizePageSize(size);
        _suppress = true;

        foreach (var item in PageSizeBox.Items.OfType<ComboBoxItem>())
        {
            if (int.TryParse(item.Tag?.ToString(), out int value) && value == normalized)
            {
                PageSizeBox.SelectedItem = item;
                break;
            }
        }

        _suppress = false;
    }

    private void PageSize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (PageSizeBox.SelectedItem is not ComboBoxItem item) return;
        if (!int.TryParse(item.Tag?.ToString(), out int size)) return;

        _pageSize = CaptureHistoryPaging.NormalizePageSize(size);
        _page = 1;
        BuildPage();
        UpdatePager();
        UpdateSelectionBar();

        try { UiStore.Modify(ConfigAnchor, ui => ui.CaptureHistoryPageSize = _pageSize); }
        catch (Exception ex) { AppLog.Warn("CaptureHistory.SavePageSize", "保存每页条数失败：" + ex.Message); }
    }

    // ============ 筛选 ============

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _debounce.Signal();
    }

    private void Date_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        ApplyFilter(resetPage: true);
    }

    private void ClearDate_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        DateStart.SelectedDate = null;
        DateEnd.SelectedDate = null;
        _suppress = false;

        ApplyFilter(resetPage: true);
    }

    private void UpdateDateState() =>
        ClearDateBtn.Visibility = CurrentRange().IsEmpty ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>来源筛选弹层：第一行「全部来源」，下面一条一个勾选框（照上游 <c>AdSelect</c> 多选）。</summary>
    private void BuildSourceMenu()
    {
        SourceListHost.Children.Clear();

        var all = new Button
        {
            Content = "全部来源",
            Style = (Style)FindResource("BtnGhost"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 4),
        };

        all.Click += (_, _) =>
        {
            _sources.Clear();
            SyncSourceChecks();
            UpdateSourceFilterText();
            ApplyFilter(resetPage: true);
        };

        SourceListHost.Children.Add(all);

        foreach (var (key, label) in CaptureHistorySource.All)
        {
            var check = new CheckBox
            {
                Content = label,
                Tag = key,
                Margin = new Thickness(4, 3, 4, 3),
            };

            check.Checked += SourceCheck_Changed;
            check.Unchecked += SourceCheck_Changed;
            SourceListHost.Children.Add(check);
        }
    }

    private void SourceFilter_Click(object sender, RoutedEventArgs e) =>
        SourcePopup.IsOpen = SourceFilterBtn.IsChecked == true;

    private void SourceCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        _sources.Clear();
        foreach (var check in SourceListHost.Children.OfType<CheckBox>())
        {
            if (check.IsChecked == true && check.Tag is string key) _sources.Add(key);
        }

        UpdateSourceFilterText();
        ApplyFilter(resetPage: true);
    }

    private void SyncSourceChecks()
    {
        _suppress = true;
        foreach (var check in SourceListHost.Children.OfType<CheckBox>())
        {
            check.IsChecked = check.Tag is string key && _sources.Contains(key);
        }
        _suppress = false;
    }

    private void UpdateSourceFilterText()
    {
        SourceFilterText.Text = _sources.Count switch
        {
            0 => "全部来源",
            1 => CaptureHistorySource.LabelOf(_sources.First()),
            _ => $"已选 {_sources.Count} 项",
        };

        SourceFilterBtn.ToolTip = _sources.Count == 0
            ? "按来源筛选（可多选）"
            : "已选：" + string.Join("、", _sources.Select(CaptureHistorySource.LabelOf)) + "（点开可改）";
    }

    // ============ 勾选 ============

    private void RowCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.DataContext is not CaptureHistoryRow row) return;

        if (box.IsChecked == true) _selected.Add(row.Entry.Id);
        else _selected.Remove(row.Entry.Id);

        UpdateSelectionBar();
    }

    private void SelectCurrentPage()
    {
        foreach (var row in _pageRows)
        {
            _selected.Add(row.Entry.Id);
            row.IsSelected = true;
        }
        UpdateSelectionBar();
    }

    private void Deselect_Click(object sender, RoutedEventArgs e) => ClearSelection(repaint: true);

    private void ClearSelection(bool repaint)
    {
        _selected.Clear();
        if (repaint)
        {
            foreach (var row in _pageRows) row.IsSelected = false;
        }
        UpdateSelectionBar();
    }

    private void UpdateSelectionBar()
    {
        int count = _selected.Count;

        SelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionText.Text = $"已选 {count} 项";
        DeleteSelectedBtn.IsEnabled = count > 0;
        BatchBtn.IsEnabled = count > 0;
        DeselectBtn.IsEnabled = count > 0;

        SelectPageBtn.IsEnabled = !CaptureHistoryPaging.AllSelected(
            _pageRows.Select(row => row.Entry.Id), _selected);
    }

    /// <summary>当前页的 id（批量动作按本页勾选顺序取记录）。</summary>
    private List<string> PageIds() => _pageRows.Select(row => row.Entry.Id).ToList();
}
