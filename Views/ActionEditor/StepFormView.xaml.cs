using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Script;
using LTools.Views.Controls;

namespace LTools.Views;

/// <summary>
/// 按 <see cref="UnitSpec"/> 生成的步骤参数表单（可复用的控件，不是窗口）：
/// 一个动作单元一套表单，不写任何按动作单元分支的代码。
/// </summary>
/// <remarks>
/// <para>
/// 输入行来自 <see cref="UnitSpec.Inputs"/>，编辑器形态由 <see cref="InputSpec.Editor"/> 决定；
/// 每个输入框下方有一行 <see cref="InputHint"/> 说明「该填什么」。
/// <c>VisibleWhen</c> 支持 <c>key == 'value'</c>（多个用 <c>||</c> 连接，值可不带引号），
/// 切换枚举值时实时收起 / 展开。
/// 「调用子动作」的按名参数由 <see cref="UnitSpec.DynamicInputs"/> + 子动作变量表生成。
/// </para>
/// <para>
/// 两个宿主共用它：动作编辑器右栏的**抽屉**（日常编辑，步骤列表始终看得见）与
/// <see cref="StepEditorDialog"/>（无窗口上下文时的模态壳）。所以这里**不许碰窗口**——
/// 不要 <c>DialogResult</c> / <c>Window.GetWindow</c> / <c>Close()</c>，
/// 「保存 / 取消」一律通过 <see cref="Saved"/> / <see cref="Cancelled"/> 交给宿主处理。
/// </para>
/// <para>
/// 内联取值由宿主下钻（<see cref="InlineEditor"/>）：抽屉里默认推入一层新表单并保留面包屑，
/// 没接这个钩子（独立窗口）时退回弹一个 <see cref="StepEditorDialog"/>。
/// </para>
/// </remarks>
public partial class StepFormView : UserControl
{
    private sealed record Row(InputSpec Spec, FrameworkElement Host, IArgEditor Editor);

    /// <summary>输出区的一行：勾选 + 类型徽标 + 变量名（+ 换成已有变量的 ▾）+ 说明。</summary>
    private sealed record OutputRow(string Key, CheckBox Toggle, TextBox Box, Button Pick,
        Action Refresh);

    private readonly Step _step;
    private readonly UnitSpec _spec;
    private readonly bool _inline;
    private readonly List<Row> _rows = new();
    private readonly List<OutputRow> _outputs = new();
    private readonly List<(VariableDef Variable, ValueField Editor)> _dynamicInputs = new();
    private readonly List<OutputRow> _dynamicOutputs = new();
    private readonly StackPanel _dynamicPanel = new() { Margin = new Thickness(0, 4, 0, 0) };

    /// <summary>「高级参数」折叠区（超时 / 轮询间隔 / 相似度这类调优项）。</summary>
    private readonly StackPanel _advancedPanel = new() { Margin = new Thickness(0, 2, 0, 0) };

    private Button? _advancedHeader;
    private bool _advancedExpanded;
    private int _advancedCount;

    /// <summary>枚举行按英文键改选中项（框选完把「区域」切到 rect 用）。</summary>
    private readonly Dictionary<string, Action<string>> _enumSetters =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>固定区域行按文本写入（框选结果落到 <c>x,y,w,h</c>）。</summary>
    private readonly Dictionary<string, Action<string>> _rectSetters =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>每个值字段按文本写入（屏幕取点一次填好同组的 X / Y）。</summary>
    private readonly Dictionary<string, Action<string>> _fieldSetters =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _syncing;

    /// <summary>上一次生成动态输入 / 输出时用的子动作 Id（换了才重建）。</summary>
    private string _dynamicSubAction = "";

    public StepFormView(Step step, bool inline = false,
        ActionProgram? program = null, Func<IEnumerable<string>>? variableNames = null,
        string? resourceRoot = null, Func<IReadOnlyList<VariableInfo>>? variableInfos = null)
    {
        InitializeComponent();

        // 必须在 BuildInputs 之前就位：「调用子动作」的子动作下拉、`InputEditor.variable`
        // 的候选名、取值动作单元里再嵌一层编辑器都依赖它们。放在对象初始化器里会在构造
        // 之后才赋值，表单已经按 null 建好（子动作就退化成只能手填 id 的空文本框）。
        Program = program;
        VariableNameSource = variableNames;
        VariableInfoSource = variableInfos;
        ResourceRoot = resourceRoot;

        _step = step.Clone();
        _spec = StepRegistry.Default.Get(_step.Unit).Spec;
        _inline = inline;

        HeaderText.Text = _spec.Name;
        CategoryText.Text = _spec.Category ?? "";
        CategoryBadge.Visibility = string.IsNullOrWhiteSpace(_spec.Category)
            ? Visibility.Collapsed : Visibility.Visible;
        DescText.Text = _spec.Description ?? "";
        DescText.Visibility = string.IsNullOrWhiteSpace(_spec.Description)
            ? Visibility.Collapsed : Visibility.Visible;

        BuildInputs();
        BuildOutputs();
        BuildOptions();

        if (inline)
        {
            OutputSection.Visibility = Visibility.Collapsed;
            OptionSection.Visibility = Visibility.Collapsed;
        }

        Loaded += (_, _) => FocusFirstEditor();
        PreviewKeyDown += StepFormView_PreviewKeyDown;
    }

    /// <summary>用户按了「确定 / 上一步 / 下一步 / 保存并继续添加」并且内容已写进 <see cref="Result"/>。</summary>
    internal event Action? Saved;

    /// <summary>用户按了「取消」（宿主负责关闭抽屉 / 窗口）。</summary>
    internal event Action? Cancelled;

    /// <summary>
    /// 宿主接管表单里某个参数的内联取值编辑：参数是「字段 / 当前内联步骤 / 参数标签」。
    /// 抽屉接这个事件推入一层并保留面包屑；**为空时**让 <see cref="ValueField"/> 自己弹窗口
    /// （模态壳 <see cref="StepEditorDialog"/> 就是这种情况）。
    /// </summary>
    internal Action<ValueField, Step, string>? InlineEditorRequested
    {
        get => _inlineHost;
        set
        {
            _inlineHost = value;
            RewireInlineEditors();
        }
    }

    private Action<ValueField, Step, string>? _inlineHost;

    /// <summary>
    /// 把宿主钩子接到已经建好的每个值字段上（宿主通常在构造之后才挂钩子）。
    /// 钩子为空时保持字段的 <c>InlineEditor</c> 为 null，<see cref="ValueField"/> 才会退回弹窗口。
    /// </summary>
    private void RewireInlineEditors()
    {
        foreach (Row row in _rows)
            if (row.Editor.Field is { } field) field.InlineEditor = _inlineHost;

        foreach (var (_, editor) in _dynamicInputs) editor.InlineEditor = _inlineHost;
    }

    /// <summary>确定后的步骤（取消时为 null）。</summary>
    public Step? Result { get; private set; }

    /// <summary>-1 / +1 = 用户点了上一步 / 下一步（编辑器据此打开相邻步骤）；0 = 正常确定。</summary>
    public int NavigateDelta { get; private set; }

    /// <summary>「保存并继续添加」：添加流程里再开一个同动作单元的空表单。</summary>
    public bool ContinueAdding { get; private set; }

    /// <summary>当前正在编辑的动作单元（宿主做面包屑 / 标题用）。</summary>
    internal UnitSpec Spec => _spec;

    /// <summary>编辑器在 ShowDialog 之前调用：显示上一步 / 下一步（没有相邻步骤时对应按钮隐藏）。</summary>
    internal void EnableNavigation(bool canPrevious, bool canNext)
    {
        PrevButton.Visibility = canPrevious ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = canNext ? Visibility.Visible : Visibility.Collapsed;
        NavPanel.Visibility = canPrevious || canNext ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>添加新步骤时调用：显示「保存并继续添加」。</summary>
    internal void EnableContinueAdding() => ContinueButton.Visibility = Visibility.Visible;

    /// <summary>
    /// 按「确定」的规则保存一次（校验必填、把控件读回 <see cref="Result"/>），不触发 <see cref="Saved"/>。
    /// 宿主在自己要关掉（窗口「确定」/ 关闭时选保存）时用它把抽屉里正在编辑的表单收尾。
    /// </summary>
    internal bool TrySaveStep()
    {
        if (!TrySave()) return false;
        NavigateDelta = 0;
        ContinueAdding = false;
        return true;
    }

    /// <summary>用户在这个表单里动过任何东西（换步骤时用来判断要不要提醒一句）。</summary>
    internal bool HasEdits { get; private set; }

    /// <summary>可用于变量编辑的变量名（由动作编辑器透传，构造时就要给）。</summary>
    internal Func<IEnumerable<string>>? VariableNameSource { get; }

    /// <summary>带类型 / 来源的变量表（▾ 菜单里显示「文本 · 第 2 步写」）。</summary>
    internal Func<IReadOnlyList<VariableInfo>>? VariableInfoSource { get; }

    /// <summary>动作体（解析「本动作」子动作、内联取值编辑用，构造时就要给）。</summary>
    internal ActionProgram? Program { get; }

    /// <summary>草稿资源根（<c>staging/&lt;id&gt;/files</c>）：模板图缩略图与内联取值编辑器用它解析 <c>files/x.png</c>。</summary>
    internal string? ResourceRoot { get; }

}
