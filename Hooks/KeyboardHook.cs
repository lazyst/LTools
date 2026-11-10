using System.Runtime.InteropServices;
using LTools.Core;
using LTools.Features;
using LTools.Features.Builtin;
using LTools.Native;

namespace LTools.Hooks;

/// <summary>
/// 低级键盘钩子（WH_KEYBOARD_LL）。在主线程安装，由 <see cref="Application.Run"/> 消息循环泵送。
/// </summary>
/// <remarks>
/// 回调返回非零值即吞掉该按键（阻止传递）。设计要点（与原 AHK 一致）：
/// - 忽略 <see cref="Win32.LlkhfInjected"/> 事件（防止自己 SendInput 的键触发递归）；
/// - CapsLock keydown/keyup 进入 <see cref="Core.CapsLockStateMachine"/>；
/// - 其他键在 CapsLock 按住期间分发到各功能模块（vim/剪贴板/符号跳转等）。
/// </remarks>
internal static class KeyboardHook
{
    private static IntPtr _handle = IntPtr.Zero;
    private static Win32.LowLevelKeyboardProc? _proc;
    // 防止委托被 GC 回收（SetWindowsHookEx 只存弱引用）
    private static GCHandle _procHandle;

    /// <summary>安装钩子。必须在主线程调用。</summary>
    public static void Install()
    {
        if (_handle != IntPtr.Zero) return;
        _proc = HookCallback;
        _procHandle = GCHandle.Alloc(_proc);
        var hMod = Win32.GetModuleHandle(null);
        _handle = Win32.SetWindowsHookEx(Win32.WhKeyboardLl, _proc!, hMod, 0);
        if (_handle == IntPtr.Zero)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) 失败");
        }
    }

    /// <summary>卸载钩子。</summary>
    public static void Uninstall()
    {
        if (_handle != IntPtr.Zero)
        {
            Win32.UnhookWindowsHookEx(_handle);
            _handle = IntPtr.Zero;
        }
        if (_procHandle.IsAllocated) _procHandle.Free();
        _proc = null;
    }

    /// <summary>预热回调 JIT：以非动作 nCode 调用一次（JIT 在方法首次调用时编译整个方法体），
    /// 把「首个真实键盘事件触发回调 JIT」的一次性停顿提前到无钩子阶段。
    /// nCode&lt;0 时回调直接放行，无副作用（CallNextHookEx 的 hhk 参数被系统忽略）。</summary>
    public static void WarmUp() => HookCallback(-1, IntPtr.Zero, IntPtr.Zero);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == Win32.HcAction)
        {
            // 钩子回调内任何异常都不得外泄：外泄会中断 CallNextHookEx 链、致按键被吞/丢失，
            // 且可能让已吞键状态失衡（keydown 已标记吞、keyup 回调却因异常未执行）。记录后放行。
            try
            {
            var kb = Marshal.PtrToStructure<Win32.Kbdllhookstruct>(lParam);
            ushort vk = (ushort)kb.Vk;
            var msg = (int)wParam;
            bool isDown = msg == Win32.WmKeydown || msg == Win32.WmSyskeydown;
            bool isUp = msg == Win32.WmKeyup || msg == Win32.WmSyskeyup;

            // 忽略注入事件（防递归）
            if ((kb.Flags & Win32.LlkhfInjected) != 0)
            {
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 自动化 / 宏回放期间按配置的停止热键停止（热键注册失败时的兜底；成对吞掉 keydown / keyup）。
            // 放在录入拦截之前：即使正在录入热键，也保留这个安全出口。
            if (AutomationRunner.IsRunning
                    ? AutomationRunner.HandleStopHotkey(vk, isDown, isUp)
                    : MacroPlayer.HandleStopHotkey(vk, isDown, isUp))
                return (IntPtr)1;

            // 自动化运行期间：屏蔽 LTools 的全部 CapsLock 手势 / 菜单 / 面板 / vim 热键。
            // 普通物理按键照常放行（用户仍可在目标应用里输入），CapsLock 本体吞掉，
            // 避免切换大小写或让状态机进入按下态。停止热键已在上方优先处理。
            if (AutomationRunner.IsRunning)
            {
                // 「等待按键」（input.waitKey）：命中的键连同它的自动重复 / 抬起一起吞掉，
                // 其余按键照常放行（用户仍可在别的窗口打字）。放在 CapsLock 之前，
                // 让「等 CapsLock」也能命中。
                if (KeyWaiter.HandleKey(vk, isDown, isUp)) return (IntPtr)1;

                if (isUp && AppState.SwallowedVks.Remove((int)vk)) return (IntPtr)1;
                if (vk == Win32.VkCapital)
                {
                    if (isDown) MarkSwallowed(vk);
                    return (IntPtr)1;
                }
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 热键录入期间：按键交给焦点对话框自己处理，LTools 的全局热键 / CapsLock 状态机让路，
            // 避免「录入 Ctrl+F2 时反而启动了宏录制」。CapsLock 仍吞掉，防止误切大小写。
            if (HotkeyCapture.IsCapturing)
            {
                return vk == Win32.VkCapital
                    ? (IntPtr)1
                    : Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 宏录制热键（注入事件已在上方过滤，不会自触发）。
            if (MacroRecorder.HandleHotkey(vk, isDown, isUp, kb.Time))
                return (IntPtr)1;

            // 录制透明模式：CapsLock 不录入、不生效；其余键只观察后原样放行，
            // 跳过 CapsLock 状态机 / 菜单 / 鼠标模式 / vim 编辑等全部 LTools 键盘功能。
            // CapsLock 需单独处理：录制中吞掉；若录制在按住时结束，随后的 keyup 也补吞，
            // 避免「down 被吞、up 放行」造成系统按键状态失衡。
            if (vk == Win32.VkCapital && MacroRecorder.ShouldHandleCapsLock)
            {
                bool capsSwallow = MacroRecorder.HandleCapsLock(isDown);
                if (capsSwallow)
                {
                    if (isDown) MarkSwallowed(vk);
                    else AppState.SwallowedVks.Remove(vk);
                    return (IntPtr)1;
                }
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            if (MacroRecorder.IsRecording)
            {
                MacroRecorder.ObserveKey(vk, isDown, kb.Time, kb.Flags);
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 全局快捷键（含 CapsLock 前缀）：命中就吞键并在后台线程跑动作。
            // 放在状态机之前：CapsLock+字母 不再走旧的 vim 分发，统一由「快捷键」配置决定。
            if (isDown)
            {
                if (HotkeyRegistry.Suspended)
                {
                    // 正在录入快捷键：按键全部交给录入框，只把 CapsLock 的「有其它键」标上，
                    // 免得 CapsLock 快速点一下就在录入框里发出一个 Esc。
                    if (vk != Win32.VkCapital) AppState.OtherKeyPressed = true;
                }
                else
                {
                    string? hotkeyAction = HotkeyRegistry.TryFire(vk);
                    if (hotkeyAction != null && BindingRef.Exists(hotkeyAction))
                    {
                        AppState.OtherKeyPressed = true;
                        MarkSwallowed(vk);
                        ActionExecutor.RunRef(hotkeyAction, 0, 0);
                        return (IntPtr)1;
                    }
                }
            }
            else if (isUp) HotkeyRegistry.KeyUp(vk);

            // CapsLock 按住期间按了别的键 → 这次不算「轻按」，keyup 不发 Esc。
            // 未绑定快捷键的键也走这里（它们照常传给前台程序，只是不再算单击）。
            if (isDown && vk != Win32.VkCapital && AppState.CapsLockIsDown)
                AppState.OtherKeyPressed = true;

            // 分发到状态机（CapsLock 单击 Esc / Ctrl+CapsLock 切灯）
            if (CapsLockStateMachine.TryHandle(vk, isDown, isUp, out bool swallow))
            {
                return swallow ? (IntPtr)1 : Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 动作面板打开期间：只拦 Esc（钩子路由，不依赖窗口焦点），其余按键全部放行，
            // 让搜索框正常输入 / 使用 IME；右键菜单或对话框打开时 IsInteracting 为 true，
            // 连 Esc 也交回 WPF / 对话框处理。
            if (isDown && SuperPanel.IsOpen && !SuperPanel.IsInteracting)
            {
                if (SuperPanel.HandleKey(vk, isDown))
                {
                    AppState.OtherKeyPressed = true;
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }
            }

            // 已吞键的 keyup：一并吞掉，保持事件平衡（防止被吞的 keydown 配对走漏）
            if (isUp && AppState.SwallowedVks.Contains((int)vk))
            {
                AppState.SwallowedVks.Remove((int)vk);
                return (IntPtr)1;
            }
        }
        catch (System.Exception ex)
        {
            CrashLog.Write("KeyboardHook", ex);
        }
        }
        return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    /// <summary>记录已吞键，使其 keyup 也被吞（保持事件平衡）。</summary>
    private static void MarkSwallowed(ushort vk)
    {
        int key = vk;
        if (!AppState.SwallowedVks.Contains(key))
            AppState.SwallowedVks.Add(key);
    }
}
