using System.Runtime.InteropServices;
using LTools.Core;
using LTools.Features;
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

            // 分发到状态机/功能模块（阶段1+ 实现）
            if (CapsLockStateMachine.TryHandle(vk, isDown, isUp, out bool swallow))
            {
                return swallow ? (IntPtr)1 : Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 菜单打开期间：数字键选项 / Esc 关菜单由钩子路由（不依赖窗口焦点，对齐 AHK #HotIf WinActive(menu)）
            if (isDown && MenuSystem.IsMenuOpen)
            {
                // CapsLock 按住 + 数字键 → 关闭当前菜单并切换到新组（而非在当前组内选项）
                if (AppState.IsCapsLockDown && vk >= '0' && vk <= '9')
                {
                    MenuSystem.Dispatch(vk); // 内部先 CloseCurrent() 再 Show 新组
                    AppState.OtherKeyPressed = true;
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }
                if (MenuSystem.HandleMenuKey(vk, isDown))
                {
                    AppState.OtherKeyPressed = true;
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }
            }

            // 超级面板打开期间：1~9 选格 / Esc 关面板（钩子路由，不依赖窗口焦点；
            // 右键菜单 / 对话框打开时 IsInteracting 为 true，交回 WPF/对话框处理，不吞键）
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

            // 鼠标模式激活：路由鼠标键（e/d/s/f/q/a/w/r/j/k/h/l/Esc/Space）到 MouseMode
            if (AppState.MouseModeActive && MouseMode.IsMouseKey(vk))
            {
                MouseMode.OnKey(vk, isDown);
                return (IntPtr)1; // 鼠标模式键一律吞掉
            }

            // CapsLock 按住 + 工具启用期间：CapsLock+Space 进入鼠标模式 + vim 分发
            if (isDown && AppState.IsCapsLockDown && AppState.IsToolEnabled)
            {
                AppState.OtherKeyPressed = true;
                if (kb.Vk == Win32.VkSpace) { MouseMode.Enter(); return (IntPtr)1; }

                // 超级面板备用热键（CapsLock+T）：阶段5 已接入长按右键手势，此热键保留为备用入口
                if (vk == 'T' && AppState.IsSuperPanelEnabled)
                {
                    SuperPanel.Toggle();
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }

                // 阶段4：帮助面板（SC029 扫描码，布局无关）+ 菜单系统（CapsLock+1~0）
                if (kb.Scan == 0x29) { HelpPanel.Toggle(); MarkSwallowed(vk); return (IntPtr)1; }
                if (vk >= '0' && vk <= '9')
                {
                    MenuSystem.Dispatch(vk);
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }

                if (TextEditor.TryHandle(vk))
                {
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }

                // 阶段5：杂项热键（放大镜/空置键/双引号花括号/快速搜索/速记/设置）
                if (MiscKeys.TryHandle(vk))
                {
                    MarkSwallowed(vk);
                    return (IntPtr)1;
                }
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
