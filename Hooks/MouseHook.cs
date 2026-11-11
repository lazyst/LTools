using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features;
using LTools.Features.Floating;
using LTools.Native;

namespace LTools.Hooks;

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL）。在主线程安装，消息循环泵送。
/// 职责：
/// - 屏幕边缘滚轮 → 系统音量调节（<see cref="Volume"/>；开关与边见设置「通用」）
/// - 裸右键长按 → 唤起超级面板；短按还原原生右键菜单
/// - 面板 / 悬浮窗菜单的点外关闭
/// </summary>
/// <remarks>
/// 窗口置顶（<see cref="WindowPin"/>）曾在这里有一条 <c>CapsLock+右键</c> 写死手势，
/// 2026-11 删除：同一个功能在内置功能目录里是 <c>common.windowPin</c>，绑快捷键 / 放面板即可，
/// 钩子里不再保留按设备写死的手势。
/// </remarks>
/// <remarks>
/// 与 <see cref="KeyboardHook"/> 一样，忽略 <see cref="Win32.LlmhfInjected"/> 事件防止递归
/// （自己注入的点击/按键不触发本钩子逻辑）。
/// </remarks>
internal static class MouseHook
{
    private static IntPtr _handle = IntPtr.Zero;
    private static Win32.LowLevelKeyboardProc? _proc; // 签名与 LowLevelMouseProc 兼容
    private static GCHandle _procHandle;

    // —— 裸右键长按唤起超级面板（阶段 5，计划 §5.1）——
    private static DispatcherTimer? _superPanelTimer;
    private static bool _suppressRButtonUp;     // 计时器触发后置位：吞掉随后的右键 up 阻止原生菜单
    private static bool _rightCloseUpPending;   // 「右键点外关闭」吞掉 down 后置位：配套 up 也必须吞
    private static bool _rightPassUpPending;    // 「右键点外关面板」把 down 放行后置位：配套 up 也原样放行
                                                // （此时面板已关闭，不置位的话 up 会落到长按手势分支：
                                                // 被吞掉并再注入一次右键，等于这次右键被吞）

    // 按下右键那一刻光标下的顶层窗口：右键不激活后台窗口，前台可能还停在别的程序上，
    // 面板的上下文场景必须按这个句柄解析（照 Quicker）
    private static IntPtr _superPanelContextHwnd;

    /// <summary>「点击窗口选取」遮罩显示期间：裸右键长按手势让路，鼠标事件原样交给遮罩。</summary>
    private static bool _windowPickerActive;

    /// <summary>本类注入鼠标事件的 dwExtraInfo 魔法标记；钩子开头据此跳过，杜绝注入回流递归。</summary>
    private static readonly IntPtr InjectedTag = (IntPtr)0x1234ABCD;

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
    /// 把「首个真实鼠标事件触发回调 JIT」的一次性停顿提前到无钩子阶段。
    /// nCode&lt;0 时回调直接放行，无副作用（CallNextHookEx 的 hhk 参数被系统忽略）。</summary>
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

            // 忽略注入事件（防递归：本类注入的左/右键点击不重新触发逻辑）。
            // 除 LLMHF_INJECTED 外，再用 dwExtraInfo 魔法标记兜底——双保险杜绝注入回流本钩子。
            if ((ms.Flags & Win32.LlmhfInjected) != 0 || ms.ExtraInfo == InjectedTag)
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);

            // 「点击窗口选取」遮罩期间：所有鼠标事件原样放行（不吞、不注入、不启动长按手势）。
            // 否则裸右键长按手势会吞掉 down/up 再注入一次右键，遮罩要连点两次才收得到一次取消；
            // 左键选中与滚轮也不该被 LTools 自己的功能截走。
            if (_windowPickerActive)
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);

            // 自动化运行期间：LTools 的鼠标手势（边缘滚轮音量 / 裸右键长按面板）全部让路，
            // 物理鼠标事件原样交给目标应用；注入事件已在上方过滤。
            if (AutomationRunner.IsRunning)
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);

            // 录制透明模式：只观察并放行，跳过边缘滚轮音量 / 超级面板等全部 LTools 鼠标功能
            // ——保证「录到的鼠标事件 = 目标应用实际收到的」。
            if (MacroRecorder.IsRecording)
            {
                MacroRecorder.ObserveMouse((int)wParam, ms.Pt.X, ms.Pt.Y, (int)ms.MouseData, ms.Time);
                return Win32.CallNextHookEx(_handle, nCode, wParam, lParam);
            }

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
                    // 必须先于「面板未打开」判断——面板可能已由 Show() 的 BeginInvoke 在 up 到达前创建，
                    // 此时 IsOpen=true，若把该判断挪进下面分支会漏吞 up 致原生菜单泄漏。
                    if (_suppressRButtonUp && (int)wParam == Win32.WmRbuttonup)
                    {
                        _suppressRButtonUp = false;
                        return (IntPtr)1; // 吞掉 up（不注入）
                    }
                    // 点外关闭——右键版（覆盖式弹层：超级面板 / 悬浮项菜单）。
                    // 与左键分支同样只在面板外部触发（右键菜单/对话框打开期间 SuperPanel.IsInteracting 不介入）。
                    //
                    // ⚠️ down/up 必须同进同出：若放行 down 却吞 up，目标窗口收不到配对的 up，
                    //    鼠标捕获不释放 → 右键永久「卡住」（须再点一次右键才解除）。
                    // 超级面板：只关面板，**这一下右键放行**给光标下窗口（用户要求：点面板外部不要吞事件，
                    // 和左键语义一致——下层程序该收到右键 / 该弹自己的菜单就弹）。
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
                        bool overSuperMenu = SuperPanel.IsOpen && SuperPanel.PointInOpenMenu(ms.Pt.X, ms.Pt.Y);
                        bool overSuper = SuperPanel.IsOpen && !SuperPanel.IsPinned && !overSuperMenu
                                         && !SuperPanel.PointInWindowRect(ms.Pt.X, ms.Pt.Y);
                        // 悬浮窗的右键菜单：popup 拿不到有效捕获（悬浮窗永远不是前台窗口，
                        // Windows 只让前台窗口捕获「窗口之外」的点击），WPF 那套「点外部关闭」
                        // 在它身上根本不触发，所以只能在这里按坐标关。
                        bool overFloatMenu = FloatingManager.IsMenuOpen
                                             && !FloatingManager.MenuContains(ms.Pt.X, ms.Pt.Y);

                        if (overSuper)
                        {
                            CancelSuperPanelGesture();
                            SuperPanel.Close();
                            _rightPassUpPending = true; // 面板已关，配套 up 也要原样放行
                            break; // 不吞：右键点面板外部照常交给光标下窗口
                        }

                        if (overFloatMenu)
                        {
                            FloatingManager.CloseOpenMenu();
                            _rightCloseUpPending = true;
                            return (IntPtr)1; // 吞 down，并提前退出（不进长按手势分支）
                        }

                        // 右键点在悬浮菜单里面：交给菜单自己处理，别起长按手势
                        // （否则会吞掉这一下再注入一次右键，菜单状态就乱了）
                        if (FloatingManager.IsMenuOpen) break;
                    }
                    // 裸右键长按手势（§5.1）：开关开 且 面板未打开时，整组吞掉 down/up。
                    // 必须吞 down（而非放行）——若放行 down 却吞 up，目标窗口会收到 down 收不到 up，
                    // 鼠标捕获不释放致右键「卡住」（须再点一次右键才解除）。
                    // 短按（up 先于计时器）→ 停表 + 注入一次原生右键 down+up 还原菜单；
                    // 注入事件带 LLMHF_INJECTED，被本钩子开头忽略，不会递归。
                    if (AppState.IsSuperPanelEnabled && !SuperPanel.IsOpen)
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
                    // 悬浮窗右键菜单开着：点菜单外就关掉，并把这一下放行给光标下窗口
                    // （popup 在悬浮窗上拿不到有效捕获，WPF 自己不会关）。
                    if (FloatingManager.IsMenuOpen)
                    {
                        FloatingManager.CloseMenuIfOutside(ms.Pt.X, ms.Pt.Y);
                        break; // 菜单开着时不参与其它鼠标手势
                    }
                    // 超级面板打开时：点击面板外部 → 关闭面板并放行点击（坐标判定）。
                    // **面板自己的右键菜单在场也算**（用户要求）：点菜单外就关面板，
                    // 菜单随面板一起销毁；点在菜单里则由菜单自己处理（子菜单也是独立 popup，一起算）。
                    if (SuperPanel.IsOpen && !SuperPanel.IsPinned
                        && !SuperPanel.PointInWindowRect(ms.Pt.X, ms.Pt.Y)
                        && !SuperPanel.PointInOpenMenu(ms.Pt.X, ms.Pt.Y))
                    {
                        SuperPanel.Close();
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

    // —— 裸右键长按手势（阶段 5，计划 §5.1）——
    // 短按（up 先于计时器）→ 停表放行，原生右键菜单不受干预；
    // 长按（计时器先触发）→ 光标处弹面板 + 标记吞掉随后的右键 up（阻止原生菜单）。
    // 计时器在主线程 Dispatcher 上跑（钩子回调亦在主线程派发），Tick 内调 SuperPanel.Show() 非阻塞。

    /// <summary>右键 down 启动长按计时（放行 down，不干预短按）。每次 down 重置计时与间隔。</summary>
    private static void StartSuperPanelGesture()
    {
        // 按下那一刻光标下的顶层窗口 = 用户要操作的对象（右键不激活后台窗口）
        _superPanelContextHwnd = Win32.TopLevelWindowAtCursor();

        var t = _superPanelTimer;
        if (t == null)
        {
            t = new DispatcherTimer(DispatcherPriority.Normal, Application.Current.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(ClampThreshold(AppState.SuperPanelThresholdMs))
            };
            t.Tick += OnSuperPanelTimerTick;
            _superPanelTimer = t;
        }
        else
        {
            t.Stop();
            t.Interval = TimeSpan.FromMilliseconds(ClampThreshold(AppState.SuperPanelThresholdMs));
        }
        t.Start();
    }

    /// <summary>短按收尾：停表放行，原生右键菜单照常弹出。</summary>
    private static void CancelSuperPanelGesture()
    {
        _superPanelTimer?.Stop();
        _superPanelContextHwnd = IntPtr.Zero;
    }

    /// <summary>清除右键长按手势的待处理状态（进入宏录制透明模式前调用，避免残留标志误吞后续 up）。</summary>
    public static void CancelPendingGesture()
    {
        _superPanelTimer?.Stop();
        _suppressRButtonUp = false;
        _rightCloseUpPending = false;
        _rightPassUpPending = false;
        _superPanelContextHwnd = IntPtr.Zero;
    }

    /// <summary>
    /// 「点击窗口选取」遮罩显示 / 关闭时切换让路状态（见 <c>Views/WindowPickOverlay</c>）。
    /// 进入时顺手清掉可能残留的长按 / 待吞 up 标志，避免上一轮的 pending 误吞点选器的第一次右键。
    /// </summary>
    public static void SetWindowPickerActive(bool active)
    {
        if (active)
        {
            _windowPickerActive = true;
            CancelPendingGesture();
        }
        else
        {
            _windowPickerActive = false;
        }
    }

    /// <summary>长按成立：光标处弹面板（上下文取按下时光标下的窗口），并标记吞掉随后的右键 up 阻止原生菜单。</summary>
    private static void OnSuperPanelTimerTick(object? sender, EventArgs e)
    {
        _superPanelTimer?.Stop();
        _suppressRButtonUp = true;
        IntPtr context = _superPanelContextHwnd;
        _superPanelContextHwnd = IntPtr.Zero;
        SuperPanel.Show(context);
    }

    /// <summary>阈值夹到合法区间（100–800ms），防止异常配置导致计时异常。</summary>
    private static int ClampThreshold(int ms) => ms < 100 ? 100 : (ms > 800 ? 800 : ms);

    /// <summary>注入一次右键 down+up（mouse_event），还原被吞掉的短按原生右键菜单。
    /// **必须在后台线程执行**——在 WH_MOUSE_LL 钩子回调内同步注入时，注入事件需经同一钩子线程处理，
    /// 而该线程正阻塞在回调里 → 互相等待，致系统鼠标卡死（钩子不返回，数秒后由系统超时移除才恢复）。
    /// 注入事件带 <see cref="Win32.LlmhfInjected"/> + <see cref="InjectedTag"/>，被本钩子开头忽略，不会递归；
    /// 位置取当前光标。</summary>
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
