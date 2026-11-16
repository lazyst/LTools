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
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features.CapturePro;
using LTools.Native;
using LTools.Views.Controls;

namespace LTools.Views.CapturePro;

/// <summary>列表里的一行：磁盘上的 <see cref="CaptureEntry"/> + 已经解码好的缩略图。</summary>
internal sealed class CaptureRow
{
    public CaptureRow(CaptureEntry entry, BitmapSource? thumbnail)
    {
        Entry = entry;
        Thumb = thumbnail;
    }

    public CaptureEntry Entry { get; }

    public BitmapSource? Thumb { get; }

    public string Title => $"{Entry.KindLabel} · {Entry.TimeText}";

    /// <summary>录屏第一行写时长与尺寸（它没有「完成动作」那么要紧），其它写尺寸 + 完成动作。</summary>
    public string Info => Entry.IsRecord
        ? $"{Entry.DurationText} · {Entry.SizeText}"
        : $"{Entry.SizeText} · {Entry.SourceText}";

    public string Summary => CaptureCatalog.SummaryOf(Entry);
}

/// <summary>
/// 采集历史中心：截图 / 识别的成品图与识别文字，左列表 + 右预览。
/// </summary>
/// <remarks>
/// <para>
/// 数据全在 <see cref="CaptureCatalog"/>（<c>captures/catalog.json</c> + 图片）；
/// 这个窗口只负责浏览、复制、另存、删除，不往磁盘写别的东西。
/// </para>
/// <para>
/// 识别文字是异步回填的：识别结果窗算完会 <see cref="CaptureCatalog.SetText"/>，
/// 窗口订阅 <see cref="CaptureCatalog.Changed"/> 自己刷新，用户不必手动点「刷新」。
/// </para>
/// </remarks>
public partial class CaptureHistoryWindow : Window
{
    private static CaptureHistoryWindow? _current;

    private readonly ObservableCollection<CaptureRow> _rows = new();
    private readonly Dictionary<string, BitmapSource> _thumbs = new(StringComparer.Ordinal);
    private readonly SearchDebounce _debounce;

    private List<CaptureRow> _all = new();
    private string _kind = "all";
    private bool _loaded;

    public CaptureHistoryWindow()
    {
        InitializeComponent();

        HistoryList.ItemsSource = _rows;
        _debounce = new SearchDebounce(150, ApplyFilter);

        Loaded += OnLoaded;
        Closed += OnClosed;
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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return;
        _loaded = true;

        CaptureCatalog.Changed += Catalog_Changed;
        Reload();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        CaptureCatalog.Changed -= Catalog_Changed;
        _debounce.Cancel();
    }

    private void Catalog_Changed()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => Reload()));
            return;
        }
        Reload();
    }

    // ============ 列表 ============

    /// <summary>重新读磁盘并重建列表（保留当前选中项）。</summary>
    internal void Reload(string? selectId = null)
    {
        selectId ??= SelectedId();

        _all = CaptureCatalog.Load()
            .Select(entry => new CaptureRow(entry, ThumbFor(entry)))
            .ToList();

        ApplyFilter(selectId);
    }

    private BitmapSource? ThumbFor(CaptureEntry entry)
    {
        if (_thumbs.TryGetValue(entry.Id, out var cached)) return cached;

        var thumb = CaptureCatalog.LoadImage(CaptureCatalog.ThumbPath(entry.Id), CaptureCatalog.ThumbSide);
        if (thumb != null) _thumbs[entry.Id] = thumb;
        return thumb;
    }

    private void ApplyFilter() => ApplyFilter(SelectedId());

    private void ApplyFilter(string? selectId)
    {
        string query = SearchBox?.Text?.Trim() ?? "";

        var filtered = _all
            .Where(row => KindMatches(row.Entry) && Matches(row.Entry, query))
            .ToList();

        _rows.Clear();
        foreach (var row in filtered) _rows.Add(row);

        StatusText.Text = query.Length > 0 || _kind != "all"
            ? $"显示 {filtered.Count} / 共 {_all.Count} 条"
            : $"共 {_all.Count} 条 · 最多保留 {CaptureCatalog.MaxEntries} 条 / {CaptureCatalog.MaxAgeDays} 天";

        EmptyHint.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyHint.Text = _all.Count == 0
            ? "还没有采集记录。截图、识别、录屏完成后，这里会出现成品图 / 文字 / 视频。"
            : "没有符合条件的记录。";

        var target = selectId == null ? null : filtered.FirstOrDefault(r => r.Entry.Id == selectId);
        HistoryList.SelectedItem = target ?? filtered.FirstOrDefault();

        if (filtered.Count > 0) HistoryList.ScrollIntoView(filtered[0]);
    }

    private bool KindMatches(CaptureEntry entry) =>
        _kind == "all" || entry.Kind == _kind;

    private static bool Matches(CaptureEntry entry, string query)
    {
        if (query.Length == 0) return true;

        return entry.Text.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.KindLabel.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.TimeText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private CaptureRow? SelectedRow => HistoryList.SelectedItem as CaptureRow;

    private string? SelectedId() => SelectedRow?.Entry.Id;

    // ============ 事件 ============

    private void Tab_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button) return;
        _kind = button.Tag as string ?? "all";

        // XAML 解析期间 IsChecked="True" 会先触发一次，那时列表还没建好
        if (HistoryList == null || !_loaded) return;
        ApplyFilter();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loaded) return;
        _debounce.Signal();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var row = SelectedRow;
        bool has = row != null;
        bool record = has && row!.Entry.IsRecord;

        CopyImageBtn.IsEnabled = has && !record;
        CopyImageBtn.Visibility = record ? Visibility.Collapsed : Visibility.Visible;
        PlayBtn.IsEnabled = record;
        PlayBtn.Visibility = record ? Visibility.Visible : Visibility.Collapsed;
        SaveAsBtn.IsEnabled = has;
        DeleteBtn.IsEnabled = has;
        CopyTextBtn.IsEnabled = has && !string.IsNullOrEmpty(row!.Entry.Text);
        CopyTextBtn.Visibility = record ? Visibility.Collapsed : Visibility.Visible;

        TextPanel.Visibility = has && row!.Entry.IsRecognize ? Visibility.Visible : Visibility.Collapsed;
        TextPreview.Text = has ? row!.Entry.Text : "";

        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;

        if (!has)
        {
            PreviewEmpty.Text = "选择左边一条查看大图";
            PreviewEmpty.Visibility = Visibility.Visible;
            PreviewInfo.Text = "";
            return;
        }

        PreviewInfo.Text = record
            ? $"{row!.Entry.KindLabel} · {row.Entry.TimeText} · {row.Entry.DurationText}"
              + $" · {row.Entry.SizeText} · {row.Entry.FormatText}"
              + $" · 文件 {FileSizeText(row.Entry)}"
            : $"{row!.Entry.KindLabel} · {row.Entry.TimeText} · {row.Entry.SizeText}"
              + $" · 完成动作：{row.Entry.SourceText}";

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(row.Entry.Id), 1600);
        if (image == null)
        {
            PreviewEmpty.Text = record ? "封面帧不见了" : "图片文件不见了";
            PreviewEmpty.Visibility = Visibility.Visible;
            return;
        }

        PreviewImage.Source = image;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewEmpty.Visibility = Visibility.Collapsed;
    }

    /// <summary>录屏成品的文件大小（<c>3.4 MB</c>）；读不到返回「—」。</summary>
    private static string FileSizeText(CaptureEntry entry)
    {
        try
        {
            string path = CaptureCatalog.FilePath(entry);
            if (path.Length == 0 || !File.Exists(path)) return "—";
            string text = CaptureCatalog.FormatBytes(new FileInfo(path).Length);
            return text.Length > 0 ? text : "—";
        }
        catch (Exception ex)
        {
            AppLog.Warn("CaptureHistory.FileSize", "读文件大小失败：" + ex.Message);
            return "—";
        }
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!ShouldCopyOnDoubleClick(e.OriginalSource as DependencyObject)) return;

        // 录屏双击 = 播放（复制封面没意义，视频本身才是内容）
        if (SelectedRow?.Entry.IsRecord == true) PlaySelected();
        else CopySelectedImage();
    }

    private void HistoryList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SelectedRow == null) return;

        if (e.Key == Key.Delete)
        {
            DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (SelectedRow.Entry.IsRecord) PlaySelected();
            else CopySelectedImage();
            e.Handled = true;
        }
    }

    /// <summary>双击整行 = 复制图片 / 播放录屏；滚动条 / 行内按钮 / 表头空白都不算。</summary>
    internal static bool ShouldCopyOnDoubleClick(DependencyObject? source)
    {
        if (source == null) return false;
        if (FindAncestor<ScrollBar>(source) != null) return false;
        if (FindAncestor<Button>(source) != null) return false;
        return FindAncestor<ListBoxItem>(source) != null;
    }

    // ============ 操作 ============

    private void Play_Click(object sender, RoutedEventArgs e) => PlaySelected();

    /// <summary>用系统默认播放器打开录屏成品。</summary>
    private void PlaySelected()
    {
        var row = SelectedRow;
        if (row == null) return;

        string path = CaptureCatalog.FilePath(row.Entry);
        if (path.Length == 0 || !File.Exists(path))
        {
            ToastService.Show("录制文件不见了", "采集历史", 2200, ToastKind.error);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ToastService.Show("打不开录制文件：" + ex.Message, "采集历史", 0, ToastKind.error);
        }
    }

    private void CopyImage_Click(object sender, RoutedEventArgs e) => CopySelectedImage();

    private void CopySelectedImage()
    {
        var row = SelectedRow;
        if (row == null) return;

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(row.Entry.Id), 0);
        if (image == null)
        {
            ToastService.Show("图片文件不见了", "采集历史", 1800, ToastKind.error);
            return;
        }

        if (NativeClipboard.SetImage(image)) ToastService.Show("已复制图片", "采集历史", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "采集历史", 0, ToastKind.error);
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var row = SelectedRow;
        if (row == null) return;

        // 录屏：另存的是视频本身；截图 / 识别：另存 png
        if (row.Entry.IsRecord)
        {
            string path = CaptureCatalog.FilePath(row.Entry);
            if (path.Length == 0 || !File.Exists(path))
            {
                ToastService.Show("录制文件不见了", "采集历史", 2200, ToastKind.error);
                return;
            }

            CaptureSave.TrySaveFile(this, path, CaptureCatalog.ExtensionOf(path));
            return;
        }

        var image = CaptureCatalog.LoadImage(CaptureCatalog.AssetPath(row.Entry.Id), 0);
        if (image == null)
        {
            ToastService.Show("图片文件不见了", "采集历史", 1800, ToastKind.error);
            return;
        }

        CaptureSave.TrySave(this, image);
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        string text = SelectedRow?.Entry.Text ?? "";
        if (string.IsNullOrEmpty(text))
        {
            ToastService.Show("没有识别文字", "采集历史", 1600);
            return;
        }

        if (NativeClipboard.SetText(text)) ToastService.Show("已复制识别文字", "采集历史", 1400);
        else ToastService.Show("复制失败（剪贴板被占用）", "采集历史", 0, ToastKind.error);
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        var row = SelectedRow;
        if (row == null) return;

        // 删完把选中落到相邻那条（列表会在 Changed 里重建，先记住 id）
        var rows = _rows.ToList();
        int index = rows.IndexOf(row);
        string? next = rows.Count <= 1
            ? null
            : rows[index + 1 < rows.Count ? index + 1 : index - 1].Entry.Id;

        if (!ConfirmDialog.Confirm(this, "删除采集记录",
                "这条记录和它的图片会被删除，不能撤销。", danger: true, okText: "删除"))
            return;

        CaptureCatalog.Remove(row.Entry.Id);
        SelectById(next);
    }

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_all.Count == 0) return;

        if (!ConfirmDialog.Confirm(this, "清空采集历史",
                $"会删除全部 {_all.Count} 条记录和它们的图片，不能撤销。", danger: true, okText: "清空"))
            return;

        CaptureCatalog.Clear();
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(CaptureCatalog.Root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CaptureCatalog.Root}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ToastService.Show("打开目录失败：" + ex.Message, "采集历史", 0, ToastKind.error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SelectById(string? id)
    {
        var row = id == null ? null : _rows.FirstOrDefault(r => r.Entry.Id == id);
        HistoryList.SelectedItem = row ?? _rows.FirstOrDefault();
        if (HistoryList.SelectedItem is CaptureRow selected) HistoryList.ScrollIntoView(selected);
    }

    // ============ 位图 ============

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T hit) return hit;

            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);

            if (d is not (Visual or System.Windows.Media.Media3D.Visual3D)) return null;
        }
        return null;
    }
}
