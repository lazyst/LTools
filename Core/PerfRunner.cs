using System.Windows;
using System.Windows.Threading;
using LTools.Features;
using LTools.Native;

namespace LTools.Core;

/// <summary>
/// <c>--perf</c> 的自动采样驱动：不需要人操作鼠标，自己反复「开窗 → 切页 → 关窗 → 空闲」。
/// 后台探针线程每 6ms 注入一个 ±1px 的鼠标移动（净位移 0），主 UI 线程上的 DispatcherTimer
/// 每 350ms 推进一步并记下相位时间轴；跑完导出 JSON 并退出。
///
/// 之所以能自动化：WH_*_LL 的回调由安装线程的消息泵派发，注入事件和真实硬件事件走同一条队列，
/// 所以「探针从生成到进入回调」的差值就是 UI 线程忙时输入消息的真实排队延迟。
/// 相位归因按事件自带的时间戳落在哪个窗口判定（见 PerfProbe.SetPhase），不依赖 dwExtraInfo。
/// </summary>
internal static class PerfRunner
{
    // 用具体子页 tag，避免 NavigateTo 落到「上次停留子页」造成每轮切的不是同一页。
    private static readonly string[] Tags =
    {
        "actions", "hotkeys", "macros-list", "superpanel-scenes", "ai-connect",
        "triggers", "logs-recent", "subactions", "terminals", "general", "backup",
    };

    private const int Rounds = 2;
    private const int StepsPerTag = 4;   // 0 开窗 / 1 切页 / 2 关窗 / 3 空闲基线

    private static volatile bool _running = true;
    private static int _step;
    private static DispatcherTimer? _timer;
    private static Thread? _probe;

    public static bool IsRequested(string[] args)
        => Array.Exists(args, a => string.Equals(a, "--perf", StringComparison.OrdinalIgnoreCase))
           || Environment.GetEnvironmentVariable("LTOOLS_PERF") == "1";

    /// <summary>钩子装好、各模块就绪之后调用。</summary>
    public static void Start()
    {
        PerfProbe.Enabled = true;
        PerfProbe.Reset();
        AppLog.Info("Perf", "诊断采样启动：探针 6ms，UI 步进 350ms");

        _probe = new Thread(ProbeLoop) { IsBackground = true, Name = "PerfProbe" };
        _probe.Start();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(350),
        };
        _timer.Tick += Tick;
        _timer.Start();
    }

    private static void ProbeLoop()
    {
        uint dx = 1;
        while (_running)
        {
            // MOUSEEVENTF_MOVE + 交替 +1/-1：净位移为 0，不会把光标推走。
            // -1 必须写成 0xFFFFFFFFu（相对移动的 dx 按有符号 LONG 解释），别用 (uint)(-1) 依赖编译选项。
            Win32.mouse_event(Win32.MouseeventfMove, dx, 0, 0, IntPtr.Zero);
            dx = dx == 1 ? 0xFFFFFFFFu : 1;
            Thread.Sleep(6);
        }
    }

    private static void Tick(object? sender, EventArgs e)
    {
        int total = Tags.Length * StepsPerTag * Rounds;
        if (_step >= total) { Finish(); return; }

        int phase = _step % StepsPerTag;
        string tag = Tags[(_step / StepsPerTag) % Tags.Length];
        try
        {
            switch (phase)
            {
                case 0:
                    PerfProbe.SetPhase(1);
                    ConfigHelper.PerfOpen();
                    break;
                case 1:
                    PerfProbe.SetPhase(2);
                    ConfigHelper.PerfNavigate(tag);
                    break;
                case 2:
                    PerfProbe.SetPhase(0);
                    ConfigHelper.PerfClose();
                    break;
                default:
                    PerfProbe.SetPhase(0);   // 空闲基线
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("Perf", "采样步异常，提前收尾：" + ex);
            Finish();
            return;
        }
        _step++;
        if (_step >= total) Finish();
    }

    private static void Finish()
    {
        if (!_running) return;
        _running = false;
        _timer?.Stop();
        PerfProbe.SetPhase(0);
        try
        {
            string path = PerfProbe.Save();
            AppLog.Info("Perf", "采样完成，结果：" + path);
        }
        catch (Exception ex)
        {
            AppLog.Error("Perf", "导出失败：" + ex);
        }
        Application.Current.Shutdown(0);
    }
}
