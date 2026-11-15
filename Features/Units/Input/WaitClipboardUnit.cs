using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>
/// 等剪贴板内容变化（<c>input.waitClipboard</c>）：「复制 → 等结果 → 读剪贴板」的一等公民。
/// </summary>
/// <remarks>
/// <para>
/// 等待类而非命中类：超时返回 <c>changed = false</c>，**不报错**（与 <c>window.wait</c> /
/// <c>input.waitKey</c> 同口径），要不要当失败由调用方用「如果」判。
/// 每轮都走 <see cref="StepContext.Wait"/> + <see cref="StepContext.EnterIteration"/>，按停止热键立刻返回。
/// </para>
/// <para>
/// 读剪贴板走 <see cref="NativeClipboard"/>（原始 Win32），不用 WinForms / OLE。
/// 干跑只判一次、不等待。
/// </para>
/// </remarks>
public sealed class WaitClipboardUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.waitClipboard",
        Name = "等待剪贴板变化",
        Category = "键鼠与输入",
        Icon = "clipboard",
        DryRun = true,
        Kind = UnitKind.pure,
        Description = "等剪贴板内容变化；超时返回 changed = false，不是失败。" +
            "等待结束后 Ctrl/C 可能还没抬起，后续模拟按键前建议加 100–200ms 延迟",
        Inputs = new[]
        {
            new InputSpec("timeoutMs", "最多等", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(10_000),
                Description: "最多等多久（毫秒）：0 = 只查一次；-1 = 一直等到变化（按停止热键结束）；最多 10 分钟"),
            new InputSpec("intervalMs", "检查间隔", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(250), Advanced: true,
                Description: "多久读一次剪贴板（毫秒），最短 20"),
        },
        Outputs = new[]
        {
            new OutputSpec("changed", "变化了", ValueKind.@bool, "clipChanged",
                Description: "超时前剪贴板内容是否变了（超时为假）"),
            new OutputSpec("text", "新内容", ValueKind.text, "clipText",
                Description: "变化后的剪贴板文本；超时为空"),
            new OutputSpec("elapsed", "等了多久", ValueKind.number, "waitElapsed",
                Description: "从开始等到变化（或超时）的毫秒数"),
            new OutputSpec("found", "等到了", ValueKind.@bool,
                Description: "同「变化了」，与等待类单元口径一致"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        long started = Environment.TickCount64;
        int timeout = ctx.DryRun ? 0 : Timeouts.Normalize(args.Number("timeoutMs"));
        long limit = Timeouts.Loop(timeout);
        int interval = (int)Math.Clamp(args.Number("intervalMs"), 20, 60_000);

        // 基线 = 等待开始那一刻的剪贴板文本；之后每次读到不一样就算变化。
        string baseline = ReadClipboard();

        while (true)
        {
            ctx.EnterIteration();
            string current = ReadClipboard();
            bool changed = Changed(baseline, current);
            long elapsed = Environment.TickCount64 - started;

            // 每轮都写输出：超时那一轮写的是「没变化」，配「失败继续」和「如果」都能直接看。
            ctx.SetOutput("changed", Value.FromBool(changed));
            ctx.SetOutput("text", Value.FromText(changed ? current : ""));
            ctx.SetOutput("elapsed", Value.FromNumber(elapsed));
            ctx.SetOutput("found", Value.FromBool(changed));

            if (changed) return;
            // 超时只是 changed = false，不抛错：要不要当失败由调用方用「如果」决定。
            if (elapsed >= limit) return;

            int slice = (int)Math.Min(interval, Math.Max(1, limit - elapsed));
            if (!ctx.Wait(slice)) throw new ActionAbortedException("已停止", userCancelled: true);
        }
    }

    /// <summary>纯判定：自检直调，不用伪造剪贴板。</summary>
    internal static bool Changed(string baseline, string current) =>
        !string.Equals(baseline ?? "", current ?? "", StringComparison.Ordinal);

    private static string ReadClipboard() =>
        NativeClipboard.TryGetText(out string text) ? text : "";
}
