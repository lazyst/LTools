using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features;
using LTools.Native;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using OpenFolderDialog = Microsoft.Win32.OpenFolderDialog;

namespace LTools.Views;

/// <summary>
/// 普通动作编辑对话框（计划 §10.1）：类型下拉（含「组合动作 ▸」跳转项）+ 类型特定字段 + 名称 + 图标。
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

    // —— 宏录制状态 ——
    private bool _macroMinimized;    // 因宏录制而最小化了本窗口（停止后恢复）
    private bool _macroRecording;    // 本次录制由本窗口发起（保存后回填 MacroId）
    private bool _macroSessionActive; // 当前类型为「键鼠宏」，录制热键在此窗口内生效
    private string? _macroId;        // 当前动作绑定的宏 Id（null = 尚未录制）
    private bool _macroTopmostPinned; // 恢复时抢不到前台，临时置顶保证用户看得见
    private bool _macroTopmostBefore; // 临时置顶前的 Topmost 值

    public ActionEditorDialog(string title, ActionDto? existing, bool allowComposite = true, bool requireName = true, bool allowMacro = true)
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
        _fieldsByType[ActionType.macro] = F_Macro;
        _fieldsByType[ActionType.@internal] = F_Internal;

        // 类型下拉：allowComposite=false（编辑组合步骤时）不含「组合动作」项，从源头禁嵌套；
        // 含该项时选中即跳转到组合动作编辑器（§10.1）。
        var types = new List<TypeItem>
        {
            new("启动程序", ActionType.launchApp),
            new("打开文件", ActionType.openFile),
            new("打开文件夹", ActionType.openFolder),
            new("打开网址", ActionType.openUrl),
            new("运行命令", ActionType.runCommand),
            new("发送文本", ActionType.sendText),
            new("模拟按键", ActionType.sendKeys),
            // 宏按 Id 引用事件文件：组合步骤已支持宏步骤（引用 macros/ 下的文件，备份一起走）。
            new("内部动作", ActionType.@internal),
        };
        if (allowMacro || _draft.Type == ActionType.macro)
            types.Insert(types.Count - 1, new("键鼠宏", ActionType.macro));
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
        // 真正拿到前台后再撤销「临时置顶」（恢复录制窗口时的兜底），避免置顶瞬间的伪激活把它撤掉；
        // 用户点窗口（或键盘激活）即视为已回前台，立刻撤销。
        Activated += (_, _) => ReleaseTopmostPin();
        PreviewMouseDown += (_, _) => ReleaseTopmostPin(force: true);
        Closing += (_, _) => { if (_recording) StopRecording(); };

        // 宏录制：录完自动回填 MacroId；录制期间最小化本窗口，避免把窗口操作录进宏。
        // 录制热键只在「键鼠宏」类型面板打开期间生效（不是全局热键）。
        MacroRecorder.RecordingStarted += MacroRecorder_RecordingStarted;
        MacroRecorder.RecordingStopped += MacroRecorder_RecordingStopped;
        MacroRecorder.MacroSaved += MacroRecorder_MacroSaved;
        Closed += (_, _) =>
        {
            // 关闭时仍在录制（例如被外部误关）：留痕，方便从日志看出「窗口为什么消失」。
            if (_macroMinimized || MacroRecorder.IsEditorRecording)
                MacroRecorder.Trace($"editor:closed minimized={_macroMinimized} " +
                                    $"rec={MacroRecorder.IsRecording} editorRec={MacroRecorder.IsEditorRecording}");
            MacroRecorder.RecordingStarted -= MacroRecorder_RecordingStarted;
            MacroRecorder.RecordingStopped -= MacroRecorder_RecordingStopped;
            MacroRecorder.MacroSaved -= MacroRecorder_MacroSaved;
            if (_macroSessionActive)
            {
                _macroSessionActive = false;
                MacroRecorder.ExitEditorSession();
            }
        };
    }

    /// <summary>返回值；取消时为 null。</summary>
    public ActionDto? Result { get; private set; }

    /// <summary>弹出模态编辑器。返回编辑后的动作；取消返回 null。</summary>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing)
        => Show(owner, title, existing, allowComposite: true);

    /// <summary>同 <see cref="Show(Window, string, ActionDto?)"/>，可禁用「组合动作」类型项（编辑组合步骤时用）。</summary>
    /// <param name="requireName">名称是否必填；false = 组合步骤场景（名称可留空，步骤行显示类型名）。</param>
    /// <param name="allowMacro">是否允许选「键鼠宏」；默认提供，特殊场景可关闭。</param>
    public static ActionDto? Show(Window owner, string title, ActionDto? existing, bool allowComposite, bool requireName = true, bool allowMacro = true)
    {
        var dlg = new ActionEditorDialog(title, existing, allowComposite, requireName, allowMacro) { Owner = owner, Topmost = owner.Topmost };
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

        TargetBox.Text = AppPaths.ToRelativeIfUnderDataRoot(_draft.Target ?? "");
        ArgsBox.Text = _draft.Args ?? "";
        LaunchWorkdirBox.Text = AppPaths.ToRelativeIfUnderDataRoot(_draft.Workdir ?? "");
        FileBox.Text = AppPaths.ToRelativeIfUnderDataRoot(_draft.Path ?? "");
        FolderBox.Text = AppPaths.ToRelativeIfUnderDataRoot(_draft.Path ?? "");
        UrlBox.Text = _draft.Url ?? "";
        CmdBox.Text = _draft.Cmd ?? "";
        CmdWorkdirBox.Text = AppPaths.ToRelativeIfUnderDataRoot(_draft.Workdir ?? "");
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
        _macroId = _draft.MacroId;
        RefreshMacroPanel();

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
        if (t == ActionType.macro) RefreshMacroPanel();
        SyncMacroSession(t == ActionType.macro);
    }

    /// <summary>只在「键鼠宏」类型下启用录制热键会话；切到其它类型立即失效。</summary>
    private void SyncMacroSession(bool active)
    {
        if (active == _macroSessionActive) return;
        _macroSessionActive = active;
        if (active) MacroRecorder.EnterEditorSession();
        else MacroRecorder.ExitEditorSession();
    }

    // —— macro（键鼠宏）——
    /// <summary>刷新宏面板：未绑定显示录制入口，已绑定显示宏信息 + 重新录制。</summary>
    private void RefreshMacroPanel()
    {
        MacroRecordHint.Text = $"点击后本窗口自动最小化并立即开录，按 {MacroSettings.RecordHotkeyDisplay} 停止并自动保存";

        if (string.IsNullOrEmpty(_macroId))
        {
            MacroRecordBtn.Content = "● 录制新宏";
            MacroInfoText.Text = "一个键鼠宏就是一个动作：点上方按钮当场录制，停止后自动保存并绑定到本动作。";
            MacroRecorder.UpdateEditorTarget(null, null);
            return;
        }

        var meta = MacroStore.List().FirstOrDefault(m => m.Id == _macroId);
        if (meta == null)
        {
            MacroRecordBtn.Content = "● 重新录制（覆盖）";
            MacroInfoText.Text = $"当前宏文件缺失（{_macroId}）。重新录制会用同一 Id 覆盖它。";
            MacroRecorder.UpdateEditorTarget(_macroId, null);
            return;
        }

        MacroRecordBtn.Content = "● 重新录制（覆盖此宏）";
        MacroInfoText.Text = $"当前宏：{meta.Name} · {meta.DurationText} · {meta.EventCount:N0} 个事件";
        MacroRecorder.UpdateEditorTarget(_macroId, meta.Name);
    }

    /// <summary>「录制新宏 / 重新录制」：最小化本窗口后立即开录，按录制热键停止时自动保存并绑定。</summary>
    private void MacroRecord_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) StopRecording();   // 先收尾「模拟按键」的窗口内录制，避免两套录制状态叠加

        if (MacroRecorder.IsRecording)
        {
            if (MacroRecorder.IsEditorRecording)
            {
                MacroRecorder.Stop();   // 本窗口发起的录制：再点一次＝停止
            }
            else
            {
                // 录制来自托盘 / 设置页：不要在这里悄悄停掉别人的录制，给个明确提示。
                MacroHint.Text = $"已有录制在进行中（{MacroRecorder.EventCount} 个事件）。"
                                 + $"按 {MacroSettings.RecordHotkeyDisplay} 或托盘「停止录制宏」结束后再录新的。";
            }
            return;
        }
        if (!MacroSettings.Enabled)
        {
            ConfirmDialog.Info(this, "键鼠宏", "宏功能已关闭：请到 设置 →「键鼠录制」勾选「启用宏热键与回放」。");
            return;
        }
        if (MacroPlayer.IsPlaying)
        {
            ConfirmDialog.Info(this, "键鼠宏", "宏正在回放，请先停止回放再录制。");
            return;
        }

        // 重新录制时沿用原宏名（覆盖事件、不改名）；新建则让 MacroRecorder 用时间戳名。
        string? name = null;
        if (!string.IsNullOrEmpty(_macroId))
            name = MacroStore.List().FirstOrDefault(m => m.Id == _macroId)?.Name;

        // 注意：不能 Hide()——对 ShowDialog 打开的模态窗口调用 Hide 会结束模态循环、让编辑器提前返回。
        // 最小化既能腾开屏幕（按键不会落进窗口），又保持模态与窗口状态。按钮这一下已处理完，不会漏进宏。
        _macroRecording = true;
        _macroMinimized = true;
        WindowState = WindowState.Minimized;
        MacroRecorder.Trace($"editor:minimize state={WindowState} vis={IsVisible}");
        MacroRecorder.StartInto(_macroId, name);
        if (!MacroRecorder.IsRecording)
        {
            // 被其他条件拒绝（理论上不会）：把窗口恢复出来，避免卡在最小化状态。
            _macroRecording = false;
            RestoreAfterMacroRecording();
        }
    }

    private void MacroRecorder_RecordingStarted()
    {
        // 只有本编辑器（宏动作面板）发起的录制才介入；托盘 / 设置页的录制不打断当前编辑。
        if (!MacroRecorder.IsEditorRecording) return;

        _macroRecording = true;
        MacroRecordBtn.Content = "■ 录制中…（" + MacroSettings.RecordHotkeyDisplay + " 停止）";

        // 本方法可能在钩子回调里被同步调用：绝不能在这里最小化窗口——窗口操作会占住
        // 钩子回调，超时后系统会摘掉钩子，录制热键随之失灵（表现为「按了停不下来」）。
        // 收窗口统一推到消息泵之后再执行。
        if (_macroMinimized || WindowState == WindowState.Minimized) return;
        _macroMinimized = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            // 极短录制 / 刚开录就按了停止：已经不在录制就别再最小化。
            if (!_macroRecording || !MacroRecorder.IsRecording) return;
            WindowState = WindowState.Minimized;
        });
    }

    private void MacroRecorder_RecordingStopped()
    {
        MacroRecorder.Trace($"editor:stopped-ui minimized={_macroMinimized} state={WindowState} vis={IsVisible}");
        // 停止可能发生在钩子回调里：恢复窗口放到消息泵之后。
        Dispatcher.BeginInvoke(() =>
        {
            // 兜底：不只看 _macroMinimized，只要窗口确实还是最小化 / 不可见就恢复，
            // 避免标志位某种时序下不同步，把编辑器永久留在最小化状态。
            if (!_macroMinimized && WindowState != WindowState.Minimized && IsVisible)
                return;
            RestoreAfterMacroRecording();

            // 兜底：个别情况下前台锁 / 窗口状态同步会让第一次恢复不生效，
            // 空闲时若仍是最小化 / 不可见，再恢复一次。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                if (WindowState == WindowState.Minimized || !IsVisible)
                    RestoreAfterMacroRecording();
            });

            if (_macroRecording && MacroRecorder.EventCount == 0)
            {
                _macroRecording = false;   // 没内容就不会有 MacroSaved 事件
                RefreshMacroPanel();
                MacroHint.Text = "没有录制到内容（未保存）。点按钮重试。";
                return;
            }
            RefreshMacroPanel();
        });
    }

    private void RestoreAfterMacroRecording()
    {
        _macroMinimized = false;
        try
        {
            if (!IsVisible) Show();

            // 录制期间用户切到了别的应用，Activate() / SetForegroundWindow 常被 Windows
            // 前台锁拦住；本窗口是 ShowDialog 打开且 ShowInTaskbar=False，恢复失败用户就
            // 再也找不回来（表现为「录完窗口不回来，没法完成新建动作」）。
            // 因此：Win32 恢复 → 同步 WPF 状态 → AttachThreadInput 抢前台；
            // 仍抢不到就临时置顶（一定看得见），等用户点进来 / 真拿到前台再撤掉置顶。
            var hwnd = new WindowInteropHelper(this).Handle;
            bool shown = false;
            if (hwnd != IntPtr.Zero)
                shown = Win32.ShowWindowAsync(hwnd, Win32.SwRestore);

            WindowState = WindowState.Normal;

            if (hwnd != IntPtr.Zero)
            {
                Win32.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    Win32.SwpNomove | Win32.SwpNosize | Win32.SwpShowwindow);
                BringToForeground(hwnd);
            }
            else
            {
                Activate();
            }

            bool foreground = hwnd != IntPtr.Zero && Win32.GetForegroundWindow() == hwnd;
            if (!foreground && hwnd != IntPtr.Zero)
            {
                PinTopmostForVisibility();
                // WPF 的 Topmost 有时不会立刻改 Z 序（后台进程 + 前台锁），显式抬到最上层。
                Win32.ShowWindowAsync(hwnd, Win32.SwRestore);
                Win32.SetWindowPos(hwnd, Win32.HwndTopmost, 0, 0, 0, 0,
                    Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate | Win32.SwpShowwindow);
                BringToForeground(hwnd);
                foreground = Win32.GetForegroundWindow() == hwnd;
            }

            Focus();
            MacroRecordBtn.Focus();

            MacroRecorder.Trace($"editor:restored shown={shown} state={WindowState} vis={IsVisible} " +
                                $"active={IsActive} fg={Win32.GetForegroundWindow()} hwnd={hwnd} " +
                                $"foreground={foreground} pinned={_macroTopmostPinned}");
        }
        catch (Exception ex)
        {
            // 窗口正在关闭等极端情况：最少保证不把编辑器留在最小化状态。
            CrashLog.Write("ActionEditorDialog.RestoreAfterRecording", ex);
        }
    }

    /// <summary>
    /// 把窗口抬到前台。前台锁会拒绝后台进程的 <c>SetForegroundWindow</c>，
    /// 先与当前前台窗口共享输入队列（<c>AttachThreadInput</c>）即可解除限制。
    /// </summary>
    private static void BringToForeground(IntPtr hwnd)
    {
        IntPtr fg = Win32.GetForegroundWindow();
        uint fgThread = fg == IntPtr.Zero ? 0 : Win32.GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint myThread = Win32.GetCurrentThreadId();
        bool attached = fgThread != 0 && fgThread != myThread
                        && Win32.AttachThreadInput(myThread, fgThread, true);
        try
        {
            Win32.BringWindowToTop(hwnd);
            Win32.SetForegroundWindow(hwnd);
            Win32.SetActiveWindow(hwnd);
        }
        finally
        {
            if (attached) Win32.AttachThreadInput(myThread, fgThread, false);
        }
    }

    /// <summary>临时置顶：抢不到前台时至少保证窗口「弹回来」能被看见。</summary>
    private void PinTopmostForVisibility()
    {
        if (_macroTopmostPinned) return;
        _macroTopmostPinned = true;
        _macroTopmostBefore = Topmost;
        Topmost = true;
    }

    /// <summary>窗口真正成为前台窗口后撤销临时置顶。</summary>
    private void ReleaseTopmostPin(bool force = false)
    {
        if (!_macroTopmostPinned) return;
        var hwnd = new WindowInteropHelper(this).Handle;
        // force = 用户已经点了本窗口：点击本身即证明它已回到前台，不必再等前台窗口同步。
        if (!force && (hwnd == IntPtr.Zero || Win32.GetForegroundWindow() != hwnd)) return;
        _macroTopmostPinned = false;
        Topmost = _macroTopmostBefore;
        MacroRecorder.Trace("editor:topmost-unpinned");
    }

    private void MacroRecorder_MacroSaved(string id, bool autoCreate)
    {
        // 只有本窗口发起的录制才回填；热键录的宏由 App 自动建动作，不劫持当前编辑。
        if (!_macroRecording) return;
        _macroRecording = false;

        // 保存完成在后台线程；回 UI 线程刷新面板并绑定 MacroId。
        Dispatcher.BeginInvoke(() =>
        {
            // 兜底：保存事件到了但窗口还最小化（停止事件的恢复没生效），这里再拉回来一次。
            if (WindowState == WindowState.Minimized || !IsVisible)
                RestoreAfterMacroRecording();

            _macroId = id;
            var meta = MacroStore.List().FirstOrDefault(m => m.Id == id);
            if (NameBox.Text.Trim().Length == 0 && meta != null) NameBox.Text = meta.Name;
            RefreshMacroPanel();
            MacroHint.Text = meta == null ? "宏已保存并绑定。" : $"宏已保存并绑定：{meta.Name}";
        });
    }

    // —— 浏览 ——
    private void BrowseTarget_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*", Title = "选择程序" };
        if (!string.IsNullOrEmpty(TargetBox.Text)) ofd.InitialDirectory = SafeDir(TargetBox.Text);
        if (ofd.ShowDialog(this) == true) TargetBox.Text = AppPaths.ToRelativeIfUnderDataRoot(ofd.FileName);
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog { Filter = "所有文件 (*.*)|*.*", Title = "选择文件" };
        if (!string.IsNullOrEmpty(FileBox.Text)) ofd.InitialDirectory = SafeDir(FileBox.Text);
        if (ofd.ShowDialog(this) == true) FileBox.Text = AppPaths.ToRelativeIfUnderDataRoot(ofd.FileName);
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "选择文件夹" };
        if (!string.IsNullOrEmpty(FolderBox.Text)) d.InitialDirectory = SafeDir(FolderBox.Text);
        if (d.ShowDialog(this) == true) FolderBox.Text = AppPaths.ToRelativeIfUnderDataRoot(d.FolderName);
    }

    private void BrowseLaunchWorkdir_Click(object sender, RoutedEventArgs e) => BrowseWorkdir(LaunchWorkdirBox);

    private void BrowseCmdWorkdir_Click(object sender, RoutedEventArgs e) => BrowseWorkdir(CmdWorkdirBox);

    /// <summary>在资源管理器中打开资源目录（脚本建议放这里，可用相对路径）。</summary>
    private void OpenResourceDir_Click(object sender, RoutedEventArgs e) => ResourceDir.OpenInExplorer();

    /// <summary>选工作目录：调文件资源管理器（.NET 8 原生 <see cref="OpenFolderDialog"/>）并回填指定框。
    /// 选到数据根下的目录时回填相对路径（备份换机后仍可用）。</summary>
    private void BrowseWorkdir(TextBox box)
    {
        var d = new OpenFolderDialog { Title = "选择工作目录" };
        if (!string.IsNullOrEmpty(box.Text)) d.InitialDirectory = SafeDir(box.Text);
        if (d.ShowDialog(this) == true) box.Text = AppPaths.ToRelativeIfUnderDataRoot(d.FolderName);
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
        _draft.Items = null; _draft.MacroId = null;

        switch (type)
        {
            case ActionType.launchApp:
                if (string.IsNullOrWhiteSpace(TargetBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写程序路径。"); return; }
                _draft.Target = AppPaths.ToRelativeIfUnderDataRoot(TargetBox.Text.Trim());
                _draft.Args = ArgsBox.Text.Trim();
                _draft.Workdir = AppPaths.ToRelativeIfUnderDataRoot(LaunchWorkdirBox.Text.Trim());
                break;
            case ActionType.openFile:
                if (string.IsNullOrWhiteSpace(FileBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写文件路径。"); return; }
                _draft.Path = AppPaths.ToRelativeIfUnderDataRoot(FileBox.Text.Trim());
                break;
            case ActionType.openFolder:
                if (string.IsNullOrWhiteSpace(FolderBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写文件夹路径。"); return; }
                _draft.Path = AppPaths.ToRelativeIfUnderDataRoot(FolderBox.Text.Trim());
                break;
            case ActionType.openUrl:
                if (string.IsNullOrWhiteSpace(UrlBox.Text)) { ConfirmDialog.Info(this, "动作", "请填写网址。"); return; }
                _draft.Url = UrlBox.Text.Trim();
                break;
            case ActionType.runCommand:
                _draft.Cmd = CmdBox.Text.Trim();
                _draft.Terminal = TerminalCombo.SelectedItem is ComboItem ti ? ti.Value : "direct";
                _draft.KeepWindow = KeepWindowBox.IsChecked == true;
                _draft.Workdir = AppPaths.ToRelativeIfUnderDataRoot(CmdWorkdirBox.Text.Trim());
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
            case ActionType.macro:
                if (string.IsNullOrEmpty(_macroId))
                {
                    ConfirmDialog.Info(this, "动作", "请先点「● 录制新宏」完成一次录制。");
                    return;
                }
                _draft.MacroId = _macroId;
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
        MacroId = a.MacroId,
        CompositeOnFail = a.CompositeOnFail,
        Steps = a.Steps != null ? new List<StepDto>(a.Steps) : null,
    };

    private int IndexOf(ActionType t)
    {
        for (int i = 0; i < TypeCombo.Items.Count; i++)
            if (TypeCombo.Items[i] is TypeItem item && item.Type == t) return i;
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
        "macro.recordToggle" => "开始 / 停止录制宏",
        "macro.manage" => "打开宏管理页",
        _ => c,
    };

    private static string SafeDir(string path)
    {
        try
        {
            string p = path?.Trim() ?? "";
            if (string.IsNullOrEmpty(p)) return "";
            // 相对路径（如 resources\foo.bat / resources）按数据根解析成绝对路径，再取目录
            if (!System.IO.Path.IsPathRooted(p))
                p = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppPaths.DataRoot, p));
            var d = System.IO.Path.GetDirectoryName(p);
            return string.IsNullOrEmpty(d) ? "" : d;
        }
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
