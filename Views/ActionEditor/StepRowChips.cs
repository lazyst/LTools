using System.Collections.Generic;
using LTools.Core.Actions;

namespace LTools.Views;

/// <summary>步骤行上的一个状态标记（chip）：文字 + 悬停说明 + 是否该用提醒色。</summary>
/// <remarks>
/// 刻意不带 <c>Brush</c>：颜色由 XAML 的 <c>DataTrigger</c> 按 <see cref="Warn"/> 决定，
/// 这样判定函数是**纯数据**的，<c>--selftest</c> 能直接断言，不需要建窗口或找资源。
/// </remarks>
public sealed record StepChip(string Text, string Tip, bool Warn);

/// <summary>
/// 步骤行的「附加状态」标记（跳过 / 失败继续 / 重试 / 延迟 / 备注）。
/// </summary>
/// <remarks>
/// <para>
/// 以前这些信息挤在两处：跳过被拼进标题（<c>文本处理（跳过）</c>）、重试被拼进摘要（<c>… · ⟳3</c>），
/// 失败继续 / 延迟 / 备注则在列表上完全看不见。现在统一成行尾的一排 chip，
/// 摘要只剩「这一步在做什么」，状态一眼能扫到。
/// </para>
/// <para>
/// 顺序固定：跳过 → 失败继续 → 重试 → 重试条件（<c>仅未找到</c>）→ 延迟 → 备注
/// （从「最影响执行结果」到「只是备注」）。
/// </para>
/// </remarks>
internal static class StepRowChips
{
    /// <summary>这一行要显示的全部 chip；没有就是空列表。</summary>
    public static List<StepChip> Describe(Step step)
    {
        var chips = new List<StepChip>();

        if (step.Disabled)
            chips.Add(new StepChip("跳过", "这一步被跳过，不会执行（右键「启用步骤」恢复）", true));

        if (step.OnFail == FailMode.@continue)
            chips.Add(new StepChip("失败继续", "失败后记录错误，然后继续下一步", true));

        if (step.Retry > 0)
        {
            chips.Add(new StepChip($"⟳{step.Retry}",
                $"失败后最多再试 {step.Retry} 次，每次间隔 {step.RetryDelayMs} 毫秒", true));
        }

        string only = ActionErrors.Describe(step.RetryOn);
        if (only.Length > 0)
        {
            chips.Add(new StepChip(only,
                $"{only}的失败才重试，其余失败直接按失败策略处理", false));
        }

        if (step.DelayMs > 0)
            chips.Add(new StepChip($"延迟 {Duration(step.DelayMs)}",
                $"这一步跑完后等 {step.DelayMs} 毫秒再往下", false));

        string note = step.Note?.Trim() ?? "";
        if (note.Length > 0)
            chips.Add(new StepChip("备注", note, false));

        return chips;
    }

    /// <summary>毫秒 → <c>500ms</c> / <c>2s</c> / <c>1.5s</c>。</summary>
    internal static string Duration(int milliseconds) =>
        milliseconds >= 1000
            ? (milliseconds / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s"
            : milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) + "ms";
}
