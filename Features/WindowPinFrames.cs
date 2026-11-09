using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「窗口置顶」的高亮边框：给每个由 LTools 置顶的窗口配一条 2px 蓝色描边。
/// </summary>
/// <remarks>
/// <para>
/// 用一条独立的置顶小窗画边框（<see cref="PinBorderWindow"/>），跟着目标窗口移动 / 缩放；
/// 目标最小化或关闭时自动收起。窗口管理器本身没有「这个窗口被置顶了」的视觉状态，
/// 不加提示很容易忘了自己置顶过谁。
/// </para>
/// <para>
/// 跟位置走 <see cref="Win32.SetWinEventHook"/> 的 <c>EVENT_OBJECT_LOCATIONCHANGE</c>：拖动 / 缩放时
/// 目标窗口每移动一次就立刻摆一次边框，肉眼看不到滞后。以前只靠 50ms 轮询，拖窗口时边框一格一格
/// 追、还能看到拖影。低频计时器只留作保洁（显隐、关闭、z 序），不再负责跟位置。
/// </para>
/// <para>
/// z 序：目标窗口被激活时会升到置顶层最前面、把边框盖住，这时才把边框再提上去；
/// 平时不抢 z 序，免得 LTools 自己的面板 / 悬浮被边框压住。
/// </para>
/// </remarks>
internal static class WindowPinFrames
{
    private static readonly Dictionary<IntPtr, PinBorderWindow> Frames = new();

    /// <summary>只做保洁的低频计时器（跟位置靠 WinEvent）。</summary>
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMilliseconds(120);

    private static DispatcherTimer? _timer;
    private static Win32.WinEventProc? _hookProc;
    private static IntPtr _hook;

    /// <summary>拖动时事件会连着来，攒到下一次渲染前统一摆一次，别让 SetWindowPos 排队堆积。</summary>
    private static readonly HashSet<IntPtr> PendingFollows = new();
    private static bool _followQueued;

    /// <summary>这个窗口现在有提示边框吗。</summary>
    public static bool Contains(IntPtr hwnd) => Frames.ContainsKey(hwnd);

    /// <summary>开始为某个窗口画边框（重复调用无副作用）。</summary>
    public static void Add(IntPtr hwnd) => OnUi(() =>
    {
        if (hwnd == IntPtr.Zero || Frames.ContainsKey(hwnd)) return;

        var frame = new PinBorderWindow();
        Frames[hwnd] = frame;
        try
        {
            frame.Show();
        }
        catch (Exception ex)
        {
            AppLog.Warn("WindowPin", "置顶边框创建失败：" + ex.Message);
            Frames.Remove(hwnd);
            return;
        }

        EnsureWatching();
        Follow(hwnd);
        EnsureTimer();
    });

    /// <summary>取消某个窗口的边框（窗口关闭时也会由保洁计时器自动清掉）。</summary>
    public static void Remove(IntPtr hwnd) => OnUi(() =>
    {
        if (!Frames.Remove(hwnd, out var frame)) return;
        PendingFollows.Remove(hwnd);
        CloseQuietly(frame);
        StopWatchingIfIdle();
        StopTimerIfIdle();
    });

    /// <summary>清掉所有边框（退出 / 关闭全部置顶时用）。</summary>
    public static void Clear() => OnUi(() =>
    {
        foreach (var frame in Frames.Values) CloseQuietly(frame);
        Frames.Clear();
        PendingFollows.Clear();
        StopWatchingIfIdle();
        StopTimerIfIdle();
    });

    // ============ 跟位置（WinEvent，拖动时也是逐帧跟手）============

    private static void EnsureWatching()
    {
        if (_hook != IntPtr.Zero) return;

        _hookProc ??= OnLocationChanged;
        _hook = Win32.SetWinEventHook(
            Win32.EventObjectLocationChange, Win32.EventObjectLocationChange,
            IntPtr.Zero, _hookProc, 0, 0, Win32.WineventOutofcontext);

        if (_hook == IntPtr.Zero)
            AppLog.Warn("WindowPin", "置顶边框的位置监听安装失败，拖动时只能靠计时器跟手");
    }

    private static void StopWatchingIfIdle()
    {
        if (_hook == IntPtr.Zero || Frames.Count > 0) return;
        Win32.UnhookWinEvent(_hook);
        _hook = IntPtr.Zero;
    }

    /// <summary>窗口矩形变化（拖动 / 缩放 / 动画）：排到下一次渲染前把边框摆过去。</summary>
    private static void OnLocationChanged(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        // 只认顶层窗口本身；子控件的位置变化跟边框无关
        if (idObject != 0 || idChild != 0) return;
        if (!Frames.ContainsKey(hwnd)) return;

        PendingFollows.Add(hwnd);
        if (_followQueued) return;

        // Render 优先级高于输入：下一次处理消息前就摆好，肉眼看不到滞后
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        _followQueued = true;
        dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(DrainFollows));
    }

    private static void DrainFollows()
    {
        _followQueued = false;
        if (PendingFollows.Count == 0) return;

        var batch = new IntPtr[PendingFollows.Count];
        PendingFollows.CopyTo(batch);
        PendingFollows.Clear();

        foreach (var hwnd in batch)
        {
            try { Follow(hwnd); }
            catch (Exception ex) { AppLog.Warn("WindowPin", "置顶边框跟随失败：" + ex.Message); }
        }
    }

    /// <summary>把某条边框贴到目标窗口的可见矩形外侧。</summary>
    private static void Follow(IntPtr hwnd)
    {
        if (!Frames.TryGetValue(hwnd, out var frame)) return;
        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return;

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        if (w <= 0 || h <= 0) return;

        if (!frame.IsVisible) frame.Show();
        // 目标窗口在前台时会盖住边框，这时才把边框提到置顶层最前面
        frame.PlaceOver(x, y, w, h, raise: Win32.GetForegroundWindow() == hwnd);
    }

    // ============ 保洁（显隐 / 关闭 / 补位）============

    private static void EnsureTimer()
    {
        if (_timer != null) return;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = SweepInterval };
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }

    private static void StopTimerIfIdle()
    {
        if (Frames.Count > 0 || _timer == null) return;
        _timer.Stop();
        _timer = null;
    }

    /// <summary>目标没了 / 最小化了就收起；还活着的顺手补一次位置（漏事件时的兜底）。</summary>
    private static void Update()
    {
        if (Frames.Count == 0)
        {
            StopTimerIfIdle();
            return;
        }

        IntPtr foreground = Win32.GetForegroundWindow();
        foreach (var pair in new List<KeyValuePair<IntPtr, PinBorderWindow>>(Frames))
        {
            IntPtr hwnd = pair.Key;
            var frame = pair.Value;

            if (!Win32.IsWindow(hwnd))
            {
                Frames.Remove(hwnd);
                CloseQuietly(frame);
                continue;
            }

            if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd))
            {
                if (frame.IsVisible) frame.Hide();
                continue;
            }

            var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
            if (w <= 0 || h <= 0)
            {
                if (frame.IsVisible) frame.Hide();
                continue;
            }

            if (!frame.IsVisible) frame.Show();
            frame.PlaceOver(x, y, w, h, raise: foreground == hwnd);
        }

        StopWatchingIfIdle();
        StopTimerIfIdle();
    }

    private static void CloseQuietly(Window window)
    {
        try { window.Close(); } catch { /* 已经关了 */ }
    }

    /// <summary>WPF 窗口只能在 UI 线程创建 / 显示；后台线程（动作、钩子）投递过去。</summary>
    private static void OnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        if (dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }
}
