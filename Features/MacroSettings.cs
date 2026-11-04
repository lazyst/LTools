using System;
using System.Linq;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 宏录制的运行时设置（从 <see cref="AppConfig.Macros"/> 载入，供钩子回调只读访问）。
/// 钩子路径上不做文件 IO：热键 VK / 采样参数都在启动或保存设置时预解析到静态字段。
/// </summary>
internal static class MacroSettings
{
    public static bool Enabled { get; private set; } = true;

    /// <summary>停止录制热键（仅键鼠宏动作编辑窗口打开时生效）。</summary>
    public static string RecordHotkey { get; private set; } = "ctrl+f2";

    /// <summary>回放前倒计时（ms，0 = 立即回放）。</summary>
    public static int PlaybackCountdownMs { get; private set; }

    /// <summary>鼠标移动最小采样间隔（ms）。0 = 不按时间抽稀。</summary>
    public static int MouseSampleMs { get; private set; } = 8;

    /// <summary>鼠标移动最小位移（px）。0 = 不按距离抽稀。</summary>
    public static int MouseSamplePx { get; private set; } = 1;

    /// <summary>单个宏最大事件数，达到后自动停止录制。</summary>
    public static int MaxEvents { get; private set; } = 200_000;

    /// <summary>默认回放速度倍率。</summary>
    public static double DefaultSpeed { get; private set; } = 1.0;

    /// <summary>回放循环次数（1–99）。</summary>
    public static int PlayLoops { get; private set; } = 1;

    // —— 预解析后的热键（供钩子 O(1) 比较）——
    public static ushort RecordHotkeyVk { get; private set; }
    public static ushort[] RecordHotkeyModifiers { get; private set; } = Array.Empty<ushort>();

    public static string RecordHotkeyDisplay => FormatHotkey(RecordHotkey);

    /// <summary>从配置载入并预解析热键。配置非法时回退默认值并写崩溃日志。</summary>
    public static void Load(AppConfig cfg)
    {
        var m = cfg.Macros ?? new MacroConfig();
        Enabled = m.Enabled;
        RecordHotkey = string.IsNullOrWhiteSpace(m.RecordHotkey) ? "ctrl+f2" : m.RecordHotkey.Trim();
        PlaybackCountdownMs = Math.Clamp(m.CountdownMs, 0, 10_000);
        MouseSampleMs = Math.Clamp(m.MouseSampleMs, 0, 100);
        MouseSamplePx = Math.Clamp(m.MouseSamplePx, 0, 20);
        MaxEvents = Math.Clamp(m.MaxEvents, 1_000, 2_000_000);
        DefaultSpeed = Math.Clamp(m.DefaultSpeed, 0.1, 5.0);
        PlayLoops = Math.Clamp(m.Loops, 1, 99);

        if (!TryParseHotkey(RecordHotkey, out var rv, out var rm, out var err))
        {
            CrashLog.Write("MacroSettings.RecordHotkey", new ArgumentException(err));
            RecordHotkey = "ctrl+f2";
            TryParseHotkey(RecordHotkey, out rv, out rm, out _);
        }
        // 兜底：解析意外失败时也必须留一个可用的键，否则热键会永远匹配不到（表现为「按了没反应」）。
        RecordHotkeyVk = rv != 0 ? rv : (ushort)0x71;                       // F2
        RecordHotkeyModifiers = rm.Length > 0 ? rm : new ushort[] { 0xA2 };  // Ctrl
    }

    /// <summary>
    /// 解析热键字符串（形如 <c>ctrl+f2</c>）：末段为主键，前面全部为修饰键。
    /// 复用 <see cref="KeyStroke"/> 的解析器，保证与「模拟按键」动作同一套键名。
    /// </summary>
    public static bool TryParseHotkey(string text, out ushort vk, out ushort[] modifiers, out string error)
    {
        vk = 0;
        modifiers = Array.Empty<ushort>();
        error = "";

        if (!KeyStroke.TryParse(text, out var vks, out error)) return false;
        if (vks.Count < 2)
        {
            error = "热键至少需要一个修饰键 + 一个主键（如 ctrl+f2）";
            return false;
        }

        vk = vks[^1];
        modifiers = vks.Take(vks.Count - 1).ToArray();
        return true;
    }

    /// <summary>把配置里的热键字符串格式化为显示文本：<c>ctrl+f2</c> → <c>Ctrl+F2</c>。</summary>
    public static string FormatHotkey(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("+", parts.Select(p =>
            p.Length == 1 ? p.ToUpperInvariant() : char.ToUpperInvariant(p[0]) + p[1..]));
    }
}
