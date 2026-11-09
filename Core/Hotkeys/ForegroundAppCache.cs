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

                var app = ForegroundApp.FromHandle(hwnd);
                lock (Gate)
                {
                    if (_hwnd == hwnd) _app = app;
                }
            }
        });
    }
}
