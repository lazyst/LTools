using System.Runtime.InteropServices;
using LTools.Core;
using LTools.Core.Link;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Features.Floating;
using LTools.Native;

namespace LTools.Hooks;

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL）。2026-11 阶段 2 起由 <see cref="HookDispatcher"/> 在专用 STA 线程
/// 安装并泵消息，回调也在那条线程上同步派发（不再与 WPF 首帧渲染 / 切页重活抢同一条消息泵）。
/// 职责：
/// - 屏幕边缘滚轮 → 系统音量调节（<see cref="Volume"/>；开关与边见设置「通用」）
/// - 裸右键长按 → 唤起超级面板；短按还原原生右键菜单
/// - 面板 / 悬浮窗菜单的点外关闭
/// </summary>
/// <remarks>
/// <para>
/// 回调里**只做纯数据判定 + 同步吞 / 放**：面板 / 菜单状态一律读
/// <see cref="HookMirror"/> 的不可变镜像（UI 线程发布），绝不碰 WPF 窗口 / 菜单 / Popup；
/// 所有 UI 副作用走 <see cref="Ui.Post"/> 排回主 UI 线程，且只排队、不等待。
/// 吞键决策必须在回调内同步完成，不能异步等结果。
/// </para>
/// <para>
/// 窗口置顶（<see cref="WindowPin"/>）曾在这里有一条 <c>CapsLock+右键</c> 写死手势，
/// 2026-11 删除：同一个功能在内置功能目录里是 <c>common.windowPin</c>，绑快捷键 / 放面板即可。
/// 与 <see cref="KeyboardHook"/> 一样，忽略 <see cref="Win32.LlmhfInjected"/> 事件防止递归。
/// </para>
/// </remarks>
internal static class MouseHook
{
    private static IntPtr _handle = IntPtr.Zero;
    private static Win32.LowLevelKeyboardProc? _proc; // 签名与 LowLevelMouseProc 兼容
    private static GCHandle _procHandle;

    // —— 裸右键长按唤起超级面板 ——
    // 钩子线程没有 WPF Dispatcher 泵，不能用 DispatcherTimer；改用 System.Threading.Timer，
    // Tick 只往钩子线程排一条命令（回调与命令在同一线程串行，状态无需加锁）。
    private static System.Threading.Timer? _superPanelTimer;
    private static bool _gesturePending;           // 计时器已起、还没被短按 up 取消
    private static bool _suppressRButtonUp;        // 计时器触发后置位：吞掉随后的右键 up 阻止原生菜单
    private static bool _rightCloseUpPending;      // 「右键点外关闭」吞掉 down 后置位：配套 up 也必须吞
    private static bool _rightPassUpPending;       // 「右键点外关面板」把 down 放行后置位：配套 up 也原样放行
                                                    // （此时面板已关闭，不置位的话 up 会落到长按手势分支：
                                                    // 被吞掉并再注入一次右键，等于这次右键被吞）

    // 按下右键那一刻光标下的顶层窗口：右键不激活后台窗口，前台可能还停在别的程序上，
    // 面板的上下文场景必须按这个句柄解析（照 Quicker）
    private static IntPtr _superPanelContextHwnd;

    // 「关窗时还按着的鼠标键」：抬起时吞掉（见 SwallowPendingReleases）。
    // 只吞 up、绝不配对任何 down——按键是在覆盖层里按下的，下面那个窗口从没见过它。
    private static bool _swallowLUp;
    private static bool _swallowRUp;
    private static bool _swallowMUp;

    /// <summary>「点击窗口选取」遮罩显示期间：裸右键长按手势让路，鼠标事件原样交给遮罩。</summary>
    private static bool _windowPickerActive;

    /// <summary>本类注入鼠标事件的 dwExtraInfo 魔法标记；钩子开头据此跳过，杜绝注入回流递归。</summary>
    private static readonly IntPtr InjectedTag = (IntPtr)0x1234ABCD;

    /// <summary>安装钩子。必须在 <see cref="HookDispatcher"/> 线程上调用。</summary>
    public static void Install()
    {
        if (_handle != IntPtr.Zero) return;
        _proc = HookCallback;
        _procHandle = GCHandle.Alloc(_proc);
        var hMod = Win32.GetModuleHandle(null);
        _handle = Win32.SetWindowsHookEx(Win32.WhMouseLl, _proc!, hMod, 0);
        if (_handle == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL) 失败");
    }

    /// <summary>卸载钩子。必须在安装它的同一条 <see cref="HookDispatcher"/> 线程上调用。</summary>
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

    /// <summary>预热回调 JIT：以非动作 nCode 调用一次（JIT 在方法首次调用时编译整个方法体）。</summary>
    public static void WarmUp() => HookCallback(-1, IntPtr.Zero, IntPtr.Zero);

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == Win32.HcAction)
        {
            // 钩子回调内任何异常都不得外泄（同 KeyboardHook）：外泄中断 CallNextHookEx 链，
            // 致鼠标事件被吞/丢失、长按手势/点外关闭状态失衡。记录后放行。
            try
            {
            var ms = Marshal.PtrToStructure<Win32.Msllhookstruct>(lParam);

            // 量化诊断必须排在注入过滤之前：探针就是注入事件，靠它量「消息到回调」的排队延迟。
            // 关闭时只有一次 volatile 读；归因按 ms.Time 落在哪个相位窗口（见 PerfProbe）。
            PerfProbe.OnHook(ms.Time);

            // 「关窗那一下的抬起」：覆盖层 / 遮罩在右键 down 里就关了，配对 up 若放行会落到
            // 下面的程序上（资源管理器直接弹出右键菜单）。只吞 up，且只吞关窗那一刻还按着的键。
            // 必须排在注入过滤之前：测试注入的 up 与物理 up 走同一条路，否则这条改不了肉眼可见的问题。
            if (TakePendingRelease((int)wParam))
                return (IntPtr)1;

            // 长截图期间：全部让路（页面自己收滚轮才滚得动），只把落**在目标窗口里**的滚轮
            // 「观察」一份交给拼接流程触发抓帧——不吞、不改，页面收到的仍是真正的滚轮。
            // 必须排在「点击窗口选取」之前：截图会话全程开着那条让路，排在它后面就永远到不了。
            // 也必须排在注入过滤**之前**：只按 dwExtraInfo 挡自己（自动滚动投出去的真滚轮带
            // MouseOps.Tag），否则触控板驱动 / 远程桌面 / 脚本发的滚轮（LLMHF_INJECTED 但没标记）
            // 全部观察不到 → 页面滚了、长图却不长，而且没有任何报错。分支里只「观察 + 放行」，
            // 不注入、不吞，所以提前于注入过滤不会带回自注入递归。
            if (ScrollCaptureHookState.Active)
            {
                if ((int)wParam == Win32.WmMousewheel && ms.ExtraInfo != InjectedTag)
                    ScrollCaptureHookState.ObserveWheel(ms.Pt.X, ms.Pt.Y);
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 忽略注入事件（防递归：本类注入的左/右键点击不重新触发逻辑）。
            // 除 LLMHF_INJECTED 外，再用 dwExtraInfo 魔法标记兜底——双保险杜绝注入回流本钩子。
            if ((ms.Flags & Win32.LlmhfInjected) != 0 || ms.ExtraInfo == InjectedTag)
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);

            // 录屏特效：录屏期间把滚轮方向喂给录制线程（「记录鼠标滚动」要它）。
            // 只入队、不吞键、不改任何 LTools 行为，所以放在所有分支之前也不会带回副作用。
            if ((int)wParam == Win32.WmMousewheel && RecordWheelFeed.Active)
                RecordWheelFeed.Push((short)((ms.MouseData >> 16) & 0xFFFF));

            // 「点击窗口选取」遮罩期间：所有鼠标事件原样放行（不吞、不注入、不启动长按手势）。
            if (_windowPickerActive)
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);

            // 自动化运行期间：LTools 的鼠标手势（边缘滚轮音量 / 裸右键长按面板）全部让路。
            if (AutomationRunner.IsRunning)
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);

            // 录制透明模式：只观察并放行，跳过边缘滚轮音量 / 超级面板等全部 LTools 鼠标功能
            // ——保证「录到的鼠标事件 = 目标应用实际收到的」。
            if (MacroRecorder.IsRecording)
            {
                MacroRecorder.ObserveMouse((int)wParam, ms.Pt.X, ms.Pt.Y, (int)ms.MouseData, ms.Time);
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

            // 面板 / 悬浮菜单的 UI 状态：只读一次不可变镜像，后面全部是纯整数比较。
            var panel = HookMirror.Panel;
            var floating = HookMirror.Float;

            switch ((int)wParam)
            {
                case Win32.WmMousewheel:
                    int delta = (short)((ms.MouseData >> 16) & 0xFFFF);
                    if (Volume.OnWheel(ms.Pt.X, ms.Pt.Y, delta))
                        return (IntPtr)1; // 吞掉滚轮
                    break;

                case Win32.WmRbuttondown:
                case Win32.WmRbuttonup:
                    // 长按收尾：计时器已触发 → 吞掉随后的 up 阻止原生菜单。
                    if (_suppressRButtonUp && (int)wParam == Win32.WmRbuttonup)
                    {
                        _suppressRButtonUp = false;
                        return (IntPtr)1; // 吞掉 up（不注入）
                    }
                    // ⚠️ down/up 必须同进同出：若放行 down 却吞 up，目标窗口收不到配对的 up，
                    //    鼠标捕获不释放 → 右键永久「卡住」。
                    if ((int)wParam == Win32.WmRbuttonup)
                    {
                        if (_rightCloseUpPending)
                        {
                            _rightCloseUpPending = false;
                            return (IntPtr)1; // 吞掉配套 up（成对）
                        }
                        if (_rightPassUpPending)
                        {
                            _rightPassUpPending = false;
                            break; // 配对放行：down 已经交给下层，up 也必须原样交给它
                        }
                    }
                    if ((int)wParam == Win32.WmRbuttondown)
                    {
                        _rightCloseUpPending = false;   // 兜底：上个 up 若丢失，清残留避免误吞
                        _rightPassUpPending = false;

                        // 面板自己的右键菜单也算面板的一部分：点在菜单里交给菜单，
                        // 点在菜单外（面板外）就是「点外部」——关面板（菜单随之销毁），这一下右键放行。
                        bool overSuperMenu = panel.Open && HookMirror.PanelMenuContains(ms.Pt.X, ms.Pt.Y);
                        bool overSuper = panel.Open && !panel.Pinned && !overSuperMenu
                                         && !HookMirror.PanelContains(ms.Pt.X, ms.Pt.Y);
                        // 悬浮窗的右键菜单：popup 拿不到有效捕获，只能按镜像里的坐标关。
                        bool overFloatMenu = floating.MenuOpen
                                             && !HookMirror.FloatMenuContains(ms.Pt.X, ms.Pt.Y);

                        if (overSuper)
                        {
                            CancelSuperPanelGesture();
                            // 关面板是 WPF 动作，排回 UI 线程；本事件不吞（右键照常交给光标下窗口）。
                            Ui.Post(() => SuperPanel.Close());
                            _rightPassUpPending = true;
                            break;
                        }

                        if (overFloatMenu)
                        {
                            Ui.Post(() => FloatingManager.CloseOpenMenu());
                            _rightCloseUpPending = true;
                            return (IntPtr)1; // 吞 down，并提前退出（不进长按手势分支）
                        }

                        // 右键点在悬浮菜单里面：交给菜单自己处理，别起长按手势
                        if (floating.MenuOpen) break;
                    }
                    // 裸右键长按手势：开关开 且 面板未打开时，整组吞掉 down/up。
                    // 面板开在宿主进程里，钩子这边靠宿主推来的状态（`panel.state`）判断。
                    // 短按 → 停表 + 注入一次原生右键 down+up 还原菜单（注入事件被本钩子开头忽略）。
                    if (AppState.IsSuperPanelEnabled && !panel.Open && !PanelHostBridge.RemotePanelOpen)
                    {
                        if ((int)wParam == Win32.WmRbuttondown)
                        {
                            StartSuperPanelGesture();       // 吞 down，启动计时
                        }
                        else
                        {
                            CancelSuperPanelGesture();      // 短按：停表
                            InjectRightClick();             // 还原原生右键菜单
                        }
                        return (IntPtr)1; // down 与 up 都吞
                    }
                    break;

                case Win32.WmLbuttondown:
                    // 悬浮窗右键菜单开着：点菜单外就关掉，并把这一下放行给光标下窗口。
                    // 判定 + 关闭都在 UI 线程做（这一支不吞键，用真实 WPF 状态最准）。
                    if (floating.MenuOpen)
                    {
                        int fx = ms.Pt.X, fy = ms.Pt.Y;
                        Ui.Post(() => FloatingManager.CloseMenuIfOutside(fx, fy));
                        break;
                    }
                    // 超级面板打开时：点击面板外部 → 关闭面板并放行点击（坐标判定）。
                    if (panel.Open && !panel.Pinned
                        && !HookMirror.PanelContains(ms.Pt.X, ms.Pt.Y)
                        && !HookMirror.PanelMenuContains(ms.Pt.X, ms.Pt.Y))
                    {
                        Ui.Post(() => SuperPanel.Close());
                        break; // 不吞点击：放行给光标下窗口
                    }
                    break;
            }
            }
            catch (System.Exception ex)
            {
                CrashLog.Write("MouseHook", ex);
            }
        }
        return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
    }

    // —— 裸右键长按手势 ——
    // 计时器是 System.Threading.Timer（线程池线程），Tick 只排一条命令回钩子线程；
    // 回调与命令在同一条线程串行执行，所以下面这些状态无需加锁 / volatile。

    /// <summary>右键 down 启动长按计时（吞 down，不干预短按）。每次 down 重置计时与间隔。</summary>
    private static void StartSuperPanelGesture()
    {
        // 按下那一刻光标下的顶层窗口 = 用户要操作的对象（右键不激活后台窗口）
        _superPanelContextHwnd = Win32.TopLevelWindowAtCursor();
        _gesturePending = true;

        int interval = ClampThreshold(AppState.SuperPanelThresholdMs);
        var t = _superPanelTimer;
        if (t == null)
        {
            t = new System.Threading.Timer(OnSuperPanelTimerTick, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            _superPanelTimer = t;
        }
        t.Change(interval, System.Threading.Timeout.Infinite);
    }

    /// <summary>短按收尾：停表放行，原生右键菜单照常弹出。</summary>
    private static void CancelSuperPanelGesture()
    {
        _gesturePending = false;
        _superPanelTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _superPanelContextHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// 窗口 / 遮罩关掉时调用：**此刻物理上还按着**的鼠标键，抬起时吞掉。
    /// </summary>
    /// <remarks>
    /// 覆盖层、点选遮罩、屏幕选字的退出都是「右键按下就走」，窗口一关，配对的那一下抬起就落到
    /// 光标下面的程序上——资源管理器会直接弹右键菜单，编辑器会把光标挪过去。这里只吞 up，
    /// 不配对任何 down：按键是在遮罩里按下的，下面那个窗口从没见过它，不会因此卡住鼠标。
    /// 状态在**当前线程**立刻采样（<see cref="Win32.GetAsyncKeyState"/>，窗口刚关、键还按着），
    /// 再把结果排到钩子线程；反过来让钩子线程稍后再采样，用户手快就会读成「已经松开」而漏吞。
    /// </remarks>
    public static void SwallowPendingReleases()
    {
        SwallowReleases(IsDown(Win32.VkLbutton), IsDown(Win32.VkRbutton), IsDown(Win32.VkMbutton));
    }

    /// <summary>
    /// 直接指定哪几个键「抬起要吞掉」（状态在别处已经采样过）。
    /// </summary>
    /// <remarks>
    /// 遮罩在宿主、钩子在这里：宿主关窗那一刻采样，经链路送过来。别在这里重新采样——跨进程要几毫秒，
    /// 手快的时候键已经抬起了（同 <see cref="KeyboardHook.ArmRelease"/>）。
    /// </remarks>
    public static void SwallowReleases(bool left, bool right, bool middle)
    {
        if (!left && !right && !middle) return;

        HookDispatcher.Post(() =>
        {
            _swallowLUp |= left;
            _swallowRUp |= right;
            _swallowMUp |= middle;
        });
    }

    private static bool IsDown(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>这次事件是不是「关窗那一下的抬起」；是就吞掉（只生效一次）。</summary>
    private static bool TakePendingRelease(int message)
    {
        switch (message)
        {
            // 先看到 down = 上一次的 up 丢了（窗口被别的路径关掉等），清残留，别误吞下一次抬起
            case Win32.WmLbuttondown: _swallowLUp = false; return false;
            case Win32.WmRbuttondown: _swallowRUp = false; return false;
            case Win32.WmMbuttondown: _swallowMUp = false; return false;

            case Win32.WmLbuttonup:
                if (!_swallowLUp) return false;
                _swallowLUp = false;
                return true;

            case Win32.WmRbuttonup:
                if (!_swallowRUp) return false;
                _swallowRUp = false;
                return true;

            case Win32.WmMbuttonup:
                if (!_swallowMUp) return false;
                _swallowMUp = false;
                return true;

            default:
                return false;
        }
    }

    /// <summary>清除右键长按手势的待处理状态（进入宏录制透明模式前调用，避免残留标志误吞后续 up）。</summary>
    /// <remarks>外部（UI 线程）调用：排到钩子线程执行，保证与回调串行。</remarks>
    public static void CancelPendingGesture() => HookDispatcher.Post(CancelPendingGestureCore);

    private static void CancelPendingGestureCore()
    {
        _gesturePending = false;
        _superPanelTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        _suppressRButtonUp = false;
        _rightCloseUpPending = false;
        _rightPassUpPending = false;
        _superPanelContextHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// 「点击窗口选取」遮罩显示 / 关闭时切换让路状态（见 <c>Views/WindowPickOverlay</c>）。
    /// 进入时顺手清掉可能残留的长按 / 待吞 up 标志，避免上一轮的 pending 误吞点选器的第一次右键。
    /// </summary>
    /// <remarks>外部（UI 线程）调用：排到钩子线程执行。</remarks>
    public static void SetWindowPickerActive(bool active)
        => HookDispatcher.Post(() => SetWindowPickerActiveCore(active));

    private static void SetWindowPickerActiveCore(bool active)
    {
        if (active)
        {
            _windowPickerActive = true;
            CancelPendingGestureCore();
        }
        else
        {
            _windowPickerActive = false;
        }
    }

    /// <summary>长按成立：标记吞掉随后的右键 up 阻止原生菜单，并把建窗排回主 UI 线程。</summary>
    private static void OnSuperPanelTimerTick(object? state)
    {
        HookDispatcher.Post(() =>
        {
            // 短按已经把 up 交掉了：计时器命令迟到也不能再弹面板 / 吞下一次 up。
            if (!_gesturePending) return;
            _gesturePending = false;
            _superPanelTimer?.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);

            _suppressRButtonUp = true;
            IntPtr context = _superPanelContextHwnd;
            _superPanelContextHwnd = IntPtr.Zero;
            Ui.Post(() => PanelLauncher.Show(context));
        });
    }

    /// <summary>阈值夹到合法区间（100–800ms），防止异常配置导致计时异常。</summary>
    private static int ClampThreshold(int ms) => ms < 100 ? 100 : (ms > 800 ? 800 : ms);

    /// <summary>注入一次右键 down+up（mouse_event），还原被吞掉的短按原生右键菜单。
    /// **必须在后台线程执行**——在 WH_MOUSE_LL 钩子回调内同步注入鼠标事件时，注入事件需经同一
    /// 钩子线程处理，而该线程正阻塞在回调里 → 互相等待，致系统鼠标卡死。注入事件带
    /// <see cref="Win32.LlmhfInjected"/> + <see cref="InjectedTag"/>，被本钩子开头忽略，不会递归。</summary>
    private static void InjectRightClick()
    {
        var t = new Thread(() =>
        {
            try
            {
                Win32.mouse_event(Win32.MouseeventfRightdown, 0, 0, 0, InjectedTag);
                Win32.mouse_event(Win32.MouseeventfRightup, 0, 0, 0, InjectedTag);
            }
            catch { /* 静默降级 */ }
        }) { IsBackground = true };
        t.Start();
    }
}
