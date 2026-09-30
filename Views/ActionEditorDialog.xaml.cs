using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CapsLockPro.Features;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace CapsLockPro.Views;

/// <summary>
/// 普通动作编辑对话框（计划 §10.1）：类型下拉（6 类，含「组合动作 ▸」跳转项）+ 类型特定字段 + 名称 + 图标。
/// 组合动作用独立窗口 <see cref="CompositeActionDialog"/>；选「组合动作 ▸」即跳转（见 <see cref="ActionEditor"/>）。
/// 编辑组合步骤时以 <c>allowComposite:false</c> 构造，从源头禁嵌套。
/// </summary>
public partial class ActionEditorDialog : Window
{
    private ActionDto _draft;
    private bool _ready;   // 初始载入期间抑制事件副作用

    /// <summary>用户把类型切到「组合动作」→ 改开组合编辑器（不带数据跳转，§10.1）。</summary>
    public bool JumpToComposite { get; private set; }

    private readonly Dictionary<ActionType, FrameworkElement> _fieldsByType = new();

    public ActionEditorDialog(string title, ActionDto? existing, bool allowComposite = true)
    {
        InitializeComponent();
        _draft = existing != null ? Clone(existing) : new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default };
        if (!string.IsNullOrEmpty(title)) Title = title;

        _fieldsByType[ActionType.launchApp] = F_LaunchApp;
        _fieldsByType[ActionType.openFile] = F_OpenFile;
        _fieldsByType[ActionType.openFolder] = F_OpenFolder;
        _fieldsByType[ActionType.openUrl] = F_OpenUrl;
        _fieldsByType[ActionType.runCommand] = F_RunCommand;
        _fieldsByType[ActionType.sendText] = F_SendText;
        _fieldsByType[ActionType.@internal] = F_Internal;

        // 类型下拉：allowComposite=false（编辑组合步骤时）不含「组合动作」项，从源头禁嵌套；
        // 含该项时选中即跳转到组合动作编辑器（§10.1）。
        var types = new List<TypeItem>
        {
            new("启动软件", ActionType.launchApp),
            new("打开文件", ActionType.openFile),
            new("打开文件夹", ActionType.openFolder),
            new("打开网址", ActionType.openUrl),
            new("运行命令", ActionType.runCommand),
            new("发送文本", ActionType.sendText),
            new("内部动作", ActionType.@internal),
        };
        if (allowComposite) types.Add(new("组合动作 ▸", ActionType.composite));
        TypeCombo.ItemsSource = types;

        // 终端下拉（与 runCommand 字段一致）
        foreach (var t in new[] { "direct", "pwsh7", "pwsh5", "cmd", "gitbash", "wslbash", "wt" })
            TerminalCombo.Items.Add(new ComboItem(TerminalLauncher.DisplayLabel(t), t));

        // 内部命令下拉（只列非 tool.toggle 的可用命令）
        foreach (var c in InternalActionRegistry.Commands)
            CommandCombo.Items.Add(new ComboItem(FormatCommand(c), c));

        LoadDraft();
        _ready = true;
        Loaded += (_, _) => NameBox.Focus();
    }

    /// <summary>返回值；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>弹出模态编辑器。返回编辑后的动作；取消返回 null。</summary>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing)
        => Show(owner, title, existing, allowComposite: true);

    /// <summary>同 <see cref="Show(Window, string, ActionDto?)"/>，可禁用「组合动作」类型项（编辑组合步骤时用）。</summary>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing, bool allowComposite)
    {
        var dlg = new ActionEditorDialog(title, existing, allowComposite) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    // —— 载入草稿到控件 ——
    private void LoadDraft()
    {
        NameBox.Text = _draft.Name;

        // 类型选择
        int ti = IndexOf(_draft.Type);
        TypeCombo.SelectedIndex = ti >= 0 && ti < TypeCombo.Items.Count ? ti : 0;

        TargetBox.Text = _draft.Target ?? "";
        ArgsBox.Text = _draft.Args ?? "";
        LaunchWorkdirBox.Text = _draft.Workdir ?? "";
        FileBox.Text = _draft.Path ?? "";
        FolderBox.Text = _draft.Path ?? "";
        UrlBox.Text = _draft.Url ?? "";
        CmdBox.Text = _draft.Cmd ?? "";
        CmdWorkdirBox.Text = _draft.Workdir ?? "";
        KeepWindowBox.IsChecked = _draft.KeepWindow ?? false;

        SendTextBox.Text = _draft.Text ?? "";
        SendModeCombo.SelectedIndex = (_draft.Mode ?? SendTextMode.auto) switch
        {
            SendTextMode.type => 1,
            SendTextMode.paste => 2,
            _ => 0,
        };
        AppendEnterBox.IsChecked = _draft.AppendEnter ?? false;

        TerminalCombo.SelectedIndex = IndexOfTerminal(_draft.Terminal ?? "direct");
        CommandCombo.SelectedIndex = IndexOfCommand(_draft.Command ?? "");

        IconPicker.SelectedIcon = _draft.Icon;
        ShowFields(_draft.Type);
    }

    private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (TypeCombo.SelectedItem is not TypeItem t) return;
        // 选「组合动作」→ 跳转到组合动作编辑器（不带数据），由 ActionEditor 循环改开
        if (t.Type == ActionType.composite)
        {
            JumpToComposite = true;
            DialogResult = false;
            return;
        }
        ShowFields(t.Type);
    }

    private void ShowFields(ActionType t)
    {
        // 只切换「类型面板自身」的可见性。注意：FieldsPanel 的唯一直接子级是外层 Border，
        // 折叠它会连整块字段区一起隐藏（各 F_* 面板在该 Border 内的 Grid 里）——故必须遍历
        // _fieldsByType.Values，而不是 FieldsPanel.Children。
        foreach (var fe in _fieldsByType.Values)
            fe.Visibility = Visibility.Collapsed;
        if (_fieldsByType.TryGetValue(t, out var selected))
            selected.Visibility = Visibility.Visible;
    }

    // —— 浏览 ——
    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*", Title = "选择程序" };
        if (!string.IsNullOrEmpty(TargetBox.Text)) ofd.InitialDirectory = SafeDir(TargetBox.Text);
        if (ofd.ShowDialog(this) == true) TargetBox.Text = ofd.FileName;
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "所有文件 (*.*)|*.*", Title = "选择文件" };
        if (!string.IsNullOrEmpty(FileBox.Text)) ofd.InitialDirectory = SafeDir(FileBox.Text);
        if (ofd.ShowDialog(this) == true) FileBox.Text = ofd.FileName;
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择文件夹" };
        if (!string.IsNullOrEmpty(FolderBox.Text)) d.InitialDirectory = SafeDir(FolderBox.Text);
        if (d.ShowDialog(this) == true) FolderBox.Text = d.FolderName;
    }

    private void BrowseLaunchWorkdir_Click(object sender, RoutedEventArgs e) => BrowseWorkdir(LaunchWorkdirBox);

    private void BrowseCmdWorkdir_Click(object sender, RoutedEventArgs e) => BrowseWorkdir(CmdWorkdirBox);

    /// <summary>选工作目录：调文件资源管理器（.NET 8 原生 <see cref="OpenFolderDialog"/>）并回填指定框。</summary>
    private void BrowseWorkdir(TextBox box)
    {
        var d = new OpenFolderDialog { Title = "选择工作目录" };
        if (!string.IsNullOrEmpty(box.Text)) d.InitialDirectory = SafeDir(box.Text);
        if (d.ShowDialog(this) == true) box.Text = d.FolderName;
    }

    // —— 确定 ——
    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            ConfirmDialog.Info(this, "动作", "名称不能为空。");
            NameBox.Focus();
            return;
        }

        var type = TypeCombo.SelectedItem is TypeItem t ? t.Type : ActionType.runCommand;
        _draft.Name = name;
        _draft.Type = type;

        // 清掉不相关类型字段，避免残留脏数据
        _draft.Target = null; _draft.Args = null; _draft.Path = null; _draft.Url = null;
        _draft.Cmd = null; _draft.Terminal = null; _draft.KeepWindow = null;
        _draft.Command = null; _draft.Steps = null; _draft.CompositeOnFail = null;
        _draft.Text = null; _draft.Mode = null; _draft.AppendEnter = null;

        switch (type)
        {
            case ActionType.launchApp:
                if (string.IsNullOrWhiteSpace(TargetBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写程序路径。"); return; }
                _draft.Target = TargetBox.Text.Trim();
                _draft.Args = ArgsBox.Text.Trim();
                _draft.Workdir = LaunchWorkdirBox.Text.Trim();
                break;
            case ActionType.openFile:
                if (string.IsNullOrWhiteSpace(FileBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写文件路径。"); return; }
                _draft.Path = FileBox.Text.Trim();
                break;
            case ActionType.openFolder:
                if (string.IsNullOrWhiteSpace(FolderBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写文件夹路径。"); return; }
                _draft.Path = FolderBox.Text.Trim();
                break;
            case ActionType.openUrl:
                if (string.IsNullOrWhiteSpace(UrlBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写网址。"); return; }
                _draft.Url = UrlBox.Text.Trim();
                break;
            case ActionType.runCommand:
                _draft.Cmd = CmdBox.Text.Trim();
                _draft.Terminal = TerminalCombo.SelectedItem is ComboItem ti ? ti.Value : "direct";
                _draft.KeepWindow = KeepWindowBox.IsChecked == true;
                _draft.Workdir = CmdWorkdirBox.Text.Trim();
                break;
            case ActionType.@internal:
                if (CommandCombo.SelectedItem is not ComboItem ci) { ConfirmDialog.Info(this, "动作", "请选择内部命令。"); return; }
                _draft.Command = ci.Value;
                break;
            case ActionType.sendText:
                if (SendTextBox.Text.Length == 0) { ConfirmDialog.Info(this, "动作", "请输入要发送的文本。"); return; }
                _draft.Text = SendTextBox.Text;
                _draft.Mode = SendModeCombo.SelectedIndex switch
                {
                    1 => SendTextMode.type,
                    2 => SendTextMode.paste,
                    _ => (SendTextMode?)null,   // auto 存 null（JSON 省略，默认值）
                };
                _draft.AppendEnter = AppendEnterBox.IsChecked == true;
                break;
        }

        _draft.Icon = IconPicker.SelectedIcon ?? IconCatalog.Default;
        Result = _draft;
        DialogResult = true;
    }

    // —— 辅助 ——
    private static ActionDto Clone(ActionDto a) => new()
    {
        Id = a.Id, Name = a.Name, Icon = a.Icon, Type = a.Type,
        Target = a.Target, Args = a.Args, Workdir = a.Workdir, Path = a.Path, Url = a.Url,
        Cmd = a.Cmd, Terminal = a.Terminal, KeepWindow = a.KeepWindow, Command = a.Command,
        Text = a.Text, Mode = a.Mode, AppendEnter = a.AppendEnter,
        CompositeOnFail = a.CompositeOnFail,
        Steps = a.Steps != null ? new List<StepDto>(a.Steps) : null,
    };

    private static int IndexOf(ActionType t)
    {
        var list = new[] { ActionType.launchApp, ActionType.openFile, ActionType.openFolder, ActionType.openUrl,
                          ActionType.runCommand, ActionType.sendText, ActionType.@internal, ActionType.composite };
        for (int i = 0; i < list.Length; i++) if (list[i] == t) return i;
        return 0;
    }

    private static int IndexOfTerminal(string t)
    {
        var list = new[] { "direct", "pwsh7", "pwsh5", "cmd", "gitbash", "wslbash", "wt" };
        for (int i = 0; i < list.Length; i++) if (list[i] == t) return i;
        return 0;
    }

    private int IndexOfCommand(string c)
    {
        for (int i = 0; i < CommandCombo.Items.Count; i++)
            if (((ComboItem)CommandCombo.Items[i]).Value == c) return i;
        return 0;
    }

    internal static string FormatCommand(string c) => c switch
    {
        "quickNote.toggle" => "速记（打开/关闭）",
        "quickSearch.run" => "快速搜索选中文本",
        "magnifier.toggle" => "放大镜（打开/关闭）",
        "windowPin.toggle" => "置顶当前窗口",
        "helpPanel.toggle" => "帮助面板",
        "settings.toggle" => "设置面板",
        _ => c,
    };

    private static string SafeDir(string path)
    {
        try { var d = System.IO.Path.GetDirectoryName(path?.Trim()); return string.IsNullOrEmpty(d) ? "" : d; }
        catch { return ""; }
    }

    private sealed record TypeItem(string Label, ActionType Type) { public override string ToString() => Label; }
    private sealed record ComboItem(string Label, string Value) { public override string ToString() => Label; }
}
