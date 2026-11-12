using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    /// <summary>是否有热键框正在等待 / 接收按键（键盘钩子回调只读）。</summary>
    public static bool IsCapturing => _active != null;

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
        if (_active != null) End(_active, commit: true);

        _active = s;
        s.Capturing = true;
        s.Accepted = false;
        s.LastRaw = "";
        s.BeforeRaw = GetHotkey(s.Box);
        s.Border = s.Box.ReadLocalValue(Control.BorderBrushProperty);
        s.Background = s.Box.ReadLocalValue(Control.BackgroundProperty);

        if (s.Box.TryFindResource("AccentBrush") is Brush accent) s.Box.BorderBrush = accent;
        if (s.Box.TryFindResource("LightAccentBrush") is Brush bg) s.Box.Background = bg;
        s.Box.ToolTip = "点击后直接按下组合键；Esc 取消，Tab 结束";
        s.Box.Text = "请按下组合键…";
    }

    private static void End(State s, bool commit)
    {
        if (!s.Capturing) return;
        s.Capturing = false;
        if (ReferenceEquals(_active, s)) _active = null;

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
