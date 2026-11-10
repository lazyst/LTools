using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace LTools.Features;

/// <summary>
/// 按键 stroke 解析与格式化（§3.2.1）。
/// stroke = 修饰键组 + 主键，形如 "ctrl+shift+k"、"alt+p"、"s"、"f5"。
/// 修饰键别名：ctrl/control、shift、alt/menu、win/meta（不区分大小写）。
/// 主键：单字符（a-z/0-9/符号）、功能键名（f1..f24、enter/return、tab、esc/escape、space、
/// backspace/back、delete/del、insert/ins、home、end、pageup/pgup、pagedown/pgdn、
/// up/down/left/right、apps、pause、printscreen/prtsc）。
/// </summary>
/// <remarks>
/// 解析与录制互为逆：录制时 <see cref="KeyInterop.VirtualKeyFromKey"/> 得 VK、
/// <see cref="Keyboard.Modifiers"/> 得修饰键 → <see cref="Format"/>；执行时
/// <see cref="TryParse"/> 还原为 VK 列表 → <see cref="InputHelper.Combo"/>。
/// </remarks>
internal static class KeyStroke
{
    // —— 修饰键名 → VK ——（用左右通用键，Combo 时等效）
    private static readonly Dictionary<string, ushort> Modifiers = new()
    {
        ["ctrl"] = 0xA2, ["control"] = 0xA2,  // VK_LCONTROL
        ["shift"] = 0xA0,                       // VK_LSHIFT
        ["alt"] = 0xA4, ["menu"] = 0xA4,       // VK_LMENU
        ["win"] = 0x5B, ["meta"] = 0x5B,      // VK_LWIN
    };

    // —— 主键名 → VK ——
    private static readonly Dictionary<string, ushort> NamedKeys = new()
    {
        ["enter"] = 0x0D, ["return"] = 0x0D,
        ["tab"] = 0x09,
        ["esc"] = 0x1B, ["escape"] = 0x1B,
        ["space"] = 0x20, ["sp"] = 0x20,
        ["backspace"] = 0x08, ["back"] = 0x08, ["bs"] = 0x08,
        ["delete"] = 0x2E, ["del"] = 0x2E,
        ["insert"] = 0x2D, ["ins"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21, ["pgup"] = 0x21, ["prior"] = 0x21,
        ["pagedown"] = 0x22, ["pgdn"] = 0x22, ["next"] = 0x22,
        ["up"] = 0x26,
        ["down"] = 0x28,
        ["left"] = 0x25,
        ["right"] = 0x27,
        // —— 小键盘（NumLock 打开时才是这些 VK；关掉时系统给的是方向 / Home…）——
        ["numpad0"] = 0x60, ["num0"] = 0x60,
        ["numpad1"] = 0x61, ["num1"] = 0x61,
        ["numpad2"] = 0x62, ["num2"] = 0x62,
        ["numpad3"] = 0x63, ["num3"] = 0x63,
        ["numpad4"] = 0x64, ["num4"] = 0x64,
        ["numpad5"] = 0x65, ["num5"] = 0x65,
        ["numpad6"] = 0x66, ["num6"] = 0x66,
        ["numpad7"] = 0x67, ["num7"] = 0x67,
        ["numpad8"] = 0x68, ["num8"] = 0x68,
        ["numpad9"] = 0x69, ["num9"] = 0x69,
        ["numpadmul"] = 0x6A, ["numpadmultiply"] = 0x6A,
        ["numpadadd"] = 0x6B, ["numpadplus"] = 0x6B,
        ["numpadsub"] = 0x6D, ["numpadminus"] = 0x6D,
        ["numpaddot"] = 0x6E, ["numpaddecimal"] = 0x6E,
        ["numpaddiv"] = 0x6F, ["numpaddivide"] = 0x6F,
        ["apps"] = 0x5D,   // 右键菜单键
        ["pause"] = 0x13,
        ["printscreen"] = 0x2A, ["prtsc"] = 0x2A, ["snapshot"] = 0x2A,
        ["scrolllock"] = 0x91, ["scroll"] = 0x91,
        ["numlock"] = 0x90,
        ["capslock"] = 0x14, ["caps"] = 0x14,
        // —— OEM 符号（US 布局固定 VK）——
        ["minus"] = 0xBD, ["-"] = 0xBD,
        ["plus"] = 0xBB, ["equals"] = 0xBB, ["="] = 0xBB,
        ["lbracket"] = 0xDB, ["["] = 0xDB,
        ["rbracket"] = 0xDD, ["]"] = 0xDD,
        ["backslash"] = 0xDC, ["\\"] = 0xDC,
        ["semicolon"] = 0xBA, [";"] = 0xBA,
        ["quote"] = 0xDE, ["'"] = 0xDE,
        ["comma"] = 0xBC, [","] = 0xBC,
        ["period"] = 0xBE, ["."] = 0xBE,
        ["slash"] = 0xBF, ["/"] = 0xBF,
        ["backquote"] = 0xC0, ["grave"] = 0xC0, ["`"] = 0xC0,
    };

    /// <summary>
    /// 解析 stroke 字符串为 VK 列表（修饰键在前、主键在末），供 <see cref="InputHelper.Combo"/> 发送。
    /// </summary>
    public static bool TryParse(string stroke, out List<ushort> vks, out string error)
    {
        vks = new List<ushort>();
        error = "";
        if (string.IsNullOrWhiteSpace(stroke)) { error = "空的按键"; return false; }

        var parts = stroke.Split('+');
        if (parts.Length == 0) { error = "空的按键"; return false; }

        for (int i = 0; i < parts.Length; i++)
        {
            var p = parts[i].Trim().ToLowerInvariant();
            if (p.Length == 0) { error = "空的按键段"; return false; }

            bool isLast = i == parts.Length - 1;
            if (!isLast)
            {
                // 中间段必须是修饰键
                if (Modifiers.TryGetValue(p, out var mvk)) vks.Add(mvk);
                else { error = $"\"{p}\" 不是修饰键（可用：ctrl/shift/alt/win）"; return false; }
            }
            else
            {
                // 末段：主键
                if (!TryVk(p, out var kvk)) { error = $"未知按键 \"{p}\""; return false; }
                vks.Add(kvk);
            }
        }
        return true;
    }

    /// <summary>把修饰键状态 + 主键 VK 格式化为 stroke 字符串（录制时用）。</summary>
    public static string Format(ModifierKeys mods, int vk)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("win");
        parts.Add(VkToName((ushort)vk));
        return string.Join("+", parts);
    }

    /// <summary>解析为 VK 数组（便捷封装，失败时 error 为 null 调用方自行处理）。</summary>
    public static ushort[]? TryParseVks(string stroke, out string? error)
    {
        if (TryParse(stroke, out var v, out var e)) { error = null; return v.ToArray(); }
        error = e;
        return null;
    }

    /// <summary>
    /// 单个键名 → VK。与 <see cref="TryParse"/> 的末段同一套写法，另外**允许修饰键本身**
    /// （<c>ctrl</c> / <c>shift</c> / <c>alt</c> / <c>win</c>）——「模拟按键」要能单独按下 / 抬起修饰键。
    /// </summary>
    public static bool TryResolveKey(string? name, out ushort vk)
    {
        string key = (name ?? "").Trim().ToLowerInvariant();
        if (key.Length == 0) { vk = 0; return false; }
        if (Modifiers.TryGetValue(key, out vk)) return true;
        return TryVk(key, out vk);
    }

    private static bool TryVk(string name, out ushort vk)
    {
        // 功能键 F1..F24
        if (name.Length >= 2 && (name[0] == 'f' || name[0] == 'F'))
        {
            if (int.TryParse(name[1..], out int n) && n >= 1 && n <= 24)
            {
                vk = (ushort)(0x6F + n);  // VK_F1 = 0x70
                return true;
            }
        }
        // 单字符：字母 / 数字
        if (name.Length == 1)
        {
            char c = name[0];
            if (c >= 'a' && c <= 'z') { vk = (ushort)(c - 'a' + 'A'); return true; }
            if (c >= 'A' && c <= 'Z') { vk = (ushort)c; return true; }
            if (c >= '0' && c <= '9') { vk = (ushort)c; return true; }
        }
        // 命名键 / 符号
        return NamedKeys.TryGetValue(name, out vk);
    }

    private static string VkToName(ushort vk)
    {
        // 功能键
        if (vk >= 0x70 && vk <= 0x87) return "f" + (vk - 0x6F);
        // 字母 / 数字
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString().ToLowerInvariant();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        // 小键盘（与 TryParse / NamedKeys 的写法一致）
        if (vk >= 0x60 && vk <= 0x69) return "numpad" + (vk - 0x60);
        if (vk == 0x6A) return "numpadmul";
        if (vk == 0x6B) return "numpadadd";
        if (vk == 0x6D) return "numpadsub";
        if (vk == 0x6E) return "numpaddot";
        if (vk == 0x6F) return "numpaddiv";
        // 反查命名表（取第一个别名）
        foreach (var kv in NamedKeys)
            if (kv.Value == vk) return kv.Key;
        return vk.ToString();
    }
}
