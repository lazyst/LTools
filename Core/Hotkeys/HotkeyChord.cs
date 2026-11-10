using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Features;

namespace LTools.Core;

/// <summary>
/// 一条全局快捷键的解析结果：主键 + 修饰键 + 是否带 CapsLock 前缀。
/// </summary>
/// <remarks>
/// <para>
/// 写法与 <see cref="KeyStroke"/> 一致，只是额外允许 <c>capslock</c>（别名 <c>caps</c>）作为修饰键：
/// <c>capslock+h</c>、<c>capslock+ctrl+h</c>、<c>ctrl+alt+k</c>、<c>f9</c>。
/// <c>RegisterHotKey</c> 不支持 CapsLock 修饰键，所以匹配统一在低级键盘钩子里做。
/// </para>
/// <para>
/// 修饰键顺序固定（capslock → ctrl → shift → alt → win），写盘一律用 canonical 小写形式，
/// 同一组合键不会出现多种写法。
/// </para>
/// </remarks>
internal readonly struct HotkeyChord
{
    public HotkeyChord(ushort vk, ushort[] modifiers, bool capsLock)
    {
        Vk = vk;
        Modifiers = modifiers;
        CapsLock = capsLock;
    }

    /// <summary>主键虚拟键码。</summary>
    public ushort Vk { get; }

    /// <summary>Ctrl / Shift / Alt / Win 的 VK（用左右通用值，匹配时按「任一侧按下」算）。</summary>
    public ushort[] Modifiers { get; }

    /// <summary>是否要求按住 CapsLock。</summary>
    public bool CapsLock { get; }

    public bool HasCtrl => Has(0xA2);
    public bool HasShift => Has(0xA0);
    public bool HasAlt => Has(0xA4);
    public bool HasWin => Has(0x5B);

    /// <summary>
    /// 是不是「单键」（没有任何修饰键，也没有 CapsLock 前缀）。单键会在所有程序里吞掉那个键，
    /// 录入 / 保存时要提示一次风险。
    /// </summary>
    public bool IsBare => Modifiers.Length == 0 && !CapsLock;

    private bool Has(ushort vk) => Modifiers.Contains(vk);

    /// <summary>
    /// 解析快捷键字符串。允许单键（<c>f9</c>、<c>a</c>、<c>space</c> 都行）——单键会在所有程序里
    /// **吞掉**那个键，所以界面上录入单键时会提示一次风险，但不再拒绝。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyChord chord, out string error)
    {
        chord = default;
        error = "";

        string[] parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "快捷键不能为空";
            return false;
        }

        bool caps = false;
        var modifiers = new List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "capslock":
                case "caps":
                    if (caps) { error = "CapsLock 重复了"; return false; }
                    caps = true;
                    break;
                case "ctrl":
                case "control":
                    Add(0xA2);
                    break;
                case "shift":
                    Add(0xA0);
                    break;
                case "alt":
                case "menu":
                    Add(0xA4);
                    break;
                case "win":
                case "meta":
                    Add(0x5B);
                    break;
                default:
                    error = $"\"{parts[i]}\" 不是修饰键（可用：capslock / ctrl / shift / alt / win）";
                    return false;
            }
        }

        // 末段是主键：交给 KeyStroke 的同一套键名（f1..f24 / left / home / a-z / 符号…）
        if (!KeyStroke.TryParse(parts[^1], out var vks, out error) || vks.Count != 1)
        {
            if (error.Length == 0) error = "主键只能有一个";
            return false;
        }

        ushort vk = vks[0];
        chord = new HotkeyChord(vk, modifiers.ToArray(), caps);
        return true;

        void Add(ushort vk)
        {
            if (!modifiers.Contains(vk)) modifiers.Add(vk);
        }
    }

    /// <summary>解析为可读文本：<c>CapsLock+Ctrl+H</c>。</summary>
    public string Display()
    {
        var parts = new List<string>();
        if (CapsLock) parts.Add("CapsLock");
        if (HasCtrl) parts.Add("Ctrl");
        if (HasShift) parts.Add("Shift");
        if (HasAlt) parts.Add("Alt");
        if (HasWin) parts.Add("Win");
        parts.Add(MainKeyName(Vk));
        return string.Join("+", parts);
    }

    /// <summary>canonical 小写写法（写配置用）；解析不了时返回输入原样。</summary>
    public string ToText()
    {
        var parts = new List<string>();
        if (CapsLock) parts.Add("capslock");
        if (HasCtrl) parts.Add("ctrl");
        if (HasShift) parts.Add("shift");
        if (HasAlt) parts.Add("alt");
        if (HasWin) parts.Add("win");
        parts.Add(KeyStrokeName(Vk));
        return string.Join("+", parts);
    }

    /// <summary>把用户输入收拢成 canonical 小写；解析失败时只做去空格 / 小写，保留原样供界面纠正。</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return TryParse(text, out var chord, out _)
            ? chord.ToText()
            : string.Join("+", text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.ToLowerInvariant()));
    }

    /// <summary>格式化为显示文本；解析失败时原样返回。</summary>
    public static string Format(string? text)
        => TryParse(text, out var chord, out _) ? chord.Display() : (text ?? "");

    private static string MainKeyName(ushort vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "NumPad" + (vk - 0x60),
        0x6A => "NumPad*",
        0x6B => "NumPad+",
        0x6D => "NumPad-",
        0x6E => "NumPad.",
        0x6F => "NumPad/",
        _ => NamedName(vk),
    };

    private static string KeyStrokeName(ushort vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "f" + (vk - 0x6F),
        >= 'A' and <= 'Z' => ((char)vk).ToString().ToLowerInvariant(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        // 规范名不能带 +（会破坏 capslock+numpadadd 的解析），所以用英文单词
        >= 0x60 and <= 0x69 => "numpad" + (vk - 0x60),
        0x6A => "numpadmul",
        0x6B => "numpadadd",
        0x6D => "numpadsub",
        0x6E => "numpaddot",
        0x6F => "numpaddiv",
        _ => NamedName(vk).ToLowerInvariant(),
    };

    private static string NamedName(ushort vk) => vk switch
    {
        0x0D => "Enter",
        0x09 => "Tab",
        0x1B => "Esc",
        0x20 => "Space",
        0x08 => "Backspace",
        0x2E => "Delete",
        0x2D => "Insert",
        0x24 => "Home",
        0x23 => "End",
        0x21 => "PageUp",
        0x22 => "PageDown",
        0x26 => "Up",
        0x28 => "Down",
        0x25 => "Left",
        0x27 => "Right",
        0x13 => "Pause",
        0x2A => "PrintScreen",
        0x91 => "ScrollLock",
        0x90 => "NumLock",
        0xBD => "-",
        0xBB => "=",
        0xDB => "[",
        0xDD => "]",
        0xDC => "\\",
        0xBA => ";",
        0xDE => "'",
        0xBC => ",",
        0xBE => ".",
        0xBF => "/",
        0xC0 => "`",
        0x5D => "Apps",
        _ => "0x" + vk.ToString("X2"),
    };
}
