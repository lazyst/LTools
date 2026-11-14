using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 动作编辑器（W4）：左「工具箱」· 中「步骤」· 右「变量 + 属性」。
/// </summary>
/// <remarks>
/// <para>
/// 步骤是**扁平行**：嵌套用缩进 +「否则 / 结束如果 / 结束循环」标记行表达；
/// 空分支有一行可选的占位行，选中后从左侧添加即可插进该分支。
/// </para>
/// <para>
/// 工具箱完全由 <see cref="StepRegistry"/> 的 <see cref="UnitSpec"/> 生成：
/// 新增一个动作单元不需要改这个窗口（W4 验收）。
/// </para>
/// <para>保存契约不变：<see cref="Result"/> 由调用方 <c>ConfigStore.AddAction</c> / <c>UpdateAction</c> 落盘。</para>
/// </remarks>
public partial class ActionEditorWindow : Window
{
    private readonly ActionDto? _draft;
    private readonly SubActionDef? _subDef;
    private readonly bool _isSubAction;
    private readonly ActionProgram _program;
    private readonly ObservableCollection<StepRow> _rows = new();
    private readonly List<VariableRowUi> _variables = new();
    private readonly DragVisuals _dragGhost;
    private int _number;

    /// <summary>右栏（变量页）宽度：抽屉收起时回到这个值。动作与子动作都有「方向」列，统一 360。</summary>
    private const double VariablesColumnWidth = 360;

    /// <summary>
    /// 编辑草稿时的资源根（<c>staging/&lt;id&gt;/files</c>）：试运行与校验把它交给图片解析，
    /// 所以 <c>files/x.png</c> 能直接找到。正式动作 / 新建为 null。
    /// </summary>
    public string? ResourceRoot { get; set; }

    // —— 试运行 ——
    private readonly ObservableCollection<TraceRow> _traceRows = new();
    private readonly ObservableCollection<SnapshotRow> _snapshotRows = new();
    private readonly List<StepTrace> _pendingTraces = new();
    private readonly Dictionary<string, TraceRow> _traceRowsByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StepRow> _traceTargets = new(StringComparer.Ordinal);

    /// <summary>上一次试运行结束时各变量的完整文本（「本次 / 上次」对比用，关窗即丢）。</summary>
    private readonly Dictionary<string, string> _previousValues = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _traceFlush;
    private bool _running;

    /// <summary>程序化改变量类型时置位，避免被当成「用户改过」。</summary>
    private bool _syncingKinds;

    /// <summary>改名时同步过步骤引用：等变量名输入框失焦后重建步骤列表（别在每次按键时重建）。</summary>
    private bool _renameTouchedSteps;

    // 拖动排序（子步骤跟着走；标记行 / 空分支占位行不是落点）
    private StepRow? _dragRow;

    /// <summary>这次拖动要移动的行：按在已选中的行上时是整组，否则只有按下的那一行。</summary>
    private List<StepRow> _dragRows = new();

    private int _dragRowIndex;
    private Point _dragOrigin;
    private bool _dragPending;
    private bool _dragging;
    private Border? _insertLine;
    private (List<Step> List, int Index)? _dropTarget;

    // 从工具箱拖动作单元进步骤列表（松手按默认参数直接插入）
    private UnitSpec? _unitSpec;
    private FrameworkElement? _unitSource;
    private Point _unitOrigin;
    private Point _unitPoint;
    private bool _unitPending;
    private bool _unitDragging;
    private bool _suppressUnitClick;
    private DropSpot? _unitDrop;
    private DispatcherTimer? _unitScroll;
    private readonly SearchDebounce _toolboxSearchDebounce;

    public ActionEditorWindow(string title, ActionDto? existing)
        : this(title, existing, null) { }

    /// <summary>
    /// 编辑全局子动作（变量多一列「方向」，用来标记输入 / 输出）。
    /// id 为空时自动生成——id 是对外调用名，生成后不再让用户随手改。
    /// </summary>
    public static ActionEditorWindow ForSubAction(string title, SubActionDef? existing)
    {
        var def = existing?.Clone() ?? new SubActionDef();
        if (string.IsNullOrWhiteSpace(def.Id)) def.Id = SubActionStore.NewId();
        return new ActionEditorWindow(title, null, def);
    }

    private ActionEditorWindow(string title, ActionDto? existing, SubActionDef? subAction)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);
        _toolboxSearchDebounce = new SearchDebounce(150, BuildToolbox);

        _subDef = subAction;
        _isSubAction = subAction != null;

        if (_isSubAction)
        {
            // 子动作与动作同构：步骤 + 变量，差别只在变量的「方向」标记。
            _program = new ActionProgram
            {
                Id = subAction!.Id,
                Name = subAction.Name,
                Icon = subAction.Icon,
                Description = subAction.Description,
            };
            foreach (var variable in subAction.Variables) _program.Variables.Add(variable.Clone());
            foreach (var step in subAction.Steps) _program.Steps.Add(step.Clone());
            _draft = null;

            IconSection.Visibility = Visibility.Collapsed;
            CodeHint.Text = "调用名：" + _program.Id;
            CodeHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "（空子动作）从左侧点一个动作单元开始，例如：设置变量 → 提示弹窗";
        }
        else if (existing != null)
        {
            _draft = existing.Clone();
            _program = _draft.Program?.Clone() ?? new ActionProgram();
            _draft.Program = _program;
        }
        else
        {
            _draft = new ActionDto { Icon = IconCatalog.Default };
            _program = new ActionProgram();
            _draft.Program = _program;
        }

        // 「方向」列两种编辑器都有，但含义不同：子动作是 输入 / 本地 / 输出（子动作的参数与回传），
        // 动作只有 输入 / 本地——输入 = 运行前问你（面板 / 快捷键 / 悬浮都会弹参数框，填过的记住），
        // 输出在动作里没有落点，所以不给这个选项。右栏因此都用 360，否则默认值那一列会被挤没。
        RightColumn.Width = new GridLength(VariablesColumnWidth);
        if (!_isSubAction)
        {
            DirectionHeader.ToolTip = "输入 = 运行前问你（填过的会记住）；本地 = 不问，直接用默认值";
            VariablesHint.Text =
                "步骤写出的变量（输出、设置变量）会自动登记在这里。方向选「输入」的变量会在运行前问你，" +
                "填过的值会记住；「本地」直接用默认值。";
        }

        if (!string.IsNullOrWhiteSpace(title)) Title = title;
        NameBox.Text = _isSubAction ? _program.Name : _draft!.Name;
        string? icon = _isSubAction ? _program.Icon : _draft!.Icon;
        IconPicker.SelectedIcon = string.IsNullOrWhiteSpace(icon) ? IconCatalog.Default : icon;

        StepList.ItemsSource = _rows;
        StepList.PreviewKeyDown += StepList_PreviewKeyDown;

        _dragGhost = new DragVisuals(this, GhostLayer);
        PreviewMouseMove += OnDragMove;
        PreviewMouseLeftButtonUp += OnDragUp;
        PreviewKeyDown += OnDragKeyDown;

        PreviewMouseMove += OnUnitDragMove;
        PreviewMouseLeftButtonUp += OnUnitDragUp;
        PreviewKeyDown += OnEditorKeyDown;

        BuildVariableRows();
        BuildToolbox();
        ReloadRows();
        CaptureBaseline();

        TraceList.ItemsSource = _traceRows;
        SnapshotList.ItemsSource = _snapshotRows;

        // 关窗时别把正在跑的试运行留在后台（它会继续操作键鼠）。
        Closed += (_, _) =>
        {
            _toolboxSearchDebounce.Cancel();
            _traceFlush?.Stop();
            if (_running) AutomationRunner.Stop();
            MacroRecorder.RecordingStopped -= OnMacroRecordingStopped;
            MacroRecorder.MacroSaved -= OnMacroSavedRefresh;
            // 关窗时还在重录（窗口是藏起来的，用户看不到）：先收尾保存，别留下无界面的录制。
            if (_macroHidden && MacroRecorder.IsRecording)
            {
                _macroHidden = false;
                MacroRecorder.Stop(force: true);
            }
            _macroHidden = false;
        };

        MacroRecorder.RecordingStopped += OnMacroRecordingStopped;
        MacroRecorder.MacroSaved += OnMacroSavedRefresh;

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>确定后的动作；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>子动作模式下的结果；取消时为 null。</summary>
    public SubActionDef? SubResult { get; private set; }

    public sealed record KindChoice(ValueKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    public sealed record DirectionChoice(VariableDirection Direction, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed class VariableRowUi
    {
        public TextBox Name = null!;
        public ComboBox Type = null!;
        public ComboBox? Direction;
        public TextBox Default = null!;
        public Grid Host = null!;
        public TextBlock OriginText = null!;

        /// <summary>这个变量是谁写进来的（「第 2 步 · 截图（输出 file）」/「手动创建」）。</summary>
        public string Origin = "";

        /// <summary>类型由动作单元声明决定（用户改过类型就为 false，不再被步骤覆盖）。</summary>
        public bool AutoType;

        /// <summary>上一次确认过的变量名（改名时用它找出要同步的 ${旧名}）。</summary>
        public string LastName = "";
    }
}

/// <summary>步骤列表的一行：真实步骤 / 标记行 / 空分支占位行。</summary>
public sealed class StepRow
{
    public Step? Step { get; init; }

    /// <summary>该步骤所在的列表（新增 / 删除 / 移动都作用在这里）。</summary>
    public List<Step>? Owner { get; init; }

    public int Index { get; init; }

    public int Depth { get; init; }

    /// <summary>带子步骤的块（如果 / 循环）才有折叠箭头。</summary>
    public bool HasChildren { get; init; }

    /// <summary>这个块现在是折叠的（子步骤不占行）。</summary>
    public bool IsCollapsed { get; init; }

    public Visibility FoldVisibility =>
        HasChildren ? Visibility.Visible : Visibility.Collapsed;

    public string FoldGlyph => IsCollapsed ? "▸" : "▾";

    public string FoldTip => IsCollapsed ? "展开子步骤" : "折叠子步骤（收起这一块）";

    public bool IsMarker { get; init; }

    public bool IsPlaceholder { get; init; }

    public string MarkerText { get; init; } = "";

    public string Number { get; init; } = "";

    public string Glyph { get; init; } = "";

    public string Title { get; init; } = "";

    public string Summary { get; init; } = "";

    public string Output { get; init; } = "";

    public Visibility OutputVisibility =>
        Output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ButtonVisibility =>
        Step != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>校验标记：<c>✕</c> = 这一步有错误、<c>！</c> = 只有提醒；空 = 没问题。</summary>
    public string IssueMark { get; init; } = "";

    public Brush IssueBrush { get; init; } = Brushes.Transparent;

    /// <summary>悬停提示：这一步的全部问题逐行列出。</summary>
    public string IssueTip { get; init; } = "";

    public Visibility IssueVisibility =>
        IssueMark.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public Thickness MarkerIndent => new(Depth * 18 + 44, 0, 0, 1);
}

/// <summary>
/// 试运行轨迹的一行（按**步骤路径**聚合：循环跑十轮，同一行只留一条，右侧显示 ×N）。
/// </summary>
/// <remarks>
/// 轨迹在后台线程到达，面板攒批后在这里更新——所以属性都带变更通知，
/// 循环里反复命中同一行时只是「改一个属性」，不重建列表。
/// </remarks>
public sealed class TraceRow : INotifyPropertyChanged
{
    private string _glyph = "·";
    private Brush _brush = Brushes.Gray;
    private string _elapsed = "";
    private string _detail = "";
    private string _toolTip = "";
    private bool _isFailed;
    private int _count = 1;

    public TraceRow(StepTrace trace)
    {
        Path = trace.Path;
        Label = trace.Label;
        Unit = trace.UnitName;
        Merge(trace);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Path { get; }

    public string Label { get; }

    public string Unit { get; }

    /// <summary>状态图标：✓ 成功、✕ 失败、! 失败但继续、■ 被停止。</summary>
    public string Glyph
    {
        get => _glyph;
        private set => Set(ref _glyph, value, nameof(Glyph));
    }

    public Brush Brush
    {
        get => _brush;
        private set => Set(ref _brush, value, nameof(Brush));
    }

    /// <summary>耗时（末次），重复执行时带 ×N。</summary>
    public string Elapsed
    {
        get => _elapsed;
        private set => Set(ref _elapsed, value, nameof(Elapsed));
    }

    /// <summary>错误信息优先，其次本步写入的变量（<c>text = 你好</c>），最后是实际拿到的参数。</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value, nameof(Detail));
    }

    /// <summary>悬停看全文：位置 / 耗时 / 解析后的参数 / 输出 / 错误（单行 Detail 放不下这些）。</summary>
    public string ToolTip
    {
        get => _toolTip;
        private set => Set(ref _toolTip, value, nameof(ToolTip));
    }

    /// <summary>是否失败（失败定位用）。</summary>
    public bool IsFailed
    {
        get => _isFailed;
        private set => Set(ref _isFailed, value, nameof(IsFailed));
    }

    /// <summary>同一步骤又执行了一次（循环）：更新耗时 / 状态 / 详情，并累加次数。</summary>
    public void Merge(StepTrace trace)
    {
        if (_count > 1 || _elapsed.Length > 0) _count++;

        (Glyph, IsFailed, Brush) = trace.Status switch
        {
            StepTraceStatus.ok => ("✓", false, Done),
            StepTraceStatus.failed => ("✕", true, Fail),
            StepTraceStatus.continued => ("!", true, Warn),
            // 干跑跳过：不是成功也不是失败，用淡色的「–」。
            StepTraceStatus.skipped => ("–", false, Muted),
            _ => ("■", false, Muted),
        };

        Elapsed = _count > 1 ? $"{trace.ElapsedMs} ms ×{_count}" : $"{trace.ElapsedMs} ms";
        if (trace.Attempt > 1) Elapsed += $" ⟳{trace.Attempt}";

        if (!string.IsNullOrEmpty(trace.Error)) Detail = trace.Error!;
        else if (trace.Outputs.Count > 0) Detail = Compact(trace.Outputs);
        else Detail = Compact(trace.Inputs);

        ToolTip = BuildToolTip(trace, _count);
    }

    /// <summary>参数 / 输出的单行摘要（每项值压缩到 80 字，整行由界面再截断）。</summary>
    internal static string Compact(IReadOnlyDictionary<string, Value> values) =>
        string.Join(" · ", values.Select(p => $"{p.Key} = {Short(p.Value)}"));

    private static string BuildToolTip(StepTrace trace, int count)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{trace.Label}　{trace.UnitName}");
        if (trace.Status == StepTraceStatus.skipped) sb.AppendLine("干跑：这一步会动键鼠 / 弹窗口，没有执行");
        sb.AppendLine($"耗时 {trace.ElapsedMs} ms" + (count > 1 ? $"（同一行执行了 {count} 次）" : ""));
        if (trace.Attempt > 1 || trace.MaxAttempts > 1)
            sb.AppendLine($"重试：第 {trace.Attempt} 次尝试（这一步最多 {trace.MaxAttempts} 次）");
        AppendSection(sb, "参数（解析后）", trace.Inputs);
        AppendSection(sb, "输出", trace.Outputs);
        if (trace.Error is { Length: > 0 }) sb.AppendLine($"错误：{trace.Error}");
        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(System.Text.StringBuilder sb,
        string title, IReadOnlyDictionary<string, Value> values)
    {
        if (values.Count == 0) return;

        sb.AppendLine(title);
        int shown = 0;
        foreach (var pair in values)
        {
            if (shown++ >= 16)
            {
                sb.AppendLine($"  …（还有 {values.Count - 16} 项）");
                break;
            }
            sb.AppendLine($"  {pair.Key} = {Short(pair.Value)}");
        }
    }

    private static string Short(Value value)
    {
        string text;
        try { text = value.AsText(); }
        catch { return "(无法显示)"; }

        text = text.Replace("\r", " ").Replace("\n", " ");
        return text.Length > 80 ? text[..80] + "…" : text;
    }

    private static readonly Brush Done = Freeze(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush Fail = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush Warn = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush Muted = Freeze(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>
/// 变量快照的一行：本次运行结束时的值，以及上一次试运行的值（变了才加粗 + 圆点）。
/// </summary>
/// <remarks>
/// 「上次」只在同一个编辑器窗口里记（关掉就没了）：要回答的是「改了一处，再跑一次，哪个变量变了」，
/// 不是长期历史——长期要看动作历史（<see cref="LTools.Core.Storage.ActionHistory"/>）。
/// </remarks>
public sealed class SnapshotRow
{
    public SnapshotRow(string name, string value, string previous, bool changed)
    {
        Name = name;
        Value = value;
        Previous = previous;
        Changed = changed;
    }

    public string Name { get; }

    public string Value { get; }

    /// <summary>上一次试运行结束时的值；第一次运行是空串。</summary>
    public string Previous { get; }

    public bool Changed { get; }

    /// <summary>变过的行前面点一个圆点（灰阶主题里比颜色更可靠）。</summary>
    public string Marker => Changed ? "•" : "";

    public FontWeight Weight => Changed ? FontWeights.SemiBold : FontWeights.Normal;

    public string ValueTip => Previous.Length == 0
        ? Value
        : $"本次：{Value}\n上次：{Previous}";

    /// <summary>
    /// 生成整张表：按变量名排序，碰上「上次」就比一次（按完整文本比，不是按显示用的截断串）。
    /// 抽成纯函数是为了能直接断言，不依赖窗口。
    /// </summary>
    internal static List<SnapshotRow> Build(IReadOnlyDictionary<string, Value> variables,
        IReadOnlyDictionary<string, string>? previous)
    {
        var rows = new List<SnapshotRow>();
        foreach (var pair in variables.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            string full = TextOf(pair.Value);
            string? was = null;
            previous?.TryGetValue(pair.Key, out was);
            bool changed = was != null && !string.Equals(was, full, StringComparison.Ordinal);
            rows.Add(new SnapshotRow(pair.Key, Clip(full), was == null ? "" : Clip(was), changed));
        }

        return rows;
    }

    /// <summary>变量值取文本；取不出来给占位，别让一个变量毁掉整张表。</summary>
    internal static string TextOf(Value value)
    {
        try { return value.AsText(); }
        catch { return "(无法显示)"; }
    }

    private static string Clip(string text) => text.Length > 300 ? text[..300] + "…" : text;
}
