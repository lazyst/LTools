using System.Runtime.InteropServices;

namespace CapsLockPro.Native;

/// <summary>
/// SendInput 封装（对应 AHK 的 Send/SendInput）。
/// 提供按键 down/up、组合键、文本输入等底层能力，供所有功能模块复用。
/// </summary>
internal static class InputHelper
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeydown = 0x0000;
    private const uint KeyEventKeyup = 0x0002;
    private const uint KeyEventExtended = 0x0001; // 扩展键标志（方向键/Delete/Home 等）
    private const uint KeyEventUnicode = 0x0004;  // KEYEVENTF_UNICODE：直接发 WM_CHAR，绕过输入法

    /// <summary>低级键盘输入结构（与 WH_KEYBOARD_LL 回调里的 KBDLLHOOKSTRUCT 一致）。</summary>
    /// <summary>x64 INPUT 布局（共 40 字节）：type@0，键盘 union@8（vk@8,scan@10,flags@12,time@16,extra@24）。</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct Input
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(8)] public ushort Vk;     // KEYBDINPUT.wVk（union 起始偏移 8）
        [FieldOffset(10)] public ushort Scan;  // wScan
        [FieldOffset(12)] public uint Flags;   // dwFlags
        [FieldOffset(16)] public uint Time;    // time
        [FieldOffset(24)] public IntPtr ExtraInfo; // dwExtraInfo
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern ushort VkKeyScanW(char ch);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    private const uint MapvkVkToScancode = 0;

    /// <summary>用扫描码发送一次按键 down+up（绕过键盘布局，更可靠）。<b>单次</b> SendInput。</summary>
    public static void Tap(ushort vk)
    {
        bool ext = IsExtendedKey(vk);
        var buf = new Input[2];
        buf[0] = NewInput(vk, KeyEventKeydown | (ext ? KeyEventExtended : 0));
        buf[1] = NewInput(vk, KeyEventKeyup | (ext ? KeyEventExtended : 0));
        SendInput(2, buf, Marshal.SizeOf<Input>());
    }

    /// <summary>按下虚拟键。</summary>
    public static void Down(ushort vk)
    {
        SendOne(vk, down: true);
    }

    /// <summary>释放虚拟键。</summary>
    public static void Up(ushort vk)
    {
        SendOne(vk, down: false);
    }

    /// <summary>发送和弦组合键（如 Ctrl+C、Ctrl+Shift+End）：依次 down 各键，再逆序 up。
    /// <b>单次</b> SendInput 发送全部事件，避免多次独立调用间修饰键状态交错（对应 AHK 的和弦 Send）。</summary>
    public static void Combo(params ushort[] vks)
    {
        if (vks.Length == 0) return;
        var buf = new Input[vks.Length * 2];
        for (int i = 0; i < vks.Length; i++)
            buf[i] = NewInput(vks[i], DownFlags(vks[i]));
        for (int i = 0; i < vks.Length; i++)
            buf[vks.Length + i] = NewInput(vks[vks.Length - 1 - i], UpFlags(vks[vks.Length - 1 - i]));
        SendInput((uint)buf.Length, buf, Marshal.SizeOf<Input>());
    }

    /// <summary>
    /// 修饰键保持下，依次发送一串按键（每个 key 完整 down+up），再释放修饰键。
    /// <b>单次</b> SendInput 发送全部事件。对应 AHK 的 <c>Send("+{End}+{Right}")</c>
    /// （Shift 全程保持，End 与 Right 为连续两次完整击键）。比两次独立 Combo 更稳定——
    /// 不会在两次击键间释放/重按修饰键，避免跨进程+LL 钩子环境下修饰键状态不一致导致选区范围漂移。
    /// </summary>
    public static void ModifiedSequence(ushort modVk, params ushort[] keyVks)
    {
        if (keyVks.Length == 0) return;
        var buf = new Input[2 + keyVks.Length * 2];
        int p = 0;
        buf[p++] = NewInput(modVk, DownFlags(modVk));
        foreach (var vk in keyVks)
        {
            buf[p++] = NewInput(vk, DownFlags(vk));
            buf[p++] = NewInput(vk, UpFlags(vk));
        }
        buf[p] = NewInput(modVk, UpFlags(modVk));
        SendInput((uint)buf.Length, buf, Marshal.SizeOf<Input>());
    }

    private static uint DownFlags(ushort vk) => KeyEventKeydown | (IsExtendedKey(vk) ? KeyEventExtended : 0);
    private static uint UpFlags(ushort vk) => KeyEventKeyup | (IsExtendedKey(vk) ? KeyEventExtended : 0);

    /// <summary>
    /// 释放当前物理按下的干扰修饰键（Alt/Ctrl/Shift），注入组合键，再恢复被释放的修饰键。
    /// 全部在<b>单次</b> SendInput 批内完成。对应 AHK <c>Send</c> 临时释放未被发送串包含的物理修饰键、
    /// 之后恢复的行为。用于 CapsLock+Alt 分支跳页方法：用户物理按住 Alt，若不释放，编辑器会收到
    /// Ctrl+Alt+Home 等不被识别为"跳到文件头尾"的组合。无 Alt 修饰的路径（Ctrl+C/V/X、普通 Home/End）
    /// 不要用此方法。<paramref name="tapAfter"/> 非 0 时在组合键后追加一次完整击键（用于删到文件头尾的 {Delete}）。
    /// </summary>
    public static void ComboReleasingHeldModifiers(params ushort[] vks)
        => ComboThenTapReleasingHeldModifiers(vks, tapAfter: 0);

    /// <summary>
    /// 同 <see cref="ComboReleasingHeldModifiers(ushort[])"/>，但在组合键后追加一次 <paramref name="tapAfter"/> 完整击键
    /// （如删到文件头尾：Ctrl+Shift+Home 后接 Delete），整组在单次 SendInput 批内发送，Alt 全程被释放。
    /// </summary>
    public static void ComboThenTapReleasingHeldModifiers(ushort[] comboVks, ushort tapAfter)
    {
        if (comboVks.Length == 0) return;
        var held = HeldModifiers();
        bool hasTap = tapAfter != 0;
        int tapCount = hasTap ? 2 : 0;
        var buf = new Input[held.Count + comboVks.Length * 2 + tapCount + held.Count];
        int p = 0;
        foreach (var vk in held) buf[p++] = NewInput(vk, UpFlags(vk));
        for (int i = 0; i < comboVks.Length; i++)
            buf[p++] = NewInput(comboVks[i], DownFlags(comboVks[i]));
        for (int i = 0; i < comboVks.Length; i++)
            buf[p++] = NewInput(comboVks[comboVks.Length - 1 - i], UpFlags(comboVks[comboVks.Length - 1 - i]));
        if (hasTap)
        {
            buf[p++] = NewInput(tapAfter, DownFlags(tapAfter));
            buf[p++] = NewInput(tapAfter, UpFlags(tapAfter));
        }
        foreach (var vk in held) buf[p++] = NewInput(vk, DownFlags(vk));
        SendInput((uint)buf.Length, buf, Marshal.SizeOf<Input>());
    }

    /// <summary>检测当前物理按下的左右 Alt/Ctrl/Shift（Win 一般不涉及，不在此处理），返回需临时释放的 VK 列表。</summary>
    private static List<ushort> HeldModifiers()
    {
        var list = new List<ushort>(6);
        if (IsDown(Win32.VkLshift)) list.Add((ushort)Win32.VkLshift);
        if (IsDown(Win32.VkRshift)) list.Add((ushort)Win32.VkRshift);
        if (IsDown(Win32.VkLcontrol)) list.Add((ushort)Win32.VkLcontrol);
        if (IsDown(Win32.VkRcontrol)) list.Add((ushort)Win32.VkRcontrol);
        if (IsDown(Win32.VkLmenu)) list.Add((ushort)Win32.VkLmenu);
        if (IsDown(Win32.VkRmenu)) list.Add((ushort)Win32.VkRmenu);
        return list;
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>发送单字符（用 VkKeyScanW 取虚拟键 + shift 状态，模拟键盘输入）。</summary>
    public static void SendChar(char ch)
    {
        var vks = VkKeyScanW(ch);
        var vk = (ushort)(vks & 0xFF);
        var shift = (vks >> 8) != 0;
        if (shift) Down(0xA0); // VK_LSHIFT
        Tap(vk);
        if (shift) Up(0xA0);
    }

    /// <summary>
    /// 字符能否用键入方式发送（<c>VkKeyScanW</c> 对当前键盘布局可映射）。
    /// 中文 / emoji / 布局外字符返回 false——键入式只能打可映射字符：
    /// <c>type</c> 模式遇 false 报错（§12），<c>auto</c> 模式改走 Unicode 注入。
    /// </summary>
    public static bool CanType(char ch) => (short)VkKeyScanW(ch) != -1;

    /// <summary>发送文本字符串（逐字符 SendChar，对应 AHK SendText）。</summary>
    public static void SendText(string text)
    {
        foreach (var ch in text) SendChar(ch);
    }

    /// <summary>
    /// 用 <c>KEYEVENTF_UNICODE</c> 批量注入文本：直接生成 <c>WM_CHAR</c>，
    /// <b>绕过键盘布局与输入法</b>（输入法只拦截 <c>WM_KEYDOWN</c>→<c>WM_CHAR</c> 的转换，
    /// 直接发 <c>WM_CHAR</c> 跳过该步骤）——中文/英文/emoji 均可发送，<b>不碰剪贴板</b>。
    /// <c>\n</c> 转 Enter、<c>\t</c> 转 Tab（VK 按键，应用解释更可靠）。
    /// 对应 sendText 的 auto 模式（§3.2.2），解决「纯 ASCII 键入受输入法干扰」。
    /// </summary>
    /// <remarks>
    /// 批量构造 INPUT 数组分批发送（每批 500 INPUT ≈ 250 字符），避免单次 SendInput 数组过大。
    /// </remarks>
    public static void SendUnicodeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        // \r\n 归一为 \n
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var batch = new List<Input>(500);
        foreach (var ch in normalized)
        {
            if (ch == '\n')
            {
                batch.Add(NewInput((ushort)Win32.VkReturn, DownFlags((ushort)Win32.VkReturn)));
                batch.Add(NewInput((ushort)Win32.VkReturn, UpFlags((ushort)Win32.VkReturn)));
            }
            else if (ch == '\t')
            {
                batch.Add(NewInput((ushort)Win32.VkTab, DownFlags((ushort)Win32.VkTab)));
                batch.Add(NewInput((ushort)Win32.VkTab, UpFlags((ushort)Win32.VkTab)));
            }
            else
            {
                batch.Add(NewUnicodeInput(ch, KeyEventKeydown | KeyEventUnicode));
                batch.Add(NewUnicodeInput(ch, KeyEventKeyup | KeyEventUnicode));
            }

            if (batch.Count >= 500)
            {
                SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<Input>());
                batch.Clear();
            }
        }
        if (batch.Count > 0)
            SendInput((uint)batch.Count, batch.ToArray(), Marshal.SizeOf<Input>());
    }

    private static Input NewUnicodeInput(char ch, uint flags) => new()
    {
        Type = (int)InputKeyboard,
        Vk = 0,
        Scan = (ushort)ch,   // Unicode 码点放 wScan（KEYEVENTF_UNICODE 时读此字段）
        Flags = flags,
        Time = 0,
        ExtraInfo = IntPtr.Zero,
    };

    private static void SendOne(ushort vk, bool down)
    {
        uint flags = (down ? KeyEventKeydown : KeyEventKeyup);
        if (IsExtendedKey(vk)) flags |= KeyEventExtended;
        var input = NewInput(vk, flags);
        var buf = new[] { input };
        SendInput(1, buf, Marshal.SizeOf<Input>());
    }

    private static Input NewInput(ushort vk, uint flags) => new()
    {
        Type = (int)InputKeyboard,
        Vk = vk,
        // 填充扫描码（与 AHK SendInput 一致）：DirectInput/RawInput 类游戏读扫描码，
        // scan=0 的事件会被忽略（原实现恒为 0，导致游戏里 CapsLock 单点击发的 Esc 不生效）。
        Scan = (ushort)MapVirtualKey(vk, MapvkVkToScancode),
        Flags = flags,
        Time = 0,
        ExtraInfo = IntPtr.Zero,
    };

    /// <summary>扩展键判定（发送时需 KEYEVENTF_EXTENDEDKEY）。</summary>
    private static bool IsExtendedKey(ushort vk) => vk switch
    {
        0x21 => true,  // VK_PRIOR  (PageUp)
        0x22 => true,  // VK_NEXT   (PageDown)
        0x23 => true,  // VK_END
        0x24 => true,  // VK_HOME
        0x25 => true,  // VK_LEFT
        0x26 => true,  // VK_UP
        0x27 => true,  // VK_RIGHT
        0x28 => true,  // VK_DOWN
        0x2D => true,  // VK_INSERT
        0x2E => true,  // VK_DELETE
        0x5B => true,  // VK_LWIN
        0x5C => true,  // VK_RWIN
        0x6F => true,  // VK_DIVIDE (numpad /)
        0xAD => true,  // VK_VOLUME_MUTE
        0xAE => true,  // VK_VOLUME_DOWN
        0xAF => true,  // VK_VOLUME_UP
        _ => false,
    };
}
