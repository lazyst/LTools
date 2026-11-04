using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 全局键鼠录制器。录制期间 LTools 进入「透明模式」：钩子只观察、放行，
/// 跳过 CapsLock 增强 / 超级面板 / 底部滚轮音量 / 资源管理器重命名等所有自有行为，
/// 保证「录到的输入 = 目标应用实际收到的输入」。
/// </summary>
/// <remarks>
/// 线程模型：钩子回调在主 UI 线程，录制状态与事件列表也只在 UI 线程读写
/// （起始 / 停止由动作编辑器、托盘或设置页触发，同样在 UI 线程），故热路径无需加锁。
/// 停止时在 UI 线程做一次不可变快照，落盘交给后台线程。
/// 录制热键只在「键鼠宏」动作编辑窗口打开期间生效，不是全局热键。
/// </remarks>
internal static class MacroRecorder
{
    private static readonly List<MacroEvent> _events = new(8192);
    private static readonly Dictionary<ushort, uint> _downKeys = new();   // vk → 扩展键标志
    private static readonly HashSet<int> _downButtons = new();
    private static readonly Stopwatch _clock = new();

    private static uint _startRaw;
    private static int _lastEventMs;
    private static int _lastMoveMs = -1000;
    private static int _lastX;
    private static int _lastY;
    private static bool _haveMove;
    private static bool _recording;
    private static bool _capsSwallowedDown;   // 录制中吞掉的 CapsLock 仍处于按下状态

    // 录制目标：非空表示覆盖同一宏（动作编辑器「重新录制」）；null 表示新建宏。
    private static string? _recordTargetId;
    private static string? _recordName;          // 覆盖录制时保留原名称；null = 用默认时间戳名
    private static bool _suppressAutoAction;    // 动作编辑器内录制：由编辑器自己绑定，不自动建动作
    private static bool _editorInitiated;       // 本次录制由宏动作编辑器发起（按钮或热键）

    // 宏动作编辑窗口会话：热键只在这个窗口打开期间生效（不再是全局热键）。
    private static int _editorSessions;
    private static string? _editorTargetId;
    private static string? _editorTargetName;

    // —— 录制状态事件（供动作编辑器等 UI 同步）——
    // 均在触发录制 / 停止的线程（通常是 UI 线程）同步触发；保存完成事件来自后台线程。
    public static event Action? RecordingStarted;
    public static event Action? RecordingStopped;

    /// <summary>宏保存完成（后台线程触发）：<c>id</c>、是否应自动在动作池创建同名宏动作。</summary>
    public static event Action<string, bool>? MacroSaved;

    // 录制开始时的虚拟桌面矩形：回放换分辨率 / 换显示器布局时按比例缩放鼠标坐标。
    private static int _deskLeft, _deskTop, _deskWidth, _deskHeight;

    // 热键按下锁存：避免键盘自动重复反复触发；keyup 时即使修饰键已先松开也要吞掉。
    private static bool _recordHotkeyDown;

    public static bool IsRecording => _recording;

    /// <summary>本次录制是否由宏动作编辑器发起（按钮或限定热键）。</summary>
    public static bool IsEditorRecording => _editorInitiated;

    /// <summary>录制热键是否生效（仅宏动作编辑窗口打开期间）。</summary>
    public static bool HotkeyActive => _editorSessions > 0;

    /// <summary>进入宏动作编辑窗口会话：之后录制热键才生效。</summary>
    public static void EnterEditorSession() { _editorSessions++; _recordHotkeyDown = false; }

    /// <summary>编辑器切换 / 保存宏后，更新热键录制要绑定到的目标。</summary>
    public static void UpdateEditorTarget(string? macroId, string? name)
    {
        _editorTargetId = string.IsNullOrWhiteSpace(macroId) ? null : macroId;
        _editorTargetName = string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>退出宏动作编辑窗口会话；最后一个窗口关闭后热键立即失效。</summary>
    public static void ExitEditorSession()
    {
        if (_editorSessions > 0) _editorSessions--;
        if (_editorSessions > 0) return;

        _editorTargetId = null;
        _editorTargetName = null;
        // 会话结束时不吞键：清掉热键锁存，避免下次进窗口后热键被上一次的残留状态卡住。
        _recordHotkeyDown = false;

        // 会话结束 = 窗口内热键失效。编辑器发起的录制若还在跑，就先收尾保存，
        // 否则录制会一直继续且没有热键可停（窗口已关、指示器还挂着）。
        // 这里必须 force：刚开录就被关窗时不能被防抖挡住而留下「无界面的录制」。
        if (_recording && _editorInitiated) Stop(force: true);
    }

    /// <summary>键盘钩子是否需要接管 CapsLock：录制中，或录制的 CapsLock 仍按住（补吞 keyup）。</summary>
    public static bool ShouldHandleCapsLock => _recording || _capsSwallowedDown;

    /// <summary>录制期间吞掉 CapsLock；返回 false 表示该事件应原样放行。</summary>
    public static bool HandleCapsLock(bool isDown)
    {
        if (isDown)
        {
            _capsSwallowedDown = true;
            return true;
        }
        if (_capsSwallowedDown)
        {
            _capsSwallowedDown = false;
            return true;
        }
        return false;   // 录制开始前就按下的 CapsLock：它的 keyup 原样放行
    }

    /// <summary>当前录制的事件数（供指示器显示）。</summary>
    public static int EventCount => _events.Count;

    /// <summary>录制已进行的毫秒数（供指示器显示）。</summary>
    public static int ElapsedMs => _recording ? (int)_clock.ElapsedMilliseconds : _lastEventMs;

    /// <summary>切换录制（托盘 / 内部动作入口）。</summary>
    public static void Toggle() => Toggle(0);

    /// <summary>切换录制；<paramref name="rawTime"/> 为钩子事件时间（0 = 取当前系统 tick）。</summary>
    public static void Toggle(uint rawTime)
    {
        if (_recording) Stop();
        else Start(rawTime);
    }

    /// <summary>开始录制。按用户要求：不弹任何提示、不倒计时，立即开始。</summary>
    public static void Start() => Start(0);

    /// <summary>开始录制；<paramref name="rawTime"/> 为热键事件的钩子时间（保留触发前的等待时长）。</summary>
    public static void Start(uint rawTime) => StartCore(rawTime, null, null, suppressAutoAction: false, editorInitiated: false);

    /// <summary>
    /// 为动作编辑器开始录制：<paramref name="targetId"/> 非空时覆盖该宏（重新录制），
    /// 否则新建；<paramref name="name"/> 为覆盖录制时保留的宏名。
    /// 编辑器自己会把 MacroId 绑回动作，故不自动创建动作。
    /// </summary>
    public static void StartInto(string? targetId, string? name)
        => StartCore(0, targetId, name, suppressAutoAction: true, editorInitiated: true);

    private static void StartCore(uint rawTime, string? targetId, string? name,
                                  bool suppressAutoAction, bool editorInitiated)
    {
        if (_recording || !MacroSettings.Enabled || MacroPlayer.IsPlaying) return;

        _events.Clear();
        _downKeys.Clear();
        _downButtons.Clear();
        _capsSwallowedDown = false;
        _recordTargetId = string.IsNullOrWhiteSpace(targetId) ? null : targetId;
        _recordName = string.IsNullOrWhiteSpace(name) ? null : name;
        _suppressAutoAction = suppressAutoAction;
        _editorInitiated = editorInitiated;
        // 以热键事件的系统 tick 作为零点：录制开始后到第一个输入的停顿也会被回放。
        _startRaw = rawTime != 0 ? rawTime : unchecked((uint)Environment.TickCount);
        _lastEventMs = 0;
        _lastMoveMs = -1000;
        _lastX = _lastY = 0;
        _haveMove = false;
        _clock.Restart();
        _recording = true;

        // 兜底停止通道：系统级热键，钩子被摘 / 修饰键判定异常时仍能停下录制。
        MacroHotkey.Register();
        Trace($"start target={targetId ?? "-"} editor={editorInitiated}");

        _deskLeft = Win32.GetSystemMetrics(Win32.SmXVirtualscreen);
        _deskTop = Win32.GetSystemMetrics(Win32.SmYVirtualscreen);
        _deskWidth = Win32.GetSystemMetrics(Win32.SmCxVirtualscreen);
        _deskHeight = Win32.GetSystemMetrics(Win32.SmCyVirtualscreen);

        // 创建窗口可能触发 WPF 首帧初始化，放到消息泵之后，避免占用钩子回调。
        // 同时清掉可能残留的面板 / 右键长按手势状态：录制期间钩子透明，这些待处理状态
        // 若不清掉会在录制结束后误作用（例如吞掉下一次右键 up）。
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Hooks.MouseHook.CancelPendingGesture();
            // 注意：不能在这里无条件 SuperPanel.Close()——从超级面板新建的动作编辑器是
            // Owner=面板 的模态窗，关闭 owner 会把对话框一起销毁（表现为「点录制后窗口直接没了」）。
            // HideForRecording 会在有对话框时改为临时隐藏，停止录制后恢复。
            SuperPanel.HideForRecording();
            MenuSystem.CloseCurrent();
            HelpPanel.Close();
            RecordingIndicatorWindow.ShowIndicator();
        });

        Raise(RecordingStarted);
    }

    /// <summary>停止录制：补齐未释放的键/鼠标键，快照后自动命名并保存（不弹命名框）。</summary>
    /// <param name="force">跳过开录防抖（会话结束等必须收尾的场景）。</param>
    public static void Stop(bool force = false)
    {
        if (!_recording) return;

        // 防抖：刚开录 600ms 内收到的「停止」一律忽略——最常见的原因是同一个点击被处理了两次
        // （按钮双击 / 鼠标抖动 / 热键与系统热键先后到达），表现为「一点录制就提示已停止」。
        if (!force && _clock.ElapsedMilliseconds < 600)
        {
            Trace($"stop-ignored(debounce) elapsed={_clock.ElapsedMilliseconds}\n{ShortStack()}");
            return;
        }

        Trace($"stop elapsed={_clock.ElapsedMilliseconds} events={_events.Count} force={force}\n{ShortStack()}");

        _recording = false;
        _clock.Stop();
        MacroHotkey.Unregister();

        int closeAt = _lastEventMs;

        // 补齐仍按下的键与鼠标键，避免回放时 Ctrl / 左键卡住。
        foreach (var kv in _downKeys)
            _events.Add(new MacroEvent { TimeMs = closeAt, Kind = MacroEventKind.KeyUp, Vk = kv.Key, Flags = kv.Value });
        _downKeys.Clear();

        foreach (var b in _downButtons)
            _events.Add(new MacroEvent { TimeMs = closeAt, Kind = MacroEventKind.MouseUp, Data = b });
        _downButtons.Clear();

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RecordingIndicatorWindow.HideIndicator();
            SuperPanel.RestoreAfterRecording();   // 录制期间让开屏幕的面板（若有）显示回来
        });
        Raise(RecordingStopped);

        string? targetId = _recordTargetId;
        string? recordName = _recordName;
        bool autoCreate = !_suppressAutoAction;
        _recordTargetId = null;
        _recordName = null;
        _suppressAutoAction = false;
        _editorInitiated = false;

        if (_events.Count == 0)
        {
            TrayService.Notify("宏录制已停止：没有录制到内容");
            return;
        }

        var snapshot = new MacroDto
        {
            Id = "",   // Id 在后台落盘线程生成（避免在钩子回调里做 File.Exists）
            Name = recordName ?? ("录制 " + DateTime.Now.ToString("MM-dd HH:mm:ss")),
            CreatedAt = DateTime.Now.ToString("O"),
            DurationMs = closeAt,
            DesktopLeft = _deskLeft,
            DesktopTop = _deskTop,
            DesktopWidth = _deskWidth,
            DesktopHeight = _deskHeight,
            Events = new List<MacroEvent>(_events),
        };

        Task.Run(() =>
        {
            try
            {
                snapshot.Id = string.IsNullOrEmpty(targetId) ? MacroStore.NewId() : targetId;
                MacroStore.Save(snapshot);
                TrayService.Notify($"宏已保存：{snapshot.Name}（{snapshot.Events.Count} 个事件）");
                RaiseSaved(snapshot.Id, autoCreate);
            }
            catch (Exception ex)
            {
                CrashLog.Write("MacroRecorder.Save", ex);
                TrayService.Notify("宏保存失败，详见崩溃日志");
            }
        });
    }

    /// <summary>
    /// 设置页「停止录制」按钮：先剔除按钮自身造成的最近鼠标点击，再停止。
    /// 否则点击按钮这一下会被录进宏，回放时在同样的屏幕位置多点一次。
    /// </summary>
    public static void StopFromUi()
    {
        if (!_recording) return;
        int cutoff = _lastEventMs - 250;
        _events.RemoveAll(e => e.TimeMs >= cutoff &&
            (e.Kind == MacroEventKind.MouseDown || e.Kind == MacroEventKind.MouseUp));
        Stop();
    }

    /// <summary>
    /// 钩子回调内调用：识别录制热键。命中返回 true（吞掉该键），未命中返回 false。
    /// 「停止」不受编辑器会话限制——只要正在录制就能停；「开始」只在键鼠宏动作编辑窗口内生效。
    /// </summary>
    public static bool HandleHotkey(ushort vk, bool isDown, bool isUp, uint rawTime)
    {
        bool canStop = _recording;
        bool canStart = !_recording && _editorSessions > 0 && MacroSettings.Enabled;

        if (!canStop && !canStart)
        {
            // 会话外不锁存：长按过热键后再关窗口，残留状态会让下一次录制热键失灵。
            if (isUp) _recordHotkeyDown = false;
            return false;
        }

        if (vk != MacroSettings.RecordHotkeyVk) return false;

        if (isDown)
        {
            if (!_recordHotkeyDown && ModifiersDown(MacroSettings.RecordHotkeyModifiers))
            {
                _recordHotkeyDown = true;
                // 录制中 → 停止；编辑器内未录制 → 绑定当前编辑目标开录。
                if (_recording) Stop();
                else StartCore(rawTime, _editorTargetId, _editorTargetName,
                               suppressAutoAction: true, editorInitiated: true);
            }
            return _recordHotkeyDown;   // 已锁存：吞掉自动重复
        }

        if (isUp && _recordHotkeyDown)
        {
            _recordHotkeyDown = false;
            return true;
        }

        return false;
    }

    /// <summary>钩子回调内调用：记录键盘事件（只追加内存，不做 IO / 序列化）。</summary>
    public static void ObserveKey(ushort vk, bool down, uint rawTime, uint flags)
    {
        if (!_recording) return;

        if (down)
        {
            _downKeys[vk] = flags & Win32.LlkhfExtended;
        }
        else if (!_downKeys.Remove(vk))
        {
            // 录制开始前就按下的键，丢弃它迟到的 keyup，避免宏以「无配对的抬起」开头。
            return;
        }

        Add(new MacroEvent
        {
            TimeMs = ToRelativeMs(rawTime),
            Kind = down ? MacroEventKind.KeyDown : MacroEventKind.KeyUp,
            Vk = vk,
            Flags = flags & Win32.LlkhfExtended,
        });
    }

    /// <summary>钩子回调内调用：记录鼠标事件（只追加内存）。</summary>
    public static void ObserveMouse(int msg, int x, int y, int data, uint rawTime)
    {
        if (!_recording) return;
        int t = ToRelativeMs(rawTime);

        switch (msg)
        {
            case Win32.WmMousemove:
                if (!_haveMove)
                {
                    _haveMove = true;
                    _lastMoveMs = t;
                    _lastX = x;
                    _lastY = y;
                    Add(new MacroEvent { TimeMs = t, Kind = MacroEventKind.MouseMove, X = x, Y = y });
                    break;
                }
                if (MacroSettings.MouseSampleMs > 0 && t - _lastMoveMs < MacroSettings.MouseSampleMs) return;
                if (MacroSettings.MouseSamplePx > 0 &&
                    Math.Abs(x - _lastX) < MacroSettings.MouseSamplePx &&
                    Math.Abs(y - _lastY) < MacroSettings.MouseSamplePx) return;
                _lastMoveMs = t;
                _lastX = x;
                _lastY = y;
                Add(new MacroEvent { TimeMs = t, Kind = MacroEventKind.MouseMove, X = x, Y = y });
                break;

            case Win32.WmLbuttondown: AddButton(t, MacroEventKind.MouseDown, 1); break;
            case Win32.WmLbuttonup: AddButton(t, MacroEventKind.MouseUp, 1); break;
            case Win32.WmRbuttondown: AddButton(t, MacroEventKind.MouseDown, 2); break;
            case Win32.WmRbuttonup: AddButton(t, MacroEventKind.MouseUp, 2); break;
            case Win32.WmMbuttondown: AddButton(t, MacroEventKind.MouseDown, 3); break;
            case Win32.WmMbuttonup: AddButton(t, MacroEventKind.MouseUp, 3); break;

            case Win32.WmXbuttondown:
            case Win32.WmXbuttonup:
                int button = ((data >> 16) & 0xFFFF) == Win32.Xbutton1 ? 4 : 5;
                AddButton(t, msg == Win32.WmXbuttondown ? MacroEventKind.MouseDown : MacroEventKind.MouseUp, button);
                break;

            case Win32.WmMousewheel:
            case Win32.WmMousehwheel:
                Add(new MacroEvent
                {
                    TimeMs = t,
                    Kind = msg == Win32.WmMousewheel ? MacroEventKind.Wheel : MacroEventKind.HWheel,
                    Data = (short)((data >> 16) & 0xFFFF),
                });
                break;
        }
    }

    private static void AddButton(int timeMs, MacroEventKind kind, int button)
    {
        if (kind == MacroEventKind.MouseDown)
        {
            _downButtons.Add(button);
        }
        else if (!_downButtons.Remove(button))
        {
            // 录制开始前就按下的鼠标键，丢弃迟到抬起。
            return;
        }

        Add(new MacroEvent { TimeMs = timeMs, Kind = kind, Data = button });
    }

    private static void Add(MacroEvent e)
    {
        _lastEventMs = e.TimeMs;
        _events.Add(e);

        if (_events.Count >= MacroSettings.MaxEvents)
        {
            // 达到上限自动停止（Stop 只做快照 + 后台落盘，仍不阻塞钩子回调）。
            Stop();
        }
    }

    private static bool ModifiersDown(ushort[] modifiers)
    {
        foreach (var m in modifiers)
            if (!IsKeyDown(m)) return false;
        return true;
    }

    /// <summary>
    /// 修饰键按「左右任一侧」判断：配置里写 <c>ctrl</c>（解析为 VK_LCONTROL）时，
    /// 右手 Ctrl 也应算命中——GetAsyncKeyState 区分左右，单查左侧会让右 Ctrl 热键失灵。
    /// </summary>
    private static bool IsKeyDown(ushort vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => Down(0xA2) || Down(0xA3) || Down(0x11),   // Ctrl
        0x10 or 0xA0 or 0xA1 => Down(0xA0) || Down(0xA1) || Down(0x10),   // Shift
        0x12 or 0xA4 or 0xA5 => Down(0xA4) || Down(0xA5) || Down(0x12),   // Alt
        0x5B or 0x5C => Down(0x5B) || Down(0x5C),                          // Win
        _ => Down(vk),
    };

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 录制生命周期跟踪：写 <c>%TEMP%\LTools-macro.log</c>（后台线程写，绝不占用钩子回调）。
    /// 用于排查「刚开录就被停」「停不下来」这类只在真机输入下复现的问题。
    /// </summary>
    internal static void Trace(string what)
    {
        Task.Run(() =>
        {
            try
            {
                string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LTools-macro.log");
                var fi = new System.IO.FileInfo(path);
                if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                System.IO.File.AppendAllText(path, $"[{DateTime.Now:MM-dd HH:mm:ss.fff}] {what}\n");
            }
            catch
            {
                // 跟踪失败不影响录制。
            }
        });
    }

    /// <summary>调用栈前几帧（足够看出是谁停的，又不至于把日志刷爆）。</summary>
    private static string ShortStack()
    {
        var lines = Environment.StackTrace.Split('\n');
        return string.Join("\n", lines.Take(8)).TrimEnd();
    }

    private static void Raise(Action? evt)
    {
        if (evt == null) return;
        foreach (Action h in evt.GetInvocationList())
        {
            try { h(); } catch (Exception ex) { CrashLog.Write("MacroRecorder.Event", ex); }
        }
    }

    private static void RaiseSaved(string id, bool autoCreate)
    {
        var evt = MacroSaved;
        if (evt == null) return;
        foreach (Action<string, bool> h in evt.GetInvocationList())
        {
            try { h(id, autoCreate); } catch (Exception ex) { CrashLog.Write("MacroRecorder.MacroSaved", ex); }
        }
    }

    private static int ToRelativeMs(uint raw)
    {
        if (_startRaw == 0) _startRaw = raw;
        // uint 减法自动回绕，录制时长 < 49 天时结果正确。
        return (int)unchecked(raw - _startRaw);
    }
}


