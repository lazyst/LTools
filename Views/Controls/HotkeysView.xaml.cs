using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;
using LTools.Features;
using LTools.Features.Builtin;

namespace LTools.Views.Controls;

/// <summary>
/// 设置 → 快捷键：左预设组 + 右热键表（布局 A）。
/// </summary>
/// <remarks>
/// <para>
/// 一个绑定只存「快捷键 + 动作 Id」；动作本体在动作管理里编辑，删除动作时
/// <see cref="Core.Storage.ActionRemoval"/> 会把引用这条动作的绑定一起清掉。
/// </para>
/// <para>
/// 改完立即 <see cref="HotkeyRegistry.Load"/>，钩子下一按就是新配置；落盘只写
/// <c>configs/hotkeys.json</c>（<see cref="HotkeyStore"/>），不碰动作目录与其它域。
/// </para>
/// </remarks>
public partial class HotkeysView : UserControl
{
    private readonly ObservableCollection<GroupRow> _groups = new();
    private readonly ObservableCollection<BindingRow> _rows = new();

    private HotkeyFile _file = new();
    private HotkeyGroup? _group;
    private string? _keepGroupId;
    private string? _keepBindingId;
    private bool _loading;
    private bool _loaded;
    private int _renderedRevision = -1;   // 本页最后一次重建时 HotkeyRegistry.Revision 的值
    private string _searchText = "";      // 防抖落定时算一次，FilterRow 不再每行读 TextBox / 重算行显示串
    private readonly SearchDebounce _searchDebounce;

    public HotkeysView()
    {
        InitializeComponent();
        GroupList.ItemsSource = _groups;
        BindingGrid.ItemsSource = _rows;

        _searchDebounce = new SearchDebounce(150, ApplySearch);
        Unloaded += (_, _) => _searchDebounce.Cancel();

        if (CollectionViewSource.GetDefaultView(_rows) is { } view) view.Filter = FilterRow;

        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Reload();
        };
    }

    // ============ 载入 / 重建 ============

    /// <summary>从运行期热键表重新载入（导入配置 / 动作改名 / 手动重载后调用）。</summary>
    public void Reload()
    {
        _renderedRevision = HotkeyRegistry.Revision;
        _loaded = true;
        _file = HotkeyRegistry.File.Clone();
        _keepGroupId ??= _group?.Id;
        _keepBindingId ??= SelectedBinding()?.Id;
        PopulateGroups();
        PopulateDefault();
    }

    /// <summary>
    /// 切到本页时调用：运行期热键表没变就不重建。低级鼠标钩子与 WPF 共用 UI 线程，
    /// 每次切页都 Clear/Add 两个 ObservableCollection 再 Refresh() 会把光标顶住一下；
    /// 数据真变了（保存 / 导入 / 播种 / 删动作）走 <see cref="Reload"/> 或 <see cref="Invalidate"/>。
    /// </summary>
    public void RefreshIfNeeded()
    {
        if (_loaded && _renderedRevision == HotkeyRegistry.Revision) return;
        Reload();
    }

    /// <summary>动作改名 / 新建 / 删除后调用：让下次切到本页重读一次（不立刻重建当前页面）。</summary>
    public void Invalidate() => _renderedRevision = -1;

    /// <summary>按当前 <see cref="_file"/> 重建左组列表与右表。</summary>
    private void PopulateGroups()
    {
        _loading = true;
        try
        {
            _groups.Clear();
            foreach (var group in _file.Groups) _groups.Add(new GroupRow(group));

            int index = _keepGroupId == null ? 0 : _file.Groups.FindIndex(g => g.Id == _keepGroupId);
            if (index < 0) index = _file.Groups.Count > 0 ? 0 : -1;

            GroupList.SelectedIndex = index;
            _group = index >= 0 ? _file.Groups[index] : null;

            PopulateBindings();
        }
        finally { _loading = false; }

        UpdateHint();
    }

    private void PopulateBindings()
    {
        var conflicts = HotkeyRegistry.FindConflicts(_file);
        _rows.Clear();
        if (_group != null)
        {
            foreach (var binding in _group.Bindings)
                _rows.Add(new BindingRow(binding, _file, conflicts.Contains(binding.Id)));
        }

        // 按 Id 选中（表格可能被搜索过滤，行下标和 _group.Bindings 不再一一对应）
        var target = _rows.FirstOrDefault(r => r.Source.Id == _keepBindingId) ?? _rows.FirstOrDefault();
        BindingGrid.SelectedItem = target;

        ConflictText.Text = conflicts.Count == 0
            ? ""
            : $"⚠ {conflicts.Count} 条快捷键重复，只有排在前面的那条会触发";
        if (conflicts.Count > 0 && TryFindResource("DangerBrush") is Brush danger)
            ConflictText.Foreground = danger;
        else
            ConflictText.ClearValue(TextBlock.ForegroundProperty);

        // _rows.Clear() 的 Reset + 逐条 Add 已经让带 Filter 的默认视图同步（Add 时按当前搜索词筛过）；
        // 再 Refresh() 会强制整表二次重建，还会丢掉上面刚设好的选中项。只有搜索框那处需要显式 Refresh。
    }

    private void PopulateDefault()
    {
        _loading = true;
        try
        {
            DefaultScopeBox.SelectedIndex = _file.DefaultScope switch
            {
                HotkeyScope.whitelist => 1,
                HotkeyScope.blacklist => 2,
                _ => 0,
            };
            DefaultProcessBox.Text = string.Join(Environment.NewLine, _file.DefaultFilter.Processes);
            DefaultTitleBox.Text = string.Join(Environment.NewLine, _file.DefaultFilter.Titles);
            DefaultClassBox.Text = string.Join(Environment.NewLine, _file.DefaultFilter.Classes);
            UpdateDefaultHeader();
        }
        finally { _loading = false; }
    }

    private void UpdateDefaultHeader()
    {
        string label = _file.DefaultScope switch
        {
            HotkeyScope.whitelist => $"全局默认：白名单（{CountRules(_file.DefaultFilter)} 条规则）",
            HotkeyScope.blacklist => $"全局默认：排除黑名单（{CountRules(_file.DefaultFilter)} 条规则）",
            _ => "全局默认：所有窗口",
        };
        DefaultHeader.Text = label;
    }

    private void UpdateHint()
    {
        int total = _file.Groups.Sum(g => g.Bindings.Count);
        int active = _file.Groups.Where(g => g.Enabled).Sum(g => g.Bindings.Count(b => b.Enabled));
        HintText.Text = _file.Groups.Count == 0
            ? "还没有预设组：点左上「＋ 新建」建一个组，再往里添加快捷键"
            : $"共 {total} 条绑定，{active} 条生效 · 组顺序 = 匹配优先级";
    }

    private static int CountRules(AppFilter filter) =>
        filter.Processes.Count + filter.Titles.Count + filter.Classes.Count;

    // ============ 保存 ============

    /// <summary>把界面改动落到运行期与磁盘（只写 hotkeys.json）。</summary>
    private void Save(string? keepGroupId = null, string? keepBindingId = null)
    {
        _keepGroupId = keepGroupId ?? _group?.Id;
        _keepBindingId = keepBindingId ?? SelectedBinding()?.Id;

        _file.Normalize();
        HotkeyRegistry.Load(_file);   // 钩子立即生效（不必重启）
        // 本页紧接着就按 _file 重建，记下新 revision，免得下次切回来又白重建一次
        _renderedRevision = HotkeyRegistry.Revision;
        Persist(_file);
        PopulateGroups();
    }

    private static void Persist(HotkeyFile file)
    {
        string path = string.IsNullOrWhiteSpace(ConfigStore.ConfigPath)
            ? ConfigLocator.FindPath()
            : ConfigStore.ConfigPath;

        try
        {
            HotkeyStore.Modify(path, store =>
            {
                var copy = file.Clone();
                store.SchemaVersion = copy.SchemaVersion;
                store.DefaultScope = copy.DefaultScope;
                store.DefaultFilter = copy.DefaultFilter;
                store.SeedVersion = copy.SeedVersion;
                store.Groups = copy.Groups;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Hotkeys", "保存 configs/hotkeys.json 失败：" + ex.Message);
        }
    }

    // ============ 预设组 ============

    private HotkeyGroup? SelectedGroup() =>
        GroupList.SelectedIndex >= 0 && GroupList.SelectedIndex < _file.Groups.Count
            ? _file.Groups[GroupList.SelectedIndex]
            : null;

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _group = SelectedGroup();
        _keepGroupId = _group?.Id;
        _keepBindingId = null;
        _loading = true;
        try { PopulateBindings(); }
        finally { _loading = false; }
    }

    /// <summary>预设组行右侧 4 个图标的分发器：按 DataContext 认行（点按钮不会选中行），再复用既有逻辑。</summary>
    private void GroupRowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not GroupRow row) return;
        GroupList.SelectedItem = row;
        switch ((el as Button)?.CommandParameter as string)
        {
            case "rename": RenameGroup_Click(sender, e); break;
            case "delete": DeleteGroup_Click(sender, e); break;
            case "up": GroupUp_Click(sender, e); break;
            case "down": GroupDown_Click(sender, e); break;
        }
    }

    private void GroupList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<CheckBox>(e.OriginalSource as DependencyObject) != null) return;
        if (FindAncestor<Button>(e.OriginalSource as DependencyObject) != null) return;   // 点行内图标不弹重命名
        RenameGroup_Click(sender, e);
    }

    private void GroupEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var row = (sender as FrameworkElement)?.DataContext as GroupRow;
        Dispatcher.BeginInvoke(new Action(() => Save(row?.Source.Id)));
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        var (ok, name) = InputDialog.Show(Window.GetWindow(this), "新建预设组", "名称：", "新预设组");
        if (!ok || string.IsNullOrWhiteSpace(name)) return;

        var group = new HotkeyGroup { Id = _file.NewId(), Name = name.Trim(), Enabled = true };
        _file.Groups.Add(group);
        Save(group.Id, null);
    }

    private void RenameGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup();
        if (group == null) return;

        var (ok, name) = InputDialog.Show(Window.GetWindow(this), "重命名预设组", "名称：", group.Name);
        if (!ok || string.IsNullOrWhiteSpace(name)) return;
        group.Name = name.Trim();
        Save(group.Id);
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        var group = SelectedGroup();
        if (group == null) return;
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除预设组",
            $"确定删除「{group.Name}」吗？组里的 {group.Bindings.Count} 条快捷键会一起删除（动作本身不受影响）。",
            danger: true)) return;

        int index = _file.Groups.IndexOf(group);
        _file.Groups.Remove(group);
        string? next = _file.Groups.Count == 0
            ? null
            : _file.Groups[Math.Min(index, _file.Groups.Count - 1)].Id;
        Save(next, null);
    }

    private void GroupUp_Click(object sender, RoutedEventArgs e) => MoveGroup(-1);

    private void GroupDown_Click(object sender, RoutedEventArgs e) => MoveGroup(1);

    private void MoveGroup(int delta)
    {
        var group = SelectedGroup();
        if (group == null) return;
        int from = _file.Groups.IndexOf(group);
        int to = from + delta;
        if (to < 0 || to >= _file.Groups.Count) return;

        (_file.Groups[from], _file.Groups[to]) = (_file.Groups[to], _file.Groups[from]);
        Save(group.Id);
    }

    // ============ 绑定 ============

    private HotkeyBinding? SelectedBinding() => (BindingGrid.SelectedItem as BindingRow)?.Source;

    /// <summary>热键表行右侧 4 个图标的分发器：按 DataContext 认行（点按钮不会选中行），再复用既有逻辑。</summary>
    private void BindingRowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not BindingRow row) return;
        BindingGrid.SelectedItem = row;
        switch ((el as Button)?.CommandParameter as string)
        {
            case "edit": EditBinding_Click(sender, e); break;
            case "delete": DeleteBinding_Click(sender, e); break;
            case "up": BindingUp_Click(sender, e); break;
            case "down": BindingDown_Click(sender, e); break;
        }
    }

    private HotkeyGroup EnsureGroup()
    {
        if (_group != null) return _group;

        var group = new HotkeyGroup { Id = _file.NewId(), Name = "我的快捷键", Enabled = true };
        _file.Groups.Add(group);
        _group = group;
        return group;
    }

    private void AddBinding_Click(object sender, RoutedEventArgs e)
    {
        var group = EnsureGroup();
        var draft = new HotkeyBinding();
        var result = HotkeyEditDialog.Show(Window.GetWindow(this), draft);
        if (result == null) return;

        result.Id = _file.NewId();
        group.Bindings.Add(result);
        Save(group.Id, result.Id);
    }

    private void EditBinding_Click(object sender, RoutedEventArgs e)
    {
        var binding = SelectedBinding();
        if (binding == null || _group == null) return;

        var result = HotkeyEditDialog.Show(Window.GetWindow(this), binding);
        if (result == null) return;

        result.Id = binding.Id;
        int index = _group.Bindings.IndexOf(binding);
        if (index < 0) return;
        _group.Bindings[index] = result;
        Save(_group.Id, result.Id);
    }

    private void DeleteBinding_Click(object sender, RoutedEventArgs e)
    {
        var binding = SelectedBinding();
        if (binding == null || _group == null) return;

        string chord = HotkeyChord.Format(binding.Chord);
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除快捷键",
            $"确定删除 {chord} 吗？（只删这条绑定，动作本身不受影响）", danger: true)) return;

        int index = _group.Bindings.IndexOf(binding);
        if (index < 0) return;
        _group.Bindings.RemoveAt(index);
        string? next = _group.Bindings.Count == 0
            ? null
            : _group.Bindings[Math.Min(index, _group.Bindings.Count - 1)].Id;
        Save(_group.Id, next);
    }

    private void BindingUp_Click(object sender, RoutedEventArgs e) => MoveBinding(-1);

    private void BindingDown_Click(object sender, RoutedEventArgs e) => MoveBinding(1);

    private void MoveBinding(int delta)
    {
        var binding = SelectedBinding();
        if (binding == null || _group == null) return;

        int from = _group.Bindings.IndexOf(binding);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _group.Bindings.Count) return;

        (_group.Bindings[from], _group.Bindings[to]) = (_group.Bindings[to], _group.Bindings[from]);
        Save(_group.Id, binding.Id);
    }

    private void BindingEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox box || box.DataContext is not BindingRow row) return;

        // 直接按勾选框的状态写回，不依赖 TwoWay 绑定的更新时机（否则会「勾上了、存盘还是旧值」）。
        row.Enabled = box.IsChecked == true;
        Dispatcher.BeginInvoke(new Action(() => Save(_group?.Id, row.Source.Id)));
    }

    private void BindingGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // DataGridCell 在「自己还没有键盘焦点」时会把这一下 MouseDown 标记成已处理（先给自己抢焦点），
        // 单元格里 Focusable="False" 的启用勾选框就收不到点击（表现为「点几次都没反应」）。
        // 在预览阶段先把单元格焦点给上，DataGridCell 自己就不再吞这一下。
        var cell = FindAncestor<DataGridCell>(source);
        if (cell != null && !cell.IsKeyboardFocusWithin) cell.Focus();

        if (e.ClickCount != 2) return;
        if (FindAncestor<CheckBox>(source) != null) return;

        var row = FindAncestor<DataGridRow>(source);
        if (row == null) return;   // 表头 / 空白区域

        // DataGridCell 会把第一次按下的 MouseDown 标记为已处理，MouseDoubleClick 就不会再合成
        // （表现是「第一次双击没反应，第二次才行」）。所以直接从预览事件认 ClickCount == 2，
        // 并把弹窗推到本次鼠标事件结束之后，避免拖动 / 捕获还挂在表格上。
        e.Handled = true;
        BindingGrid.SelectedItem = row.Item;
        Dispatcher.BeginInvoke(new Action(() => EditBinding_Click(sender, e)));
    }

    // ============ 全局默认名单 ============

    private void DefaultScope_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _file.DefaultScope = ((DefaultScopeBox.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "whitelist" => HotkeyScope.whitelist,
            "blacklist" => HotkeyScope.blacklist,
            _ => HotkeyScope.all,
        };
        UpdateDefaultHeader();
        Save();
    }

    // 全局默认名单三行也能从系统里点选；直接改 Text 不会走 LostFocus，手动触发一次保存。
    private void DefaultProcess_Click(object sender, RoutedEventArgs e)
    {
        SystemPickerDialog.AppendLine(DefaultProcessBox,
            SystemPickerDialog.PickProcess(Window.GetWindow(this)));
        DefaultFilter_LostFocus(sender, e);
    }

    private void DefaultTitle_Click(object sender, RoutedEventArgs e)
    {
        SystemPickerDialog.AppendLine(DefaultTitleBox,
            SystemPickerDialog.PickWindow(Window.GetWindow(this))?.Title);
        DefaultFilter_LostFocus(sender, e);
    }

    private void DefaultClass_Click(object sender, RoutedEventArgs e)
    {
        SystemPickerDialog.AppendLine(DefaultClassBox,
            SystemPickerDialog.PickWindow(Window.GetWindow(this))?.ClassName);
        DefaultFilter_LostFocus(sender, e);
    }

    private void DefaultFilter_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var filter = new AppFilter
        {
            Processes = Lines(DefaultProcessBox),
            Titles = Lines(DefaultTitleBox),
            Classes = Lines(DefaultClassBox),
        };
        if (SameFilter(filter, _file.DefaultFilter)) return;

        _file.DefaultFilter = filter;
        UpdateDefaultHeader();
        Save();
    }

    private static bool SameFilter(AppFilter a, AppFilter b) =>
        a.Processes.SequenceEqual(b.Processes, StringComparer.Ordinal)
        && a.Titles.SequenceEqual(b.Titles, StringComparer.Ordinal)
        && a.Classes.SequenceEqual(b.Classes, StringComparer.Ordinal);

    private static List<string> Lines(TextBox box) => box.Text
        .Replace("\r\n", "\n").Replace('\r', '\n')
        .Split('\n')
        .Select(x => x.Trim())
        .Where(x => x.Length > 0)
        .ToList();

    // ============ 搜索 ============

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _searchDebounce.Signal();
    }

    /// <summary>
    /// 防抖落定后才刷筛选视图：一次 Refresh 会重跑所有行并让 DataGrid 重新布局，
    /// 每敲一键都跑会顶住鼠标（钩子与 UI 共用线程）。量化埋点走 PerfProbe（默认关）。
    /// </summary>
    private void ApplySearch()
    {
        _searchText = SearchBox.Text.Trim();
        using (PerfProbe.Measure("hotkeys:search"))
            CollectionViewSource.GetDefaultView(_rows)?.Refresh();
    }

    private bool FilterRow(object item)
    {
        if (item is not BindingRow row) return false;
        return _searchText.Length == 0 || row.Matches(_searchText);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null && source is not T) source = VisualTreeHelper.GetParent(source);
        return source as T;
    }

    // ============ 行模型 ============

    public sealed class GroupRow
    {
        public GroupRow(HotkeyGroup group) => Source = group;

        public HotkeyGroup Source { get; }

        public string Name => Source.Name;

        public string Count
        {
            get
            {
                int total = Source.Bindings.Count;
                if (total == 0) return "空";
                int unset = Source.Bindings.Count(b => b.Chord.Length == 0);
                if (unset == 0) return $"{total} 条";
                return unset == total ? $"{total} 项未设" : $"{total} 条 · {unset} 未设";
            }
        }

        public bool Enabled
        {
            get => Source.Enabled;
            set => Source.Enabled = value;
        }
    }

    public sealed class BindingRow : INotifyPropertyChanged
    {
        private readonly HotkeyFile _file;

        public BindingRow(HotkeyBinding binding, HotkeyFile file, bool conflict)
        {
            Source = binding;
            _file = file;
            Conflict = conflict;
            // 搜索文本建行时算一次：Chord / ActionName / ScopeText 都是格式化后的显示串，
            // 原来 FilterRow 每敲一键对每行重算四次；拼成一串后只做一次 Contains。
            SearchText = string.Join('\n', Chord, ActionName, ScopeText, Note);
        }

        /// <summary>搜索用的预算文本（不是值，不参与保存）。</summary>
        public string SearchText { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public HotkeyBinding Source { get; }

        public bool Conflict { get; }

        public bool Enabled
        {
            get => Source.Enabled;
            set
            {
                if (Source.Enabled == value) return;
                Source.Enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        public string Chord
        {
            get
            {
                string text = HotkeyChord.Format(Source.Chord);
                return text.Length == 0 ? "（未设置）" : text;
            }
        }

        public string ChordTip => Conflict
            ? "这个组合键在已启用的组里出现了多次：运行期只有排在前面的那条会触发"
            : Chord;

        public string ActionName => string.IsNullOrWhiteSpace(Source.ActionId)
            ? "（未选动作）"
            : BindingRef.Describe(Source.ActionId);

        public string ScopeText => AppFilterMatch.DescribeScope(Source, _file);

        public string ScopeTip => AppFilterMatch.DescribeScopeLong(Source, _file);

        public string Note => Source.Note;

        public bool Matches(string query) =>
            SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
    }
}
