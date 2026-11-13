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

    /// <summary>框选 / 截图完成后把区域交给宿主（找图 / 找字顺手收紧搜索区域）。</summary>
    public event Action<Int32Rect>? Captured;

    /// <summary>要在屏幕上取点：参数是屏幕坐标，宿主一次填好同组的 x / y。</summary>
    public event Action<int, int>? PointPicked;

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

    // ============ 值的三态 ============

    private void Load(StepArg? value)
    {
        _loading = true;
        try
        {
            value ??= StepArg.FromText("");
            _inline = null;
            _boundVariable = null;
            _listLiteral = null;

            switch (value.Kind)
            {
                case StepArgKind.variable:
                    _boundVariable = value.VariablePath;
                    Box.Text = VariableNameMode
                        ? value.VariablePath
                        : "${" + value.VariablePath + "}";
                    break;

                case StepArgKind.inline:
                    _inline = value.InlineStep ?? Step.Of(FirstPureUnit());
                    break;

                default:
                    _listLiteral = value.Literal.Kind == ValueKind.list ? value.Literal : null;
                    Box.Text = _listLiteral != null
                        ? ListText.Display(_listLiteral)
                        : value.Literal.AsText();
                    break;
            }

            UpdatePanels();
        }
        finally { _loading = false; }
    }

    private void UpdatePanels()
    {
        bool inline = _inline != null;
        bool image = _spec?.Editor == InputEditor.image;

        // 输入框永远可见：字面量 / 变量 / 内联取值都在这一个框里表达，
        // 图片缩略图、区域框选、取值动作单元都不再把它换掉。
        Box.Visibility = Visibility.Visible;
        Box.IsReadOnly = inline;
        Box.Cursor = inline ? Cursors.Arrow : Cursors.IBeam;

        ThumbPanel.Visibility = image && !inline ? Visibility.Visible : Visibility.Collapsed;

        if (inline) UpdateInline();
        else if (image) UpdateImage();

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        // 颜色也在这里定：必填红字 / 「写成文本了」红字都要能回到主题的提示色。
        StatusText.ClearValue(TextBlock.ForegroundProperty);

        if (_missing)
        {
            StatusText.Text = "必填：这一项还没填";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        string hint = _spec == null ? "" : InputHint.Of(_spec);
        StatusText.ToolTip = hint.Length > 0 ? hint : null;

        if (_inline != null)
        {
            var spec = StepRegistry.Default.TryGet(_inline.Unit, out var unit) ? unit.Spec : null;
            string name = spec?.Name ?? _inline.Unit;
            var missing = MissingRequired(_inline, spec);
            StatusText.Text = missing.Count > 0
                ? $"取值：{name} · 还缺必填参数：{string.Join("、", missing)}"
                : spec is { Inputs.Count: > 0 }
                    ? $"取值：{name} · 点 ▾ 里的「改参数…」调整它"
                    : $"取值：{name}（不需要参数）";
            return;
        }

        if (_boundVariable != null)
        {
            var info = VariableInfos().FirstOrDefault(v =>
                string.Equals(v.Name, _boundVariable, StringComparison.OrdinalIgnoreCase));
            string type = info == null ? "" : "（" + KindLabel(info.Kind) + "）";
            string from = string.IsNullOrWhiteSpace(info?.Origin) ? "" : " · " + info!.Origin;
            StatusText.Text = $"来自变量 {_boundVariable}{type}{from}";
            return;
        }

        // 文本里正好写了某个变量名（没加 ${}）：运行时会被当成字面量，「如果」就永远成立。
        // 变量名输入框（flow.set 的变量名）里本来就该是裸名字，跳过。
        string literal = (Box.Text ?? "").Trim();
        if (!VariableNameMode && literal.Length > 0
            && !literal.Contains("${", StringComparison.Ordinal)
            && VariableInfos().Any(v =>
                string.Equals(v.Name, literal, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = $"这里写的是文本「{literal}」；要取值请写成 ${{{literal}}}";
            StatusText.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        string selected = ChoiceLabelOf(literal);
        StatusText.Text = selected.Length == 0
            ? hint
            : hint.Length == 0 ? selected : selected + " · " + hint;
    }

    /// <summary>
    /// 当前值正好是某个候选项（宏 / 子动作存的是 Id）：状态行补一句「已选 …」，
    /// 输入框里仍然是真实存进步骤的值，不玩「显示名 ≠ 存的值」。
    /// </summary>
    private string ChoiceLabelOf(string literal)
    {
        if (literal.Length == 0 || ChoiceSource == null || !Pickers.HasFlag(PickerKind.choices))
            return "";

        try
        {
            var match = ChoiceSource().FirstOrDefault(c =>
                string.Equals(c.Value, literal, StringComparison.OrdinalIgnoreCase));
            return match == null ? "" : "已选「" + match.Label + "」";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>内联取值：输入框只读显示「取哪个值」，改参数 / 切回文本都在 ▾ 里。</summary>
    private void UpdateInline()
    {
        var spec = StepRegistry.Default.TryGet(_inline!.Unit, out var unit) ? unit.Spec : null;
        string text = "取值：" + (spec?.Name ?? _inline.Unit);

        bool wasLoading = _loading;
        _loading = true;
        try { Box.Text = text; }
        finally { _loading = wasLoading; }
    }

    private void UpdateImage()
    {
        string stored = (Box.Text ?? "").Trim();
        if (stored.Length == 0)
        {
            Thumb.Source = null;
            ImageName.Text = "无图";
            ImageName.Foreground = (Brush)FindResource("HintTextBrush");
            ImageSize.Text = "点这里框选 / 换图";
            return;
        }

        string path = ImageLibrary.Resolve(stored, ResourceRoot);
        if (!File.Exists(path))
        {
            Thumb.Source = null;
            ImageName.Text = stored;
            ImageName.Foreground = (Brush)FindResource("DangerBrush");
            ImageSize.Text = "文件不存在";
            return;
        }

        try
        {
            var bmp = LoadBitmap(path);
            Thumb.Source = bmp;
            ImageName.Text = Path.GetFileName(path);
            ImageName.Foreground = (Brush)FindResource("TextBrush");
            ImageSize.Text = $"{bmp.PixelWidth} × {bmp.PixelHeight}";
        }
        catch
        {
            Thumb.Source = null;
            ImageName.Text = stored;
            ImageName.Foreground = (Brush)FindResource("DangerBrush");
            ImageSize.Text = "读不出来";
        }
    }

    private static BitmapImage LoadBitmap(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.UriSource = new Uri(Path.GetFullPath(path));
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void Box_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;

        // 手动改过文本就不再是「整串一个变量」：${a}${b} 这种模板仍按文本存。
        if (_boundVariable != null && (Box.Text ?? "").Trim() != "${" + _boundVariable + "}")
            _boundVariable = null;

        if (_missing) MarkMissing(false);
        UpdatePanels();
        Changed?.Invoke();
    }

    /// <summary>
    /// 内联取值时输入框是只读描述：点一下就是「我要自己写字」，切回文本态
    /// （清掉取值动作单元，光标落进空框，接着打字即可）。
    /// </summary>
    private void Box_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_inline == null) return;
        LeaveInline();
    }

    /// <summary>
    /// 标出「必填但还空着」：状态行换成红字。用状态行而不是红框——输入框的边框
    /// 归主题样式（悬停 / 焦点各有自己的颜色），就地改会盖掉那套视觉。
    /// </summary>
    public void MarkMissing(bool on)
    {
        if (_missing == on) return;
        _missing = on;
        UpdateStatus();
    }

    private void Image_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        OpenMenu();
    }

    // ============ 来源菜单 ============

    private void Source_Click(object sender, RoutedEventArgs e) => OpenMenu();

    private void OpenMenu()
    {
        if (Pickers == PickerKind.none) return;
        if (Menu.IsOpen) { Menu.IsOpen = false; return; }

        BuildMenu();
        AttachMenuWindow();
        Menu.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchBox.Text = "";
            FitMenuHeight();     // 先按「所有行都显示」量高度：两页共用一个高度，换页才不跳
            ApplyFilter("");
            SearchBox.Focus();
        }), DispatcherPriority.Input);
    }

    private void BuildMenu()
    {
        MenuList.Children.Clear();
        _rows.Clear();
        _active = null;
        _showingUnits = false;
        MenuScroll.Height = double.NaN;
        BuildMenuCore();
    }

    /// <summary>
    /// 两页一次建好（主列表 + 取值动作单元）：换页只切可见性——不关弹层、不改高度。
    /// 搜索时跨页匹配，所以在动作单元页也能输入变量名。
    /// </summary>
    private void BuildMenuCore()
    {
        AddVariables();

        if (HasSelectionPickers)
        {
            AddHeader("选择", 0);
            AddSelectionPickers();
        }

        AddChoices();
        AddSuggestions();

        if (_inline != null && Pickers.HasFlag(PickerKind.inline))
        {
            var inlineSpec = StepRegistry.Default.TryGet(_inline.Unit, out var inlineUnit)
                ? inlineUnit.Spec
                : null;

            AddHeader("当前取值", 0);
            AddItem("▷", "改参数…",
                inlineSpec is { Inputs.Count: > 0 } ? "调整这个取值动作单元的参数" : "这个取值不需要参数",
                "取值 改参数 inline", EditInline);
            AddItem("✎", "切回文本", "清掉取值动作单元，改成直接写字",
                "取值 切回文本 inline 文本", LeaveInline);
        }

        if (Pickers.HasFlag(PickerKind.inline))
        {
            AddHeader("取值", 0);
            AddItem("▷", "取值动作单元…", "运行到这里时现场取一个值",
                "取值动作单元 inline 取值", () => ShowMenuPage(true), page: 0, nav: true);

            AddHeader("取值动作单元", 1);
            AddUnitItems();
        }

        if (Pickers.HasFlag(PickerKind.clear))
        {
            AddSeparator(0);
            AddItem("⌫", "清空", "", "清空 clear", ClearValue);
        }
    }

    /// <summary>同一弹层里换页（导航项）：保持打开，清掉搜索词并回到顶部。</summary>
    private void ShowMenuPage(bool units)
    {
        _showingUnits = units;
        SearchBox.Text = "";
        ApplyFilter("");
        MenuScroll.ScrollToTop();
        SearchBox.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowMenuPage(false);

    /// <summary>
    /// 高度按「所有行都显示」来定：两页共用一个高度，换页不会忽大忽小；
    /// 行少时保持紧凑，多到放不下就让列表滚。
    /// </summary>
    private void FitMenuHeight()
    {
        MenuScroll.Height = double.NaN;
        foreach (var row in _rows) row.Host.Visibility = Visibility.Visible;
        MenuList.UpdateLayout();

        // 弹层刚开、还没量出来：保持自适应（下一次打开会量到）。
        if (MenuList.ActualHeight <= 0) return;
        MenuScroll.Height = Math.Clamp(MenuList.ActualHeight + 2, 120, 330);
    }

    private void AddVariables()
    {
        if (!Pickers.HasFlag(PickerKind.variable)) return;

        var infos = VariableInfos();
        if (infos.Count == 0) return;

        AddHeader("变量", 0);
        foreach (var info in infos)
        {
            string detail = KindLabel(info.Kind);
            if (!string.IsNullOrWhiteSpace(info.Origin)) detail += " · " + info.Origin;
            AddItem("·", info.Name, detail,
                info.Name + " " + detail,
                () => InsertVariable(info.Name));
        }
    }

    private bool HasSelectionPickers =>
        (Pickers & (PickerKind.file | PickerKind.folder | PickerKind.image | PickerKind.region
                    | PickerKind.window | PickerKind.windowClass | PickerKind.process
                    | PickerKind.point | PickerKind.color | PickerKind.keys)) != PickerKind.none;

    private void AddSelectionPickers()
    {
        if (Pickers.HasFlag(PickerKind.window))
            AddItem("▤", "选择窗口…", "填窗口标题（按包含匹配）", "窗口 window 标题", PickWindowTitle);

        if (Pickers.HasFlag(PickerKind.windowClass))
            AddItem("▤", "选择窗口…", "填窗口类名（精确匹配）", "窗口 window 类名", PickWindowClass);

        if (Pickers.HasFlag(PickerKind.process))
            AddItem("▥", "选择进程…", "从正在运行的程序里挑", "进程 process", PickProcessName);

        if (Pickers.HasFlag(PickerKind.file))
            AddItem("▦", "文件…", "", "文件 file 浏览", PickFile);

        if (Pickers.HasFlag(PickerKind.folder))
            AddItem("▦", "文件夹…", "", "文件夹 folder 目录", PickFolder);

        if (Pickers.HasFlag(PickerKind.image))
        {
            AddItem("▧", "框选截图…", "截下来的就是模板图", "截图 框选 图片 image", CaptureImage);
            AddItem("▧", "换图…", "从磁盘选一张", "换图 图片 image 文件", PickImageFile);
            AddItem("▧", "预览", "", "预览 图片 preview", PreviewImage);
        }

        if (Pickers.HasFlag(PickerKind.region))
            AddItem("▣", "屏幕区域…", "拖一块，填 x,y,w,h", "区域 region 框选", PickRegion);

        if (Pickers.HasFlag(PickerKind.point))
            AddItem("✛", "屏幕取点…", "单击一次，填好 X / Y", "坐标 取点 point", PickPoint);

        if (Pickers.HasFlag(PickerKind.color))
            AddItem("◐", "屏幕取色…", "填 #RRGGBB", "颜色 取色 color", PickColor);

        if (Pickers.HasFlag(PickerKind.keys))
            AddItem("⌨", "录制按键…", "按一下组合键，追加一行", "按键 录制 keys", RecordKey);
    }

    /// <summary>
    /// 「从列表选…」：按键 / 宏 / 子动作这类候选随用户数据变化，收进 ▾ 之后就不再在行内
    /// 摆一个下拉；副标题写分组（键盘分区 / 宏时长 / 子动作来源）。
    /// </summary>
    private void AddChoices()
    {
        if (!Pickers.HasFlag(PickerKind.choices) || ChoiceSource == null) return;

        List<StepChoice> choices;
        try { choices = ChoiceSource().ToList(); }
        catch { return; }
        if (choices.Count == 0) return;

        AddHeader("从列表选", 0);
        foreach (var choice in choices)
        {
            string label = choice.Label.Length > 0 ? choice.Label : choice.Value;
            AddItem("·", label, choice.Group,
                label + " " + choice.Group + " " + choice.Value,
                () => SetLiteral(choice.Value));
        }
    }

    private void AddSuggestions()
    {
        if (Pickers.HasFlag(PickerKind.noteCategory))
        {
            AddHeader("速记分类", 0);
            var dirs = NoteCategories();
            if (dirs.Count == 0)
                AddItem("·", "（还没有分类）", "在速记窗口里新建分类后会出现在这里",
                    "速记 分类 note", () => { });
            foreach (string name in dirs)
                AddItem("·", name, "", "速记 分类 " + name, () => SetLiteral(name));
        }

        if (Pickers.HasFlag(PickerKind.timeFormat))
        {
            AddHeader("常用格式", 0);
            foreach (string format in new[]
                     {
                         "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "HH:mm:ss",
                         "yyyyMMdd_HHmmss", "M月d日 HH:mm", "yyyy年M月d日",
                     })
                AddItem("·", format, "", "时间 格式 " + format, () => SetLiteral(format));
        }

        if (Pickers.HasFlag(PickerKind.fileFilter))
        {
            AddHeader("常用筛选", 0);
            foreach (var (label, filter) in new[]
                     {
                         ("所有文件", "所有文件|*.*"),
                         ("图片", "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif"),
                         ("文本 / 日志", "文本|*.txt;*.md;*.log;*.json"),
                         ("文档", "文档|*.docx;*.pdf;*.txt;*.md"),
                         ("可执行 / 脚本", "程序|*.exe;*.bat;*.cmd;*.ps1;*.ahk"),
                     })
                AddItem("·", label, filter, "筛选 文件类型 " + label + " " + filter,
                    () => SetLiteral(filter));
        }

        if (Pickers.HasFlag(PickerKind.separator))
        {
            // 存的是 \n / \t 转义写法（不是看不见的真字符）：单行框里能看见自己填了什么，
            // 「文本处理」「列表操作」读分隔符时会还原成真正的换行 / 制表符（TextEscapes）。
            AddHeader("常用分隔符", 0);
            foreach (var (label, value) in new (string, string)[]
                     {
                         ("逗号 ,", ","), ("分号 ;", ";"), ("竖线 |", "|"),
                         ("空格", " "),
                         ("换行 \\n", "\\n"),
                         ("Windows 换行 \\r\\n", "\\r\\n"),
                         ("Tab \\t", "\\t"),
                     })
                AddItem("·", label, "", "分隔符 separator " + label, () => SetLiteral(value));
        }
    }

    /// <summary>取值动作单元（第二页）：运行时现场取值的纯动作单元。</summary>
    private void AddUnitItems()
    {
        var units = StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => u.Spec)
            .OrderBy(s => UnitCategories.Rank(s.Category))
            .ThenBy(s => s.Category, StringComparer.CurrentCulture)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture);

        foreach (var spec in units)
            AddItem("▷", spec.Name, spec.Category, spec.Name + " " + spec.Category + " " + spec.Key,
                () =>
                {
                    _inline = Step.Of(spec.Key);
                    UpdatePanels();
                    Changed?.Invoke();
                },
                page: 1);
    }

    // ============ 菜单行 ============

    private void AddHeader(string text, int page)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = (Brush)FindResource("HintTextBrush"),
            Margin = new Thickness(8, 6, 8, 3),
        };
        var host = new Border { Child = label };
        MenuList.Children.Add(host);
        _rows.Add(new MenuRow(host, MenuRowKind.header, page));
    }

    private void AddSeparator(int page)
    {
        var host = new Border
        {
            Height = 1,
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)FindResource("BorderBrush"),
        };
        MenuList.Children.Add(host);
        _rows.Add(new MenuRow(host, MenuRowKind.separator, page));
    }

    private void AddItem(string glyph, string label, string detail, string search, Action run,
        int page = 0, bool nav = false)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock
        {
            Text = glyph,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("SecondaryTextBrush"),
        };
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        grid.Children.Add(icon);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (detail.Length > 0)
        {
            var sub = new TextBlock
            {
                Text = detail,
                FontSize = 11,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("HintTextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(sub, 2);
            grid.Children.Add(sub);
        }

        var host = new Border
        {
            Child = grid,
            Padding = new Thickness(6, 5, 6, 5),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        var row = new MenuRow(host, MenuRowKind.item, page, search, run, nav);
        host.MouseEnter += (_, _) => SetActive(row);
        host.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Activate(row);
        };

        MenuList.Children.Add(host);
        _rows.Add(row);
    }

    /// <summary>
    /// 执行一行：换页项（导航）保持弹层打开、就地换页；其余先关（紧接着可能弹对话框 / 框选）。
    /// </summary>
    private void Activate(MenuRow row)
    {
        if (row.Invoke == null) return;
        if (!row.Nav) CloseMenuImmediately();
        row.Invoke();
    }

    /// <summary>
    /// 立刻收起来源弹层：紧随其后的入口（屏幕取点 / 框选 / 点窗口）会在同一 UI 线程里
    /// Thread.Sleep 等桌面重绘再截图，Fade 淡出动画要等渲染帧才跑得完——否则弹层会被
    /// 拍进冻结画面、或浮在取点窗口上。普通关闭（Esc / 点外部 / 失焦）仍走淡出动画。
    /// </summary>
    private void CloseMenuImmediately()
    {
        if (!Menu.IsOpen) return;
        Menu.PopupAnimation = PopupAnimation.None;
        Menu.IsOpen = false;
        Menu.PopupAnimation = PopupAnimation.Fade;
    }

    private void SetActive(MenuRow? row)
    {
        if (_active != null)
            _active.Host.Background = Brushes.Transparent;
        _active = row;
        if (_active != null)
            _active.Host.Background = (Brush)FindResource("PressedBgBrush");
    }

    private void MoveActive(int delta)
    {
        var visible = _rows.Where(r => r.IsItem && r.Host.Visibility == Visibility.Visible).ToList();
        if (visible.Count == 0) return;

        int index = _active == null ? -1 : visible.IndexOf(_active);
        index = (index + delta + visible.Count) % visible.Count;
        SetActive(visible[index]);
        visible[index].Host.BringIntoView();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    /// <summary>
    /// 按当前页 / 搜索词决定哪些行可见：没搜索时只看当前页；**搜索时跨页匹配**
    /// （在「取值动作单元」页也能直接搜到变量和选择方式）。
    /// </summary>
    private void ApplyFilter(string query)
    {
        query = (query ?? "").Trim();
        bool searching = query.Length > 0;
        SetActive(null);

        foreach (var row in _rows)
        {
            if (!row.IsItem) continue;

            bool visible = searching
                ? row.Search.Contains(query, StringComparison.OrdinalIgnoreCase)
                : row.Page == (_showingUnits ? 1 : 0);

            // 「取值动作单元…」是换页入口：只在主列表、且没搜索时出现（搜索时动作单元自己就在结果里）。
            if (row.Nav) visible = !searching && !_showingUnits;

            row.Host.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }

        // 标题 / 分隔线都是一段的分界：到下一个分界之前有可见条目才显示。
        bool seen = false;
        for (int i = _rows.Count - 1; i >= 0; i--)
        {
            var row = _rows[i];
            if (row.IsItem)
            {
                seen |= row.Host.Visibility == Visibility.Visible;
                continue;
            }

            row.Host.Visibility = seen ? Visibility.Visible : Visibility.Collapsed;
            seen = false;
        }

        UpdateCrumb();
        MoveActive(1);
    }

    /// <summary>面包屑：主列表写「来源」，二级页写「取值动作单元」并露出返回。</summary>
    private void UpdateCrumb()
    {
        CrumbText.Text = _showingUnits ? "取值动作单元" : "来源";
        BackButton.Visibility = _showingUnits ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.ToolTip = _showingUnits
            ? "在取值动作单元里搜索（会连变量一起找）；点 ‹ 返回"
            : "搜索变量、选择方式、取值动作单元";
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                e.Handled = true;
                MoveActive(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveActive(-1);
                break;
            case Key.Enter:
                e.Handled = true;
                if (_active != null) Activate(_active);
                break;
            case Key.Escape:
                e.Handled = true;
                Menu.IsOpen = false;
                break;
        }
    }

    // ============ 自己管关闭（StaysOpen=True）============

    private void AttachMenuWindow()
    {
        Window? window = Window.GetWindow(this);
        if (ReferenceEquals(window, _menuWindow)) return;

        DetachMenuWindow();
        _menuWindow = window;
        if (window == null) return;

        window.PreviewMouseDown += MenuWindow_MouseDown;
        window.PreviewMouseWheel += MenuWindow_Wheel;
        window.Deactivated += MenuWindow_Deactivated;
        window.LocationChanged += MenuWindow_Moved;
        window.SizeChanged += MenuWindow_Moved;
    }

    private void DetachMenuWindow()
    {
        if (_menuWindow == null) return;

        _menuWindow.PreviewMouseDown -= MenuWindow_MouseDown;
        _menuWindow.PreviewMouseWheel -= MenuWindow_Wheel;
        _menuWindow.Deactivated -= MenuWindow_Deactivated;
        _menuWindow.LocationChanged -= MenuWindow_Moved;
        _menuWindow.SizeChanged -= MenuWindow_Moved;
        _menuWindow = null;
    }

    /// <summary>
    /// 点弹层外就关（弹层里的点击走不到窗口这一层）。▾ 与图片胶囊自己负责开关，
    /// 所以点它们时不让这里先关一次——否则会「关掉又立刻开」。
    /// </summary>
    private void MenuWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!Menu.IsOpen || Menu.Child.IsMouseOver) return;
        if (SourceButton.IsMouseOver || ThumbPanel.IsMouseOver) return;
        Menu.IsOpen = false;
    }

    private void MenuWindow_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (Menu.IsOpen && !Menu.Child.IsMouseOver) Menu.IsOpen = false;
    }

    private void MenuWindow_Deactivated(object? sender, EventArgs e) => Menu.IsOpen = false;

    private void MenuWindow_Moved(object? sender, EventArgs e) => Menu.IsOpen = false;

    // ============ 插入 / 选择 ============

    private void InsertVariable(string name)
    {
        // 插入变量 = 用户要的是一个值，不再是「现场取值的动作单元」。
        _inline = null;

        if (VariableNameMode)
        {
            Box.Text = name;
            Box.CaretIndex = name.Length;
            Box.Focus();
            Changed?.Invoke();
            return;
        }

        string token = "${" + name + "}";
        string text = Box.Text ?? "";
        int start;
        int length;

        if (Box.SelectionLength > 0)
        {
            start = Math.Clamp(Box.SelectionStart, 0, text.Length);
            length = Math.Clamp(Box.SelectionLength, 0, text.Length - start);
        }
        else if (string.Equals(text.Trim(), name, StringComparison.OrdinalIgnoreCase))
        {
            // 整段就是个变量名（常见的「写成文本」错误）：直接换掉。
            start = 0;
            length = text.Length;
        }
        else if (Box.IsKeyboardFocusWithin)
        {
            start = Math.Clamp(Box.CaretIndex, 0, text.Length);
            length = 0;
        }
        else
        {
            start = text.Length;
            length = 0;
        }

        Box.Text = text.Remove(start, length).Insert(start, token);
        Box.CaretIndex = start + token.Length;

        // 整串正好是一个变量 → 存成引用，保留 list / object / number 类型。
        _boundVariable = string.Equals(Box.Text.Trim(), token, StringComparison.Ordinal) ? name : null;
        Box.IsReadOnly = false;
        UpdatePanels();
        Box.Focus();
        Changed?.Invoke();
    }

    private void SetLiteral(string text)
    {
        LiteralText = text;
        Changed?.Invoke();
    }

    private void ClearValue()
    {
        _inline = null;
        _boundVariable = null;
        _listLiteral = null;
        Box.Text = "";
        UpdatePanels();
        Changed?.Invoke();
    }

    private void PickWindowTitle()
    {
        var info = SystemPickerDialog.PickWindow(Window.GetWindow(this));
        if (info != null) SetLiteral(info.Title);
    }

    private void PickWindowClass()
    {
        var info = SystemPickerDialog.PickWindow(Window.GetWindow(this));
        if (info != null && info.ClassName.Length > 0) SetLiteral(info.ClassName);
    }

    private void PickProcessName()
    {
        string? name = SystemPickerDialog.PickProcess(Window.GetWindow(this));
        if (!string.IsNullOrWhiteSpace(name)) SetLiteral(name!);
    }

    private void PickFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择文件",
            Filter = "所有文件|*.*",
            InitialDirectory = Directory.Exists(AppPaths.DefaultWorkdir())
                ? AppPaths.DefaultWorkdir() : "",
        };
        if (dlg.ShowDialog() == true) SetLiteral(dlg.FileName);
    }

    private void PickFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择文件夹",
            InitialDirectory = Directory.Exists(AppPaths.DefaultWorkdir())
                ? AppPaths.DefaultWorkdir() : "",
        };
        if (dlg.ShowDialog() == true) SetLiteral(dlg.FolderName);
    }

    private void CaptureImage()
    {
        var window = Window.GetWindow(this);
        if (!ScreenPicker.TryPickRegion(window, out var area, out var image)) return;

        try
        {
            string path = ImageLibrary.SaveGenerated(image);
            SetLiteral(ImageLibrary.ToStoredPath(path));
            Captured?.Invoke(area);
        }
        catch (Exception ex)
        {
            ConfirmDialog.Info(window, "框选截图", "保存模板图失败：" + ex.Message);
        }
    }

    private void PickImageFile()
    {
        ImageLibrary.EnsureDir();
        var dlg = new OpenFileDialog
        {
            Title = "选择图片",
            Filter = "图像|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
            InitialDirectory = ImageLibrary.UserDir,
        };
        if (dlg.ShowDialog() != true) return;
        SetLiteral(ImageLibrary.ImportUserImage(dlg.FileName));
    }

    private void PreviewImage()
    {
        string stored = (Box.Text ?? "").Trim();
        string path = stored.Length == 0 ? "" : ImageLibrary.Resolve(stored, ResourceRoot);
        if (!File.Exists(path))
        {
            ToastService.Show("没有可预览的图片", "参数", 2500, ToastKind.warning);
            return;
        }

        try
        {
            ImagePreview.Show(Window.GetWindow(this), LoadBitmap(path), Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ToastService.Show("预览失败：" + ex.Message, "参数", ToastService.StickyMs, ToastKind.error);
        }
    }

    private void PickRegion()
    {
        if (!ScreenPicker.TryPickRegion(Window.GetWindow(this), out var area, out _)) return;
        SetLiteral(string.Format(CultureInfo.InvariantCulture,
            "{0},{1},{2},{3}", area.X, area.Y, area.Width, area.Height));
        Captured?.Invoke(area);
    }

    private void PickPoint()
    {
        if (!ScreenPicker.TryPickPoint(Window.GetWindow(this), out int x, out int y)) return;

        if (PointPicked != null) PointPicked(x, y);
        else SetLiteral(string.Equals(_spec?.Key, "y", StringComparison.OrdinalIgnoreCase)
            ? y.ToString(CultureInfo.InvariantCulture)
            : x.ToString(CultureInfo.InvariantCulture));
    }

    private void PickColor()
    {
        if (!ScreenPicker.TryPickColor(Window.GetWindow(this), out string hex)) return;
        if (hex.Length > 0) SetLiteral(hex);
    }

    private void RecordKey()
    {
        _inline = null;
        HotkeyCapture.SetHotkey(KeyBox, "");
        KeyPopup.IsOpen = true;
        Dispatcher.BeginInvoke(new Action(() => KeyBox.Focus()), DispatcherPriority.Input);
    }

    private void OnKeyRecorded(string chord)
    {
        KeyPopup.IsOpen = false;
        if (chord.Length == 0) return;

        string text = (Box.Text ?? "").TrimEnd();
        Box.Text = text.Length == 0 ? chord : text + Environment.NewLine + chord;
        Box.CaretIndex = Box.Text.Length;
        Box.IsReadOnly = false;
        UpdatePanels();
        Box.Focus();
        Changed?.Invoke();
    }

    // ============ 内联取值 ============

    /// <summary>切回文本态：丢掉取值动作单元，输入框恢复可编辑并清空。</summary>
    private void LeaveInline()
    {
        if (_inline == null) return;

        _loading = true;
        try
        {
            _inline = null;
            _boundVariable = null;
            _listLiteral = null;
            Box.Text = "";
        }
        finally { _loading = false; }

        Box.IsReadOnly = false;
        UpdatePanels();
        Box.Focus();
        Changed?.Invoke();
    }

    private void EditInline()
    {
        if (_inline == null) return;

        var dlg = new StepEditorDialog(_inline, inline: true,
            program: Program, variableNames: VariableNameSource, resourceRoot: ResourceRoot,
            variableInfos: VariableInfoSource)
        {
            Owner = Window.GetWindow(this),
        };
        if (dlg.ShowDialog() == true && dlg.Result != null)
        {
            _inline = dlg.Result;
            UpdatePanels();
            Changed?.Invoke();
        }
    }

    // ============ 杂项 ============

    /// <summary>
    /// 取值动作单元还缺哪些必填参数：新用户从状态行就知道要先去点「改参数…」，
    /// 而不是运行到一半才发现没填。
    /// </summary>
    private static List<string> MissingRequired(Step step, UnitSpec? spec)
    {
        var missing = new List<string>();
        if (spec == null) return missing;

        foreach (var input in spec.Inputs)
        {
            if (!input.Required) continue;
            // 有默认值的参数没填也能跑（引擎回落到 Default），不算「还缺」。
            if (input.Default != null) continue;
            if (!StepSummary.IsVisible(input, step, spec)) continue;
            if (!step.In.TryGetValue(input.Key, out var arg) || arg.IsEmpty) missing.Add(input.Label);
        }

        return missing;
    }

    private IReadOnlyList<VariableInfo> VariableInfos()
    {
        try
        {
            if (VariableInfoSource != null) return VariableInfoSource();
            return (VariableNameSource?.Invoke() ?? Enumerable.Empty<string>())
                .Select(n => (n ?? "").Trim())
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.CurrentCulture)
                .Select(n => new VariableInfo(n, ValueKind.text, ""))
                .ToList();
        }
        catch
        {
            return Array.Empty<VariableInfo>();
        }
    }

    private static IReadOnlyList<string> NoteCategories()
    {
        try
        {
            return Directory.Exists(AppPaths.NotesDir)
                ? Directory.GetDirectories(AppPaths.NotesDir)
                    .Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => n!)
                    .OrderBy(n => n, StringComparer.CurrentCulture)
                    .ToList()
                : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string KindLabel(ValueKind kind) => kind switch
    {
        ValueKind.number => "数字",
        ValueKind.@bool => "开关",
        ValueKind.list => "列表",
        ValueKind.@object => "对象",
        _ => "文本",
    };

    /// <summary>
    /// 新切到「取值动作单元」时默认选中的动作单元：挑最常用、参数最少的一个，
    /// 别让新用户一进来就撞上「进程」这种要填进程名、说明又长的动作单元。
    /// </summary>
    private static readonly string[] PreferredDefaults =
    {
        "text.selection", "data.time", "input.cursor", "screen.findImage",
    };

    private static string FirstPureUnit()
    {
        var keys = StepRegistry.Default.Units
            .Where(u => u.Spec.Kind == UnitKind.pure)
            .Select(u => u.Spec.Key)
            .ToList();

        foreach (string preferred in PreferredDefaults)
            if (keys.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                return preferred;

        return keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
               ?? "text.process";
    }
}
