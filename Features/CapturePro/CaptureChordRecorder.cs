using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LTools.Features.CapturePro;

/// <summary>
/// 把文本框变成「点一下，按组合键」的录入框——专供截图 Pro 的快捷键表。
/// </summary>
/// <remarks>
/// <para>
/// 与全局热键用的 <see cref="HotkeyCapture"/> 有两处不同，所以没有复用：
/// ① **裸修饰键也是合法值**（表里 <c>Shift</c> 锁正方形、<c>Alt</c> 切参考线，上游就是「按住」），
/// <see cref="HotkeyCapture"/> 把修饰键当成「还没按完」；② 主键可以是 <c>Esc</c> / <c>Tab</c> /
/// <c>Enter</c> / <c>,</c> / <c>.</c> / <c>`</c> 这些，不能拿它们当「结束录入」。
/// </para>
/// <para>
/// 录入期间 <see cref="HotkeyCapture.PushSuspend"/> 让全局钩子放行：否则用户录一个 <c>Q</c>
/// 可能顺手触发一条全局快捷键。
/// </para>
/// </remarks>
internal static class CaptureChordRecorder
{
    private sealed class State
    {
        public State(TextBox box) => Box = box;

        public TextBox Box { get; }
        public Action<string>? OnCommitted { get; init; }
        public string Before = "";
        public bool Capturing;
        public bool ModifierOnly;
        public ushort ModifierVk;
        public object? Border;
        public object? Background;
    }

    private static readonly Dictionary<TextBox, State> States = new();
    private static State? _active;

    /// <summary>给文本框装上录入行为（只读、点击或聚焦开始监听）。</summary>
    public static void Attach(TextBox box, Action<string>? onCommitted = null)
    {
        var state = new State(box) { OnCommitted = onCommitted };
        States[box] = state;

        box.IsReadOnly = true;
        box.IsUndoEnabled = false;
        box.Cursor = Cursors.Hand;
        box.ToolTip = "点一下再按组合键；修饰键可以单独按（如 Shift）。Esc 也能绑。";

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
        box.PreviewKeyUp += (_, e) => OnKeyUp(state, e);
    }

    /// <summary>读出框里当前的组合键（空 = 没绑）。</summary>
    public static string GetChord(TextBox box) => States.TryGetValue(box, out var s) ? s.Before : box.Text;

    private static void Begin(State s)
    {
        if (s.Capturing) return;
        if (_active != null) End(_active, commit: true);

        _active = s;
        s.Capturing = true;
        s.ModifierOnly = false;
        s.ModifierVk = 0;
        s.Before = s.Box.Text;

        s.Border = s.Box.ReadLocalValue(Control.BorderBrushProperty);
        s.Background = s.Box.ReadLocalValue(Control.BackgroundProperty);
        if (s.Box.TryFindResource("AccentBrush") is Brush accent) s.Box.BorderBrush = accent;
        if (s.Box.TryFindResource("LightAccentBrush") is Brush bg) s.Box.Background = bg;

        s.Box.Text = "请按下组合键…";
        HotkeyCapture.PushSuspend();
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

        s.Box.Text = s.Before;
        HotkeyCapture.PopSuspend();

        if (!commit) return;
        s.OnCommitted?.Invoke(s.Before);
    }

    private static void OnKeyDown(State s, KeyEventArgs e)
    {
        if (!s.Capturing) return;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        e.Handled = true;

        if (IsModifier(key))
        {
            // 修饰键先只做预览：单独抬起 = 绑裸修饰键，接着按别的键 = 正常组合
            s.ModifierOnly = true;
            s.ModifierVk = (ushort)KeyInterop.VirtualKeyFromKey(key);
            s.Box.Text = ModifierName(key) + "…";
            return;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        var chord = new CaptureChord((ushort)vk,
            (Keyboard.Modifiers & ModifierKeys.Control) != 0,
            (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
            (Keyboard.Modifiers & ModifierKeys.Alt) != 0,
            (Keyboard.Modifiers & ModifierKeys.Windows) != 0);

        Commit(s, chord.Display());
    }

    private static void OnKeyUp(State s, KeyEventArgs e)
    {
        if (!s.Capturing) return;
        e.Handled = true;

        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!IsModifier(key) || !s.ModifierOnly) return;
        if ((ushort)KeyInterop.VirtualKeyFromKey(key) != s.ModifierVk) return;

        // 只按了这一个修饰键：绑成裸修饰键（Shift / Ctrl / Alt / Win）
        Commit(s, new CaptureChord(s.ModifierVk, false, false, false, false).Display());
    }

    private static void Commit(State s, string chord)
    {
        s.Before = chord;
        s.Box.Text = chord;
        s.ModifierOnly = false;
        s.ModifierVk = 0;
        s.Capturing = false;

        End(s, commit: false);
        s.OnCommitted?.Invoke(chord);
    }

    private static bool IsModifier(Key k) => k is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
        or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string ModifierName(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl => "Ctrl",
        Key.LeftShift or Key.RightShift => "Shift",
        Key.LeftAlt or Key.RightAlt => "Alt",
        _ => "Win",
    };
}
