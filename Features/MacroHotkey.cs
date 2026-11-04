using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 录制期间用 <c>RegisterHotKey</c> 注册的「停止录制」热键，作为低级钩子之外的兜底通道。
/// </summary>
/// <remarks>
/// 为什么不只用钩子：钩子由我们自己解析 VK 与修饰键状态，一旦钩子被系统摘掉、
/// 或修饰键判定出现偏差（左右 Ctrl / 现场状态），热键就会「按了没反应」——录制中尤其致命，
/// 因为录制一旦停不下来，用户只能强退。这里在录制开始 / 结束时注册 / 注销系统热键：
/// 由系统做匹配与吞键，只要进程还活着就能停。
/// 只在**录制期间**注册，所以不会长期抢占别人的 Ctrl+F2。
/// </remarks>
internal static class MacroHotkey
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4C54;          // 'LT'
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static HwndSource? _source;

    /// <summary>开始录制后调用：把当前录制热键注册到系统。失败（被别的程序占用等）不影响钩子路径。</summary>
    public static void Register()
    {
        try
        {
            // 消息窗口必须在启动时建好：本方法可能从钩子回调里调用，不能在这里建窗口。
            if (_source == null) return;
            Unregister();

            ushort vk = MacroSettings.RecordHotkeyVk;
            if (vk == 0) return;

            uint mods = ToModFlags(MacroSettings.RecordHotkeyModifiers) | ModNoRepeat;
            if (!Win32.RegisterHotKey(_source.Handle, HotkeyId, mods, vk))
            {
                // 被占用 / 无权限：钩子仍是主路径，这里只是没有兜底。
                CrashLog.Write("MacroHotkey.Register",
                    new InvalidOperationException("RegisterHotKey 失败，错误码 " + Marshal.GetLastWin32Error()));
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroHotkey.Register", ex);
        }
    }

    /// <summary>停止录制后调用：注销兜底热键，把组合键还给其它程序。</summary>
    public static void Unregister()
    {
        try
        {
            if (_source != null) Win32.UnregisterHotKey(_source.Handle, HotkeyId);
        }
        catch (Exception ex)
        {
            CrashLog.Write("MacroHotkey.Unregister", ex);
        }
    }

    /// <summary>启动时调用（UI 线程）：创建接收 WM_HOTKEY 的消息窗口。</summary>
    public static void Initialize()
    {
        try { EnsureSource(); }
        catch (Exception ex) { CrashLog.Write("MacroHotkey.Initialize", ex); }
    }

    private static void EnsureSource()
    {
        if (_source != null) return;

        // 消息专用窗口（HWND_MESSAGE）：不可见、不抢焦点，只用来收 WM_HOTKEY。
        var p = new HwndSourceParameters("LToolsMacroHotkey")
        {
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            // 刚开录的 300ms 内忽略：如果系统把「开始录制那一下」也派生成 WM_HOTKEY，
            // 会立刻把刚开的录制停掉。真正要停止时早已超过这个窗口。
            if (MacroRecorder.IsRecording && MacroRecorder.ElapsedMs > 300) MacroRecorder.Stop();
        }
        return IntPtr.Zero;
    }

    private static uint ToModFlags(ushort[] modifiers)
    {
        uint flags = 0;
        foreach (var m in modifiers)
        {
            flags |= m switch
            {
                0x11 or 0xA2 or 0xA3 => ModControl,
                0x10 or 0xA0 or 0xA1 => ModShift,
                0x12 or 0xA4 or 0xA5 => ModAlt,
                0x5B or 0x5C => ModWin,
                _ => 0u,
            };
        }
        return flags;
    }
}
