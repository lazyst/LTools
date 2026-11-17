using System;

namespace LTools.Features.CapturePro;

/// <summary>
/// 自动滚动的节奏：按「抓帧 + 拼接」的实测耗时，算出「这一格滚完要等多久」。
/// </summary>
/// <remarks>
/// <para>
/// 照 snow-shot 的 <c>AdaptiveScrollingCaptureCadence</c>
/// （<c>snow_shot/src/presentation/capture/adaptivescrollingcapturecadence.h</c>）：
/// ① 抓帧与拼接各自记一份 EWMA（权重 0.25），取两者的较大值当「流水线成本」；
/// ② 可持续间隔 = 成本 × 1.25（留 25% 余量），再夹进
///    [<see cref="MinIntervalMs"/>, <see cref="MaxIntervalMs"/>]；
/// ③ 连续「没动 / 认不出重叠」时按次数退避——页面被别的滚动条、惯性滚动、懒加载抢走时，
///    滚得越快越拼不上，慢下来反而能接住。
/// </para>
/// <para>
/// 为什么不照抄 FPS：snow-shot 是「连续抓帧 + 邮箱丢旧帧」的流式管道，帧率就是节奏；
/// 我们是「投一格 → 等落定 → 抓帧 → 拼接」的步进循环，**间隔**才是能直接用的量。
/// 换算关系仍是 <c>间隔 ≈ 1000 / 可持续帧率</c>，两边口径一致。
/// </para>
/// <para>
/// 纯逻辑（不碰窗口、不碰计时器），<c>--selftest</c> 用假耗时直接断言。
/// </para>
/// </remarks>
internal sealed class ScrollCadence
{
    /// <summary>间隔下限（毫秒）：再快页面也来不及落定（snow-shot 的自动滚动下限是 128ms）。</summary>
    public const int MinIntervalMs = 120;

    /// <summary>间隔上限（毫秒）：再慢就成「一格一格挪」了。</summary>
    public const int MaxIntervalMs = 1600;

    /// <summary>成本之外留多少余量（1.25 = 留 25%）。</summary>
    public const double Headroom = 1.25;

    /// <summary>退避最多累计几档（再多也没意义：已经是 MaxInterval 了）。</summary>
    public const int MaxPenalty = 3;

    /// <summary>EWMA 的采样权重（0.25 = 更看重历史，单帧抖动不会把节奏带跑）。</summary>
    private const double SampleWeight = 0.25;

    /// <summary>每档退避把间隔拉长多少（0.35 = 一档 +35%）。</summary>
    private const double PenaltyFactor = 0.35;

    private double _capture;
    private double _captureLatest;
    private double _stitch;
    private double _stitchLatest;

    /// <summary>抓帧耗时的 EWMA（毫秒）；0 = 还没有样本。</summary>
    public double CaptureMs => _capture;

    /// <summary>拼接耗时的 EWMA（毫秒）；0 = 还没有样本。</summary>
    public double StitchMs => _stitch;

    /// <summary>流水线成本（毫秒）：抓帧与拼接取大的那个（两者串行，瓶颈就是它）。</summary>
    public double CostMs => Math.Max(Math.Max(_capture, _captureLatest), Math.Max(_stitch, _stitchLatest));

    /// <summary>当前退避档数（0 = 一切正常）。</summary>
    public int Penalty { get; private set; }

    /// <summary>回到「还没量过任何东西」的状态（每次开始自动滚动 / 换目标窗口时调）。</summary>
    public void Reset()
    {
        _capture = 0;
        _captureLatest = 0;
        _stitch = 0;
        _stitchLatest = 0;
        Penalty = 0;
    }

    /// <summary>记一次抓帧耗时（毫秒）。</summary>
    public void RecordCapture(double milliseconds) => Sample(milliseconds, ref _capture, ref _captureLatest);

    /// <summary>记一次拼接耗时（毫秒）。</summary>
    public void RecordStitch(double milliseconds) => Sample(milliseconds, ref _stitch, ref _stitchLatest);

    /// <summary>
    /// 记一次「这一格有没有接上内容」：<paramref name="moved"/> = false（没动 / 认不出重叠）时
    /// 退避一档，true 时立刻恢复。到底了不用调这个——那是「滚完了」，不是「拼不上」。
    /// </summary>
    public void NoteProgress(bool moved)
    {
        if (moved)
        {
            Penalty = 0;
            return;
        }

        if (Penalty < MaxPenalty) Penalty++;
    }

    /// <summary>
    /// 下一格滚轮该等多久（毫秒）。
    /// <paramref name="userInterval"/> = 用户/默认的节奏，<paramref name="overheadMs"/> = 投完滚轮还要等的
    /// 落定时间（<c>ScrollSettleMs</c>）：成本与落定都算进这一格里，才不会被「等页面落定」吃掉余量。
    /// </summary>
    public int NextInterval(int userInterval, int overheadMs)
    {
        int cost = (int)Math.Ceiling(CostMs * Headroom);
        int baseInterval = Math.Max(Math.Max(0, userInterval), cost + Math.Max(0, overheadMs));
        double backoff = 1 + PenaltyFactor * Penalty;

        int interval = (int)Math.Round(baseInterval * backoff, MidpointRounding.AwayFromZero);
        return Math.Clamp(interval, MinIntervalMs, MaxIntervalMs);
    }

    private static void Sample(double milliseconds, ref double ewma, ref double latest)
    {
        if (double.IsNaN(milliseconds) || milliseconds < 0) return;

        latest = milliseconds;
        if (latest <= 0) return;

        ewma = ewma <= 0 ? latest : ewma * (1 - SampleWeight) + latest * SampleWeight;
    }
}
