using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Builtin;

namespace LTools.Views.Controls;

/// <summary>
/// 设置 → 自动触发：单页表格（见 <c>docs/AUTO_TRIGGER.md</c> §9）。
/// </summary>
/// <remarks>
/// <para>
/// 编辑副本来自 <see cref="TriggerRegistry.File"/>.Clone()；改完一律走 <see cref="Save"/>——
/// <c>Normalize</c> → <see cref="TriggerService.Reload"/>（立即生效、重算排程，不必重启）→
/// <see cref="TriggerStore"/> 只写 <c>configs/triggers.json</c>（照 HotkeysView.Save）。
/// </para>
/// <para>
/// 三种触发方式（定时 / 启动后 / 事件）都在表格里显示；事件规则的参数由编辑对话框按
/// <c>docs/AUTO_TRIGGER.md</c> §13.5 动态生成；三个事件源（窗口 / 剪贴板 / 文件）已接入。
/// </para>
/// </remarks>
public partial class TriggersView : UserControl
{
    private readonly ObservableCollection<RuleRow> _rows = new();
    private readonly ObservableCollection<HistoryRow> _history = new();
    private TriggersFile _file = new();
    private bool _loading;
    private bool _loaded;

    // 进程内「复制规则」剪贴板（跨动作窗口共享，跟步骤编辑器一个做法）。
    private static readonly List<TriggerRule> _ruleClipboard = new();

    public TriggersView()
    {
        InitializeComponent();
        RuleGrid.ItemsSource = _rows;
        HistoryGrid.ItemsSource = _history;
        if (CollectionViewSource.GetDefaultView(_rows) is { } view) view.Filter = FilterRow;

        Loaded += (_, _) =>
        {
            if (_loaded) return;
            _loaded = true;
            Reload();
        };
    }

    // ============ 载入 / 重建 ============

    /// <summary>从运行期规则表重新载入（切到本页 / 导入配置 / 删动作后调用）。</summary>
    public void Reload()
    {
        _file = TriggerRegistry.File.Clone();
        _loading = true;
        try { MasterBox.IsChecked = _file.Enabled; }
        finally { _loading = false; }
        PopulateRows();
        UpdateEmergency();
        LoadHistory();
    }

    private void PopulateRows(string? keepId = null)
    {
        _loading = true;
        try
        {
            _rows.Clear();
            foreach (var rule in _file.Rules) _rows.Add(new RuleRow(rule));
            RuleGrid.SelectedItem = (keepId == null
                ? _rows.FirstOrDefault()
                : _rows.FirstOrDefault(r => r.Source.Id == keepId)) ?? _rows.FirstOrDefault();
        }
        finally { _loading = false; }

        UpdateHint();
        CollectionViewSource.GetDefaultView(_rows)?.Refresh();
    }

    private void UpdateHint()
    {
        int total = _file.Rules.Count;
        int active = _file.Rules.Count(r => r.Enabled);
        HintText.Text = total == 0
            ? "还没有规则：点右上「＋ 新建规则…」建一条"
            : TriggerService.EmergencyDisabled
                ? $"共 {total} 条规则 · {active} 条启用 · 已被紧急禁用，全部暂停"
                : $"共 {total} 条规则 · {active} 条启用 · 总开关关闭时全部不生效";
        MasterLabel.Text = _file.Enabled ? "总开关 · 已开启" : "总开关 · 已关闭";
    }

    private void UpdateEmergency()
    {
        EmergencyButton.Content = TriggerService.EmergencyDisabled ? "恢复自动触发…" : "紧急禁用…";
    }

    // ============ 触发记录（只读） ============

    /// <summary>从 <c>state/trigger-history.jsonl</c> 读倒序快照填表。</summary>
    private void LoadHistory()
    {
        _history.Clear();
        foreach (var r in TriggerHistory.Load()) _history.Add(new HistoryRow(r));
        HistoryCount.Text = $"（{_history.Count} 条 · 最多 {TriggerHistory.MaxRecords} 条）";
    }

    private void History_Expanded(object sender, RoutedEventArgs e) => LoadHistory();

    private void HistoryRefresh_Click(object sender, RoutedEventArgs e) => LoadHistory();

    private void HistoryClear_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "清空触发记录",
                "确定清空全部触发记录吗？（只删记录，不影响规则）", danger: true)) return;
        TriggerHistory.Clear();
        LoadHistory();
    }

    // ============ 保存 ============

    /// <summary>把界面改动落到运行期与磁盘（只写 triggers.json）。</summary>
    private void Save(string? keepId = null)
    {
        _file.Normalize();
        TriggerService.Reload(_file);   // 立即生效 + 重算排程
        Persist(_file);
        PopulateRows(keepId);
    }

    private static void Persist(TriggersFile file)
    {
        string path = string.IsNullOrWhiteSpace(ConfigStore.ConfigPath)
            ? ConfigLocator.FindPath()
            : ConfigStore.ConfigPath;
        try
        {
            TriggerStore.Modify(path, store =>
            {
                var copy = file.Clone();
                store.SchemaVersion = copy.SchemaVersion;
                store.Enabled = copy.Enabled;
                store.Rules = copy.Rules;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Triggers", "保存 configs/triggers.json 失败：" + ex.Message);
        }
    }

    // ============ 总开关 ============

    private void MasterBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _file.Enabled = MasterBox.IsChecked == true;
        Save();
    }

    // ============ 紧急禁用 ============

    private void Emergency_Click(object sender, RoutedEventArgs e)
    {
        bool wasDisabled = TriggerService.EmergencyDisabled;
        TriggerService.SetEmergencyDisabled(!wasDisabled);
        UpdateEmergency();
        UpdateHint();
        ConfirmDialog.Info(Window.GetWindow(this), "自动触发",
            wasDisabled ? "已恢复自动触发（紧急禁用解除）。" : "已暂停全部自动触发（紧急禁用生效）。");
    }

    // ============ 规则 CRUD ============

    private RuleRow? SelectedRule() => RuleGrid.SelectedItem as RuleRow;

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var result = TriggerRuleEditDialog.Show(Window.GetWindow(this), new TriggerRule());
        if (result == null) return;
        result.Id = _file.NewId();
        _file.Rules.Add(result);
        Save(result.Id);
    }

    private void EditRule(RuleRow row)
    {
        var result = TriggerRuleEditDialog.Show(Window.GetWindow(this), row.Source.Clone());
        if (result == null) return;
        result.Id = row.Source.Id;   // 编辑不换 Id
        int index = _file.Rules.IndexOf(row.Source);
        if (index < 0) return;
        _file.Rules[index] = result;
        Save(result.Id);
    }

    private void DeleteRule(RuleRow row)
    {
        if (!ConfirmDialog.Confirm(Window.GetWindow(this), "删除规则",
                $"确定删除「{row.Note}」吗？（只删这条规则，动作本身不受影响）", danger: true)) return;
        _file.Rules.Remove(row.Source);
        Save();
    }

    private void MoveRule(RuleRow row, int delta)
    {
        int from = _file.Rules.IndexOf(row.Source);
        int to = from + delta;
        if (from < 0 || to < 0 || to >= _file.Rules.Count) return;
        (_file.Rules[from], _file.Rules[to]) = (_file.Rules[to], _file.Rules[from]);
        Save(row.Source.Id);
    }

    private void CopyRule_Click(object sender, RoutedEventArgs e) => CopyRule(SelectedRule());

    private static void CopyRule(RuleRow? row)
    {
        if (row == null) return;
        _ruleClipboard.Clear();
        _ruleClipboard.Add(row.Source.Clone());
    }

    private void PasteRule_Click(object sender, RoutedEventArgs e)
    {
        if (_ruleClipboard.Count == 0) return;
        string? last = null;
        foreach (var src in _ruleClipboard)
        {
            var copy = src.Clone();
            copy.Id = _file.NewId();
            copy.Note = string.IsNullOrWhiteSpace(src.Note) ? "规则 副本" : src.Note + " 副本";
            _file.Rules.Add(copy);
            last = copy.Id;
        }
        Save(last);
    }

    // ============ 启用勾选框（一次点击生效） ============

    private void RuleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not CheckBox box || box.DataContext is not RuleRow row) return;
        // 直接按勾选框状态写回，不依赖 TwoWay 绑定的更新时机（否则「勾上了、存盘还是旧值」）。
        row.Enabled = box.IsChecked == true;
        Dispatcher.BeginInvoke(new Action(() => Save(row.Source.Id)));
    }

    /// <summary>
    /// DataGridCell 在「自己还没有键盘焦点」时会把这下 MouseDown 标记成已处理（先抢焦点），
    /// 单元格里 Focusable="False" 的启用勾选框就收不到点击。预览阶段先把单元格焦点给上。
    /// </summary>
    private void RuleGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var cell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell != null && !cell.IsKeyboardFocusWithin) cell.Focus();
    }

    // ============ 操作列：5 个常驻图标 + 双击整行编辑 ============

    /// <summary>
    /// 5 个图标共用这个分发器：动作标识走 <c>CommandParameter</c>（不是 Tag——Tag 被 BtnGhost
    /// 当悬停色用）。菜单时代要「推迟到菜单关掉之后再弹模态框」，现在没有菜单，直接执行即可。
    /// </summary>
    private void RowAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not RuleRow row) return;
        switch ((el as Button)?.CommandParameter as string)
        {
            case "edit": EditRule(row); break;
            case "copy": CopyRule(row); break;
            case "delete": DeleteRule(row); break;
            case "up": MoveRule(row, -1); break;
            case "down": MoveRule(row, 1); break;
        }
    }

    /// <summary>双击整行 = 编辑（用户嫌「⋯ 要小心翼翼点」）。</summary>
    private void RuleGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DoubleClickEditRow(e.OriginalSource as DependencyObject) is { } row) EditRule(row);
    }

    /// <summary>
    /// 双击是否落在「可编辑的数据行」上：避开「启用」勾选框、操作列图标按钮，
    /// 表头 / 空白取不到 <see cref="DataGridRow"/> 也一律返回 null。
    /// 抽成纯函数方便无头冒烟直接断言，不用伪造鼠标事件 / 弹模态框。
    /// </summary>
    internal static RuleRow? DoubleClickEditRow(DependencyObject? src)
    {
        if (src == null) return null;
        if (FindAncestor<CheckBox>(src) != null || FindAncestor<Button>(src) != null) return null;
        return FindAncestor<DataGridRow>(src)?.DataContext as RuleRow;
    }

    // ============ 搜索 ============

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        CollectionViewSource.GetDefaultView(_rows)?.Refresh();
    }

    private bool FilterRow(object item)
    {
        if (item is not RuleRow row) return false;
        string query = SearchBox.Text.Trim();
        if (query.Length == 0) return true;
        return row.Matches(query);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source != null && source is not T) source = VisualTreeHelper.GetParent(source);
        return source as T;
    }

    // ============ 行模型 ============

    public sealed class RuleRow : INotifyPropertyChanged
    {
        public RuleRow(TriggerRule rule) => Source = rule;

        public event PropertyChangedEventHandler? PropertyChanged;

        public TriggerRule Source { get; }

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

        public string Note => string.IsNullOrWhiteSpace(Source.Note) ? "（未命名）" : Source.Note;

        public string KindText => Source.Kind switch
        {
            TriggerKind.startup => "启动后",
            TriggerKind.@event => "事件 · " + EventTypeName,
            _ => "定时",
        };

        /// <summary>事件类型中文短名（配置存英文键，显示名在 <see cref="OptionLabels"/>）。</summary>
        private string EventTypeName => string.IsNullOrWhiteSpace(Source.EventType)
            ? "未设类型"
            : OptionLabels.Of(Source.EventType);

        /// <summary>条件摘要：把规则字段拼成一行短文本（照 StepSummary 的写法）。</summary>
        public string Summary
        {
            get
            {
                var parts = new List<string>();
                switch (Source.Kind)
                {
                    case TriggerKind.timer:
                        // 摘要优先级与 TriggerScheduler.FirstDue 一致：Cron > 间隔 > 每天时刻。
                        if (!string.IsNullOrWhiteSpace(Source.Cron))
                            parts.Add("Cron " + Source.Cron);
                        else if (Source.IntervalMs is > 0)
                            parts.Add("间隔 " + (Source.IntervalMs.Value / 60000.0).ToString(CultureInfo.InvariantCulture) + " 分钟");
                        else if (!string.IsNullOrWhiteSpace(Source.DailyAt))
                            parts.Add("每天 " + Source.DailyAt);
                        else
                            parts.Add("（未设时间）");
                        break;
                    case TriggerKind.startup:
                        parts.Add(Source.DelaySeconds > 0 ? $"延迟 {Source.DelaySeconds} 秒" : "启动即运行");
                        if (Source.OnlyFirstStartToday) parts.Add("仅每日首次");
                        if (Source.OnlyOnAutoStart) parts.Add("仅开机自启时");
                        break;
                    default:   // @event：事件名 + 按 §13.5 拼的参数 + 命中即停
                        parts.Add(EventTypeName);
                        parts.AddRange(EventParamSummary());
                        if (Source.SkipFurther) parts.Add("命中即停");
                        break;
                }
                return string.Join(" · ", parts);
            }
        }

        /// <summary>
        /// 按 <c>docs/AUTO_TRIGGER.md</c> §13.5 把事件参数拼成几段短文本。
        /// 只列「条件类」参数：<c>excludeOwn</c> / <c>ignoreOwnWrites</c> / <c>includeSubdirectories</c>
        /// 这类布尔默认项不进摘要（否则每行都挂一串 true，反而看不出用户设了什么）。
        /// </summary>
        private IEnumerable<string> EventParamSummary()
        {
            var p = Source.Params;
            if (p == null || p.Count == 0) yield break;
            string Val(string key) => p.TryGetValue(key, out string? v) ? (v ?? "").Trim() : "";

            switch ((Source.EventType ?? "").ToLowerInvariant())
            {
                case "window.foreground":
                    if (Val("title") is { Length: > 0 } t) yield return "标题含 " + t;
                    if (Val("process") is { Length: > 0 } pr) yield return "进程 " + pr;
                    if (Val("class") is { Length: > 0 } c) yield return "类名 " + c;
                    if (Val("prevTitle") is { Length: > 0 } pt) yield return "前窗含 " + pt;
                    if (Val("prevProcess") is { Length: > 0 } pp) yield return "前窗进程 " + pp;
                    break;
                case "clipboard.change":
                    if (Val("text") is { Length: > 0 } ct) yield return "文本含 " + ct;
                    if (Val("formats") is { Length: > 0 } f) yield return "格式 " + f;
                    break;
                case "file.change":
                    string dir = Val("path"), filter = Val("filter");
                    if (dir.Length > 0)
                        yield return (filter.Length > 0 && filter != "*.*")
                            ? dir.TrimEnd('\\', '/') + "\\" + filter : dir;
                    if (Val("events") is { Length: > 0 } ev) yield return FileEventNames(ev);
                    if (Val("pathMatch") is { Length: > 0 } pm) yield return "路径含 " + pm;
                    break;
            }
        }

        /// <summary>created;changed;deleted;renamed → 新建·修改·删除·重命名（未知键原样保留）。</summary>
        private static string FileEventNames(string value)
        {
            var names = new List<string>();
            foreach (string raw in value.Split(';'))
            {
                string key = raw.Trim().ToLowerInvariant();
                if (key.Length == 0) continue;
                names.Add(key switch
                {
                    "created" => "新建",
                    "changed" => "修改",
                    "deleted" => "删除",
                    "renamed" => "重命名",
                    _ => raw.Trim(),
                });
            }
            return string.Join("·", names);
        }

        public string ActionName => string.IsNullOrWhiteSpace(Source.ActionRef)
            ? "（未选动作）"
            : BindingRef.Describe(Source.ActionRef);

        public bool Matches(string query) =>
            Note.Contains(query, StringComparison.OrdinalIgnoreCase)
            || KindText.Contains(query, StringComparison.OrdinalIgnoreCase)
            || Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
            || ActionName.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>触发记录表格的一行（只读显示模型，不写回）。</summary>
    public sealed class HistoryRow
    {
        internal HistoryRow(TriggerRecord r)
        {
            Time = r.Time;
            Rule = string.IsNullOrWhiteSpace(r.Note) ? r.RuleId : r.Note;
            Event = string.IsNullOrWhiteSpace(r.EventType) ? "定时 / 启动后" : OptionLabels.Of(r.EventType);
            Result = r.Ran ? "运行" : "丢弃 · " + r.Reason;
            Elapsed = r.ElapsedMs > 0 ? r.ElapsedMs + " ms" : "—";
        }

        public string Time { get; }
        public string Rule { get; }
        public string Event { get; }
        public string Result { get; }
        public string Elapsed { get; }
    }
}
