using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using LTools.Core.Actions;
using LTools.Features.Script;
using LTools.Views.Controls;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>
/// 按 <see cref="UnitSpec"/> 生成的步骤参数表单：一个单元一个对话框，不写任何按单元分支的代码。
/// </summary>
/// <remarks>
/// 输入行来自 <see cref="UnitSpec.Inputs"/>，编辑器形态由 <see cref="InputSpec.Editor"/> 决定；
/// 每个输入框下方有一行 <see cref="InputHint"/> 说明「该填什么」。
/// <c>VisibleWhen</c> 支持 <c>key == 'value'</c>（多个用 <c>||</c> 连接，值可不带引号），
/// 切换枚举值时实时收起 / 展开。
/// 「调用子动作」的按名参数由 <see cref="UnitSpec.DynamicInputs"/> + 子动作变量表生成。
/// </remarks>
public partial class StepEditorDialog : Window
{
    private sealed record Row(InputSpec Spec, FrameworkElement Host, Func<StepArg?> Read);

    /// <summary>输出区的一行：勾选 + 类型徽标 + 变量名 + 说明。</summary>
    private sealed record OutputRow(string Key, CheckBox Toggle, TextBox Box, Action Refresh);

    private readonly Step _step;
    private readonly UnitSpec _spec;
    private readonly bool _inline;
    private readonly List<Row> _rows = new();
    private readonly List<OutputRow> _outputs = new();
    private readonly List<(VariableDef Variable, ValueSourceEditor Editor)> _dynamicInputs = new();
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

    private bool _syncing;

    public StepEditorDialog(Step step, bool inline = false,
        ActionProgram? program = null, Func<IEnumerable<string>>? variableNames = null,
        string? resourceRoot = null)
    {
        InitializeComponent();
        WindowChromeHelper.FixMaximize(this);

        // 必须在 BuildInputs 之前就位：「调用子动作」的子动作下拉、`InputEditor.variable`
        // 的候选名、取值单元里再嵌一层编辑器都依赖它们。放在对象初始化器里会在构造
        // 之后才赋值，表单已经按 null 建好（子动作就退化成只能手填 id 的空文本框）。
        Program = program;
        VariableNameSource = variableNames;
        ResourceRoot = resourceRoot;

        _step = step.Clone();
        _spec = StepRegistry.Default.Get(_step.Unit).Spec;
        _inline = inline;

        Title = _spec.Name;
        HeaderText.Text = inline ? _spec.Name + " · 取值" : _spec.Name;
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
    }

    /// <summary>确定后的步骤（取消时为 null）。</summary>
    public Step? Result { get; private set; }

    /// <summary>-1 / +1 = 用户点了上一步 / 下一步（编辑器据此打开相邻步骤）；0 = 正常确定。</summary>
    public int NavigateDelta { get; private set; }

    /// <summary>「保存并继续添加」：添加流程里再开一个同单元的空表单。</summary>
    public bool ContinueAdding { get; private set; }

    /// <summary>编辑器在 ShowDialog 之前调用：显示上一步 / 下一步（没有相邻步骤时对应按钮隐藏）。</summary>
    internal void EnableNavigation(bool canPrevious, bool canNext)
    {
        PrevButton.Visibility = canPrevious ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Visibility = canNext ? Visibility.Visible : Visibility.Collapsed;
        NavPanel.Visibility = canPrevious || canNext ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>添加新步骤时调用：显示「保存并继续添加」。</summary>
    internal void EnableContinueAdding()
    {
        ContinueButton.Visibility = Visibility.Visible;
        NavPanel.Visibility = Visibility.Visible;
    }

    /// <summary>可用于变量编辑的变量名（由动作编辑器透传，构造时就要给）。</summary>
    internal Func<IEnumerable<string>>? VariableNameSource { get; }

    /// <summary>动作体（解析「本动作」子动作、内联取值编辑用，构造时就要给）。</summary>
    internal ActionProgram? Program { get; }

    /// <summary>草稿资源根（<c>staging/&lt;id&gt;/files</c>）：模板图缩略图与内联取值编辑器用它解析 <c>files/x.png</c>。</summary>
    internal string? ResourceRoot { get; }

    // ============ 输入 ============

    private void BuildInputs()
    {
        var advanced = new List<InputSpec>();

        foreach (var input in _spec.Inputs)
        {
            if (input.Advanced)
            {
                advanced.Add(input);
                continue;
            }

            FrameworkElement editor = BuildEditor(input, out var read);
            _rows.Add(new Row(input, AddRow(Form, input, editor), read));
        }

        Form.Children.Add(_dynamicPanel);
        if (advanced.Count > 0) BuildAdvancedSection(advanced);

        SyncStepArgs();
        if (_spec.DynamicInputs) RebuildDynamic();
        UpdateVisibility();
    }

    /// <summary>
    /// 调优参数（超时 / 轮询间隔 / 相似度…）收进「高级参数」：默认折叠，
    /// **已经设过非默认值的自动展开**，不会把用户填过的参数藏起来。
    /// </summary>
    private void BuildAdvancedSection(List<InputSpec> advanced)
    {
        var host = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        var header = new Button
        {
            Style = (Style)FindResource("BtnGhost"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(8, 3, 8, 3),
            Margin = new Thickness(0, 4, 0, 2),
            ToolTip = "超时、轮询间隔、相似度这类调优参数；平时不用改",
        };

        _advancedHeader = header;
        _advancedCount = advanced.Count;
        SetAdvancedExpanded(advanced.Any(input => _step.In.ContainsKey(input.Key)));

        header.Click += (_, _) => SetAdvancedExpanded(!_advancedExpanded);

        host.Children.Add(header);
        host.Children.Add(_advancedPanel);
        Form.Children.Add(host);

        foreach (var input in advanced)
        {
            FrameworkElement editor = BuildEditor(input, out var read);
            _rows.Add(new Row(input, AddRow(_advancedPanel, input, editor), read));
        }
    }

    /// <summary>折叠 / 展开高级参数（标题箭头与面板可见性一起改）。</summary>
    private void SetAdvancedExpanded(bool expanded)
    {
        _advancedExpanded = expanded;
        if (_advancedHeader != null)
            _advancedHeader.Content = (expanded ? "▾ " : "▸ ") + $"高级参数（{_advancedCount}）";
        _advancedPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildEditor(InputSpec input, out Func<StepArg?> read)
    {
        StepArg? current = CurrentArg(input);

        if (_step.Unit == "sub.run" && input.Key == "subAction" && Program != null)
        {
            var combo = new ComboBox
            {
                MinHeight = 28,
                MinWidth = 240,
                ToolTip = "「本动作 · …」是当前动作里定义的子动作，「全局 · …」来自设置 → 子动作库",
            };
            var choices = SubActionChoices();
            string id = current?.Literal.AsText() ?? "";
            if (id.Length > 0 && choices.All(c =>
                    !string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
                choices.Add(new SubChoice(id, id + "（未找到）"));
            if (choices.Count == 0 && id.Length == 0)
                choices.Add(new SubChoice("",
                    "（还没有子动作：先到 设置 → 子动作库 新建一个）"));

            // 先把候选补齐再设 ItemsSource：普通 List 不通知集合变化，先绑定再加会少一项。
            combo.ItemsSource = choices;
            combo.DisplayMemberPath = nameof(SubChoice.Display);

            combo.SelectedItem = choices.FirstOrDefault(c =>
                string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase))
                ?? choices.FirstOrDefault();
            if (combo.SelectedItem == null && choices.Count > 0) combo.SelectedIndex = 0;

            read = () => StepArg.FromText((combo.SelectedItem as SubChoice)?.Id ?? "");
            combo.SelectionChanged += (_, _) =>
            {
                if (_syncing) return;
                SyncStepArgs();
                DropUnknownDynamicArgs();
                RebuildDynamic();
                BuildOutputs();
                OnEdited();
            };
            return combo;
        }

        switch (input.Editor)
        {
            case InputEditor.boolean:
            {
                var check = new CheckBox
                {
                    // 没存过的开关按参数默认值勾（以前 `current == null` 就勾上，会把
                    // 没有 Default 的开关（如「递归」）白白存成 true）。
                    IsChecked = current?.Literal.AsBool() ?? false,
                    VerticalAlignment = VerticalAlignment.Center,
                    Content = "启用",
                };
                read = () => StepArg.FromBool(check.IsChecked == true);
                check.Checked += (_, _) => OnEdited();
                check.Unchecked += (_, _) => OnEdited();
                return check;
            }

            case InputEditor.enumList:
            {
                var combo = new ComboBox { MinHeight = 28, MinWidth = 180 };
                var options = input.Options ?? Array.Empty<string>();
                // 显示中文名，Tag 里留英文键——存进步骤的仍是键，老配置与单元实现不受影响。
                foreach (string option in options)
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = OptionLabels.For(input, option),
                        Tag = option,
                    });

                string want = current?.Literal.AsText() ?? input.Default?.AsText() ?? "";
                int index = -1;
                for (int i = 0; i < options.Count; i++)
                {
                    if (!string.Equals(options[i], want, StringComparison.OrdinalIgnoreCase)) continue;
                    index = i;
                    break;
                }

                // 老配置里可能有已下线的选项（内置命令改名 / 删掉）：把它原样留在下拉里并选中，
                // 否则会静默落到第一项，点一次确定就把配置改掉了。
                if (index < 0 && want.Length > 0)
                {
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = want + "（未知选项）",
                        Tag = want,
                    });
                    index = combo.Items.Count - 1;
                }

                combo.SelectedIndex = index >= 0 ? index : 0;

                read = () => StepArg.FromText(
                    ((combo.SelectedItem as ComboBoxItem)?.Tag as string) ?? "");
                combo.SelectionChanged += (_, _) => OnEdited();

                // 让别的行能按英文键改这一行的选中项（框选区域后自动切到「固定区域」）。
                _enumSetters[input.Key] = key =>
                {
                    foreach (ComboBoxItem item in combo.Items)
                    {
                        if (!string.Equals(item.Tag as string, key, StringComparison.OrdinalIgnoreCase))
                            continue;
                        combo.SelectedItem = item;
                        break;
                    }
                };

                // 有配套「固定区域」的枚举（区域 / 取图方式）直接给个「框选…」：
                // 不必先切到「固定区域」，拖完自动切好并填上坐标。
                return IsRegionDriver(input) ? WithButton(combo, PickRegionButton()) : combo;
            }

            case InputEditor.variable:
            {
                var combo = new ComboBox { IsEditable = true, MinHeight = 28, MinWidth = 200 };
                var names = VariableNameSource?.Invoke()?.ToList() ?? new List<string>();
                combo.ItemsSource = names;
                if (input.Type == ValueKind.list)
                    combo.ToolTip = "写变量名；也可以直接写多项，每行一项或用逗号分隔";
                else if (input.Type == ValueKind.@object)
                    combo.ToolTip = "写变量名；对象不能直接内联，先用 JSON 单元取出来";

                // 列表字面量按「每行一项」显示；改了才重写，没改就原样保留
                //（嵌套项是 JSON，重新拆一遍会散架）。
                string initial = current == null ? ""
                    : current.Kind == StepArgKind.variable ? current.VariablePath
                    : ListText.Display(current.Literal);
                combo.Text = initial;

                // 变量名以「变量引用」保存（保留类型）；少数把名字本身当文本的输入
                //（flow.set 的 name）仍按字面量存。
                bool asReference = input.Type != ValueKind.text;
                bool edited = false;
                read = () =>
                {
                    string text = (combo.Text ?? "").Trim();
                    if (!asReference) return StepArg.FromText(text);

                    // 只认「整串就是一个 ${…}」：否则 ${a},${b} 这种内联列表会被当成变量名。
                    if (text.StartsWith("${", StringComparison.Ordinal) && text.EndsWith('}')
                        && text.IndexOf('{', 2) < 0 && text.LastIndexOf('}') == text.Length - 1)
                        return StepArg.FromVar(text[2..^1].Trim());

                    // 列表参数可以直接写字面量（每行一项 / 逗号分隔），绑定参数时拆成列表；
                    // 写了已有变量名还是按引用走，别把人家的变量名当文本。
                    if (input.Type == ValueKind.list
                        && !names.Contains(text, StringComparer.OrdinalIgnoreCase))
                    {
                        // 没动过就保留原来的列表值：嵌套项是 JSON，重新按行 / 逗号拆会散架。
                        if (current != null && current.Kind == StepArgKind.literal
                            && current.Literal.Kind == ValueKind.list
                            && (!edited || string.Equals(text, initial, StringComparison.Ordinal)))
                            return current;

                        return StepArg.FromText(text);
                    }

                    return StepArg.FromVar(text);
                };
                combo.AddHandler(TextBoxBase.TextChangedEvent,
                    new TextChangedEventHandler((_, _) =>
                    {
                        edited = true;
                        OnEdited();
                    }));
                return combo;
            }

            case InputEditor.condition:
            {
                var value = new ValueSourceEditor
                {
                    Type = input.Type,
                    VariableNameSource = VariableNameSource,
                    Program = Program,
                    Value = current ?? StepArg.FromText(""),
                };
                value.Changed += OnEdited;
                read = () => value.Value;
                return value;
            }

            case InputEditor.image:
            {
                if (current != null && current.Kind != StepArgKind.literal)
                {
                    var value = new ValueSourceEditor
                    {
                        Type = input.Type,
                        VariableNameSource = VariableNameSource,
                        Program = Program,
                        ResourceRoot = ResourceRoot,
                        Value = current,
                    };
                    value.Changed += OnEdited;
                    read = () => value.Value;
                    return value;
                }

                var picker = new TemplateImagePicker
                {
                    ResourceRoot = ResourceRoot,
                    Path = current?.Literal.AsText() ?? "",
                };
                read = () => StepArg.FromText(picker.Path);
                picker.Changed += OnEdited;

                // 框选模板图时顺手把搜索区域收紧到刚框的那块：找图 / 找字更快也更准。
                // 没有 region / rect 的单元（如「文字识别」）什么也不做。
                picker.Captured += ApplyPickedRegion;
                return picker;
            }

            case InputEditor.region:
            {
                var value = new ValueSourceEditor
                {
                    Type = input.Type,
                    VariableNameSource = VariableNameSource,
                    Program = Program,
                    Value = current ?? StepArg.FromText(""),
                };
                value.Changed += OnEdited;

                _rectSetters[input.Key] = text => value.LiteralText = text;
                read = () => value.Value;
                return WithButton(value, PickRegionButton());
            }

            case InputEditor.file:
            case InputEditor.folder:
            {
                var value = new ValueSourceEditor
                {
                    Type = input.Type,
                    VariableNameSource = VariableNameSource,
                    Program = Program,
                    Value = current ?? StepArg.FromText(""),
                };
                value.Changed += OnEdited;

                var browse = new Button
                {
                    Content = "浏览…",
                    Style = (Style)FindResource("BtnGhost"),
                    MinHeight = 28,
                    Margin = new Thickness(6, 0, 0, 0),
                    Padding = new Thickness(10, 2, 10, 2),
                };
                browse.Click += (_, _) =>
                {
                    string? picked = input.Editor == InputEditor.folder
                        ? PickFolder(value.LiteralText)
                        : PickFile(value.LiteralText);
                    if (picked != null) value.LiteralText = picked;
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.Children.Add(value);
                Grid.SetColumn(browse, 1);
                grid.Children.Add(browse);

                read = () => value.Value;
                return grid;
            }

            case InputEditor.code:
            {
                var box = new TextBox
                {
                    Text = current?.Literal.AsText() ?? "",
                    FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
                    AcceptsReturn = true,
                    AcceptsTab = true,
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MinHeight = 260,
                    Padding = new Thickness(8, 6, 8, 6),
                    VerticalContentAlignment = VerticalAlignment.Top,
                    ToolTip = "AHK v2 代码。LT_Var 读变量、LT_Set 写回变量、LT_Print 写标准输出。",
                };

                var check = new Button
                {
                    Content = "检查语法",
                    Style = (Style)FindResource("Btn"),
                    MinHeight = 28,
                    Padding = new Thickness(10, 2, 10, 2),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ToolTip = "只做语法检查，不会执行脚本（也不会产生任何副作用）",
                };

                var status = new TextBlock
                {
                    FontSize = 12,
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                };

                check.Click += (_, _) =>
                {
                    status.Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush");
                    status.Text = "检查中…";
                    check.IsEnabled = false;
                    try
                    {
                        AhkCheckResult result = AhkRunner.Check(box.Text ?? "");
                        Status(status, result.Ok, result.Ok ? "语法没问题" : result.Message);
                    }
                    catch (Exception ex)
                    {
                        Status(status, false, ex.Message);
                    }
                    finally
                    {
                        check.IsEnabled = true;
                    }
                };

                var bar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 6, 0, 0),
                };
                bar.Children.Add(check);
                bar.Children.Add(status);

                var panel = new Grid();
                panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                panel.Children.Add(box);
                Grid.SetRow(bar, 1);
                panel.Children.Add(bar);

                read = () => StepArg.FromText(box.Text ?? "");
                box.TextChanged += (_, _) => OnEdited();
                return panel;
            }

            case InputEditor.multiline:
            case InputEditor.number:
            default:
            {
                var value = new ValueSourceEditor
                {
                    Type = input.Type,
                    // 列表参数按「每行一项」写，单行框看不清也容易误删；
                    // 绑定参数时仍由引擎按行 / 逗号拆成列表。
                    Multiline = input.Editor == InputEditor.multiline
                                || (input.Editor == InputEditor.text && input.Type == ValueKind.list),
                    VariableNameSource = VariableNameSource,
                    Program = Program,
                    Value = current ?? StepArg.FromText(""),
                };
                value.Changed += OnEdited;
                read = () => value.Value;
                return value;
            }
        }
    }

    private StepArg? CurrentArg(InputSpec input)
    {
        if (_step.In.TryGetValue(input.Key, out var arg)) return arg;
        return input.Default != null ? StepArg.FromValue(input.Default) : null;
    }

    /// <summary>代码编辑器的检查结果显示：成功用次要色，失败用与其它表单错误一致的危险色。</summary>
    private static void Status(TextBlock label, bool ok, string text)
    {
        label.Foreground = (System.Windows.Media.Brush)label.FindResource(
            ok ? "SecondaryTextBrush" : "DangerBrush");
        label.Text = text;
    }
    private FrameworkElement AddRow(Panel host, InputSpec input, FrameworkElement editor)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock
        {
            Text = input.Required ? input.Label + " *" : input.Label,
            Foreground = (System.Windows.Media.Brush)FindResource(
                input.Required ? "TextBrush" : "SecondaryTextBrush"),
            // 图片选择器是一整块（预览 + 按钮 + 路径），标签贴顶更贴近第一行；
            // 普通输入框仍居中对齐。
            VerticalAlignment = editor is TemplateImagePicker
                ? VerticalAlignment.Top
                : VerticalAlignment.Center,
            Margin = editor is TemplateImagePicker ? new Thickness(0, 6, 0, 0) : new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
        };

        // 第一行放输入控件，第二行（有说明时）放「该填什么」——说明不占标签列，
        // 长文本可以贴着输入框的宽度换行。
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        string hint = InputHint.Of(input);
        if (hint.Length > 0)
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        row.Children.Add(label);
        Grid.SetColumn(editor, 1);
        row.Children.Add(editor);

        if (hint.Length > 0)
        {
            var note = new TextBlock
            {
                Text = hint,
                Style = (Style)FindResource("HintLabel"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            };
            Grid.SetRow(note, 1);
            Grid.SetColumn(note, 1);
            row.Children.Add(note);
        }

        host.Children.Add(row);
        return row;
    }

    /// <summary>输入控件 + 右侧一个小按钮（「浏览…」/「框选…」）。</summary>
    private static Grid WithButton(FrameworkElement editor, Button button)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(editor);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return grid;
    }

    /// <summary>「框选…」：拖一块屏幕，结果写进固定区域并切好枚举。</summary>
    private Button PickRegionButton()
    {
        var pick = new Button
        {
            Content = "框选…",
            Style = (Style)FindResource("BtnGhost"),
            MinHeight = 28,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 2, 10, 2),
            ToolTip = "在屏幕上拖一块，自动填好 x,y,w,h（并把区域切到「固定区域」）",
        };
        pick.Click += (_, _) =>
        {
            if (!ScreenPicker.TryPickRegion(Window.GetWindow(this), out var area, out _)) return;
            ApplyPickedRegion(area);
        };
        return pick;
    }

    /// <summary>这个枚举值切换时会不会露出「固定区域」行（<c>region</c> / <c>mode</c>）。</summary>
    private bool IsRegionDriver(InputSpec input) => _spec.Inputs.Any(i =>
        string.Equals(i.Key, "rect", StringComparison.OrdinalIgnoreCase)
        && StepSummary.TryGetVisibleRule(i, out string key, out _)
        && string.Equals(key, input.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 框选完一块区域：先写「固定区域」文本，再把配套枚举（<c>region</c> / <c>mode</c>）切到 <c>rect</c>。
    /// 顺序不能反——枚举一切换就会同步参数并把隐藏行丢掉，那时文本还没写进去。
    /// </summary>
    private void ApplyPickedRegion(Int32Rect area)
    {
        string rect = string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3}", area.X, area.Y, area.Width, area.Height);

        var rectSpec = _spec.Inputs.FirstOrDefault(i =>
            string.Equals(i.Key, "rect", StringComparison.OrdinalIgnoreCase));
        if (rectSpec == null) return;   // 这个单元没有固定区域参数（如「文字识别」）

        if (_rectSetters.TryGetValue(rectSpec.Key, out var setRect)) setRect(rect);

        if (StepSummary.TryGetVisibleRule(rectSpec, out string key, out string value)
            && _enumSetters.TryGetValue(key, out var setEnum))
            setEnum(value);

        OnEdited();
    }

    private void UpdateVisibility()
    {
        foreach (var row in _rows)
            row.Host.Visibility = StepSummary.IsVisible(row.Spec, _step, _spec)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把编辑器里的值写回 <see cref="_step"/>（含可见性所依赖的枚举值）。</summary>
    private void SyncStepArgs()
    {
        if (_syncing) return;
        _syncing = true;
        try
        {
            var all = new Dictionary<string, StepArg>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in _rows)
            {
                var arg = row.Read();
                if (arg != null && ShouldKeep(row.Spec, arg)) all[row.Spec.Key] = arg;
            }

            foreach (var (variable, editor) in _dynamicInputs)
            {
                StepArg value = editor.Value;
                if (value.Kind == StepArgKind.literal
                    && value.Literal.Kind == ValueKind.text
                    && value.Literal.AsText().Length == 0) continue;
                all[variable.Key] = value;
            }

            // 声明之外的参数只有动态参数单元（sub.run）才保留；
            // 其它单元的未知键会在运行时被判为拼写错误，顺手清掉。
            var extra = _spec.DynamicInputs
                ? _step.In.Where(p => !_spec.Inputs.Any(i =>
                    string.Equals(i.Key, p.Key, StringComparison.OrdinalIgnoreCase))).ToList()
                : new List<KeyValuePair<string, StepArg>>();

            _step.In.Clear();
            foreach (var pair in extra) _step.In[pair.Key] = pair.Value;
            foreach (var pair in all) _step.In[pair.Key] = pair.Value;

            // 隐藏的参数（如 operation=read 时的 text）保存时丢掉，避免留下无关字段。
            foreach (var row in _rows)
            {
                if (row.Spec.VisibleWhen != null
                    && !StepSummary.IsVisible(row.Spec, _step, _spec))
                    _step.In.Remove(row.Spec.Key);
            }
        }
        finally { _syncing = false; }
    }

    private void OnEdited()
    {
        if (_syncing) return;
        SyncStepArgs();
        UpdateVisibility();
    }

    /// <summary>
    /// 空的可选文本、false 的开关不必写进步骤（运行时按默认值处理），
    /// 这样保存出来的 JSON 只留用户真正填过的参数。
    /// </summary>
    private static bool ShouldKeep(InputSpec spec, StepArg arg)
    {
        if (arg.Kind != StepArgKind.literal) return true;

        if (arg.Literal.Kind == ValueKind.@bool)
            return arg.Literal.AsBool() != (spec.Default?.AsBool() ?? false);

        if (arg.Literal.Kind == ValueKind.number)
            return spec.Default == null || arg.Literal.AsNumber() != spec.Default.AsNumber();

        if (arg.Literal.Kind != ValueKind.text) return true;

        string text = arg.Literal.AsText();
        if (text.Length > 0) return true;
        if (spec.Required) return true;
        return spec.Default != null && spec.Default.AsText().Length > 0;
    }

    // ============ 子动作动态参数 ============

    private void DropUnknownDynamicArgs()
    {
        var allowed = new HashSet<string>(
            _spec.Inputs.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
        string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
        var def = ResolveSubAction(id);
        if (def != null)
            foreach (var variable in def.Variables) allowed.Add(variable.Key);

        foreach (string key in _step.In.Keys.ToList())
            if (!allowed.Contains(key)) _step.In.Remove(key);
    }

    private void RebuildDynamic()
    {
        _dynamicPanel.Children.Clear();
        _dynamicInputs.Clear();

        if (_step.Unit != "sub.run" || !_spec.DynamicInputs) return;

        string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
        var def = ResolveSubAction(id);

        _dynamicPanel.Children.Add(new TextBlock
        {
            Text = def == null
                ? "（选一个子动作后，这里会出现它的输入参数）"
                : "子动作参数",
            Style = (Style)FindResource(def == null ? "HintLabel" : "SectionLabel"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6),
        });

        if (def == null) return;

        foreach (var variable in def.Variables.Where(v => v.Direction == VariableDirection.input))
        {
            var editor = new ValueSourceEditor
            {
                Type = variable.Type,
                VariableNameSource = VariableNameSource,
                Program = Program,
                Value = _step.In.TryGetValue(variable.Key, out var existing)
                    ? existing : StepArg.FromText(""),
            };
            editor.Changed += OnEdited;

            var spec = new InputSpec(variable.Key,
                $"{variable.Key}（{KindLabel(variable.Type)}）", variable.Type);
            _dynamicPanel.Children.Add(BuildRowFor(spec, editor));
            _dynamicInputs.Add((variable, editor));
        }

        if (_dynamicInputs.Count == 0)
        {
            _dynamicPanel.Children.Add(new TextBlock
            {
                Text = "这个子动作没有输入参数。",
                Style = (Style)FindResource("HintLabel"),
                Margin = new Thickness(0, 0, 0, 4),
            });
        }
    }

    private FrameworkElement BuildRowFor(InputSpec spec, FrameworkElement editor)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(118) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new TextBlock
        {
            Text = spec.Label,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        });
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private List<SubChoice> SubActionChoices()
    {
        var list = new List<SubChoice>();
        if (Program != null)
        {
            foreach (var def in Flatten(Program.SubActions))
                list.Add(new SubChoice(def.Id, $"本动作 · {Label(def)}"));
        }

        foreach (var def in SubActionStore.LoadAll())
            list.Add(new SubChoice(def.Id, $"全局 · {Label(def)}"));

        return list;
    }

    private static IEnumerable<SubActionDef> Flatten(IEnumerable<SubActionDef> defs)
    {
        foreach (var def in defs)
        {
            yield return def;
            foreach (var child in Flatten(def.SubActions)) yield return child;
        }
    }

    private SubActionDef? ResolveSubAction(string id) => SubActionStore.Resolve(Program, id);

    private static string Label(SubActionDef def) =>
        string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;

    private static string KindLabel(ValueKind? kind) => kind == null ? "任意" : KindLabel(kind.Value);

    private static string KindLabel(ValueKind kind) => kind switch
    {
        ValueKind.text => "文本",
        ValueKind.number => "数字",
        ValueKind.@bool => "布尔",
        ValueKind.list => "列表",
        ValueKind.@object => "对象",
        _ => "任意",
    };

    // ============ 输出 ============

    private void BuildOutputs()
    {
        Outputs.Children.Clear();
        _outputs.Clear();
        _dynamicOutputs.Clear();

        if (_step.Unit == "sub.run")
        {
            BuildDynamicOutputs();
        }
        else
        {
            // 主结果（整步结果）排最前：多数情况下用户只需要它，其余是拆出来的字段。
            foreach (var output in _spec.Outputs.OrderByDescending(o =>
                         string.Equals(o.Key, "value", StringComparison.OrdinalIgnoreCase)))
            {
                bool on = _step.Out.TryGetValue(output.Key, out string? target);
                var row = AddOutputRow(output.Key, output.Label, output.Type, output.Description,
                    on ? target ?? "" : output.DefaultVariable ?? output.Key);
                WireRow(row, on);
                _outputs.Add(row);
            }
        }

        // 没有输出就别留一个空白的「输出变量」区（内置功能 / 没选子动作的调用子动作）。
        OutputSection.Visibility = _inline || _outputs.Count + _dynamicOutputs.Count == 0
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void BuildDynamicOutputs()
    {
        string id = _step.In.TryGetValue("subAction", out var arg) ? arg.Literal.AsText() : "";
        var def = ResolveSubAction(id);
        if (def == null) return;

        foreach (var variable in def.Variables.Where(v => v.Direction == VariableDirection.output))
        {
            bool on = _step.Out.TryGetValue(variable.Key, out string? target);
            var row = AddOutputRow(variable.Key, variable.Key, variable.Type,
                $"子动作「{Label(def)}」声明的输出参数",
                on ? target ?? "" : variable.Key);
            WireRow(row, on);
            _dynamicOutputs.Add(row);
        }
    }

    /// <summary>勾选状态决定变量名是否可编辑，并刷新下面的说明行。</summary>
    private static void WireRow(OutputRow row, bool on)
    {
        row.Toggle.IsChecked = on;
        row.Box.IsEnabled = on;
        row.Toggle.Checked += (_, _) => { row.Box.IsEnabled = true; row.Refresh(); };
        row.Toggle.Unchecked += (_, _) => { row.Box.IsEnabled = false; row.Refresh(); };
        row.Box.TextChanged += (_, _) => row.Refresh();
        row.Refresh();
    }

    /// <summary>
    /// 一行输出：<c>☑ 标签 [类型] → [变量名]</c>，下一行说明「写入 ${变量}：装的是什么」。
    /// 说明行**不看勾选状态**都显示——用户要先读到「退出码是否为 0」才知道值不值得勾。
    /// </summary>
    private OutputRow AddOutputRow(string key, string label, ValueKind? type,
        string? description, string value)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var toggle = new CheckBox
        {
            Content = label,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var badge = new Border
        {
            Background = (System.Windows.Media.Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = KindLabel(type),
                FontSize = 11.5,
                Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            },
        };

        var arrow = new TextBlock
        {
            Text = "→",
            Margin = new Thickness(7, 0, 7, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (System.Windows.Media.Brush)FindResource("HintTextBrush"),
        };

        var box = new TextBox
        {
            MinHeight = 28,
            Text = value,
            FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"),
        };

        var hint = new TextBlock
        {
            FontSize = 12,
            Foreground = (System.Windows.Media.Brush)FindResource("SecondaryTextBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
        };

        grid.Children.Add(toggle);
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        Grid.SetColumn(arrow, 2);
        grid.Children.Add(arrow);
        Grid.SetColumn(box, 3);
        grid.Children.Add(box);
        Grid.SetRow(hint, 1);
        Grid.SetColumn(hint, 1);
        Grid.SetColumnSpan(hint, 3);
        grid.Children.Add(hint);
        Outputs.Children.Add(grid);

        Action refresh = () =>
        {
            string name = (box.Text ?? "").Trim();
            string target = name.Length == 0 ? "（还没填变量名）" : "${" + name + "}";
            string text = toggle.IsChecked == true ? $"写入 {target}" : $"勾选后写入 {target}";
            if (!string.IsNullOrWhiteSpace(description)) text += "：" + description;
            hint.Text = text;
        };

        return new OutputRow(key, toggle, box, refresh);
    }

    // ============ 选项 ============

    private void BuildOptions()
    {
        FailBox.SelectedIndex = _step.OnFail == FailMode.@continue ? 1 : 0;
        DelayBox.Text = _step.DelayMs > 0
            ? _step.DelayMs.ToString(CultureInfo.InvariantCulture) : "";
        DisabledBox.IsChecked = _step.Disabled;
        NoteBox.Text = _step.Note ?? "";
    }

    private void FocusFirstEditor()
    {
        var first = _rows.FirstOrDefault(r => r.Host.Visibility == Visibility.Visible);
        if (first?.Host is not null)
        {
            if (first.Host is TextBox box) { box.Focus(); box.SelectAll(); }
            else if (first.Host is ValueSourceEditor value) value.Focus();
            else if (first.Host is Panel panel && FindEditorBox(panel) is { } inner)
            {
                inner.Focus();
                if (first.Spec.Editor == InputEditor.code) inner.CaretIndex = 0;
            }
        }
    }

    /// <summary>包装过的编辑器（文件 / 代码）里第一个可输入的文本框，用于打开对话框时聚焦。</summary>
    private static TextBox? FindEditorBox(DependencyObject root) =>
        FindChild<TextBox>(root, box => box.IsEnabled && box.IsVisible);

    private static T? FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T typed && match(typed)) return typed;
            if (FindChild(child, match) is { } found) return found;
        }
        return null;
    }

    // ============ 确定 / 取消 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave()) return;
        NavigateDelta = 0;
        DialogResult = true;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (!TrySave()) return;
        ContinueAdding = true;
        DialogResult = true;
    }

    /// <summary>上一步 / 下一步：先按「确定」的规则保存，再让编辑器打开相邻步骤。</summary>
    private void Navigate(int delta)
    {
        if (!TrySave()) return;
        NavigateDelta = delta;
        DialogResult = true;
    }

    /// <summary>Alt+← / Alt+→ 也能翻（按钮不可用时不管，别吞掉文本框自己的按键）。</summary>
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if ((System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Alt) == 0)
            return;

        if (e.Key == System.Windows.Input.Key.Left && PrevButton.Visibility == Visibility.Visible)
        {
            Navigate(-1);
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Right && NextButton.Visibility == Visibility.Visible)
        {
            Navigate(1);
            e.Handled = true;
        }
    }

    /// <summary>把表单写回步骤；必填项为空时提示并返回 false（对话框不关）。</summary>
    private bool TrySave()
    {
        SyncStepArgs();

        foreach (var row in _rows)
        {
            if (!row.Spec.Required) continue;
            if (!StepSummary.IsVisible(row.Spec, _step, _spec)) continue;
            if (!_step.In.TryGetValue(row.Spec.Key, out var arg) || IsEmptyArg(arg))
            {
                // 必填项在高级参数里时先展开，别让用户对着看不见的框找错误。
                if (row.Spec.Advanced) SetAdvancedExpanded(true);
                ConfirmDialog.Info(this, _spec.Name, $"「{row.Spec.Label}」不能为空。");
                return false;
            }
        }

        _step.Out.Clear();
        foreach (var row in _outputs.Concat(_dynamicOutputs))
        {
            if (row.Toggle.IsChecked != true) continue;
            string name = (row.Box.Text ?? "").Trim();
            if (name.Length > 0) _step.Out[row.Key] = name;
        }

        _step.OnFail = FailBox.SelectedIndex == 1 ? FailMode.@continue : FailMode.stop;
        _step.DelayMs = int.TryParse((DelayBox.Text ?? "").Trim(),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay)
            ? Math.Max(0, delay) : 0;
        _step.Disabled = DisabledBox.IsChecked == true;
        _step.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text.Trim();

        Result = _step;
        return true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private static bool IsEmptyArg(StepArg arg) => arg.Kind switch
    {
        StepArgKind.variable => arg.VariablePath.Trim().Length == 0,
        StepArgKind.inline => arg.InlineStep == null,
        _ => arg.Literal.Kind == ValueKind.text && arg.Literal.AsText().Length == 0,
    };

    // ============ 文件选择 ============

    private static string? PickFile(string? current)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择文件",
            Filter = "所有文件|*.*",
        };
        if (!string.IsNullOrWhiteSpace(current) && System.IO.File.Exists(current))
            dlg.InitialDirectory = System.IO.Path.GetDirectoryName(current);
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    private static string? PickFolder(string? current)
    {
        var dlg = new OpenFolderDialog { Title = "选择文件夹" };
        if (!string.IsNullOrWhiteSpace(current) && System.IO.Directory.Exists(current))
            dlg.InitialDirectory = current;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    public sealed record SubChoice(string Id, string Display)
    {
        public override string ToString() => Display;
    }
}
