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
    private record NoteRow(string Title, string MtimeText, string Path, string Category, DateTime Mtime, string Body,
        Visibility CategoryTagVis);

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
        CreateCategory(name.Trim());
    }

    /// <summary>新建分类并切到该分类视图：左栏刷新为新分类的（空）列表、右侧清为新建态。</summary>
    /// <remarks>
    /// 必须显式 <see cref="ReloadList"/> + <see cref="NewNote"/>：<see cref="PopulateCategoryBox"/>
    /// 以 <c>_loading=true</c> 抑制了 <c>CategoryBox_SelectionChanged</c>（该 handler 才是常规的
    /// ReloadList 触发点），故新建分类不会自动刷新——旧分类的列表与已打开的旧笔记会残留。
    /// 切视图前先经 <see cref="EnsureSavedOrDiscarded"/> 防止未保存改动被静默丢弃。
    /// </remarks>
    private void CreateCategory(string cat)
    {
        if (string.IsNullOrWhiteSpace(cat)) return;
        _repo.EnsureCategory(cat);
        if (!EnsureSavedOrDiscarded()) return;   // 用户取消：保留当前视图与编辑内容
        PopulateCategoryBox(cat);
        NewNote();                                // 清空右侧：新分类尚无笔记，不显示旧分类的笔记
        ReloadList();                             // 左栏切到新分类（空列表）
        TrayService.Notify("已新建分类「" + cat + "」");
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

    /// <summary>删除列表中选中的速记（右键菜单 / Ctrl+Del）。删除的是列表条目，
    /// 只有恰好是正在编辑的笔记时才清空编辑区。</summary>
    private void DeleteNote_Click(object sender, RoutedEventArgs e) => DeleteSelectedNote();

    private void DeleteSelectedNote()
    {
        var row = NoteList.SelectedItem as NoteRow;
        if (row == null)
        {
            ConfirmDialog.Info(this, "删除速记", "请先在列表中选中要删除的速记");
            return;
        }

        bool isCurrent = _current != null
            && string.Equals(_current.Path, row.Path, StringComparison.OrdinalIgnoreCase);

        string path = row.Path;
        if (isCurrent)
        {
            // 新建未保存：磁盘上没有对应文件
            if (_current!.IsNew)
            {
                ConfirmDialog.Info(this, "删除速记", "当前是新建未保存内容，没有可删除的速记");
                return;
            }
            // 已保存但有未落盘改动：先保存/丢弃/取消，避免静默丢失修改
            if (_dirty)
            {
                if (!EnsureSavedOrDiscarded()) return;
                // 保存后路径可能变更（新建、改名或改分类），要按最终路径删除
                path = _current?.Path ?? row.Path;
            }
        }

        var name = Path.GetFileName(path);
        if (!ConfirmDialog.Confirm(this, "删除速记", "确认删除「" + name + "」？此操作不可撤销。", danger: true))
            return;

        _repo.Delete(path);
        TrayService.Notify("已删除「" + name + "」");
        if (isCurrent)
        {
            _current = null;
            _dirty = false;
            NewNote();
        }
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

    /// <summary>右键列表项：先确定性选中命中项，再弹出右键菜单。
    /// WPF 的 ListBox 默认只在左键下选中，右键不保证更新 SelectedItem；
    /// 若不处理，菜单的「删除」可能作用于陈旧选中项而误删。</summary>
    private void NoteList_RightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is NoteRow) NoteList.SelectedItem = item.DataContext;
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
        UpdatePlaceholders();
        BodyBox.Focus();
        BodyBox.CaretIndex = BodyBox.Text.Length;
    }

    private void ReloadList()
    {
        string? cat = CategoryBox.SelectedItem as string;
        if (string.IsNullOrEmpty(cat) || cat == NoteRepository.AllCategories) cat = null;
        // 「全部」视图跨分类：每行显示分类标签；单分类视图里每行分类相同，隐藏避免冗余
        bool showCat = cat == null;
        var entries = _repo.List(cat, _filter);
        var rows = entries.Select(e => new NoteRow(
            string.IsNullOrEmpty(e.Title) ? Path.GetFileNameWithoutExtension(e.Path) : e.Title,
            e.Mtime.ToString("M/d HH:mm"),
            e.Path, e.Category, e.Mtime, e.Body,
            showCat ? Visibility.Visible : Visibility.Collapsed)).ToList();
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
        UpdatePlaceholders();
        TitleBox.Focus();
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        if (_loading) return;
        MarkDirty();
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        if (_loading) return;
        MarkDirty();
    }

    private void UpdatePlaceholders()
    {
        TitlePlaceholder.Visibility = string.IsNullOrEmpty(TitleBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        BodyPlaceholder.Visibility = string.IsNullOrEmpty(BodyBox.Text) ? Visibility.Visible : Visibility.Collapsed;
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
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            if (e.Key == Key.S)
            {
                SaveCurrent();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete && !IsTextCaretFocused())
            {
                // 焦点在标题/正文时 Ctrl+Del 留给编辑（删到文末），不触发删除速记
                DeleteSelectedNote();
                e.Handled = true;
            }
        }
    }

    /// <summary>焦点是否落在标题或正文编辑框（有光标可输入的文本控件）。</summary>
    private static bool IsTextCaretFocused() => Keyboard.FocusedElement is TextBox;

}
