using System;
using System.Threading;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 「文字点击」：在指定区域里识别文字，找到指定的文字就点它（找不到按步骤失败策略处理）。
/// </summary>
/// <remarks>
/// <para>
/// 定位逻辑在 <see cref="TextLocator"/>（与「找文字」取值器共用）：截取区域 → 带坐标 OCR →
/// 按「包含」匹配 → 点命中文字的外接矩形中心。匹配忽略大小写与词间空格，同一行 / 多行命中时取最上面那个。
/// </para>
/// <para>
/// 区域与「截图找图」同一套语义：前台窗口 / 整个屏幕 / 固定矩形（后者的坐标是虚拟桌面物理像素，
/// 与鼠标注入、截图一致）。坐标也是物理像素，不受 DPI 缩放影响。
/// </para>
/// <para>
/// 找不到就是失败：不会「假装点过了」——交给步骤的失败策略（继续下一步 / 中止），
/// 报错里带上识别到的文字，方便发现是 OCR 没认出来还是文字写错了。
/// 需要等按钮出现时把「找不到时最多等」调大（内部会反复截图重试），不用自己在流程里套循环。
/// </para>
/// </remarks>
internal static class TextClickAction
{
    public static VarValue Run(TextClickRequest request, RunContext? ctx)
    {
        string needle = (request.Text ?? "").Trim();
        if (needle.Length == 0)
            throw ActionErrors.Precondition($"「{request.Name}」还没有指定要点击的文字");

        // 提示文案用原始值（-1 = 一直等），等待循环用换算后的期限。
        int timeout = request.TimeoutMs;
        int index = Math.Max(1, request.Index);

        TextLookup lookup = TextLocator.LocateAll(needle, request.Region, request.Rect, request.OcrEngine,
            Timeouts.Loop(timeout), request.IntervalMs, request.Exact, index, ctx);

        if (lookup.Hits.Count == 0)
            throw ActionErrors.NotFound(TextLocator.MissMessage(request.Name, needle, lookup.Seen, timeout));

        if (lookup.Hits.Count < index)
            throw ActionErrors.NotFound(
                $"「{request.Name}」只找到 {lookup.Hits.Count} 处「{needle}」，没有第 {index} 处" +
                "（调低「点第几个」，或把「匹配方式」改成包含 / 收窄区域）");

        TextHit hit = lookup.Hits[index - 1];
        MouseOps.Move(hit.X, hit.Y);
        Thread.Sleep(10);   // 让光标先落定，再发按键（与「鼠标操作」一致）
        MouseOps.Perform(request.MouseAction, request.WheelDelta);

        AppLog.Info("TextClick", $"「{request.Name}」点到「{hit.Text}」：{hit.X},{hit.Y}");
        return new VarValue { Text = $"{hit.X},{hit.Y}", X = hit.X, Y = hit.Y, Flag = true };
    }
}

/// <summary>
/// 「文字点击」的入参。动作单元把参数求值后装成它，不再为了复用这套实现去拼一个
/// <see cref="ActionDto"/>。
/// </summary>
internal sealed record TextClickRequest
{
    /// <summary>动作 / 步骤名（只用于报错与日志）。</summary>
    public string Name { get; init; } = "";

    /// <summary>要点击的文字（支持模板，由调用方先求值）。</summary>
    public string? Text { get; init; }

    /// <summary>搜索区域；默认前台窗口。</summary>
    public ImageRegion Region { get; init; } = ImageRegion.foreground;

    /// <summary><see cref="ImageRegion.rect"/> 时的区域 [x, y, w, h]（虚拟桌面物理像素）。</summary>
    public int[]? Rect { get; init; }

    /// <summary>识别引擎（<c>null</c> / <c>windows</c> = 系统 OCR；<c>v6tiny</c> / <c>v6small</c> / <c>v6medium</c> = PP-OCRv6）。</summary>
    public string? OcrEngine { get; init; }

    /// <summary>等目标出现的最长时间（ms，0 = 只找一次，-1 = 一直等）。</summary>
    public int TimeoutMs { get; init; }

    /// <summary>两次查找之间的间隔（ms）。</summary>
    public int IntervalMs { get; init; } = TextLocator.DefaultIntervalMs;

    /// <summary>命中后的鼠标动作（默认单击）。</summary>
    public MouseActionKind MouseAction { get; init; } = MouseActionKind.click;

    /// <summary>滚轮刻度（默认 120）。</summary>
    public int WheelDelta { get; init; } = 120;

    /// <summary>点第几处（1 起，按从上到下、从左到右；默认第一处）。</summary>
    public int Index { get; init; } = 1;

    /// <summary>是否要求「完全一致」（默认包含匹配：「保存」也命中「保存并关闭」）。</summary>
    public bool Exact { get; init; }
}
