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
using LTools.Features.Migration;
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
/// 新增一个单元不需要改这个窗口（W4 验收）。
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
    private DispatcherTimer? _traceFlush;
    private bool _running;

    /// <summary>程序化改变量类型时置位，避免被当成「用户改过」。</summary>
    private bool _syncingKinds;

    // 拖动排序（只移动步骤本身，子步骤跟着走；标记行 / 空分支占位行不是落点）
    private StepRow? _dragRow;
    private int _dragRowIndex;
    private Point _dragOrigin;
    private bool _dragPending;
    private bool _dragging;
    private Border? _insertLine;
    private (List<Step> List, int Index)? _dropTarget;

    // 从工具箱拖单元进步骤列表（松手按默认参数直接插入）
    private UnitSpec? _unitSpec;
    private FrameworkElement? _unitSource;
    private Point _unitOrigin;
    private Point _unitPoint;
    private bool _unitPending;
    private bool _unitDragging;
    private bool _suppressUnitClick;
    private DropSpot? _unitDrop;
    private DispatcherTimer? _unitScroll;

    public ActionEditorWindow(string title, ActionDto? existing)
        : this(title, existing, null) { }

    /// <summary>
    /// 编辑全局子动作（变量多一列「方向」，用来标记输入 / 输出）。
    /// id 为空时自动生成——id 是对外调用名，生成后不再让用户随手改。
    /// </summary>
    public static ActionEditorWindow ForSubAction(string title, SubActionDef? existing)
    {
        var def = existing?.Clone() ?? new SubActionDef();
        if (string.IsNullOrWhiteSpace(def.Id)) def.Id = NewSubActionId();
        return new ActionEditorWindow(title, null, def);
    }

    private ActionEditorWindow(string title, ActionDto? existing, SubActionDef? subAction)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

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
            foreach (var child in subAction.SubActions) _program.SubActions.Add(child.Clone());
            _draft = null;

            IconSection.Visibility = Visibility.Collapsed;
            CodeHint.Text = "调用名：" + _program.Id;
            CodeHint.Visibility = Visibility.Visible;
            EmptyHint.Text = "（空子动作）从左侧点一个单元开始，例如：设置变量 → 提示弹窗";
            // 多一列「方向」，右栏放宽一点，否则默认值、图标会被挤没
            RightColumn.Width = new GridLength(360);
        }
        else if (existing != null)
        {
            _draft = existing.Clone();
            _program = ActionPrograms.For(_draft).Clone();
            _draft.Program = _program;
            DirectionColumn.Width = new GridLength(0);
            DirectionHeader.Visibility = Visibility.Collapsed;
        }
        else
        {
            _draft = new ActionDto { Type = ActionType.composite, Icon = IconCatalog.Default };
            _program = new ActionProgram();
            _draft.Program = _program;
            DirectionColumn.Width = new GridLength(0);
            DirectionHeader.Visibility = Visibility.Collapsed;
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

        TraceList.ItemsSource = _traceRows;
        SnapshotList.ItemsSource = _snapshotRows;

        // 关窗时别把正在跑的试运行留在后台（它会继续操作键鼠）。
        Closed += (_, _) =>
        {
            _traceFlush?.Stop();
            if (_running) AutomationRunner.Stop();
        };

        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>确定后的动作；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>子动作模式下的结果；取消时为 null。</summary>
    public SubActionDef? SubResult { get; private set; }

    private static string NewSubActionId() =>
        "sub-" + Guid.NewGuid().ToString("N")[..8];

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

        /// <summary>类型由单元声明决定（用户改过类型就为 false，不再被步骤覆盖）。</summary>
        public bool AutoType;
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

    public Thickness Indent => new(Depth * 18, 0, 0, 0);

    public Thickness MarkerIndent => new(Depth * 18 + 26, 0, 0, 1);
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

    /// <summary>错误信息优先，其次是本步写入的变量（<c>text = 你好</c>）。</summary>
    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value, nameof(Detail));
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
            _ => ("■", false, Muted),
        };

        Elapsed = _count > 1 ? $"{trace.ElapsedMs} ms ×{_count}" : $"{trace.ElapsedMs} ms";

        Detail = !string.IsNullOrEmpty(trace.Error)
            ? trace.Error!
            : string.Join(" · ", trace.Outputs.Select(p => $"{p.Key} = {Short(p.Value)}"));
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

/// <summary>变量快照的一行（运行结束时的值）。</summary>
public sealed record SnapshotRow(string Name, string Value);
