using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Config;
using LTools.Features;
using LTools.Native;

namespace LTools.Core;

/// <summary>一条可命中的绑定：预设组 + 绑定 + 解析后的组合键。</summary>
internal sealed record HotkeyEntry(HotkeyGroup Group, HotkeyBinding Binding, HotkeyChord Chord);

/// <summary>
/// 运行期热键表：启动 / 保存后从 <see cref="HotkeyFile"/> 重建，钩子回调只做数组遍历与字符串比较。
/// </summary>
/// <remarks>
/// <para>
/// 顺序 = 「组顺序 → 组内顺序」，同一条快捷键出现在多个启用组里时取第一条（保存时界面会标红）。
/// </para>
/// <para>
/// 修饰键是**精确匹配**：<c>ctrl+f2</c> 不会被 <c>ctrl+shift+f2</c> 命中，反之亦然。
/// 带 <c>capslock</c> 前缀的绑定要求「CapsLock 键功能」开着（否则它是原生大小写键）。
/// </para>
/// </remarks>
internal static class HotkeyRegistry
{
    private static HotkeyFile _file = new();
    private static HotkeyEntry[] _entries = Array.Empty<HotkeyEntry>();
    private static int _revision;

    /// <summary>
    /// 每次 <see cref="Load(LTools.Config.HotkeyFile)"/> 自增。界面（<c>HotkeysView</c>）用它做
    /// 「运行期热键表换没换」的廉价判定，避免每次切到「快捷键」页都整表重建拖住 UI 线程。
    /// 读的是纯 int，钩子路径不增加任何开销。
    /// </summary>
    public static int Revision => System.Threading.Volatile.Read(ref _revision);

    /// <summary>本次按下已经触发过的键：按住不放时系统会重复 keydown，不能连发。</summary>
    private static readonly HashSet<ushort> Fired = new();

    /// <summary>当前生效的配置（界面直接改它再 <see cref="Rebuild"/>）。</summary>
    public static HotkeyFile File => _file;

    /// <summary>热键录入期间为 true：钩子跳过匹配，否则刚录的组合键会立刻触发动作。</summary>
    public static bool Suspended { get; set; }

    /// <summary>可命中的绑定（按优先级排序）。</summary>
    public static IReadOnlyList<HotkeyEntry> Entries => _entries;

    public static void Load(AppConfig cfg) => Load(cfg.Hotkeys ?? new HotkeyFile());

    public static void Load(HotkeyFile file)
    {
        System.Threading.Interlocked.Increment(ref _revision);
        _file = file ?? new HotkeyFile();
        _file.Normalize();
        Rebuild();
    }

    /// <summary>按当前 <see cref="File"/> 重建匹配表（保存配置后调用）。</summary>
    public static void Rebuild()
    {
        var list = new List<HotkeyEntry>();
        foreach (var group in _file.Groups)
        {
            if (!group.Enabled) continue;
            foreach (var binding in group.Bindings)
            {
                if (!binding.Enabled) continue;
                if (binding.ActionId.Length == 0) continue;
                if (!HotkeyChord.TryParse(binding.Chord, out var chord, out _)) continue;
                list.Add(new HotkeyEntry(group, binding, chord));
            }
        }
        _entries = list.ToArray();
        Fired.Clear();
    }

    /// <summary>
    /// 键盘钩子入口（keydown）：命中且名单放行时返回对应动作 Id；否则 null。
    /// **不吞键**由调用方决定——名单挡掉时要把按键正常交给下游程序。
    /// </summary>
    public static string? TryFire(ushort vk)
    {
        if (Fired.Contains(vk))
        {
            // 物理键已经抬起 = 上一次按下的锁存没被清（keyup 被别的分支提前吞掉 / 没送到钩子）。
            // 就地自愈，否则这条单键会一直「隔一次才触发」。
            if (!Down(vk)) Fired.Remove(vk);
            else return null;
        }

        if (_entries.Length == 0) return null;

        foreach (var entry in _entries)
        {
            if (entry.Chord.Vk != vk) continue;
            if (!ModifiersMatch(entry.Chord)) continue;

            if (!AppFilterMatch.Allows(entry.Binding, _file, ForegroundAppCache.Current)) return null;

            Fired.Add(vk);
            return entry.Binding.ActionId;
        }
        return null;
    }

    /// <summary>按键抬起：清掉「已触发」锁存，下一次按下才能再触发。</summary>
    public static void KeyUp(ushort vk)
    {
        if (Fired.Count > 0) Fired.Remove(vk);
    }

    /// <summary>窗口切换 / 停止动作时清空锁存（防止 keyup 被别的窗口吃掉后卡住）。</summary>
    public static void ClearLatches() => Fired.Clear();

    /// <summary>修饰键是否与这条组合键**完全一致**（多按一个 Ctrl 就不算命中）。</summary>
    private static bool ModifiersMatch(HotkeyChord chord)
    {
        bool ctrl = Down(0xA2) || Down(0xA3) || Down(0x11);
        bool shift = Down(0xA0) || Down(0xA1) || Down(0x10);
        bool alt = Down(0xA4) || Down(0xA5) || Down(0x12);
        bool win = Down(0x5B) || Down(0x5C);
        bool caps = AppState.CapsLockIsDown && AppState.CapsLockModifier;

        return ctrl == chord.HasCtrl
            && shift == chord.HasShift
            && alt == chord.HasAlt
            && win == chord.HasWin
            && caps == chord.CapsLock;
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>
    /// 同一条快捷键在「已启用的组」里出现多次 → 返回这些绑定 Id。
    /// 界面用来标红并提示「运行期只会触发排在前面的那条」。
    /// </summary>
    public static HashSet<string> FindConflicts() => FindConflicts(_file);

    /// <summary>同一条快捷键在「已启用的组」里出现多次 → 返回这些绑定 Id（界面标红用）。</summary>
    public static HashSet<string> FindConflicts(HotkeyFile file)
    {
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in file.Groups)
        {
            if (!group.Enabled) continue;
            foreach (var binding in group.Bindings)
            {
                if (!binding.Enabled || binding.Chord.Length == 0) continue;
                if (seen.TryGetValue(binding.Chord, out string? first))
                {
                    conflicts.Add(binding.Id);
                    conflicts.Add(first);
                }
                else seen[binding.Chord] = binding.Id;
            }
        }
        return conflicts;
    }
}
