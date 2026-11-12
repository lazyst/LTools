using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LTools.Core;

/// <summary>
/// 输入链路量化诊断（默认关闭，只有 <c>--perf</c> / <c>LTOOLS_PERF=1</c> 才启用）。
///
/// 一把尺子量两件事，阶段 2 前后用同一套探针复测：
/// ① 钩子排队延迟：<c>PerfRunner</c> 在后台线程周期性注入一个鼠标移动事件，钩子回调在
///    **注入过滤之前**用 <c>MSLLHOOKSTRUCT.time</c>（= 事件生成时的 GetTickCount）与进入回调时的
///    <c>Environment.TickCount</c> 求差。UI 线程被同步重活占住时，差就是「这条输入消息在队列里
///    等了多久」；钩子搬到专用线程后，这个差应当回落到基线。
/// ② UI 分段耗时：<see cref="Measure"/> 包住开窗构造与逐页切换，按名字汇总 p50/p95/max。
///
/// 归因不依赖 <c>dwExtraInfo</c>（实测 mouse_event 的该参数在低层钩子里不可靠），而是按事件
/// 自带的 <c>time</c> 落在哪个「相位窗口」判定：即使回调被延迟到相位结束之后才执行，样本也
/// 仍归到造成延迟的那一段。相位切换由 UI 驱动调 <see cref="SetPhase"/> 记录时间轴。
/// 数据只写 <c>cache/perf/</c>（运行缓存，不进备份）；关闭时打点只有一次 volatile 读。
/// </summary>
internal static class PerfProbe
{
    /// <summary>总开关。关闭时 <c>OnHook</c> / <c>Measure</c> 都只做一次 volatile 读。</summary>
    public static volatile bool Enabled;

    /// <summary>当前相位：0 空闲 / 1 开窗 / 2 切页（供外部观察，不参与归因）。</summary>
    public static volatile int Phase;

    private const int Capacity = 1 << 18;
    private static readonly int[] Latency = new int[Capacity];
    private static readonly byte[] Phases = new byte[Capacity];
    private static int _next;
    private static int _seen;
    private static long _started;

    // 相位时间轴（Environment.TickCount 单位）。SetPhase 由 UI 线程写，OnHook 由钩子线程读。
    private static volatile int _phaseStart;
    private static volatile int _phase;
    private static volatile int _prevStart;
    private static volatile int _prevPhase;

    private static readonly object SegGate = new();
    private static readonly Dictionary<string, List<double>> Segments = new(StringComparer.Ordinal);

    public static void Reset()
    {
        _started = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _next, 0);
        Interlocked.Exchange(ref _seen, 0);
        lock (SegGate) Segments.Clear();
        _phaseStart = _prevStart = Environment.TickCount;
        _phase = _prevPhase = 0;
        Phase = 0;
    }

    /// <summary>切换相位（UI 驱动每一步开头调用），记下时间轴上的一个分界点。</summary>
    public static void SetPhase(int phase)
    {
        int now = Environment.TickCount;
        _prevStart = _phaseStart;
        _prevPhase = _phase;
        _phaseStart = now;
        _phase = phase;
        Phase = phase;
    }

    /// <summary>事件生成时刻属于哪个相位。用 unchecked 差值处理 32 位 TickCount 回绕。</summary>
    private static int PhaseAt(int msgTime)
    {
        int cur = _phase, curStart = _phaseStart;
        if (unchecked(msgTime - curStart) >= 0) return cur;
        int prev = _prevPhase, prevStart = _prevStart;
        if (unchecked(msgTime - prevStart) >= 0) return prev;
        return cur;   // 跨了两个以上相位（极端延迟）：样本极少，归当前
    }

    /// <summary>钩子回调入口调用（必须排在 <c>LLKHF_INJECTED</c> / <c>LLMHF_INJECTED</c> 过滤之前）。</summary>
    public static void OnHook(uint msgTime)
    {
        if (!Enabled) return;
        Interlocked.Increment(ref _seen);

        // GetTickCount 是 32 位毫秒计数，用 unchecked 处理回绕；负值（时钟源差异）按 0 计。
        int delay = unchecked((int)(Environment.TickCount - (int)msgTime));
        if (delay < 0) delay = 0;

        int i = Interlocked.Increment(ref _next) - 1;
        if ((uint)i >= Capacity) return;
        Phases[i] = (byte)PhaseAt((int)msgTime);
        Latency[i] = delay + 1;   // +1：0 表示「还没写」，Dump 时跳过
    }

    /// <summary>包住一段 UI 线程上的同步工作，记录耗时（ms）。</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _name;
        private readonly long _start;

        internal Scope(string name, long start) { _name = name; _start = start; }

        public void Dispose()
        {
            if (_name == null) return;
            double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
            lock (SegGate)
            {
                if (!Segments.TryGetValue(_name, out var list))
                    Segments[_name] = list = new List<double>();
                list.Add(ms);
            }
        }
    }

    public static Scope Measure(string name)
        => Enabled ? new Scope(name, Stopwatch.GetTimestamp()) : default;

    private static double Quantile(double[] sorted, double q)
        => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];

    public static string Dump()
    {
        int count = Math.Min(Volatile.Read(ref _next), Capacity);
        var buckets = new Dictionary<int, List<double>>();
        for (int i = 0; i < count; i++)
        {
            int stored = Latency[i];
            if (stored == 0) continue;
            int phase = Phases[i];
            if (!buckets.TryGetValue(phase, out var list)) buckets[phase] = list = new List<double>();
            list.Add(stored - 1);
        }

        var hook = new List<object>();
        foreach (var kv in buckets.OrderBy(k => k.Key))
        {
            double[] a = kv.Value.OrderBy(x => x).ToArray();
            hook.Add(new
            {
                phase = kv.Key,
                count = a.Length,
                p50 = Math.Round(Quantile(a, 0.50), 2),
                p95 = Math.Round(Quantile(a, 0.95), 2),
                p99 = Math.Round(Quantile(a, 0.99), 2),
                max = Math.Round(a[^1], 2),
            });
        }

        var segs = new List<object>();
        lock (SegGate)
        {
            foreach (var kv in Segments.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                double[] a = kv.Value.OrderBy(x => x).ToArray();
                segs.Add(new
                {
                    name = kv.Key,
                    count = a.Length,
                    p50 = Math.Round(Quantile(a, 0.50), 2),
                    p95 = Math.Round(Quantile(a, 0.95), 2),
                    max = Math.Round(a[^1], 2),
                    total = Math.Round(a.Sum(), 2),
                });
            }
        }

        var payload = new
        {
            capturedAt = DateTime.Now.ToString("o"),
            elapsedMs = Math.Round((Stopwatch.GetTimestamp() - _started) * 1000.0 / Stopwatch.Frequency, 1),
            hookEventsSeen = Volatile.Read(ref _seen),
            hookLatencyMs = hook,
            uiSegmentsMs = segs,
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>导出到 <c>cache/perf/</c>，返回绝对路径。</summary>
    public static string Save()
    {
        string dir = Path.Combine(AppPaths.DataRoot, "cache", "perf");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"perf-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, Dump(), new UTF8Encoding(false));
        return path;
    }
}
