using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Features;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace LTools.Views;

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

    /// <summary>名称是否必填（组合步骤场景为 false，名称可留空）。</summary>
    private readonly bool _requireName;

    // —— sendKeys 序列编辑状态 ——
    private readonly ObservableCollection<KeyItemView> _keyItems = new();
    private bool _recording;
    private readonly List<string> _pendingStrokes = new();
    private readonly HashSet<Key> _downKeys = new();
    private DispatcherTimer? _idleTimer;

    public ActionEditorDialog(string title, ActionDto? existing, bool allowComposite = true, bool requireName = true)
    {
        InitializeComponent();
        _draft = existing != null ? Clone(existing) : new ActionDto { Id = "", Name = "", Icon = IconCatalog.Default };
        _requireName = requireName;
        if (!string.IsNullOrEmpty(title)) Title = title;

        _fieldsByType[ActionType.launchApp] = F_LaunchApp;
        _fieldsByType[ActionType.openFile] = F_OpenFile;
        _fieldsByType[ActionType.openFolder] = F_OpenFolder;
        _fieldsByType[ActionType.openUrl] = F_OpenUrl;
        _fieldsByType[ActionType.runCommand] = F_RunCommand;
        _fieldsByType[ActionType.sendText] = F_SendText;
        _fieldsByType[ActionType.sendKeys] = F_SendKeys;
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
            new("模拟按键", ActionType.sendKeys),
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

        // sendKeys 序列编辑：列表绑定 + 空闲成条计时器
        KeysList.ItemsSource = _keyItems;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _idleTimer.Tick += (_, _) => { _idleTimer.Stop(); FlushPending(); };

        LoadDraft();
        _ready = true;
        Loaded += (_, _) => NameBox.Focus();
        Closing += (_, _) => { if (_recording) StopRecording(); };
    }

    /// <summary>返回值；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>弹出模态编辑器。返回编辑后的动作；取消返回 null。</summary>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing)
        => Show(owner, title, existing, allowComposite: true);

    /// <summary>同 <see cref="Show(Window, string, ActionDto?)"/>，可禁用「组合动作」类型项（编辑组合步骤时用）。</summary>
    /// <param name="requireName">名称是否必填；false = 组合步骤场景（名称可留空，步骤行显示类型名）。</param>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing, bool allowComposite, bool requireName = true)
    {
        var dlg = new ActionEditorDialog(title, existing, allowComposite, requireName) { Owner = owner, Topmost = owner.Topmost };
        dlg.ShowDialog();
        return dlg.Result;
    }

    // —— 载入草稿到控件 ——
    private void LoadDraft()
    {
        NameBox.Text = _draft.Name;
        NameHint.Visibility = _requireName ? Visibility.Collapsed : Visibility.Visible;

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

        // sendKeys Items 载入序列列表
        _keyItems.Clear();
        if (_draft.Items != null)
            foreach (var m in _draft.Items) _keyItems.Add(WrapKeyItem(m));
        RefreshKeyNumbers();

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
        if (_requireName && name.Length == 0)
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
        _draft.Items = null;

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
            case ActionType.sendKeys:
                if (_keyItems.Count == 0) { ConfirmDialog.Info(this, "动作", "请至少添加一条输入条目。"); return; }
                if (_recording) StopRecording();
                var items = new List<KeyItem>();
                foreach (var v in _keyItems) items.Add(v.Model);
                _draft.Items = items;
                break;
        }

        _draft.Icon = IconPicker.SelectedIcon ?? IconCatalog.Default;
        Result = _draft;
        DialogResult = true;
    }

    private static KeyItemView WrapKeyItem(KeyItem m) => new(m);

    // —— sendKeys 序列编辑：录制 / 手动添加 / 列表操作 ——

    private static string DescribeKeyItem(KeyItem m) => m.Kind switch
    {
        KeyItemKind.chord => "组合键  " + (m.Strokes != null && m.Strokes.Count > 0 ? string.Join(", ", m.Strokes) : "(空)"),
        KeyItemKind.text => "文本  " + BriefKeyText(m.Text),
        KeyItemKind.sleep => $"延时  {m.Ms}ms",
        _ => "",
    };

    private static string BriefKeyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var s = text!.Replace("\r", "\\r").Replace("\n", "\\n");
        return s.Length > 30 ? s[..30] + "…" : s;
    }

    /// <summary>刷新列表序号（ObservableCollection 已处理增删，这里只更新序号显示）。</summary>
    private void RefreshKeyNumbers()
    {
        for (int i = 0; i < _keyItems.Count; i++)
            _keyItems[i].Number = $"{i + 1}.";
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        _recording = true;
        _pendingStrokes.Clear();
        _downKeys.Clear();
        RecordBtn.Content = "■ 停止录制";
        RecordHint.Text = "正在录制…（空闲 800ms 成条，Esc 停止）";
        // 焦点收归窗口：录制监听挂在窗口 PreviewKeyDown 上，焦点须在对话框内
        Focus();
        Keyboard.Focus(this);
    }

    private void StopRecording()
    {
        _recording = false;
        _idleTimer?.Stop();
        FlushPending();
        _downKeys.Clear();
        RecordBtn.Content = "● 开始录制";
        RecordHint.Text = "";
    }

    /// <summary>把累积的 pending strokes 作为一个 chord 条目入库（空闲成条 / 停止时调用）。</summary>
    private void FlushPending()
    {
        if (_pendingStrokes.Count == 0) return;
        _keyItems.Add(new KeyItemView(new KeyItem
        {
            Kind = KeyItemKind.chord,
            Strokes = new List<string>(_pendingStrokes),
        }));
        _pendingStrokes.Clear();
        RefreshKeyNumbers();
    }

    private static bool IsModifierKey(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    // 录制监听：挂在窗口（模态对话框有焦点），不开新的低级钩子（§3.2.1）
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;   // 录制中吞掉所有键，不触发对话框按钮 / 焦点切换

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape) { StopRecording(); return; }

        // 去重：长按重复触发的 KeyDown 只算第一次
        if (!_downKeys.Add(key)) return;

        if (IsModifierKey(key)) return;   // 修饰键不进条目

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _pendingStrokes.Add(KeyStroke.Format(Keyboard.Modifiers, vk));
        _idleTimer?.Stop();
        _idleTimer?.Start();   // 空闲 800ms → 成条
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _downKeys.Remove(key);
    }

    // —— 手动添加 ——

    private void AddChord_Click(object sender, RoutedEventArgs e)
    {
        // 可视化选键对话框（§12 交付后 UX 优化）：修饰键复选 + 特殊键表格 + 实时校验，
        // 替代原裸文本框；确定后入库单条 stroke（数据层不变）。
        var dlg = new ChordPickerDialog { Owner = this, Topmost = this.Topmost };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Stroke)) return;

        _keyItems.Add(new KeyItemView(new KeyItem
        {
            Kind = KeyItemKind.chord,
            Strokes = new List<string> { dlg.Stroke },
        }));
        RefreshKeyNumbers();
    }

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var (ok, val) = InputDialog.Show(this, "添加文本",
            "输入要发送的文本（自动 Unicode 注入，中英文/emoji 均可，不碰剪贴板）", "");
        if (!ok || val.Length == 0) return;
        _keyItems.Add(new KeyItemView(new KeyItem { Kind = KeyItemKind.text, Text = val }));
        RefreshKeyNumbers();
    }

    private void AddSleep_Click(object sender, RoutedEventArgs e)
    {
        var (ok, val) = InputDialog.Show(this, "添加延时", "延时毫秒数（如 100）", "100");
        if (!ok || !int.TryParse(val.Trim(), out var ms) || ms <= 0)
        {
            if (ok) ConfirmDialog.Info(this, "动作", "请输入大于 0 的毫秒数。");
            return;
        }
        _keyItems.Add(new KeyItemView(new KeyItem { Kind = KeyItemKind.sleep, Ms = ms }));
        RefreshKeyNumbers();
    }

    // —— 列表行内操作（编辑 / 删除）——

    private void KeyDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is KeyItemView v)
        {
            _keyItems.Remove(v);
            RefreshKeyNumbers();
        }
    }

    private void KeyEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is KeyItemView v) EditKeyItem(v);
    }

    private void KeysList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource)) return;   // 别把按钮的双击当成编辑行
        if (KeysList.SelectedItem is KeyItemView v) EditKeyItem(v);
    }

    /// <summary>双击 / ✎ 编辑一条输入条目：chord→选键或文本编辑，text→文本，sleep→延时。</summary>
    private void EditKeyItem(KeyItemView v)
    {
        var m = v.Model;
        switch (m.Kind)
        {
            case KeyItemKind.chord:
                var strokes = m.Strokes;
                // 多 stroke（录制产生的连续按键序列）→ 文本编辑（逗号分隔，逐条校验）；
                // 单 stroke → 可视化选键对话框（与「＋ 组合键」一致）
                if (strokes != null && strokes.Count > 1)
                {
                    var (ok, val) = InputDialog.Show(this, "编辑按键序列",
                        "按键以逗号分隔（如 ctrl+a, s, f5）", string.Join(", ", strokes));
                    if (!ok) return;
                    var segs = val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (segs.Length == 0) { ConfirmDialog.Info(this, "模拟按键", "请至少输入一个按键。"); return; }
                    for (int i = 0; i < segs.Length; i++)
                        if (!KeyStroke.TryParse(segs[i], out _, out var err))
                        { ConfirmDialog.Info(this, "模拟按键", $"第 {i + 1} 个按键无效：{err}"); return; }
                    m.Strokes = segs.ToList();
                }
                else
                {
                    string pre = strokes != null && strokes.Count == 1 ? strokes[0] : "";
                    var dlg = new ChordPickerDialog(pre) { Owner = this, Topmost = this.Topmost };
                    dlg.Title = "编辑组合键";
                    if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Stroke)) return;
                    m.Strokes = new List<string> { dlg.Stroke };
                }
                break;
            case KeyItemKind.text:
                var (okT, valT) = InputDialog.Show(this, "编辑文本",
                    "输入要发送的文本（自动 Unicode 注入，中英文/emoji 均可，不碰剪贴板）", m.Text ?? "");
                if (!okT || valT.Length == 0) return;
                m.Text = valT;
                break;
            case KeyItemKind.sleep:
                var (okS, valS) = InputDialog.Show(this, "编辑延时", "延时毫秒数（如 100）", m.Ms.ToString());
                if (!okS || !int.TryParse(valS.Trim(), out var ms) || ms <= 0)
                { if (okS) ConfirmDialog.Info(this, "模拟按键", "请输入大于 0 的毫秒数。"); return; }
                m.Ms = ms;
                break;
            default:
                return;
        }
        v.Desc = DescribeKeyItem(m);
    }

    // —— sendKeys 序列拖拽排序（把手拖动，幽灵跟随 + 插入线，与组合动作编辑器同款）——
    // 走窗口级鼠标跟踪而非 OLE DoDragDrop：拖动中只跟幽灵 + 插入指示线，释放时才重排
    // （与超级面板 §5.6 / 组合动作编辑器节奏一致）。
    private int? _keyGripIdx;                   // 把手按下时记录的源条目索引
    private Point _keyGripOrigin;               // 按下坐标（进入拖动态的阈值判定）
    private bool _keyGripDragging;              // 是否已进入拖动态
    private FrameworkElement? _keyGripSource;   // 把手元素（鼠标捕获载体）
    private ListBoxItem? _keyGripSrcContainer;  // 源行（半透明占位）

    private void Grip_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not KeyItemView v) return;
        int idx = _keyItems.IndexOf(v);
        if (idx < 0) return;
        KeysList.SelectedIndex = idx;
        _keyGripSource = fe;
        _keyGripIdx = idx;
        _keyGripOrigin = e.GetPosition(this);
        _keyGripDragging = false;
        fe.CaptureMouse();      // 捕获：拖出窗口仍能收到移动 / 释放
        e.Handled = true;       // 吞掉，别让 ListBox 抢走选中
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_keyGripIdx == null) return;
        KeyGripDragTick(e);
        if (_keyGripDragging) e.Handled = true;
    }

    private void Window_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_keyGripIdx is not int src || !_keyGripDragging) { EndKeyReorderDrag(); return; }

        int hover = KeyGripHoverRow(e);
        int insertAt = hover >= 0 ? KeyGripInsertAt(e, hover) : -1;
        EndKeyReorderDrag();
        KeysList.SelectedIndex = src;      // 先回落到源行（重排成功时会再改）
        if (insertAt >= 0) ApplyKeyReorder(src, insertAt);
        e.Handled = true;
    }

    /// <summary>位移超阈值 → 显示幽灵跟随光标；目标行高亮。不在这里重排（见 <see cref="ApplyKeyReorder"/>）。</summary>
    private void KeyGripDragTick(MouseEventArgs e)
    {
        if (_keyGripIdx is not int idx) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndKeyReorderDrag(); return; }

        var pos = e.GetPosition(this);
        if (!_keyGripDragging)
        {
            if (Math.Abs(pos.X - _keyGripOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(pos.Y - _keyGripOrigin.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            _keyGripDragging = true;
            ShowKeyGhost(idx);
        }

        UpdateKeyGhost(pos);
        PositionKeyInsertLine(e);
    }

    /// <summary>把插入线定位到光标所停留行的上/下边缘（上半=该行前，下半=该行后）。</summary>
    private void PositionKeyInsertLine(MouseEventArgs e)
    {
        int hover = KeyGripHoverRow(e);
        if (hover < 0) { InsertLine.Visibility = Visibility.Collapsed; return; }
        var c = KeysList.ItemContainerGenerator.ContainerFromIndex(hover) as FrameworkElement;
        if (c == null) { InsertLine.Visibility = Visibility.Collapsed; return; }
        bool after = KeyGripInsertAt(e, hover) > hover;
        var p = c.TransformToAncestor(GhostLayer).Transform(new Point(0, after ? c.ActualHeight : 0));
        Canvas.SetLeft(InsertLine, p.X);
        Canvas.SetTop(InsertLine, p.Y - 1.5);
        InsertLine.Width = c.ActualWidth > 0 ? c.ActualWidth : KeysList.ActualWidth;
        InsertLine.Visibility = Visibility.Visible;
    }

    private void ShowKeyGhost(int index)
    {
        var v = _keyItems[index];
        GhostBadge.Text = $"{index + 1}.";
        GhostName.Text = v.Desc;
        DragGhost.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = Cursors.SizeAll;
        // 源行半透明占位
        if (KeysList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem c)
        {
            c.Opacity = 0.35;
            _keyGripSrcContainer = c;
        }
    }

    private void UpdateKeyGhost(Point windowPos)
    {
        var rel = TranslatePoint(windowPos, GhostLayer);   // 窗口坐标 → Canvas 坐标
        double w = DragGhost.ActualWidth > 0 ? DragGhost.ActualWidth : DragGhost.MinWidth;
        double h = DragGhost.ActualHeight > 0 ? DragGhost.ActualHeight : 34;
        Canvas.SetLeft(DragGhost, rel.X - w / 2);
        Canvas.SetTop(DragGhost, rel.Y - h / 2);
    }

    private void EndKeyReorderDrag()
    {
        bool wasDragging = _keyGripDragging;
        _keyGripSource?.ReleaseMouseCapture();
        _keyGripSource = null;
        _keyGripIdx = null;
        _keyGripDragging = false;
        if (!wasDragging) return;      // 只是点了下把手（未拖动）：不碰光标与幽灵
        DragGhost.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = null;
        if (_keyGripSrcContainer != null) { _keyGripSrcContainer.Opacity = 1; _keyGripSrcContainer = null; }
        InsertLine.Visibility = Visibility.Collapsed;
    }

    /// <summary>拖动中的目标行：先按坐标判定是否落在列表视口内（列表外 → -1），
    /// 再找命中的行；命中列表内但没落在行上（项间空隙 / ItemsPanel）→ 末行。</summary>
    private int KeyGripHoverRow(MouseEventArgs e)
    {
        var p = e.GetPosition(KeysList);
        if (p.X < 0 || p.Y < 0 || p.X > KeysList.ActualWidth || p.Y > KeysList.ActualHeight)
            return -1;                       // 列表外

        if (KeysList.InputHitTest(p) is DependencyObject d)
        {
            var container = ItemsControl.ContainerFromElement(KeysList, d) as ListBoxItem;
            if (container != null)
            {
                int i = KeysList.ItemContainerGenerator.IndexFromContainer(container);
                if (i >= 0) return i;
            }
        }
        return _keyItems.Count - 1;          // 列表内但未命中行 → 末行（下半即追加到末尾）
    }

    /// <summary>命中行 → 插入位置（0.._keyItems.Count）：上半 = 该行之前，下半 = 该行之后。</summary>
    private int KeyGripInsertAt(MouseEventArgs e, int hover)
    {
        var container = KeysList.ItemContainerGenerator.ContainerFromIndex(hover) as ListBoxItem;
        if (container == null) return hover;
        return e.GetPosition(container).Y < container.ActualHeight / 2 ? hover : hover + 1;
    }

    /// <summary>释放：把源条目移到插入位置（<paramref name="insertAt"/> 基于移动前的列表）。</summary>
    private void ApplyKeyReorder(int src, int insertAt)
    {
        if (src < 0 || src >= _keyItems.Count) return;
        if (insertAt < 0 || insertAt > _keyItems.Count) return;
        int target = insertAt > src ? insertAt - 1 : insertAt;   // 移除源后索引前移
        if (target == src) return;
        var old = Views.Controls.DragFx.CaptureByIndex(KeysList);
        _keyItems.Move(src, target);
        RefreshKeyNumbers();
        Views.Controls.DragFx.AnimateReorderByIndex(KeysList, old,
            i => Views.Controls.DragFx.OldIndexOfMove(i, src, target));
        KeysList.SelectedIndex = Math.Clamp(target, 0, _keyItems.Count - 1);
    }

    // —— 辅助 ——

    /// <summary>原始命中的元素是否落在交互控件（TextBox/ComboBox/Button）内——避免双击按钮误触行编辑。</summary>
    private static bool IsInteractive(object? src)
    {
        var d = src as DependencyObject;
        while (d != null)
        {
            if (d is TextBox || d is ComboBox || d is Button) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null;
        }
        return false;
    }

    private static ActionDto Clone(ActionDto a) => new()
    {
        Id = a.Id, Name = a.Name, Icon = a.Icon, Type = a.Type,
        Target = a.Target, Args = a.Args, Workdir = a.Workdir, Path = a.Path, Url = a.Url,
        Cmd = a.Cmd, Terminal = a.Terminal, KeepWindow = a.KeepWindow, Command = a.Command,
        Text = a.Text, Mode = a.Mode, AppendEnter = a.AppendEnter,
        Items = a.Items?.ConvertAll(i => i.Clone()),
        CompositeOnFail = a.CompositeOnFail,
        Steps = a.Steps != null ? new List<StepDto>(a.Steps) : null,
    };

    private static int IndexOf(ActionType t)
    {
        var list = new[] { ActionType.launchApp, ActionType.openFile, ActionType.openFolder, ActionType.openUrl,
                          ActionType.runCommand, ActionType.sendText, ActionType.sendKeys,
                          ActionType.@internal, ActionType.composite };
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

    /// <summary>序列列表的行视图（序号 + 描述，供 ListBox 绑定）。</summary>
    private sealed class KeyItemView : INotifyPropertyChanged
    {
        public KeyItem Model { get; }

        private string _number = "";
        public string Number { get => _number; set { if (_number != value) { _number = value; OnPn(); } } }

        private string _desc;
        public string Desc { get => _desc; set { if (_desc != value) { _desc = value; OnPn(); } } }

        public KeyItemView(KeyItem m)
        {
            Model = m;
            _desc = DescribeKeyItem(m);
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPn([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
