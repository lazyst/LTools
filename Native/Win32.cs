using System.Runtime.InteropServices;

namespace LTools.Native;

/// <summary>
/// 常用 Win32 P/Invoke 声明：键盘/鼠标状态查询、光标、窗口、模块、钩子结构。
/// 集中放置以避免各模块重复声明。对应原 AHK 的 GetKeyState/GetCursorPos 等。
/// </summary>
internal static class Win32
{
    // —— 键盘/鼠标状态 ——
    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern short GetKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out Point lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>GetAncestor 取根拥有者窗口（跟随 owner 链到顶层，对应 AHK MouseGetPos 的顶层窗口）。</summary>
    public const uint GaRootOwner = 3;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool GetGUIThreadInfo(uint idThread, ref Guithreadinfo lpgui);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    // —— 钩子常量 ——
    public const int WhKeyboardLl = 13;
    public const int WhMouseLl = 14;
    public const int HcAction = 0;
    public const int WmKeydown = 0x0100;
    public const int WmKeyup = 0x0101;
    public const int WmSyskeydown = 0x0104;
    public const int WmSyskeyup = 0x0105;

    // —— 常用虚拟键码 ——
    public const int VkCapital = 0x14;   // CapsLock
    public const int VkLshift = 0xA0;
    public const int VkRshift = 0xA1;
    public const int VkLcontrol = 0xA2;
    public const int VkRcontrol = 0xA3;
    public const int VkLmenu = 0xA4;     // Alt
    public const int VkRmenu = 0xA5;
    public const int VkLwin = 0x5B;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkReturn = 0x0D;
    public const int VkTab = 0x09;
    public const int VkBack = 0x08;
    public const int VkDelete = 0x2E;
    public const int VkVolumeUp = 0xAF;
    public const int VkVolumeDown = 0xAE;

    // —— 方向/导航键 ——
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;
    public const int VkHome = 0x24;
    public const int VkEnd = 0x23;
    public const int VkF2 = 0x71;   // 重命名键（资源管理器选中文件 F2）

    // —— OEM 符号键（CapsLock+ 符号动作用）——
    public const int VkOem1 = 0xBA;     // ;:
    public const int VkOemPlus = 0xBB;  // =+
    public const int VkOemComma = 0xBC;
    public const int VkOemMinus = 0xBD;
    public const int VkOemPeriod = 0xBE;
    public const int VkOem2 = 0xBF;      // /?
    public const int VkOem4 = 0xDB;      // [{
    public const int VkOem5 = 0xDC;      // \|
    public const int VkOem6 = 0xDD;      // ]}
    public const int VkOem7 = 0xDE;      // '"

    // —— 结构 ——
    [StructLayout(LayoutKind.Sequential)]
    public struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    /// <summary>
    /// GUITHREADINFO（x64 布局，共 72 字节）。
    /// 字段偏移与原 AHK GetCaretPosEx 的 NumGet 偏移一致：
    /// hwndFocus=16, hwndCaret=48, rcCaret=56。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Guithreadinfo
    {
        public int cbSize;          // 0
        public uint flags;          // 4
        public IntPtr hwndActive;   // 8
        public IntPtr hwndFocus;    // 16
        public IntPtr hwndCapture;  // 24
        public IntPtr hwndMenuOwner;// 32
        public IntPtr hwndMoveSize; // 40
        public IntPtr hwndCaret;    // 48
        public Rect rcCaret;        // 56
    }

    /// <summary>低级键盘钩子事件数据（WH_KEYBOARD_LL）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Kbdllhookstruct
    {
        public uint Vk;        // vkCode，偏移 0（DWORD）
        public uint Scan;      // scanCode，偏移 4（DWORD）
        public uint Flags;     // flags，偏移 8（DWORD）
        public uint Time;      // 偏移 12
        public IntPtr ExtraInfo; // 偏移 16（x64）
    }

    /// <summary>低级鼠标钩子事件数据（WH_MOUSE_LL）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Msllhookstruct
    {
        public Point Pt;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // KBDLLHOOKSTRUCT.Flags 位
    public const uint LlkhfInjected = 0x10;

    // MSLLHOOKSTRUCT.Flags 位（注意与键盘不同：LLMHF_INJECTED=0x01，非 0x10）
    public const uint LlmhfInjected = 0x01;

    // 鼠标消息
    public const int WmLbuttondown = 0x0201;
    public const int WmRbuttondown = 0x0204;
    public const int WmRbuttonup = 0x0205;
    public const int WmMousewheel = 0x020A;

    // mouse_event 标志
    public const uint MouseeventfLeftdown = 0x0002;
    public const uint MouseeventfLeftup = 0x0004;
    public const uint MouseeventfRightdown = 0x0008;
    public const uint MouseeventfRightup = 0x0010;
    public const uint MouseeventfWheel = 0x0800;
    public const uint MouseeventfHwheel = 0x01000;
    public const uint WheelDelta = 120;

    // ShowWindow 命令
    public const int SwMinimize = 6;
    public const uint WmClose = 0x0010;
}
