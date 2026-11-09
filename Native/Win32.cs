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
    public static extern int GetSystemMetrics(int nIndex);

    // GetSystemMetrics 索引（虚拟桌面 = 所有显示器合并后的矩形，可能含负坐标）
    public const int SmXVirtualscreen = 76;
    public const int SmYVirtualscreen = 77;
    public const int SmCxVirtualscreen = 78;
    public const int SmCyVirtualscreen = 79;

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    /// <summary>显示器有效 DPI（Shcore；Win8.1+）。失败返回非 0。</summary>
    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>MonitorFromPoint：取最近的显示器（坐标不在任何显示器内时也不返回空）。</summary>
    public const uint MonitorDefaultToNearest = 2;

    /// <summary>GetDpiForMonitor 的 MDT_EFFECTIVE_DPI。</summary>
    public const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;   // 物理像素
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumCallbackFunction, IntPtr lParam);

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // —— 前台窗口变化事件（热键名单缓存用）——
    public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectLocationChange = 0x800B;
    public const uint WineventOutofcontext = 0x0000;

    /// <summary>在父窗口的直接子窗口里按点查找（按 z 序），用于从顶层窗口往下钻。</summary>
    [DllImport("user32.dll")]
    public static extern IntPtr ChildWindowFromPointEx(IntPtr hWndParent, Point pt, uint flags);

    public const uint CwpSkipinvisible = 0x0001;
    public const uint CwpSkiptransparent = 0x0004;

    public const uint GaRoot = 2;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GwlExstyle = -20;
    public const long WsExTransparent = 0x00000020;
    public const long WsExToolwindow = 0x00000080;

    /// <summary>点了也不抢焦点（提示弹窗用：自动化正在往别的窗口输入时不能抢前台）。</summary>
    public const long WsExNoactivate = 0x08000000;

    /// <summary>DWM 窗口属性：实际可见边框（不含不可见的阴影边距）。</summary>
    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out Rect pvAttribute, int cbAttribute);

    public const int DwmwaExtendedFrameBounds = 9;

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(Point pt);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    /// <summary>GetAncestor 取根拥有者窗口（跟随 owner 链到顶层，对应 AHK MouseGetPos 的顶层窗口）。</summary>
    public const uint GaRootOwner = 3;

    /// <summary>
    /// 光标下的顶层窗口：<see cref="WindowFromPoint"/> 取最深窗口（含子窗口）再取根祖先。
    /// 长按右键唤起面板时用它当上下文——右键不会激活后台窗口，此刻
    /// <see cref="GetForegroundWindow"/> 可能还停在别的程序上（照 Quicker 的取法）。
    /// 取不到返回 <see cref="IntPtr.Zero"/>。
    /// </summary>
    public static IntPtr TopLevelWindowAtCursor()
    {
        if (!GetCursorPos(out var pt)) return IntPtr.Zero;
        IntPtr hit = WindowFromPoint(pt);
        if (hit == IntPtr.Zero) return IntPtr.Zero;
        IntPtr root = GetAncestor(hit, GaRoot);
        return root != IntPtr.Zero ? root : hit;
    }

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll")]
    public static extern IntPtr SetActiveWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Point lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);

    /// <summary>同步 ShowWindow：窗口状态改完立刻可读（异步版要等消息队列处理）。</summary>
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>窗口是否已最大化。</summary>
    [DllImport("user32.dll")]
    public static extern bool IsZoomed(IntPtr hWnd);

    // SetWindowPos 标志（HWND_TOP = IntPtr.Zero）
    public const uint SwpNosize = 0x0001;
    public const uint SwpNomove = 0x0002;
    public const uint SwpNozorder = 0x0004;
    public const uint SwpNoactivate = 0x0010;
    public const uint SwpShowwindow = 0x0040;

    /// <summary>SetWindowPos 的 HWND_TOPMOST。</summary>
    public static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>发消息并在目标无响应时按超时放弃（不会无限期卡住调用线程）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeout, out IntPtr result);

    /// <summary>SendMessageTimeout 的 SMTO_ABORTIFHUNG。</summary>
    public const uint SmtoAbortIfHung = 0x0002;

    /// <summary>WM_COPY：让窗口把当前选区复制到剪贴板（标准消息，不是按键模拟）。</summary>
    public const uint WmCopy = 0x0301;

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
    public const int VkVolumeMute = 0xAD;

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
    public const uint LlkhfExtended = 0x01;   // 扩展键（右 Ctrl/Alt、小键盘 Enter 等）
    public const uint LlkhfInjected = 0x10;

    // MSLLHOOKSTRUCT.Flags 位（注意与键盘不同：LLMHF_INJECTED=0x01，非 0x10）
    public const uint LlmhfInjected = 0x01;

    // 鼠标消息
    public const int WmMousemove = 0x0200;
    public const int WmLbuttondown = 0x0201;
    public const int WmLbuttonup = 0x0202;
    public const int WmRbuttondown = 0x0204;
    public const int WmRbuttonup = 0x0205;
    public const int WmMbuttondown = 0x0207;
    public const int WmMbuttonup = 0x0208;
    public const int WmMousewheel = 0x020A;
    public const int WmXbuttondown = 0x020B;
    public const int WmXbuttonup = 0x020C;
    public const int WmMousehwheel = 0x020E;

    // mouse_event 标志
    public const uint MouseeventfMove = 0x0001;
    public const uint MouseeventfLeftdown = 0x0002;
    public const uint MouseeventfLeftup = 0x0004;
    public const uint MouseeventfRightdown = 0x0008;
    public const uint MouseeventfRightup = 0x0010;
    public const uint MouseeventfMiddledown = 0x0020;
    public const uint MouseeventfMiddleup = 0x0040;
    public const uint MouseeventfXdown = 0x0080;
    public const uint MouseeventfXup = 0x0100;
    public const uint MouseeventfWheel = 0x0800;
    public const uint MouseeventfHwheel = 0x01000;
    public const uint MouseeventfVirtualdesk = 0x4000;
    public const uint MouseeventfAbsolute = 0x8000;
    public const uint MouseeventfMoveNocoalesce = 0x2000;   // SendInput 专用：不合并相邻移动
    public const uint WheelDelta = 120;

    // XBUTTON（WM_XBUTTONDOWN/UP 的 MouseData 高 16 位）
    public const int Xbutton1 = 0x0001;
    public const int Xbutton2 = 0x0002;

    // ShowWindow 命令
    public const int SwMaximize = 3;
    public const int SwShownoactivate = 4;
    public const int SwMinimize = 6;
    public const int SwRestore = 9;
    public const uint WmClose = 0x0010;
}
