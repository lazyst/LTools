using System.Collections.Concurrent;
using LTools.Core;
using LTools.Native;

namespace LTools.Hooks;

/// <summary>
/// 低级键鼠钩子的专用 STA 线程（2026-11 阶段 2）。
///
/// 原来两个 <c>SetWindowsHookEx(WH_*_LL, dwThreadId: 0)</c> 装在主 UI STA 线程上，回调由 WPF
/// Dispatcher 的消息泵同步派发——设置窗口首帧渲染 / 切页这类同步重活会把输入消息顶在队列里，
/// 实测开窗 p95 141ms、切页 max 297ms（逼近系统摘钩的 300ms），见 <c>cache/perf/</c> 的基线。
/// 现在把「安装 + 回调派发」整体搬到这条独立线程：它只跑一个纯 Win32 消息泵，回调里只做
/// 纯数据判定与同步吞 / 放，任何 WPF 副作用走 <see cref="Ui.Post"/> 排回主 UI 线程（不等待）。
///
/// 线程模型要点：
/// - WH_*_LL 回调由「安装它的线程」的消息循环派发，所以这条线程必须有消息泵；
/// - 命令队列 + <c>PostThreadMessage</c> 唤醒：UI 线程改钩子线程自己的状态时用 <see cref="Post"/>，
///   回调与命令在同一条线程上串行执行，不需要给这些状态加锁；
/// - 卸载也必须在安装线程上做（进程退出前泵收尾时执行）。
/// </summary>
internal static class HookDispatcher
{
    private static readonly ConcurrentQueue<Action> Commands = new();
    private static Thread? _thread;
    private static uint _threadId;
    private static int _managedThreadId;
    private static ManualResetEventSlim? _ready;
    private static volatile bool _started;
    private static volatile bool _stopped;

    /// <summary>当前线程是不是钩子线程（只允许在诊断 / 断言里用，别拿它做业务逻辑分支）。</summary>
    public static bool IsHookThread => Environment.CurrentManagedThreadId == _managedThreadId;

    /// <summary>启动专用线程并等钩子装好（安装只是两次 P/Invoke，最多等 2 秒）。</summary>
    public static void Start()
    {
        if (_started) return;
        _started = true;
        _ready = new ManualResetEventSlim(false);
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "HookDispatcher" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(2));
    }

    private static void ThreadMain()
    {
        _managedThreadId = Environment.CurrentManagedThreadId;
        // 强制创建线程消息队列：PostThreadMessage 要求目标线程已经有队列，否则会失败。
        Win32.PeekMessage(out _, IntPtr.Zero, 0, 0, Win32.PmNoremove);
        _threadId = Win32.GetCurrentThreadId();

        // 先预热回调 JIT（把首次编译提前到「钩子刚装好、还没有真实事件」这一刻），再安装。
        try
        {
            KeyboardHook.WarmUp();
            MouseHook.WarmUp();
            KeyboardHook.Install();
            AppLog.Info("Hook", "键盘钩子已安装（HookDispatcher）");
        }
        catch (Exception ex) { CrashLog.Write("KeyboardHookInstall", ex); }
        try
        {
            MouseHook.Install();
            AppLog.Info("Hook", "鼠标钩子已安装（HookDispatcher）");
        }
        catch (Exception ex) { CrashLog.Write("MouseHookInstall", ex); }

        _ready?.Set();

        while (true)
        {
            int r = Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (r <= 0) break;   // 0 = WM_QUIT，-1 = 出错，都收尾
            if (msg.Message == Win32.WmAppHookCommand) DrainCommands();
            else
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }
        }

        // 卸载必须在安装线程；泵退出前顺手把残余命令排空。
        try
        {
            KeyboardHook.Uninstall();
            MouseHook.Uninstall();
            AppLog.Info("Hook", "键鼠钩子已卸载（HookDispatcher）");
        }
        catch (Exception ex) { CrashLog.Write("HookUninstall", ex); }
        DrainCommands();
    }

    private static void DrainCommands()
    {
        while (Commands.TryDequeue(out var cmd))
        {
            try { cmd(); }
            catch (Exception ex) { CrashLog.Write("HookCommand", ex); }
        }
    }

    /// <summary>
    /// 把一段工作排到钩子线程（在回调之间串行执行），立刻返回、不等待。
    /// 用于改「只属于钩子线程」的状态：点选器让路、清 pending 手势、长按计时成立等。
    /// </summary>
    public static void Post(Action action)
    {
        if (_stopped) return;
        Commands.Enqueue(action);
        if (_threadId != 0)
            Win32.PostThreadMessage(_threadId, Win32.WmAppHookCommand, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>退出：排一条 PostQuitMessage 让泵收尾卸载。绝不在 UI 线程上 Join 等它。</summary>
    public static void Stop()
    {
        if (!_started || _stopped) return;
        Post(() => Win32.PostQuitMessage(0));
        _stopped = true;
    }
}
