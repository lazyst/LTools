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
/// 同一时刻只允许一个等待者：同时等两个键没有确定语义，后挂的替换前一个。
/// </para>
/// </remarks>
internal static class KeyWaiter
{
    private static readonly object Gate = new();

    // 阶段 2：Arm / Disarm 由动作后台线程在 Gate 内写，HandleKey 由钩子线程无锁读 → volatile 保证可见。
    private static volatile Waiter? _active;

    /// <summary>
    /// 已经吞掉 keydown、正在等它抬起的键（自动重复也要继续吞）。
    /// <b>撤掉等待者时不清它</b>：keyup 还没到就清掉，目标程序会收到一个没有 keydown 的抬起。
    /// </summary>
    private static volatile int _capturedVk;

    /// <summary>吞下这颗键的时刻（<see cref="Environment.TickCount64"/>），用来兜底「抬起永远没来」。</summary>
    private static long _capturedAt;

    /// <summary>吞键后最多再等这么久的抬起；超时说明抬起丢了（拔键盘 / 切换会话），别再吞。</summary>
    private const long CaptureLifetimeMs = 3000;

    /// <summary>一次等待。由单元创建，钩子回调写入结果。</summary>
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
            _active = waiter;
            _capturedVk = 0;
        }
        return waiter;
    }

    /// <summary>撤掉等待者（超时 / 停止 / 结束时都要调）。已吞下的 keydown 仍会吞掉它的 keyup。</summary>
    public static void Disarm(Waiter waiter)
    {
        lock (Gate)
        {
            if (ReferenceEquals(_active, waiter)) _active = null;
        }
    }

    /// <summary>
    /// 钩子回调里调用。返回 true = 吞掉这个按键（keydown 与它配对的 keyup 都由这里管，
    /// 不要再走 <c>AppState.SwallowedVks</c>）。
    /// </summary>
    public static bool HandleKey(ushort vk, bool isDown, bool isUp)
    {
        var waiter = _active;

        if (isUp)
        {
            if (_capturedVk != vk) return false;
            _capturedVk = 0;
            return true;
        }

        if (!isDown) return false;

        // 自动重复：命中过的那颗键在抬起前继续吞，否则会往目标程序里打字。
        if (_capturedVk == vk)
        {
            if (Environment.TickCount64 - _capturedAt < CaptureLifetimeMs) return true;
            _capturedVk = 0;
        }

        if (waiter == null || IsModifier(vk)) return false;

        string chord = KeyStroke.Format(CurrentModifiers(), vk);
        bool match = waiter.Expected.Length == 0 ||
                     string.Equals(chord, waiter.Expected, StringComparison.OrdinalIgnoreCase);

        if (!match) return false;

        waiter.Chord = chord;
        waiter.Done = true;
        if (!waiter.Swallow) return false;

        _capturedVk = vk;
        _capturedAt = Environment.TickCount64;
        return true;
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
        var mods = ModifierKeys.None;
        if (Down(0xA2) || Down(0xA3)) mods |= ModifierKeys.Control;
        if (Down(0xA0) || Down(0xA1)) mods |= ModifierKeys.Shift;
        if (Down(0xA4) || Down(0xA5)) mods |= ModifierKeys.Alt;
        if (Down(0x5B) || Down(0x5C)) mods |= ModifierKeys.Windows;
        return mods;
    }

    /// <summary>挂上等待者时用户可能已经按着 Ctrl：按物理状态补一次（<see cref="CurrentModifiers"/> 读的就是物理状态）。</summary>
    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

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
