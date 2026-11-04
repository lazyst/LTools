using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 宏回放器。按事件时间戳在后台线程逐条注入键盘 / 鼠标事件；
/// 可被设置页的「停止回放」按钮取消，结束时强制释放所有按下未松开的键与鼠标键。
/// </summary>
/// <remarks>
/// 线程模型：回放线程是普通后台线程，绝不在钩子回调里 Sleep / 注入。
/// 注入事件带 <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c>，会被 LTools 自己的钩子忽略，
/// 因此回放不会再次触发 LTools 的 CapsLock 增强或录制，天然防递归。
/// </remarks>
internal static class MacroPlayer
{
    /// <summary>本类注入鼠标事件的 dwExtraInfo 标记（与钩子的 LLMHF_INJECTED 双保险）。</summary>
    private static readonly IntPtr InjectedTag = (IntPtr)0x1234ABCD;

    private static readonly ManualResetEventSlim _wake = new(false);
    private static volatile bool _stop;
    private static int _playing;

    public static bool IsPlaying => _playing != 0;

    /// <summary>按 Id 异步回放（不阻塞调用方）。</summary>
    public static void PlayAsync(string id)
    {
        if (Interlocked.Exchange(ref _playing, 1) != 0) return;

        _stop = false;
        _wake.Reset();

        Task.Run(() =>
        {
            try
            {
                var macro = MacroStore.Load(id);
                if (macro == null)
                {
                    _playing = 0;
                    TrayService.Notify("宏不存在或读取失败");
                    return;
                }
                PlayCore(macro, EffectiveSpeed(macro.Id));
            }
            catch (Exception ex)
            {
                _playing = 0;
                CrashLog.Write("MacroPlayer.PlayAsync", ex);
                TrayService.Notify("宏回放失败，详见崩溃日志");
            }
        });
    }

    /// <summary>同步回放（供 <see cref="ActionExecutor"/> 在后台线程按步骤顺序调用）。</summary>
    public static void PlaySync(MacroDto macro, double speed = 1.0)
    {
        if (Interlocked.Exchange(ref _playing, 1) != 0)
            throw new InvalidOperationException("已有宏正在回放");

        _stop = false;
        _wake.Reset();
        PlayCore(macro, speed);
    }

    /// <summary>某个宏实际使用的回放速度：专属速度优先，否则用全局默认。</summary>
    public static double EffectiveSpeed(string? macroId)
    {
        double speed = string.IsNullOrEmpty(macroId) ? 0 : MacroStore.SpeedOf(macroId);
        return speed > 0 ? speed : MacroSettings.DefaultSpeed;
    }

    /// <summary>请求停止回放；回放线程会在下一个等待点退出并释放按键。</summary>
    public static void Stop()
    {
        _stop = true;
        _wake.Set();
    }

    private static void PlayCore(MacroDto macro, double speed)
    {
        var keys = new Dictionary<ushort, bool>();
        var buttons = new HashSet<int>();
        var map = ScreenMap.For(macro);

        try
        {
            if (MacroSettings.PlaybackCountdownMs > 0)
                Countdown(MacroSettings.PlaybackCountdownMs);

            var sw = Stopwatch.StartNew();
            var events = macro.Events ?? new List<MacroEvent>();
            int loops = Math.Clamp(MacroSettings.PlayLoops, 1, 99);

            for (int lap = 0; lap < loops && !_stop; lap++)
            {
                // 每一轮都从 0 重新计时（首轮沿用已运行的 Stopwatch，保持倒计时后的起点）。
                if (lap > 0) sw.Restart();

                foreach (var e in events)
                {
                    int target = (int)(e.TimeMs / Math.Max(0.1, speed));
                    if (!WaitUntil(sw, target)) break;

                    // Stop 可能在等待返回后、注入前到达。
                    if (_stop) break;
                    Inject(e, keys, buttons, map);
                }

                if (events.Count == 0) break;

                // 每轮之间补足录制时长里末尾的静默（DurationMs 可能大于最后一个事件的时间）。
                if (lap < loops - 1 && !WaitUntil(sw, (int)(macro.DurationMs / Math.Max(0.1, speed)))) break;
            }
        }
        finally
        {
            // 无论正常结束、异常还是中途停止，都把按下的键 / 鼠标键释放掉。
            foreach (var kv in keys)
                InputHelper.UpEx(kv.Key, kv.Value);
            foreach (var b in buttons)
                MouseUp(b);

            _playing = 0;
            _wake.Reset();
        }
    }

    /// <summary>等到相对开始时间的 targetMs；可被 <see cref="Stop"/> 立即唤醒。</summary>
    private static bool WaitUntil(Stopwatch sw, int targetMs)
    {
        while (true)
        {
            if (_stop) return false;

            int remain = targetMs - (int)sw.ElapsedMilliseconds;
            if (remain <= 0) return true;

            // 大间隔用可中断等待；最后几毫秒自旋，避免 Thread.Sleep 的 ~15ms 粒度。
            if (remain > 4) _wake.Wait(remain - 3);
            else Thread.SpinWait(200);
        }
    }

    private static void Countdown(int totalMs)
    {
        int remaining = totalMs;
        while (remaining > 0 && !_stop)
        {
            string text = ((int)Math.Ceiling(remaining / 1000.0)).ToString();
            Application.Current?.Dispatcher.BeginInvoke(() => RecordingIndicatorWindow.ShowCountdown(text));

            int slice = Math.Min(200, remaining);
            _wake.Wait(slice);
            remaining -= slice;
        }
        Application.Current?.Dispatcher.BeginInvoke(() => RecordingIndicatorWindow.HideIndicator());
    }

    private static void Inject(MacroEvent e, Dictionary<ushort, bool> keys, HashSet<int> buttons, ScreenMap map)
    {
        bool extended = (e.Flags & Win32.LlkhfExtended) != 0;

        switch (e.Kind)
        {
            case MacroEventKind.KeyDown:
                InputHelper.DownEx(e.Vk, extended);
                keys[e.Vk] = extended;
                break;
            case MacroEventKind.KeyUp:
                InputHelper.UpEx(e.Vk, extended);
                keys.Remove(e.Vk);
                break;
            case MacroEventKind.MouseMove:
                map.MoveTo(e.X, e.Y, InjectedTag);
                break;
            case MacroEventKind.MouseDown:
                MouseDown(e.Data);
                buttons.Add(e.Data);
                break;
            case MacroEventKind.MouseUp:
                MouseUp(e.Data);
                buttons.Remove(e.Data);
                break;
            case MacroEventKind.Wheel:
                Win32.mouse_event(Win32.MouseeventfWheel, 0, 0, unchecked((uint)e.Data), InjectedTag);
                break;
            case MacroEventKind.HWheel:
                Win32.mouse_event(Win32.MouseeventfHwheel, 0, 0, unchecked((uint)e.Data), InjectedTag);
                break;
        }
    }

    /// <summary>
    /// 录制时与回放时的虚拟桌面坐标映射。分辨率 / 显示器布局变了就按比例缩放，
    /// 否则原样使用；旧宏（未记录桌面尺寸）按原坐标回放。
    /// </summary>
    private readonly struct ScreenMap
    {
        private readonly int _srcL, _srcT, _srcW, _srcH;
        private readonly int _dstL, _dstT, _dstW, _dstH;
        private readonly bool _scale;

        private ScreenMap(int srcL, int srcT, int srcW, int srcH,
                          int dstL, int dstT, int dstW, int dstH, bool scale)
        {
            _srcL = srcL; _srcT = srcT; _srcW = srcW; _srcH = srcH;
            _dstL = dstL; _dstT = dstT; _dstW = dstW; _dstH = dstH;
            _scale = scale;
        }

        public static ScreenMap For(MacroDto macro)
        {
            int dstL = Win32.GetSystemMetrics(Win32.SmXVirtualscreen);
            int dstT = Win32.GetSystemMetrics(Win32.SmYVirtualscreen);
            int dstW = Win32.GetSystemMetrics(Win32.SmCxVirtualscreen);
            int dstH = Win32.GetSystemMetrics(Win32.SmCyVirtualscreen);

            int srcL = macro.DesktopLeft;
            int srcT = macro.DesktopTop;
            int srcW = macro.DesktopWidth;
            int srcH = macro.DesktopHeight;

            // 只在尺寸真的变了时缩放；左右 / 上下排列变化不影响绝对坐标。
            bool scale = srcW > 1 && srcH > 1 && dstW > 1 && dstH > 1 &&
                         (srcW != dstW || srcH != dstH);

            return new ScreenMap(srcL, srcT, srcW, srcH, dstL, dstT, dstW, dstH, scale);
        }

        public void MoveTo(int x, int y, IntPtr tag)
        {
            if (_scale)
            {
                x = _dstL + Map(x - _srcL, _dstW - 1, _srcW - 1);
                y = _dstT + Map(y - _srcT, _dstH - 1, _srcH - 1);
                x = Math.Clamp(x, _dstL, _dstL + _dstW - 1);
                y = Math.Clamp(y, _dstT, _dstT + _dstH - 1);
            }
            InputHelper.MoveAbsolute(x, y, _dstL, _dstT, _dstW, _dstH, tag);
        }

        private static int Map(int offset, int dstSpan, int srcSpan)
            => srcSpan <= 0 ? 0 : (int)Math.Round(offset * (double)dstSpan / srcSpan);
    }

    private static void MouseDown(int button)
    {
        switch (button)
        {
            case 1: Win32.mouse_event(Win32.MouseeventfLeftdown, 0, 0, 0, InjectedTag); break;
            case 2: Win32.mouse_event(Win32.MouseeventfRightdown, 0, 0, 0, InjectedTag); break;
            case 3: Win32.mouse_event(Win32.MouseeventfMiddledown, 0, 0, 0, InjectedTag); break;
            case 4: Win32.mouse_event(Win32.MouseeventfXdown, 0, 0, (uint)Win32.Xbutton1, InjectedTag); break;
            case 5: Win32.mouse_event(Win32.MouseeventfXdown, 0, 0, (uint)Win32.Xbutton2, InjectedTag); break;
        }
    }

    private static void MouseUp(int button)
    {
        switch (button)
        {
            case 1: Win32.mouse_event(Win32.MouseeventfLeftup, 0, 0, 0, InjectedTag); break;
            case 2: Win32.mouse_event(Win32.MouseeventfRightup, 0, 0, 0, InjectedTag); break;
            case 3: Win32.mouse_event(Win32.MouseeventfMiddleup, 0, 0, 0, InjectedTag); break;
            case 4: Win32.mouse_event(Win32.MouseeventfXup, 0, 0, (uint)Win32.Xbutton1, InjectedTag); break;
            case 5: Win32.mouse_event(Win32.MouseeventfXup, 0, 0, (uint)Win32.Xbutton2, InjectedTag); break;
        }
    }
}
