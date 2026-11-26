using System.Windows;
using LTools.Core;

namespace LTools.Views;

/// <summary>「接收确认」的答复。</summary>
internal enum InboundAnswer
{
    /// <summary>本机点了「允许接收」。</summary>
    Allowed,

    /// <summary>本机点了「取消」（或没有界面可问）。</summary>
    Declined,

    /// <summary>等到超时没人点。</summary>
    Timeout,

    /// <summary>前一个确认还挂着，排队的太多。</summary>
    Busy,
}

/// <summary>
/// 「需要确认」模式下，对方发来内容时在本机弹一次确认框（见 <c>docs/LAN_SHARE.md</c> §3.3）。
/// </summary>
/// <remarks>
/// <para>
/// 调用方是 Kestrel 请求线程，这里只做两件事：**投到 UI 线程**弹
/// <see cref="ConfirmDialog"/>，然后等答复。一次只问一个（后来的排队），
/// 单次最多等 <see cref="PromptMs"/>；超时或对端断开都当拒绝，并把这扇模态框收掉——
/// 否则它会一直压在界面上。
/// </para>
/// <para>
/// 确认框**没有 Owner、自己置顶**：共享窗口常被别的程序盖着（它就是留着后台跑的），
/// 挂到它身上当子窗口，用户根本看不见这一问——那「需要确认」就等于不收。
/// </para>
/// </remarks>
internal static class ShareInboundPrompt
{
    /// <summary>单次确认最多等多久（没人点就算拒绝）。</summary>
    public const int PromptMs = 60_000;

    /// <summary>排队等前一个确认最多多久（超了直接回「忙」）。</summary>
    public const int QueueMs = 20_000;

    private static readonly SemaphoreSlim Turn = new(1, 1);

    /// <summary>冒烟用：有它就不弹框、直接按它答（控制台里没有 UI 线程也没有人点）。</summary>
    public static Func<string?, string, bool>? AutoAnswer { get; set; }

    /// <summary>
    /// 问一次。<paramref name="what"/> 是「发来的是什么」（如 <c>文件「报告.pdf」（1.2 MB）</c>），
    /// <paramref name="device"/> 是浏览器报上来的设备名（可能为空）。
    /// </summary>
    public static InboundAnswer Ask(string? device, string what, CancellationToken cancel)
    {
        if (AutoAnswer is { } auto) return auto(device, what) ? InboundAnswer.Allowed : InboundAnswer.Declined;

        // 没有 UI 线程（控制台冒烟 / 启动早期）就没人能点：按拒绝处理，别白等一分钟。
        if (!Ui.Available) return InboundAnswer.Declined;

        try
        {
            if (!Turn.Wait(QueueMs, cancel)) return InboundAnswer.Busy;
        }
        catch (OperationCanceledException)
        {
            return InboundAnswer.Timeout;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            cts.CancelAfter(PromptMs);

            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Ui.Post(() =>
            {
                try { done.TrySetResult(Show(device, what, cts.Token)); }
                catch { done.TrySetResult(false); }
            });

            // ConfirmDialog 会在 cts 触发时自己关掉并返回 false，所以这里正常在 PromptMs 结束；
            // 多给的几秒只是兜底（UI 线程卡住时别把请求线程永远挂着）。
            if (done.Task.Wait(PromptMs + 8_000))
                return done.Task.Result ? InboundAnswer.Allowed : InboundAnswer.Declined;

            cts.Cancel();
            return InboundAnswer.Timeout;
        }
        finally
        {
            Turn.Release();
        }
    }

    private static bool Show(string? device, string what, CancellationToken cancel)
    {
        string who = string.IsNullOrWhiteSpace(device) ? "有设备" : $"「{device.Trim()}」";
        string message =
            $"{who} 想发来{what}。\n\n" +
            "允许接收才会存到本机（文字还会放进剪贴板）。";

        return ConfirmDialog.Confirm(owner: null, "接收确认", message, "允许接收", cancel);
    }
}
