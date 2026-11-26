using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>变量下拉里的一行：名字 + 类型 + 谁写进来的。</summary>
public sealed record VariableInfo(string Name, ValueKind Kind, string Origin);

/// <summary>
/// 「从列表选…」里的一项：<see cref="Value"/> 是存进步骤的字面量，
/// <see cref="Label"/> 是界面上看到的名字，<see cref="Group"/> 只作副标题 / 搜索词。
/// </summary>
internal sealed record StepChoice(string Value, string Label, string Group = "");

/// <summary>
/// 一个参数值 = 一个输入框 + 右侧「▾ 来源」。
/// </summary>
/// <remarks>
/// <para>
/// 取代旧的「文本模板 / 变量 / 取值动作单元」三态下拉 + 右侧「变量」按钮：来源全部收进 ▾ 菜单，
/// 菜单里只列这个参数适用的项（由 <see cref="InputSpec.PickerSet"/> 决定），不适用的根本不出现。
/// </para>
/// <para>
/// 值语义没变，仍是 <see cref="StepArg"/> 的三态：字面量（可含 <c>${变量}</c>）、变量引用、
/// 内联取值步骤。所以引擎、摘要、校验都不用改。
/// </para>
/// </remarks>
public partial class ValueField : UserControl
{
    private bool _loading;
    private Step? _inline;
    private Value? _listLiteral;
    private InputSpec? _spec;

    /// <summary>整串就是 <c>${名}</c> 时记下名字，保存时写成变量引用（保留 list / object 类型）。</summary>
    private string? _boundVariable;

    private readonly List<MenuRow> _rows = new();
    private MenuRow? _active;
    private bool _showingUnits;

    /// <summary>
    /// 弹层打开期间挂上的宿主窗口：<c>StaysOpen=True</c> 之后关闭由我们自己管
    /// （点外部 / 滚动 / 移窗 / 失焦），不再用「刚关就别再开」的冷却补丁。
    /// </summary>
    private Window? _menuWindow;

    /// <summary>当前是否被标成「必填但还空着」（改动内容后自动恢复）。</summary>
    private bool _missing;

    /// <summary>菜单里一行：条目 / 分组标题 / 分隔线；<c>Page</c> 0 = 主列表、1 = 取值动作单元。</summary>
    private enum MenuRowKind { item, header, separator }

    private sealed record MenuRow(
        Border Host,
        MenuRowKind Kind,
        int Page,
        string Search = "",
        Action? Invoke = null,
        bool Nav = false)
    {
        public bool IsItem => Kind == MenuRowKind.item;
    }

    public ValueField()
    {
        InitializeComponent();
        HotkeyCapture.Attach(KeyBox, OnKeyRecorded);
        KeyBox.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (e.Key == Key.Escape) KeyPopup.IsOpen = false;
        }), handledEventsToo: true);
        Menu.Closed += (_, _) =>
        {
            DetachMenuWindow();
            _showingUnits = false;
        };
    }

    /// <summary>值变化（宿主用它刷新可见性 / 摘要 / 保存状态）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 在「区域」参数上框选了一块：把区域交给宿主（写 <c>rect</c> 并把 <c>region</c> 切过去）。
    /// 模板图（<see cref="InputEditor.image"/>）的框选**不触发它**——那只取图，不顺手改搜索区域。
    /// </summary>
    public event Action<Int32Rect>? Captured;

    /// <summary>要在屏幕上取点：参数是屏幕坐标，宿主一次填好同组的 x / y。</summary>
    public event Action<int, int>? PointPicked;

    /// <summary>在屏幕上选了一个控件：宿主一次填好名称 / AutomationId / 类型 / 窗口类名。</summary>
    public event Action<PickedControl>? ControlPicked;

    /// <summary>这个参数的声明（决定编辑器形态与可用来源）。</summary>
    public InputSpec? Spec
    {
        get => _spec;
        set
        {
            _spec = value;
            Pickers = value?.PickerSet ?? (PickerKind.variable | PickerKind.inline | PickerKind.clear);
            ApplySpec();
        }
    }

    /// <summary>可用来源；默认由 <see cref="Spec"/> 推。</summary>
    public PickerKind Pickers { get; private set; } =
        PickerKind.variable | PickerKind.inline | PickerKind.clear;

    /// <summary>期望类型；数字 / 开关的字面量按它转换。</summary>
    public ValueKind? Type { get; set; }

    /// <summary>多行字面量（命令 / 正文 / 按键列表）。</summary>
    public bool Multiline
    {
        get => Box.AcceptsReturn;
        set
        {
            Box.AcceptsReturn = value;
            Box.TextWrapping = value ? TextWrapping.Wrap : TextWrapping.NoWrap;
            Box.MinHeight = value ? 76 : 30;
            Box.VerticalContentAlignment = value ? VerticalAlignment.Top : VerticalAlignment.Center;
            Box.VerticalScrollBarVisibility =
                value ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
        }
    }

    /// <summary>
    /// 「变量名」输入框（<see cref="InputEditor.variable"/>）：框里只放名字，
    /// 保存成变量引用而不是 <c>${名}</c> 文本。
    /// </summary>
    public bool VariableNameMode { get; set; }

    /// <summary>内联取值编辑时可见的子动作库与变量（宿主透传）。</summary>
    internal ActionProgram? Program { get; set; }

    internal Func<IEnumerable<string>>? VariableNameSource { get; set; }

    /// <summary>带类型 / 来源的变量表；没有时退回只有名字的 <see cref="VariableNameSource"/>。</summary>
    internal Func<IReadOnlyList<VariableInfo>>? VariableInfoSource { get; set; }

    /// <summary>
    /// 「从列表选…」的候选（按键分组 / 已录制的宏 / 子动作库）。
    /// 只在 <see cref="PickerKind.choices"/> 出现在 <see cref="Pickers"/> 里时用。
    /// </summary>
    internal Func<IEnumerable<StepChoice>>? ChoiceSource { get; set; }

    /// <summary>草稿资源根（缩略图认 <c>files/x.png</c>）。</summary>
    internal string? ResourceRoot { get; set; }

    /// <summary>当前值；直接构造，不修改传入对象。</summary>
    public StepArg Value
    {
        get
        {
            if (_inline != null) return StepArg.FromInline(_inline);

            string text = (Box.Text ?? "").Trim();

            if (VariableNameMode)
            {
                // flow.set 的变量名是数据本身，不能当引用。
                if (Type == ValueKind.text) return StepArg.FromText(text);
                if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}'))
                    return StepArg.FromVar(text[2..^1].Trim());
                return text.Length == 0 ? StepArg.FromText("") : StepArg.FromVar(text);
            }

            if (_boundVariable != null && text == "${" + _boundVariable + "}")
                return StepArg.FromVar(_boundVariable);

            if (Type == ValueKind.number
                && double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out double number))
                return StepArg.FromNumber(number);
            if (Type == ValueKind.@bool && bool.TryParse(text, out bool flag))
                return StepArg.FromBool(flag);

            // 列表没改过就保留原值：嵌套项是 JSON，重新按行拆会散架。
            if (_listLiteral != null
                && string.Equals(text, ListText.Display(_listLiteral), StringComparison.Ordinal))
                return StepArg.FromValue(_listLiteral);

            return StepArg.FromText(text);
        }
        set => Load(value);
    }

    /// <summary>字面量文本（选择器回填路径 / 坐标 / 颜色用）。</summary>
    public string LiteralText
    {
        get => Box.Text ?? "";
        set
        {
            _inline = null;
            _boundVariable = null;
            _listLiteral = null;
            Box.Text = value ?? "";
            UpdatePanels();
        }
    }

    /// <summary>把光标放进输入框（宿主打开对话框时用）。</summary>
    public void FocusEditor()
    {
        // 输入框永远在（内联取值时是只读描述），点它就能切回写字。
        Box.Focus();
    }

    private void ApplySpec()
    {
        SourceButton.Visibility = Pickers == PickerKind.none ? Visibility.Collapsed : Visibility.Visible;
        UpdatePanels();
    }

}
