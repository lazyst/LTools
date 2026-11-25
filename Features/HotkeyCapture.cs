using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Features;

/// <summary>
/// 把普通文本框变成「点击后直接按下组合键即可录入」的热键框。
/// 录入期间 <see cref="IsCapturing"/> 为 true，键盘钩子据此让路：
/// 正在设置的热键（例如当前的录制热键）不会同时触发 LTools 自己的功能。
/// </summary>
/// <remarks>
/// 显示与存储分离：<see cref="TextBox.Text"/> 只负责好看（<c>Ctrl+F2</c>），
/// canonical 值（<c>ctrl+f2</c>，与配置文件一致）由 <see cref="GetHotkey"/> / <see cref="SetHotkey"/>
/// 存取。写配置一律用 canonical 值，避免「录一次就多出大写写法」。
/// 校验仍走 <see cref="MacroSettings.TryParseHotkey"/>；录入只在窗口获得焦点时进行，不额外安装低级钩子。
/// </remarks>
internal static class HotkeyCapture
{
    private sealed class State
    {
        public State(TextBox box) => Box = box;

        public TextBox Box { get; }
        public Action<string>? OnCommitted;
        public string BeforeRaw = "";
        public string LastRaw = "";
        public object? Border;
        public object? Background;
        public bool Capturing;
        public bool Accepted;
    }

    /// <summary>文本框 → canonical 值（小写）。用弱表，窗口关闭后自动释放。</summary>
    private static readonly ConditionalWeakTable<TextBox, RawValue> RawValues = new();

    private sealed class RawValue
    {
        public string Value = "";
    }

    // 阶段 2：Begin / End / Cancel 由 UI 线程写，IsCapturing 由钩子线程读 → volatile 保证可见。
    private static volatile State? _active;

    /// <summary>额外的「让钩子放行」计数（截图 Pro 的快捷键录入框用，见 <see cref="CaptureChordRecorder"/>）。</summary>
    private static int _extra;

    /// <summary>
    /// 宿主里的录入框：钩子在提权进程，开始 / 结束时让它整体让路（由 <c>UiHost</c> 挂
    /// <c>InputBridge.SetHotkeyCapture</c>）。执行器里是 null——它就是钩子那一侧。
    /// </summary>
    public static Action<bool>? RemoteSink;

    /// <summary>宿主推来的「正在录入」。**断开链路要清掉**（否则快捷键永远被让路）。</summary>
    private static int _remote;

    /// <summary>上一次推给执行器的状态（只在真变了时推一次）。</summary>
    private static int _signaled;

    /// <summary>是否有热键框正在等待 / 接收按键（键盘钩子回调只读）。</summary>
    public static bool IsCapturing =>
        _active != null || Volatile.Read(ref _extra) > 0 || Volatile.Read(ref _remote) > 0;

    /// <summary>宿主里有没有录入框在等按键（重连时补推一次用）。</summary>
    public static bool IsHostCapturing => _active != null || Volatile.Read(ref _extra) > 0;

    /// <summary>执行器收到宿主的录入状态（事件；断开时按「没在录入」清掉）。</summary>
    public static void ApplyRemote(bool capturing) => Volatile.Write(ref _remote, capturing ? 1 : 0);

    /// <summary>宿主断开了：它那份录入状态作废，别让钩子一直让路。</summary>
    public static void ResetRemote() => Volatile.Write(ref _remote, 0);

    /// <summary>临时让全局钩子放行（与热键录入同一条通道，配对调用 <see cref="PopSuspend"/>）。</summary>
    public static void PushSuspend() { Interlocked.Increment(ref _extra); Publish(); }

    /// <summary>结束放行。</summary>
    public static void PopSuspend() { Interlocked.Decrement(ref _extra); Publish(); }

    /// <summary>
    /// 把「现在要不要让钩子放行」推给提权进程（只在本进程是宿主时）。状态只在这里算一次：
    /// 录入框改了、暂停计数改了都调它，别在 <see cref="Begin"/> / <see cref="End"/> 里各推一次
    /// （换一个录入框会先 End 再 Begin，逐次推会让钩子中间露出一下）。
    /// </summary>
    private static void Publish()
    {
        if (!LTools.Core.AppRole.IsUiHost) return;

        int now = IsHostCapturing ? 1 : 0;
        if (Interlocked.Exchange(ref _signaled, now) == now) return;

        try { RemoteSink?.Invoke(now != 0); }
        catch (Exception ex) { LTools.Core.AppLog.Warn("HotkeyCapture", "通知录入状态失败：" + ex.Message); }
    }

    /// <summary>宿主窗口失焦 / 关闭时调用：结束录入，避免键盘钩子一直让路。</summary>
    public static void Cancel()
    {
        if (_active != null) End(_active, commit: true);
    }

    /// <summary>
    /// 读取热键框的 canonical 值（小写，如 <c>ctrl+f2</c>）。写配置、比较热键都用它。
    /// 未通过 <see cref="SetHotkey"/> 设置过时，按当前显示文本规范化兜底。
    /// </summary>
    public static string GetHotkey(TextBox box)
        => RawValues.TryGetValue(box, out var raw) ? raw.Value : MacroSettings.NormalizeHotkey(box.Text);

    /// <summary>
    /// 设置热键框：内部保存 canonical 小写值，<see cref="TextBox.Text"/> 显示为 <c>Ctrl+F2</c>。
    /// </summary>
    public static void SetHotkey(TextBox box, string text)
    {
        string raw = MacroSettings.NormalizeHotkey(text);
        RawValues.GetOrCreateValue(box).Value = raw;
        box.Text = raw.Length == 0 ? "" : MacroSettings.FormatHotkey(raw);
    }

    /// <summary>给文本框装上录制式热键录入行为（只读、点击或聚焦开始监听）。</summary>
    /// <param name="onCommitted">录到有效组合并结束时回调（参数为 canonical 值，如 <c>ctrl+f4</c>）。</param>
    public static void Attach(TextBox box, Action<string>? onCommitted = null)
    {
        var state = new State(box) { OnCommitted = onCommitted };
        box.IsReadOnly = true;
        box.IsUndoEnabled = false;
        box.Cursor = Cursors.Hand;
        box.ToolTip = "点击后直接按下组合键；Esc 取消，Tab 结束";

        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            box.Focus();
            Begin(state);
            e.Handled = true;
        };
        box.GotKeyboardFocus += (_, _) => Begin(state);
        box.LostKeyboardFocus += (_, _) => End(state, commit: true);
        box.Unloaded += (_, _) => End(state, commit: true);
        box.PreviewKeyDown += (_, e) => OnKeyDown(state, e);
        box.PreviewKeyUp += (_, e) =>
        {
            if (state.Capturing) e.Handled = true;
        };
    }

    private static void Begin(State s)
    {
        if (s.Capturing) return;

        // 先把新的挂上再结束旧的：<see cref="Publish"/> 只在真变化时推，
        // 换一个录入框（失焦 → 另一个聚焦）不会向执行器推出「没在录入」。
        State? previous = _active;
        _active = s;
        s.Capturing = true;
        if (previous != null) End(previous, commit: true);

        s.Accepted = false;
        s.LastRaw = "";
        s.BeforeRaw = GetHotkey(s.Box);
        s.Border = s.Box.ReadLocalValue(Control.BorderBrushProperty);
        s.Background = s.Box.ReadLocalValue(Control.BackgroundProperty);

        if (s.Box.TryFindResource("AccentBrush") is Brush accent) s.Box.BorderBrush = accent;
        if (s.Box.TryFindResource("LightAccentBrush") is Brush bg) s.Box.Background = bg;
        s.Box.ToolTip = "点击后直接按下组合键；Esc 取消，Tab 结束";
        s.Box.Text = "请按下组合键…";
        Publish();
    }

    private static void End(State s, bool commit)
    {
        if (!s.Capturing) return;
        s.Capturing = false;
        if (ReferenceEquals(_active, s)) _active = null;
        Publish();

        if (s.Border == DependencyProperty.UnsetValue) s.Box.ClearValue(Control.BorderBrushProperty);
        else s.Box.BorderBrush = (Brush)s.Border!;
        if (s.Background == DependencyProperty.UnsetValue) s.Box.ClearValue(Control.BackgroundProperty);
        else s.Box.Background = (Brush)s.Background!;
        s.Box.ToolTip = "点击后直接按下组合键；Esc 取消，Tab 结束";
        // 取消或没录到有效组合：恢复进入录入前的值；录到过则保留最后一次有效值
        // （避免「先按对 Ctrl+F2，又误按一个无效键，最后离开时把正确值丢掉」）。
        if (commit && s.Accepted)
        {
            SetHotkey(s.Box, s.LastRaw);
            s.OnCommitted?.Invoke(s.LastRaw);
        }
        else
        {
            SetHotkey(s.Box, s.BeforeRaw);
        }
    }

    private static void OnKeyDown(State s, KeyEventArgs e)
    {
        if (!s.Capturing) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Esc 始终取消；Tab / Enter 不带修饰键时结束录入并把焦点让给窗口。
        if (key == Key.Escape)
        {
            e.Handled = true;
            End(s, commit: false);
            return;
        }
        if (key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None)
        {
            End(s, commit: true);
            return;
        }
        if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            End(s, commit: true);
            return;
        }

        e.Handled = true;

        if (IsModifier(key))
        {
            s.Box.Text = ModifierPreview(key) + "…";
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        string stroke = KeyStroke.Format(Keyboard.Modifiers, vk);
        if (!MacroSettings.TryParseHotkey(stroke, out _, out _, out var err))
        {
            s.Box.Text = "不可用：" + err;
            s.Box.ToolTip = err;   // 框窄，完整原因放悬停提示
            return;
        }

        s.LastRaw = stroke;   // KeyStroke.Format 已是 canonical 小写（ctrl+f4）
        s.Accepted = true;
        // 录到有效组合立即结束录入：释放钩子拦截，新热键可马上使用；
        // 同时通过 OnCommitted 让宿主立即持久化（全局热键不依赖动作保存）。
        End(s, commit: true);
    }

    private static bool IsModifier(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    /// <summary>修饰键单独按下时的提示（把刚按下的那个键也算进去）。</summary>
    private static string ModifierPreview(Key key)
    {
        var mods = Keyboard.Modifiers;
        mods |= key switch
        {
            Key.LeftCtrl or Key.RightCtrl => ModifierKeys.Control,
            Key.LeftShift or Key.RightShift => ModifierKeys.Shift,
            Key.LeftAlt or Key.RightAlt => ModifierKeys.Alt,
            Key.LWin or Key.RWin => ModifierKeys.Windows,
            _ => ModifierKeys.None,
        };

        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return string.Join("+", parts);
    }
}
