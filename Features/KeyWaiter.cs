using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 「等待按键」的登记处：键盘钩子在自动化运行期间把按键转给它，流程在后台线程等结果。
/// </summary>
/// <remarks>
/// <para>
/// 自动化运行时键盘钩子只保留「停止热键」一条通道，其余按键原样传给前台程序——
/// 所以「等待按键」不能靠钩子里的手势，而是<b>临时挂一个等待者</b>：
/// 钩子见到按键先问它，命中就吞掉（连同它的自动重复与抬起），没命中照常放行。
/// </para>
/// <para>
/// 只吞命中的那个键：等 F2 时用户照样能在别的窗口打字。修饰键从不吞（吞了会让系统
/// 认为 Ctrl 一直按着），所以「按 Ctrl+S」判定的是 Ctrl 按下状态 + S 键。
/// </para>
/// <para>
/// **可以有多个等待者**（并发运行各等各的）：按 arm 顺序，先命中者独占这个键（含它的自动重复 /
/// 抬起），其余继续等。同一次按键不会被两个运行同时消费。
/// </para>
/// </remarks>
internal static class KeyWaiter
{
    private static readonly object Gate = new();

    // 写（Arm / Disarm）在锁内换整个数组，钩子线程无锁读一份快照。
    private static volatile Waiter[] _active = Array.Empty<Waiter>();

    /// <summary>
    /// 已经吞掉 keydown、正在等它抬起的键（自动重复也要继续吞）。
    /// 只在钩子线程读写。
    /// <b>撤掉等待者时不清它</b>：keyup 还没到就清掉，目标程序会收到一个没有 keydown 的抬起。
    /// </summary>
    private static readonly Dictionary<int, long> _captured = new();

    /// <summary>吞键后最多再等这么久的抬起；超时说明抬起丢了（拔键盘 / 切换会话），别再吞。</summary>
    private const long CaptureLifetimeMs = 3000;

    /// <summary>一次等待。由动作单元创建，钩子回调写入结果。</summary>
    public sealed class Waiter
    {
        internal Waiter(string expected, bool swallow)
        {
            Expected = expected;
            Swallow = swallow;
        }

        /// <summary>期望的组合键（canonical 小写）；空 = 任意键。</summary>
        public string Expected { get; }

        /// <summary>命中后是否吞掉这个键（否则目标程序也会收到）。</summary>
        public bool Swallow { get; }

        /// <summary>是否已经等到（含「按任意键」的任意命中）。</summary>
        public volatile bool Done;

        /// <summary>实际按下的组合键（canonical 小写，如 <c>ctrl+s</c>）。</summary>
        public volatile string Chord = "";
    }

    /// <summary>挂一个等待者；<paramref name="expected"/> 为空表示任意键。</summary>
    public static Waiter Arm(string expected, bool swallow)
    {
        var waiter = new Waiter(Normalize(expected), swallow);
        lock (Gate)
        {
            _active = _active.Append(waiter).ToArray();
        }
        return waiter;
    }

    /// <summary>撤掉等待者（超时 / 停止 / 结束时都要调）。已吞下的 keydown 仍会吞掉它的 keyup。</summary>
    public static void Disarm(Waiter waiter)
    {
        lock (Gate)
        {
            _active = _active.Where(w => !ReferenceEquals(w, waiter)).ToArray();
        }
    }

    /// <summary>
    /// 钩子回调里调用。返回 true = 吞掉这个按键（keydown 与它配对的 keyup 都由这里管，
    /// 不要再走 <c>AppState.SwallowedVks</c>）。
    /// </summary>
    public static bool HandleKey(ushort vk, bool isDown, bool isUp)
    {
        if (isUp)
        {
            if (!_captured.Remove((int)vk)) return false;
            return true;
        }

        if (!isDown) return false;

        long now = Environment.TickCount64;

        // 自动重复：命中过的那颗键在抬起前继续吞，否则会往目标程序里打字。
        if (_captured.TryGetValue(vk, out long capturedAt))
        {
            if (now - capturedAt < CaptureLifetimeMs) return true;
            _captured.Remove((int)vk);
        }

        Waiter[] waiters = _active;
        if (waiters.Length == 0 || IsModifier(vk)) return false;

        string chord = KeyStroke.Format(CurrentModifiers(), vk);

        foreach (Waiter waiter in waiters)
        {
            bool match = waiter.Expected.Length == 0 ||
                         string.Equals(chord, waiter.Expected, StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            waiter.Chord = chord;
            waiter.Done = true;
            if (!waiter.Swallow) return false;

            _captured[(int)vk] = now;
            return true;
        }

        return false;
    }

    /// <summary>把用户写的键名收拢成 canonical 写法（<c>Ctrl+S</c> → <c>ctrl+s</c>）。</summary>
    private static string Normalize(string expected)
    {
        expected = (expected ?? "").Trim();
        if (expected.Length == 0) return "";
        if (!KeyStroke.TryParse(expected, out var vks, out _)) return expected.ToLowerInvariant();

        var mods = ModifierKeys.None;
        for (int i = 0; i < vks.Count - 1; i++) mods |= ModifierOf(vks[i]);
        return KeyStroke.Format(mods, vks[^1]);
    }

    private static ModifierKeys CurrentModifiers()
    {
        KeyMods mods = KeyState.Modifiers();
        var result = ModifierKeys.None;
        if ((mods & KeyMods.Ctrl) != 0) result |= ModifierKeys.Control;
        if ((mods & KeyMods.Shift) != 0) result |= ModifierKeys.Shift;
        if ((mods & KeyMods.Alt) != 0) result |= ModifierKeys.Alt;
        if ((mods & KeyMods.Win) != 0) result |= ModifierKeys.Windows;
        return result;
    }

    private static ModifierKeys ModifierOf(ushort vk) => vk switch
    {
        0xA2 or 0xA3 => ModifierKeys.Control,
        0xA0 or 0xA1 => ModifierKeys.Shift,
        0xA4 or 0xA5 => ModifierKeys.Alt,
        0x5B or 0x5C => ModifierKeys.Windows,
        _ => ModifierKeys.None,
    };

    private static bool IsModifier(ushort vk) => ModifierOf(vk) != ModifierKeys.None;
}
