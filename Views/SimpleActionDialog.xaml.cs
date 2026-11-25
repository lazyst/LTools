using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Features.Panel;
using Microsoft.Win32;

namespace LTools.Views;

/// <summary>简单动作表单的结果：名称、参数，以及「转换为组合动作」是否被点下。</summary>
internal sealed record SimpleActionEdit(string Name, Dictionary<string, string> Values, bool Convert);

/// <summary>
/// 简单动作的极简表单：一种预设只问它自己的那一两个参数（文件 / 文件夹 / 程序 / 网址 / 宏…）。
/// </summary>
/// <remarks>
/// <para>
/// 它替代不了组合动作编辑器——**只改这一个动作单元的参数与名称**，要加步骤 / 变量 / 分支，
/// 走左下角「转换为组合动作…」（清掉 <see cref="ActionDto.SimpleKind"/> 后开编辑器）。
/// </para>
/// <para>
/// 字段按种类现建（照 <see cref="FeatureParamsDialog"/> 的做法），新增简单动作种类时只补一个
/// <c>switch</c> 分支；别为每种预设各写一个窗口。
/// </para>
/// </remarks>
public partial class SimpleActionDialog : Window
{
    private readonly SimpleActions.Kind _kind;
    private readonly ActionDto? _existing;
    private readonly List<MacroMeta> _macros;

    private TextBox? _nameBox;
    private TextBox? _text;
    private TextBox? _extra;
    private TextBox? _keyBox;
    private ComboBox? _macroBox;
    private ComboBox? _terminalBox;
    private ComboBox? _modeBox;

    private bool _nameTouched;
    private bool _settingName;

    private SimpleActionDialog(SimpleActions.Kind kind, ActionDto? existing)
    {
        InitializeComponent();

        _kind = kind;
        _existing = existing;
        _macros = MacroStore.List();
        _nameTouched = existing != null;   // 已有动作的名字是用户的，不跟着参数变

        Title = (existing == null ? "新建简单动作 · " : "编辑简单动作 · ") + kind.Name;
        Headline.Text = kind.Name;
        ConvertBtn.Visibility = existing == null ? Visibility.Collapsed : Visibility.Visible;

        BuildForm();
    }

    /// <summary>弹出表单；取消返回 null。</summary>
    internal static SimpleActionEdit? Show(Window? owner, SimpleActions.Kind kind, ActionDto? existing)
    {
        var dialog = new SimpleActionDialog(kind, existing)
        {
            Owner = owner,
            Topmost = owner == null,
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>确定后的结果（取消时为 null）。</summary>
    internal SimpleActionEdit? Result { get; private set; }

    // ============ 表单 ============

    private void BuildForm()
    {
        var values = SimpleActions.ValuesOf(_kind, _existing);

        var nameHost = FieldHost("名称");
        _nameBox = new TextBox
        {
            MinHeight = 30,
            Text = _existing?.Name ?? SimpleActions.DefaultName(_kind, values, MacroNameOf(values)),
        };
        _nameBox.TextChanged += (_, _) =>
        {
            if (!_settingName) _nameTouched = true;
        };
        nameHost.Children.Add(_nameBox);
        FormHost.Children.Add(nameHost);

        switch (_kind.Key)
        {
            case SimpleActions.OpenFile:
                _text = TextRow("文件", values["path"], "要打开的文件");
                AddBrowseButton(_text, PickFile);
                break;

            case SimpleActions.OpenFolder:
                _text = TextRow("文件夹", values["path"], "要打开的文件夹");
                AddBrowseButton(_text, PickFolder);
                break;

            case SimpleActions.LaunchApp:
                _text = TextRow("程序", values["target"], "可执行文件 / 快捷方式 / 脚本");
                AddBrowseButton(_text, PickProgram);
                _extra = TextRow("参数", values["args"], "可留空；会原样追加在程序路径后面");
                break;

            case SimpleActions.OpenUrl:
                _text = TextRow("网址", values["url"], "如 https://example.com");
                break;

            case SimpleActions.ReplayMacro:
                AddMacroRow(values["macroId"]);
                break;

            case SimpleActions.RunCommand:
                _text = TextRow("命令", values["command"], "一行或多行；按下面选的终端执行", multiline: true);
                AddTerminalRow(values["terminal"]);
                AddModeRow(values["capture"] == "true");
                break;

            case SimpleActions.SendKeys:
                _keyBox = TextRow("按键", "", "点这里，然后直接按下要发送的组合键（如 Ctrl+S）",
                    hint: "点击按键框后直接按下组合键；Esc 取消、Tab 结束录入。");
                HotkeyCapture.SetHotkey(_keyBox, ChordOf(values));
                HotkeyCapture.Attach(_keyBox, _ => RefreshName());
                break;
        }

        FormHost.Children.Add(new TextBlock
        {
            Text = _kind.Description,
            Style = (Style)FindResource("HintLabel"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        // 新建时默认名跟着第一个字段走（回放宏会带出选中的宏名）
        RefreshName();
    }

    private StackPanel FieldHost(string label)
    {
        var host = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        host.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("SectionLabel"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        return host;
    }

    /// <summary>一行文本（可多行）；右边可由调用方补「浏览…」。</summary>
    private TextBox TextRow(string label, string value, string placeholder, bool multiline = false,
        string? hint = null)
    {
        var host = FieldHost(label);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var box = new TextBox
        {
            MinHeight = 30,
            Text = value,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            Height = multiline ? 88 : double.NaN,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };
        Controls.TextBoxPlaceholder.SetText(box, placeholder);
        box.TextChanged += (_, _) =>
        {
            if (!ReferenceEquals(box, _keyBox)) RefreshName();
        };
        grid.Children.Add(box);

        var browse = new Button
        {
            Content = "浏览…",
            Style = (Style)FindResource("BtnGhost"),
            MinWidth = 70,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);

        host.Children.Add(grid);
        if (!string.IsNullOrWhiteSpace(hint))
        {
            host.Children.Add(new TextBlock
            {
                Text = hint,
                Style = (Style)FindResource("HintLabel"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        FormHost.Children.Add(host);
        return box;
    }

    private void AddBrowseButton(TextBox box, Func<Window, string?> pick)
    {
        if (box.Parent is not Grid grid) return;
        var button = grid.Children.OfType<Button>().FirstOrDefault();
        if (button == null) return;

        button.Visibility = Visibility.Visible;
        button.Click += (_, _) =>
        {
            string? picked = pick(this);
            if (!string.IsNullOrEmpty(picked)) box.Text = picked;
        };
    }

    private void AddMacroRow(string selectedId)
    {
        var host = FieldHost("宏");

        var box = new ComboBox { MinHeight = 30 };
        foreach (var macro in _macros)
        {
            box.Items.Add(new ComboBoxItem
            {
                Content = string.IsNullOrWhiteSpace(macro.Name) ? macro.Id : macro.Name,
                Tag = macro.Id,
            });
        }

        bool missing = selectedId.Length > 0 && _macros.All(m => m.Id != selectedId);
        if (missing)
        {
            // 宏文件被删了也别静默换成第一项：原样留住，用户看得到问题
            box.Items.Add(new ComboBoxItem { Content = "（宏文件缺失：" + selectedId + "）", Tag = selectedId });
        }
        if (box.Items.Count == 0)
            box.Items.Add(new ComboBoxItem { Content = "（还没有录制的宏）", Tag = "" });

        SelectTag(box, selectedId);
        box.SelectionChanged += (_, _) => RefreshName();
        host.Children.Add(box);
        if (_macros.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "还没有录制的宏：先到「设置 → 键鼠宏」录制一段。",
                Style = (Style)FindResource("HintLabel"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }
        FormHost.Children.Add(host);
        _macroBox = box;
    }

    private void AddTerminalRow(string selected)
    {
        var host = FieldHost("终端");
        var box = new ComboBox { MinHeight = 30 };
        foreach (string value in new[] { "direct", "pwsh7", "pwsh5", "cmd", "gitbash", "wslbash", "wt" })
        {
            box.Items.Add(new ComboBoxItem { Content = OptionLabels.Of(value), Tag = value });
        }
        SelectTag(box, selected);
        host.Children.Add(box);
        FormHost.Children.Add(host);
        _terminalBox = box;
    }

    private void AddModeRow(bool capture)
    {
        var host = FieldHost("运行方式");
        var box = new ComboBox { MinHeight = 30 };
        box.Items.Add(new ComboBoxItem { Content = "打开终端窗口运行", Tag = "window" });
        box.Items.Add(new ComboBoxItem { Content = "在后台运行并收下输出", Tag = "capture" });
        SelectTag(box, capture ? "capture" : "window");
        host.Children.Add(box);
        FormHost.Children.Add(host);
        _modeBox = box;
    }

    private static void SelectTag(ComboBox box, string? tag)
    {
        foreach (var item in box.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                box.SelectedItem = item;
                return;
            }
        }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    // ============ 读值 ============

    private Dictionary<string, string> ReadValues()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (_kind.Key)
        {
            case SimpleActions.OpenFile:
            case SimpleActions.OpenFolder:
                values["path"] = _text?.Text.Trim() ?? "";
                break;

            case SimpleActions.LaunchApp:
                values["target"] = _text?.Text.Trim() ?? "";
                values["args"] = _extra?.Text.Trim() ?? "";
                break;

            case SimpleActions.OpenUrl:
                values["url"] = _text?.Text.Trim() ?? "";
                break;

            case SimpleActions.ReplayMacro:
                values["macroId"] = TagOf(_macroBox);
                break;

            case SimpleActions.RunCommand:
                values["command"] = _text?.Text.Trim() ?? "";
                values["terminal"] = TagOf(_terminalBox) is { Length: > 0 } terminal ? terminal : "direct";
                values["capture"] = TagOf(_modeBox) == "capture" ? "true" : "false";
                break;

            case SimpleActions.SendKeys:
            {
                string chord = _keyBox == null ? "" : HotkeyCapture.GetHotkey(_keyBox);
                SplitChord(chord, out string key, out string mods);
                values["key"] = key;
                values["mods"] = mods;
                break;
            }
        }

        return values;
    }

    private void RefreshName()
    {
        if (_nameBox == null || _nameTouched) return;

        var values = ReadValues();
        string name = SimpleActions.DefaultName(_kind, values, MacroNameOf(values));
        if (name.Length == 0 || name == _nameBox.Text) return;

        _settingName = true;
        _nameBox.Text = name;
        _settingName = false;
    }

    private string? MacroNameOf(IReadOnlyDictionary<string, string> values)
    {
        string id = values.TryGetValue("macroId", out string? value) ? value : "";
        return id.Length == 0 ? null : _macros.FirstOrDefault(m => m.Id == id)?.Name;
    }

    // ============ 确定 / 转换 ============

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (TryBuild(requireFields: true, convert: false) is not { } edit) return;
        Result = edit;
        DialogResult = true;
    }

    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (TryBuild(requireFields: false, convert: true) is not { } edit) return;
        Result = edit;
        DialogResult = true;
    }

    /// <summary>把表单读成结果；必填项不合格（只在校验时）返回 null 并就地提示。</summary>
    internal SimpleActionEdit? TryBuild(bool requireFields, bool convert)
    {
        var values = ReadValues();

        if (requireFields && Missing(values) is { } missing)
        {
            ConfirmDialog.Info(this, _kind.Name, missing);
            return null;
        }

        string name = _nameBox?.Text.Trim() ?? "";
        if (name.Length == 0) name = SimpleActions.DefaultName(_kind, values, MacroNameOf(values));
        if (name.Length == 0) name = _kind.Name;

        return new SimpleActionEdit(name, values, convert);
    }

    private string? Missing(IReadOnlyDictionary<string, string> values)
    {
        string Get(string key) => values.TryGetValue(key, out string? value) ? value : "";

        return _kind.Key switch
        {
            SimpleActions.OpenFile when Get("path").Length == 0 => "先选一个要打开的文件。",
            SimpleActions.OpenFolder when Get("path").Length == 0 => "先选一个文件夹。",
            SimpleActions.LaunchApp when Get("target").Length == 0 => "先选要启动的程序。",
            SimpleActions.OpenUrl when Get("url").Length == 0 => "先填要打开的网址。",
            SimpleActions.ReplayMacro when Get("macroId").Length == 0 => "先选一个录好的宏。",
            SimpleActions.RunCommand when Get("command").Length == 0 => "填要运行的命令。",
            SimpleActions.SendKeys when Get("key").Length == 0 => "点按键框，然后按下要发送的组合键。",
            _ => null,
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ============ 选择文件 ============

    private static string? PickFile(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要打开的文件",
            CheckFileExists = true,
            Filter = "所有文件|*.*",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static string? PickProgram(Window owner)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择程序",
            CheckFileExists = true,
            Filter = "程序|*.exe;*.lnk;*.bat;*.cmd;*.ps1|所有文件|*.*",
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    private static string? PickFolder(Window owner)
    {
        var dialog = new OpenFolderDialog { Title = "选择文件夹" };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    // ============ 小工具 ============

    private static string TagOf(ComboBox? box) =>
        (box?.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private static string ChordOf(IReadOnlyDictionary<string, string> values)
    {
        string key = values.TryGetValue("key", out string? k) ? k : "";
        string mods = values.TryGetValue("mods", out string? m) ? m : "";
        if (key.Length == 0) return "";
        return mods.Length > 0 ? mods + "+" + key : key;
    }

    /// <summary>把 <c>ctrl+shift+s</c> 拆成主键 <c>s</c> 与修饰键 <c>ctrl+shift</c>。</summary>
    private static void SplitChord(string chord, out string key, out string mods)
    {
        string[] parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) { key = ""; mods = ""; return; }

        key = parts[^1].ToLowerInvariant();
        mods = string.Join("+", parts[..^1]).ToLowerInvariant();
    }
}
