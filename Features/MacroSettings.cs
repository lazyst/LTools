using System;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 宏录制的运行时设置（从 <see cref="AppConfig.Macros"/> 载入，供钩子回调只读访问）。
/// 钩子路径上不做文件 IO：热键 VK / 采样参数都在启动或保存设置时预解析到静态字段。
/// </summary>
internal static class MacroSettings
{
    /// <summary>恢复默认时使用的录制热键（与 <see cref="MacroConfig.RecordHotkey"/> 默认值一致）。</summary>
    public const string DefaultRecordHotkey = "ctrl+f2";

    /// <summary>恢复默认时使用的停止回放热键（与 <see cref="MacroConfig.PlaybackStopHotkey"/> 默认值一致）。</summary>
    public const string DefaultPlaybackStopHotkey = "ctrl+f3";

    // —— 阶段 2：钩子专用线程热路径会读的字段一律 volatile ——
    // Load() 由 UI / 后台线程整体写一次，钩子回调（HandleHotkey / ObserveMouse / Add /
    // HandleStopHotkey）每次输入都读；引用类型的数组整体替换、内容不再改，volatile 即可保证可见性。
    // 只在「设置页改设置 → 下次操作」才变的 string / double / 倒计时 / 循环数保持自动属性
    //（double 不能标 volatile，且这些都不在钩子热路径上）。
    private static volatile bool _enabled = true;
    public static bool Enabled => _enabled;

    private static volatile int _mouseSampleMs = 8;
    /// <summary>鼠标移动最小采样间隔（ms）。0 = 不按时间抽稀。</summary>
    public static int MouseSampleMs => _mouseSampleMs;

    private static volatile int _mouseSamplePx = 1;
    /// <summary>鼠标移动最小位移（px）。0 = 不按距离抽稀。</summary>
    public static int MouseSamplePx => _mouseSamplePx;

    private static volatile int _maxEvents = 200_000;
    /// <summary>单个宏最大事件数，达到后自动停止录制。</summary>
    public static int MaxEvents => _maxEvents;

    private static volatile ushort _recordHotkeyVk;
    public static ushort RecordHotkeyVk => _recordHotkeyVk;
    private static volatile ushort[] _recordHotkeyModifiers = Array.Empty<ushort>();
    public static ushort[] RecordHotkeyModifiers => _recordHotkeyModifiers;

    private static volatile ushort _playbackStopVk;
    public static ushort PlaybackStopVk => _playbackStopVk;
    private static volatile ushort[] _playbackStopModifiers = Array.Empty<ushort>();
    public static ushort[] PlaybackStopModifiers => _playbackStopModifiers;

    /// <summary>停止录制热键（录制期间临时注册，开始录制用面板按钮 / 托盘）。</summary>
    public static string RecordHotkey { get; private set; } = DefaultRecordHotkey;

    /// <summary>停止宏回放热键（仅回放期间注册 / 匹配）。</summary>
    public static string PlaybackStopHotkey { get; private set; } = DefaultPlaybackStopHotkey;

    /// <summary>回放前倒计时的全局默认值（ms，0 = 立即回放；宏元数据未设置时使用）。</summary>
    public static int PlaybackCountdownMs { get; private set; }

    /// <summary>默认回放速度倍率（宏未设置专属速度时使用）。double 不能标 volatile，且只在回放后台线程读。</summary>
    public static double DefaultSpeed { get; private set; } = 1.0;

    /// <summary>默认回放循环次数（宏未单独设置时使用，1–99）。</summary>
    public static int PlayLoops { get; private set; } = 1;

    public static string RecordHotkeyDisplay => FormatHotkey(RecordHotkey);

    public static string PlaybackStopHotkeyDisplay => FormatHotkey(PlaybackStopHotkey);

    /// <summary>从配置载入并预解析热键。配置非法时回退默认值并写崩溃日志。</summary>
    public static void Load(AppConfig cfg)
    {
        var m = cfg.Macros ?? new MacroConfig();
        _enabled = m.Enabled;
        RecordHotkey = NormalizeHotkey(string.IsNullOrWhiteSpace(m.RecordHotkey) ? DefaultRecordHotkey : m.RecordHotkey);
        PlaybackStopHotkey = NormalizeHotkey(string.IsNullOrWhiteSpace(m.PlaybackStopHotkey) ? DefaultPlaybackStopHotkey : m.PlaybackStopHotkey);
        PlaybackCountdownMs = Math.Clamp(m.CountdownMs, 0, 10_000);
        _mouseSampleMs = Math.Clamp(m.MouseSampleMs, 0, 100);
        _mouseSamplePx = Math.Clamp(m.MouseSamplePx, 0, 20);
        _maxEvents = Math.Clamp(m.MaxEvents, 1_000, 2_000_000);
        DefaultSpeed = Math.Clamp(m.DefaultSpeed, 0.1, 5.0);
        PlayLoops = Math.Clamp(m.Loops, 1, 99);

        if (!TryParseHotkey(RecordHotkey, out var rv, out var rm, out var err))
        {
            CrashLog.Write("MacroSettings.RecordHotkey", new ArgumentException(err));
            RecordHotkey = DefaultRecordHotkey;
            TryParseHotkey(RecordHotkey, out rv, out rm, out _);
        }
        // 兜底：解析意外失败时也必须留一个可用的键，否则热键会永远匹配不到（表现为「按了没反应」）。
        // 注意 rm 为空是合法值（裸 F2 这类无修饰键热键），不能在这里补 Ctrl。
        if (rv == 0)
        {
            rv = (ushort)0x71;                       // F2
            rm = new ushort[] { 0xA2 };              // Ctrl
        }
        _recordHotkeyVk = rv;
        _recordHotkeyModifiers = rm;

        if (!TryParseHotkey(PlaybackStopHotkey, out var pv, out var pm, out var perr))
        {
            CrashLog.Write("MacroSettings.PlaybackStopHotkey", new ArgumentException(perr));
            PlaybackStopHotkey = DefaultPlaybackStopHotkey;
            TryParseHotkey(PlaybackStopHotkey, out pv, out pm, out _);
        }
        if (pv == 0)
        {
            pv = (ushort)0x72;                       // F3
            pm = new ushort[] { 0xA2 };              // Ctrl
        }
        _playbackStopVk = pv;
        _playbackStopModifiers = pm;
    }

    /// <summary>
    /// 解析热键字符串（形如 <c>ctrl+f2</c> / <c>f2</c>）：末段为主键，前面全部为修饰键。
    /// 复用 <see cref="KeyStroke"/> 的解析器，保证与「模拟按键」动作同一套键名。
    /// </summary>
    /// <remarks>
    /// 单键也允许（<c>f2</c>、<c>a</c> 都行）：宏的录制 / 停止回放热键与快捷键页同一套规则，
    /// 但单键命中会吞掉那个按键，界面上要提示一次风险（见 <c>HotkeyEditDialog</c>）。
    /// </remarks>
    public static bool TryParseHotkey(string text, out ushort vk, out ushort[] modifiers, out string error)
    {
        vk = 0;
        modifiers = Array.Empty<ushort>();
        error = "";

        if (!KeyStroke.TryParse(text, out var vks, out error)) return false;
        if (vks.Count == 0)
        {
            error = "热键不能为空";
            return false;
        }

        if (vks.Count == 1)
        {
            vk = vks[0];
            modifiers = Array.Empty<ushort>();
            return true;
        }

        vk = vks[^1];
        modifiers = vks.Take(vks.Count - 1).ToArray();
        return true;
    }

    /// <summary>把配置里的热键字符串格式化为显示文本：<c>ctrl+f2</c> → <c>Ctrl+F2</c>。</summary>
    /// <remarks>
    /// 能解析就交给 <see cref="HotkeyChord.Format"/>：功能键 / 小键盘（<c>NumPad5</c>）与快捷键页
    /// 共用一套习惯名，录入框预览不会和表格里写成两种样子。
    /// </remarks>
    public static string FormatHotkey(string text)
    {
        string pretty = HotkeyChord.Format(text);
        if (pretty != text) return pretty;

        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts.Select(p =>
            p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// 把热键收拢成配置用的 canonical 形式（小写、无空格）：<c>Ctrl+F2</c> → <c>ctrl+f2</c>。
    /// 界面只负责显示大写，写盘一律经此转换，避免同一热键出现多种写法。
    /// </summary>
    public static string NormalizeHotkey(string text)
        => string.Join("+", text
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant()));

    /// <summary>
    /// 判断热键的修饰键是否全部按下（供钩子回调 O(1) 使用，不做 IO）。
    /// 无修饰键热键反过来要求「一个修饰键都没按」，避免 <c>Ctrl+F2</c> 误触发裸 <c>F2</c>。
    /// </summary>
    public static bool AreModifiersDown(ushort[] modifiers)
    {
        if (modifiers.Length == 0) return AreNoModifiersDown();
        foreach (var m in modifiers)
            if (!IsKeyDown(m)) return false;
        return true;
    }

    /// <summary>当前是否没有任何 Ctrl / Shift / Alt / Win 按下（左右两侧都查）。</summary>
    private static bool AreNoModifiersDown()
        => !Down(0xA2) && !Down(0xA3) && !Down(0x11)     // Ctrl
        && !Down(0xA0) && !Down(0xA1) && !Down(0x10)     // Shift
        && !Down(0xA4) && !Down(0xA5) && !Down(0x12)     // Alt
        && !Down(0x5B) && !Down(0x5C);                   // Win

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
}
