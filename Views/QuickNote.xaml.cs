using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CapsLockPro.Core;
using CapsLockPro.Features;

namespace CapsLockPro.Views;

/// <summary>速记 GUI（双列：左列表 + 右编辑区）。数据层见 <see cref="NoteRepository"/>。
/// 保留手动 Ctrl+S 保存；切换笔记 / 关闭窗口时若有未保存改动则提示。</summary>
public partial class QuickNoteWindow : Window
{
    private readonly NoteRepository _repo;
    private NoteEntry? _current;
    private bool _loading;
    private bool _dirty;
    private string _filter = "";

    // 搜索防抖：按键间隙不重扫目录，停顿 300ms 后统一刷新一次
    private readonly DispatcherTimer _searchDebounce;

    // 列表行（供绑定；NoteEntry 的 Mtime 是 DateTime 不便直接显示）
    private record NoteRow(string Title, string MtimeText, string Path, string Category, DateTime Mtime, string Body);

    internal QuickNoteWindow(NoteRepository repo)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        _repo = repo;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); ReloadList(); };
        Loaded += OnLoaded;
        PopulateCategoryBox(NoteRepository.Unclassified);
        ReloadList();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NewNote();
        TitleBox.Focus();
    }

    /// <summary>外部（QuickNote.Refresh）通知仓库可能变化，重刷分类+列表。</summary>
    public void ReloadFromRepository()
    {
        string? sel = CategoryBox.SelectedItem as string;
        PopulateCategoryBox(sel ?? NoteRepository.Unclassified);
        ReloadList();
    }

    // —— 顶栏：分类 ——

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        ReloadList();
    }

    private void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        var (ok, name) = InputDialog.Show(this, "新建分类", "输入分类名:");
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        _repo.EnsureCategory(name.Trim());
        PopulateCategoryBox(name.Trim());
    }

    private void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        string? cat = SelectedRealCategory();
        if (cat == null)
        {
            ConfirmDialog.Info(this, "重命名分类", "请先在下拉框选择一个具体分类（不能是“全部”）");
            return;
        }
        var (ok, name) = InputDialog.Show(this, "重命名分类", "输入新分类名:", cat);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        try { _repo.RenameCategory(cat, name.Trim()); }
        catch (Exception ex) { ConfirmDialog.Info(this, "重命名分类", ex.Message); return; }
        PopulateCategoryBox(name.Trim());
        // 当前编辑中的笔记若在被改名的分类，更新其分类归属
        if (_current != null && _current.Category == cat) _current = _repo.Load(_current.Path);
        ReloadList();
        TrayService.Notify("已重命名「" + cat + "」→「" + name.Trim() + "」");
    }

    private void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        string? cat = SelectedRealCategory();
        if (cat == null)
        {
            ConfirmDialog.Info(this, "删除分类", "请先在下拉框选择一个具体分类（不能是“全部”）");
            return;
        }
        int count = _repo.CountNotes(cat);
        string msg = count == 0
            ? "确认删除空分类「" + cat + "」？"
            : "确认删除分类「" + cat + "」及其下 " + count + " 条速记？此操作不可撤销。";
        if (!ConfirmDialog.Confirm(this, "删除分类", msg, danger: true)) return;
        int removed = _repo.DeleteCategory(cat);
        if (_current != null && _current.Category == cat) { _current = null; NewNote(); }
        PopulateCategoryBox(NoteRepository.AllCategories);
        ReloadList();
        TrayService.Notify("已删除分类「" + cat + "」（" + removed + " 条速记）");
    }

    private string? SelectedRealCategory()
        => CategoryBox.SelectedItem is string s && s != NoteRepository.AllCategories ? s : null;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filter = SearchBox.Text ?? "";
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(_filter) ? Visibility.Visible : Visibility.Collapsed;
        // 防抖：停顿 300ms 后重扫一次，避免每键都全量扫目录+读文件
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureSavedOrDiscarded()) return;
        NewNote();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null || _current.IsNew)
        {
            ConfirmDialog.Info(this, "删除速记", "当前是新建未保存内容，没有可删除的速记");
            return;
        }
        var name = Path.GetFileName(_current.Path);
        if (!ConfirmDialog.Confirm(this, "删除速记", "确认删除「" + name + "」？此操作不可撤销。", danger: true)) return;
        _repo.Delete(_current.Path);
        TrayService.Notify("已删除「" + name + "」");
        _current = null;
        _dirty = false;
        NewNote();
        ReloadList();
    }

    // —— 列表 ——

    private void NoteList_Click(object sender, MouseButtonEventArgs e)
    {
        // 从命中点沿可视化树向上找 ListBoxItem，取其 DataContext(NoteRow)
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not NoteRow row) return;
        // 点击的就是当前正在编辑的笔记 → 无需切换
        if (_current != null && string.Equals(_current.Path, row.Path, StringComparison.OrdinalIgnoreCase)) return;
        if (!EnsureSavedOrDiscarded()) return;
        var entry = _repo.Load(row.Path);
        if (entry == null) { ReloadList(); return; }
        LoadEntry(entry);
    }

    private void LoadEntry(NoteEntry entry)
    {
        _loading = true;
        _current = entry;
        _dirty = false;
        TitleBox.Text = entry.Title;
        BodyBox.Text = entry.Body;
        _loading = false;
        BodyBox.ScrollToHome();
        StatusBar.Text = "正在编辑「" + entry.Title + "」";
        UpdateSaveBadge();
        BodyBox.Focus();
        BodyBox.CaretIndex = BodyBox.Text.Length;
    }

    private void ReloadList()
    {
        string? cat = CategoryBox.SelectedItem as string;
        if (string.IsNullOrEmpty(cat) || cat == NoteRepository.AllCategories) cat = null;
        var entries = _repo.List(cat, _filter);
        var rows = entries.Select(e => new NoteRow(
            string.IsNullOrEmpty(e.Title) ? Path.GetFileNameWithoutExtension(e.Path) : e.Title,
            e.Mtime.ToString("M/d HH:mm"),
            e.Path, e.Category, e.Mtime, e.Body)).ToList();
        string? keep = _current?.Path;
        NoteList.ItemsSource = rows;
        EmptyHint.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keep != null)
        {
            var sel = rows.FirstOrDefault(r => string.Equals(r.Path, keep, StringComparison.OrdinalIgnoreCase));
            if (sel != null) NoteList.SelectedItem = sel;
        }
    }

    private void PopulateCategoryBox(string selectName)
    {
        _loading = true;
        var cats = _repo.Categories();
        var items = new List<string> { NoteRepository.AllCategories };
        items.AddRange(cats);
        CategoryBox.ItemsSource = items;
        CategoryBox.SelectedItem = items.Contains(selectName) ? selectName : NoteRepository.AllCategories;
        _loading = false;
    }

    // —— 编辑区 ——

    private void NewNote()
    {
        _loading = true;
        _current = null;
        _dirty = false;
        TitleBox.Text = "";
        BodyBox.Text = "";
        _loading = false;
        BodyBox.ScrollToHome();
        StatusBar.Text = "输入标题与正文后 Ctrl+S 保存";
        UpdateSaveBadge();
        TitleBox.Focus();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        MarkDirty();
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        StatusBar.Text = "未保存改动 · Ctrl+S 保存";
        UpdateSaveBadge();
    }

    private void UpdateSaveBadge()
    {
        if (_dirty)
        {
            SaveBadge.Text = "未保存";
            SaveBadge.Foreground = (Brush)FindResource("HintTextBrush");
        }
        else if (_current != null)
        {
            SaveBadge.Text = "已保存";
            SaveBadge.Foreground = (Brush)FindResource("SuccessBrush");
        }
        else
        {
            SaveBadge.Text = "";
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private void SaveCurrent()
    {
        string title = TitleBox.Text.Trim();
        string body = BodyBox.Text;
        if (string.IsNullOrWhiteSpace(body) && string.IsNullOrWhiteSpace(title))
        {
            ConfirmDialog.Info(this, "速记", "标题和正文都为空，未保存");
            return;
        }

        string category = ResolveSaveCategory();
        string? oldPath = _current?.Path;
        var entry = new NoteEntry
        {
            Path = oldPath ?? "",
            Title = title,
            Category = category,
            Body = body,
            Mtime = DateTime.Now,
        };

        string savedPath;
        try { savedPath = _repo.Save(entry, oldPath); }
        catch (Exception ex) { ConfirmDialog.Info(this, "速记", "保存失败: " + ex.Message); return; }

        TrayService.Notify("已保存「" + (string.IsNullOrEmpty(title) ? Path.GetFileNameWithoutExtension(savedPath) : title) + "」");
        _current = _repo.Load(savedPath);
        _dirty = false;
        PopulateCategoryBox(category);
        ReloadList();
        StatusBar.Text = "已保存";
        UpdateSaveBadge();
        BodyBox.Focus();
    }

    private string ResolveSaveCategory()
    {
        if (CategoryBox.SelectedItem is string s && !string.IsNullOrEmpty(s) && s != NoteRepository.AllCategories)
            return s;
        return _current?.Category ?? NoteRepository.Unclassified;
    }

    // —— 未保存保护 ——

    /// <summary>若有未保存改动，提示保存/丢弃/取消。返回是否可继续（已保存或已丢弃）。</summary>
    private bool EnsureSavedOrDiscarded()
    {
        if (!_dirty) return true;
        var r = ConfirmDialog.ConfirmDiscard(this, "速记", "当前速记有未保存的改动，是否保存？");
        switch (r)
        {
            case SaveConfirmResult.Save:
                SaveCurrent();
                return true;
            case SaveConfirmResult.Discard:
                _dirty = false;
                return true;
            default:
                return false;
        }
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_dirty) return;
        var r = ConfirmDialog.ConfirmDiscard(this, "速记", "当前速记有未保存的改动，是否保存？");
        switch (r)
        {
            case SaveConfirmResult.Save:
                SaveCurrent();
                break;
            case SaveConfirmResult.Cancel:
                e.Cancel = true;
                break;
            case SaveConfirmResult.Discard:
                _dirty = false;
                break;
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            SaveCurrent();
            e.Handled = true;
        }
    }
}
