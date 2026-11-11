using System;
using System.Threading;
using System.Threading.Tasks;
using LTools.Native;

namespace LTools.Core;

/// <summary>
/// 前台程序缓存：跟踪 <c>EVENT_SYSTEM_FOREGROUND</c>，按窗口句柄在**后台线程**解析进程名 / 路径。
/// </summary>
/// <remarks>
/// <para>
/// 低级键盘钩子回调不能查进程（<see cref="System.Diagnostics.Process.MainModule"/> 遇到卡死的
/// 进程可能阻塞几百毫秒，超过钩子的 300ms 上限会被系统卸载），所以钩子里只做字符串比较。
/// </para>
/// <para>
/// 解析没跟上时 <see cref="Current"/> 返回 null，名单判定按「放行」处理——宁可按了有效，
/// 也不要按了没反应。
/// </para>
/// </remarks>
internal static class ForegroundAppCache
{
    private static readonly object Gate = new();

    private static IntPtr _hwnd;
    private static ForegroundApp? _app;
    private static IntPtr _pending;
    private static int _resolving;
    private static IntPtr _hook;
    private static Win32.WinEventProc? _proc;   // 保住委托，别被 GC
    private static IntPtr _notifiedHwnd;        // 上次已通知订阅者的前台窗口；同一窗口不重复通知

    /// <summary>
    /// 前台窗口**切换**并解析完成后触发（后台线程）：参数是「新窗口 / 上一个窗口」，都可能为 null。
    /// 自动触发的窗口切换事件源订阅它；启动时的初始解析不通知——那不是一次「切换」，
    /// 否则用户一开 LTools 就会白跑一次动作。同一窗口的补解析（<see cref="Current"/> 触发）
    /// 与系统重复发的 WinEvent 也按窗口句柄去重，不再重复通知。
    /// </summary>
    public static event Action<ForegroundApp?, ForegroundApp?>? ForegroundChanged;

    /// <summary>当前前台窗口的信息；还没解析出来时为 null。</summary>
    public static ForegroundApp? Current
    {
        get
        {
            IntPtr hwnd;
            ForegroundApp? app;
            lock (Gate)
            {
                hwnd = _hwnd;
                app = _app;
            }

            if (app != null && app.Hwnd == hwnd) return app;

            // 缓存还没跟上（启动瞬间 / 事件晚到）：补一次解析，本次放行。
            lock (Gate) _pending = hwnd;
            ScheduleResolve();
            return null;
        }
    }

    /// <summary>开始跟踪（托盘起来之后调用一次）。</summary>
    public static void Start()
    {
        lock (Gate)
        {
            _hwnd = Win32.GetForegroundWindow();
            _pending = _hwnd;
            _app = null;
            _notifiedHwnd = _hwnd;   // 启动时的前台窗口不算「切换」：初始解析静默
        }
        ScheduleResolve();

        if (_hook != IntPtr.Zero) return;
        _proc = OnForegroundChanged;   // 静态方法 + 显式委托，Unhook 前一直有效
        _hook = Win32.SetWinEventHook(Win32.EventSystemForeground, Win32.EventSystemForeground,
            IntPtr.Zero, _proc, 0, 0, Win32.WineventOutofcontext);
    }

    public static void Stop()
    {
        if (_hook == IntPtr.Zero) return;
        try { Win32.UnhookWinEvent(_hook); } catch { /* 退出期忽略 */ }
        _hook = IntPtr.Zero;
        _proc = null;
    }

    private static void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) hwnd = Win32.GetForegroundWindow();
        lock (Gate)
        {
            if (hwnd == _hwnd && _app != null) return;
            _hwnd = hwnd;
            _pending = hwnd;
        }
        ScheduleResolve();
    }

    /// <summary>后台解析；同一时间只跑一条，期间来的窗口只保留最新一个。</summary>
    private static void ScheduleResolve()
    {
        if (Interlocked.Exchange(ref _resolving, 1) == 1) return;

        Task.Run(() =>
        {
            while (true)
            {
                IntPtr hwnd;
                lock (Gate)
                {
                    hwnd = _pending;
                    _pending = IntPtr.Zero;
                    if (hwnd == IntPtr.Zero)
                    {
                        _resolving = 0;
                        return;
                    }
                }

                ForegroundApp? app;
                try { app = ForegroundApp.FromHandle(hwnd); }
                catch (Exception ex)
                {
                    // 不能让单个坏窗口把解析循环打断：_resolving 会一直卡在 1，缓存再也不更新。
                    AppLog.Warn("Foreground", "解析前台窗口失败：" + ex.Message);
                    app = null;
                }

                ForegroundApp? previous;
                bool notify;
                lock (Gate)
                {
                    previous = _app;
                    bool stillCurrent = _hwnd == hwnd;   // 解析期间又切了窗口，旧结果就别用
                    if (stillCurrent) _app = app;
                    // 只有「前台窗口真的换了 + 解析出快照」才通知一次：Current 的补解析、
                    // 系统对同一窗口重复发的 WinEvent 都不再惊动订阅者（自动触发的
                    // window.foreground 曾因此在没换程序时反复误触发）。
                    notify = stillCurrent && app != null && hwnd != _notifiedHwnd;
                    if (notify) _notifiedHwnd = hwnd;
                }
                if (notify)
                {
                    // 订阅者抛异常同样不能拖垮循环（照上面 FromHandle 的坑）。
                    try { ForegroundChanged?.Invoke(app, previous); }
                    catch (Exception ex) { AppLog.Warn("Foreground", "前台切换订阅者异常：" + ex.Message); }
                }
            }
        });
    }
}
