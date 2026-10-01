using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features;
using LTools.Native;

namespace LTools.Hooks;

/// <summary>
/// 低级鼠标钩子（WH_MOUSE_LL）。在主线程安装，消息循环泵送。
/// 职责（跟随全局启用开关与 CapsLock 按下态）：
/// - 屏幕底部 5px 滚轮 → 系统音量调节（<see cref="Volume"/>）
/// - CapsLock 按住 + 右键按下 → 切换光标下窗口置顶（<see cref="WindowPin"/>）
/// - CapsLock 按住 + 左键按下 → 资源管理器选中文件重命名（F2，对应 lib/Workspace.ahk）
/// </summary>
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

            switch ((int)wParam)
            {
                case Win32.WmMousewheel:
                    int delta = (short)((ms.MouseData >> 16) & 0xFFFF);
                    if (Volume.OnWheel(ms.Pt.X, ms.Pt.Y, delta))
                        return (IntPtr)1; // 吞掉滚轮
                    break;

                case Win32.WmRbuttondown:
                case Win32.WmRbuttonup:
                    // 优先级（§5.2）：CapsLock 按下 → 走窗口置顶，整组吞掉（down+up），否则
                    // WM_RBUTTONUP 仍会合成 WM_CONTEXTMENU 弹出原生菜单。AHK 原版 RButton:: 吞 down+up。
                    if (AppState.IsToolEnabled && AppState.IsCapsLockDown)
                    {
                        if ((int)wParam == Win32.WmRbuttondown)
                            WindowPin.ToggleAtCursor(ms.Pt.X, ms.Pt.Y);
                        return (IntPtr)1; // 吞掉
                    }
                    // 长按收尾：计时器已触发 → 吞掉随后的 up 阻止原生菜单。
                    // 必须先于「面板未打开」判断——面板可能已由 Show() 的 BeginInvoke 在 up 到达前创建，
                    // 此时 IsOpen=true，若把该判断挪进下面分支会漏吞 up 致原生菜单泄漏。
                    if (_suppressRButtonUp && (int)wParam == Win32.WmRbuttonup)
                    {
                        _suppressRButtonUp = false;
                        return (IntPtr)1; // 吞掉 up（不注入）
                    }
                    // 点外关闭——右键版（三处覆盖式弹层：超级面板 / 菜单组 / 帮助面板）。
                    // 与左键分支同样只在面板外部触发（右键菜单/对话框打开期间 SuperPanel.IsInteracting 不介入）。
                    // 语义（用户已定）：关掉弹层，且这一下右键**整组吞掉**、不作用到下层（不弹原生菜单）。
                    //
                    // ⚠️ down/up 必须同进同出：若放行 down 却吞 up，目标窗口收不到配对的 up，
                    //    鼠标捕获不释放 → 右键永久「卡住」（须再点一次右键才解除）。
                    // ⚠️ 关闭面板后 `!SuperPanel.IsOpen` 会变 true，故此处必须 return 提前退出，
                    //    绝不能落到下面的长按手势分支——否则 up 会被吞 + 注入，重新制造上述卡键。
                    if ((int)wParam == Win32.WmRbuttonup && _rightCloseUpPending)
                    {
                        _rightCloseUpPending = false;
                        return (IntPtr)1; // 吞掉配套 up（成对）
                    }
                    if ((int)wParam == Win32.WmRbuttondown)
                    {
                        _rightCloseUpPending = false;   // 兜底：上个 up 若丢失，清残留避免误吞

                        bool overSuper = SuperPanel.IsOpen && !SuperPanel.IsInteracting
                                         && !SuperPanel.PointInWindowRect(ms.Pt.X, ms.Pt.Y);
                        bool overMenu = MenuSystem.IsMenuOpen
                                        && !MenuSystem.PointInMenuRect(ms.Pt.X, ms.Pt.Y);
                        bool overHelp = HelpPanel.IsOpen
                                        && !HelpPanel.PointInWindowRect(ms.Pt.X, ms.Pt.Y);

                        if (overSuper || overMenu || overHelp)
                        {
                            if (overSuper) { CancelSuperPanelGesture(); SuperPanel.Close(); }  // 优先级同左键分支
                            else if (overMenu) MenuSystem.CloseCurrent();
                            else HelpPanel.Close();

                            _rightCloseUpPending = true;
                            return (IntPtr)1; // 吞 down，并提前退出（不进长按手势分支）
                        }
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
                    // 超级面板打开时：点击面板外部 → 关闭面板并放行点击（坐标判定，与 MenuPopup/HelpPanel
                    // 同思路；右键菜单/对话框打开期间 IsInteracting，不介入，避免误关）。
                    if (SuperPanel.IsOpen && !SuperPanel.IsInteracting
                        && !SuperPanel.PointInWindowRect(ms.Pt.X, ms.Pt.Y))
                    {
                        SuperPanel.Close();
                        break; // 不吞点击：放行给光标下窗口
                    }
                    // 菜单打开时：点击面板外部 → 关闭菜单并放行点击（与 HelpPanel 同思路，
                    // 用坐标判定确定性关闭，不依赖 Deactivated/前台状态，避免切换组后与
                    // 旧窗口淡出动画的激活竞态导致点击外部不触发关闭）。点击放行给下层窗口。
                    if (MenuSystem.IsMenuOpen && !MenuSystem.PointInMenuRect(ms.Pt.X, ms.Pt.Y))
                    {
                        MenuSystem.CloseCurrent();
                        break; // 不吞点击：放行给光标下窗口
                    }
                    // 帮助面板打开时：点击面板外部 → 关闭面板并放行点击。
                    // 不依赖 Deactivated/前台状态——Activate() 因前台权限/时序竞争会间歇性失败，
                    // 导致面板非活动窗口时点击外部不触发 Deactivated。改用坐标判定确定性关闭，
                    // 对应 AHK 轮询前台窗口的“点击外部即关”语义。点击放行给下层窗口。
                    if (HelpPanel.IsOpen && !HelpPanel.PointInWindowRect(ms.Pt.X, ms.Pt.Y))
                    {
                        HelpPanel.Close();
                        break; // 不吞点击：放行给光标下窗口
                    }
                    if (AppState.IsToolEnabled && AppState.IsCapsLockDown
                        && !MenuSystem.IsMenuOpen && !HelpPanel.IsOpen && !SuperPanel.IsOpen)
                    {
                        // 吞掉左键，异步执行点击+重命名（对应 lib/Workspace.ahk LButton 热键）
                        AppState.OtherKeyPressed = true;
                        StartRenameSequence(ms.Pt);
                        return (IntPtr)1;
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
    private static void CancelSuperPanelGesture() => _superPanelTimer?.Stop();

    /// <summary>长按成立：光标处弹面板，并标记吞掉随后的右键 up 阻止原生菜单。</summary>
    private static void OnSuperPanelTimerTick(object? sender, EventArgs e)
    {
        _superPanelTimer?.Stop();
        _suppressRButtonUp = true;
        SuperPanel.Show();
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

    // —— 资源管理器重命名（对应 lib/Workspace.ahk：LButton / PerformClick / LButtonRenamer）——

    private static void StartRenameSequence(Win32.Point cursorAtTrigger)
    {
        // 触发瞬间捕获前台窗口与光标下窗口的类名（决定是否需要先激活）
        string activeClass = GetWindowClass(Win32.GetForegroundWindow());
        IntPtr mouseWin = TopLevelFromPoint(cursorAtTrigger);
        string mouseClass = GetWindowClass(mouseWin);
        bool needActivate = IsExplorerBrowserOwnerCase(activeClass, mouseClass);

        var t = new Thread(() =>
        {
            try
            {
                if (needActivate)
                {
                    // 先激活光标下窗口，再点击，再重命名
                    Win32.SetForegroundWindow(mouseWin);
                    Thread.Sleep(40);
                    DoLeftClick();
                    Thread.Sleep(20);
                    TryRenameUnderCursor();
                }
                else
                {
                    DoLeftClick();
                    Thread.Sleep(20);
                    TryRenameUnderCursor();
                }
            }
            catch { /* 重命名失败静默降级 */ }
        }) { IsBackground = true };
        t.Start();
    }

    /// <summary>注入一次左键点击（mouse_event；注入事件被本钩子忽略，不递归）。</summary>
    private static void DoLeftClick()
    {
        Win32.mouse_event(Win32.MouseeventfLeftdown, 0, 0, 0, IntPtr.Zero);
        Win32.mouse_event(Win32.MouseeventfLeftup, 0, 0, 0, IntPtr.Zero);
    }

    /// <summary>取当前光标下窗口的类名；若为资源管理器/桌面类 → 注入 F2 进入重命名。</summary>
    private static void TryRenameUnderCursor()
    {
        Win32.GetCursorPos(out var pt);
        IntPtr hwnd = TopLevelFromPoint(pt);
        if (hwnd == IntPtr.Zero) return;
        string cls = GetWindowClass(hwnd);
        if (cls == "CabinetWClass" || cls == "ExploreWClass" ||
            cls == "Progman" || cls == "WorkerW" || cls == "ExplorerBrowserOwner")
        {
            InputHelper.Tap((ushort)Win32.VkF2);
        }
    }

    /// <summary>取光标下顶层拥有者窗口（<see cref="Win32.WindowFromPoint"/> 返回子控件如列表视图，
    /// 需上溯到顶层；否则类名是子控件类名而非 CabinetWClass/Progman 等，重命名判定永不命中）。
    /// 对应 AHK <c>MouseGetPos</c> 第三输出返回的顶层窗口。</summary>
    private static IntPtr TopLevelFromPoint(Win32.Point pt)
    {
        var raw = Win32.WindowFromPoint(pt);
        if (raw == IntPtr.Zero) return IntPtr.Zero;
        var root = Win32.GetAncestor(raw, Win32.GaRootOwner);
        return root != IntPtr.Zero ? root : raw;
    }

    private static bool IsExplorerBrowserOwnerCase(string activeClass, string mouseClass)
    {
        bool a = activeClass == "ExplorerBrowserOwner";
        bool m = mouseClass == "ExplorerBrowserOwner";
        return a ^ m; // 恰好一方是 ExplorerBrowserOwner
    }

    private static string GetWindowClass(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        Win32.GetClassName(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
