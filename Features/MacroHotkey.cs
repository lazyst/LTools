using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using LTools.Core;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 宏相关的系统热键：录制期间注册「停止录制」热键，回放期间注册「停止回放」热键
/// （两者都可在宏动作编辑面板配置），作为低级钩子之外的兜底通道。
/// </summary>
/// <remarks>
/// 为什么不只用钩子：钩子由我们自己解析 VK 与修饰键状态，一旦钩子被系统摘掉、
/// 或修饰键判定出现偏差（左右 Ctrl / 现场状态），热键就会「按了没反应」——录制中尤其致命，
/// 因为录制一旦停不下来，用户只能强退。这里在录制开始 / 结束时注册 / 注销系统热键：
/// 由系统做匹配与吞键，只要进程还活着就能停。
/// 只在**录制 / 回放期间**注册，结束立即注销，所以不会长期抢占别人的组合键。
/// </remarks>
internal static class MacroHotkey
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0x4C54;          // 'LT'
    private const int HotkeyIdPlayback = 0x4C55;  // 'LU'：停止宏回放
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private static volatile HwndSource? _source;

    // 停止回放热键的引用计数：自动化流程注册后，流程内的宏步骤也会注册 / 注销，
    // 简单「后注册者注销」会把流程剩余部分的停止出口提前拆掉。
    private static readonly object _playbackStopLock = new();
    private static int _playbackStopRefs;
    private static bool _playbackStopRegistered;

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

    /// <summary>
    /// 回放开始前注册「停止回放」热键（宏面板可配置，默认 Ctrl+F3）。只在回放期间占用，结束立即释放。
    /// </summary>
    /// <returns>注册是否成功；失败（被其他程序占用）时回放仍可依赖键盘钩子停止，不阻止回放启动。</returns>
    public static bool RegisterPlaybackStop()
    {
        lock (_playbackStopLock)
        {
            _playbackStopRefs++;
            if (_playbackStopRefs > 1) return _playbackStopRegistered;

            try
            {
                if (_source == null) return false;
                ushort vk = MacroSettings.PlaybackStopVk;
                if (vk == 0) return false;
                uint mods = ToModFlags(MacroSettings.PlaybackStopModifiers) | ModNoRepeat;
                // 被其他程序占用时返回 false 即可：调用方会依赖键盘钩子停止，不写崩溃日志。
                _playbackStopRegistered = Win32.RegisterHotKey(_source.Handle, HotkeyIdPlayback, mods, vk);
                return _playbackStopRegistered;
            }
            catch (Exception ex)
            {
                CrashLog.Write("MacroHotkey.RegisterPlaybackStop", ex);
                return false;
            }
        }
    }

    /// <summary>回放 / 流程结束（正常 / 停止 / 异常）后释放停止热键；按引用计数，最后一个使用者负责注销。</summary>
    public static void UnregisterPlaybackStop()
    {
        lock (_playbackStopLock)
        {
            if (_playbackStopRefs <= 0) return;
            _playbackStopRefs--;
            if (_playbackStopRefs > 0) return;

            try
            {
                if (_source != null && _playbackStopRegistered)
                    Win32.UnregisterHotKey(_source.Handle, HotkeyIdPlayback);
            }
            catch (Exception ex)
            {
                CrashLog.Write("MacroHotkey.UnregisterPlaybackStop", ex);
            }
            finally
            {
                _playbackStopRegistered = false;
            }
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
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyIdPlayback)
        {
            handled = true;
            // 流程内的宏停止热键由流程接管：停止整个流程（并连带停止正在回放的宏）。
            if (AutomationRunner.IsRunning) AutomationRunner.Stop();
            else if (MacroPlayer.IsPlaying) MacroPlayer.Stop();
            return IntPtr.Zero;
        }

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
