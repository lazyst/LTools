using System;

namespace LTools.Native;

/// <summary>此刻按着的修饰键（轮询 <c>GetAsyncKeyState</c> 得到的快照）。</summary>
[Flags]
internal enum KeyMods
{
    None = 0,
    Ctrl = 1,
    Shift = 2,
    Alt = 4,
    Win = 8,
}

/// <summary>
/// 「这个键此刻按着吗」「现在按着哪些修饰键」的唯一实现。
/// </summary>
/// <remarks>
/// 快捷键匹配、等待按键、宏回放的门禁、录屏特效、录屏 / 粘贴条的让路判断都靠轮询
/// <c>GetAsyncKeyState</c>——**别在各处再写一份 <c>(Win32.GetAsyncKeyState(vk) &amp; 0x8000) != 0</c>**。
/// 修饰键一律「左 / 右 / 通用」三份都查：只按了 VK_LCONTROL 时通用 VK_CONTROL 也会置位，
/// 反过来某些注入 / 远程会话只置通用位——两边都算才不会判成「没按 Ctrl」。
/// </remarks>
internal static class KeyState
{
    /// <summary>这个键此刻按着。</summary>
    public static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>此刻按着的修饰键。</summary>
    public static KeyMods Modifiers()
    {
        KeyMods mods = KeyMods.None;
        if (IsDown(0xA2) || IsDown(0xA3) || IsDown(0x11)) mods |= KeyMods.Ctrl;
        if (IsDown(0xA0) || IsDown(0xA1) || IsDown(0x10)) mods |= KeyMods.Shift;
        if (IsDown(0xA4) || IsDown(0xA5) || IsDown(0x12)) mods |= KeyMods.Alt;
        if (IsDown(0x5B) || IsDown(0x5C)) mods |= KeyMods.Win;
        return mods;
    }
}
