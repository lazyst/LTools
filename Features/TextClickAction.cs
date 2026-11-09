using System;
using System.Threading;
using LTools.Core;

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
            throw new InvalidOperationException($"「{request.Name}」还没有指定要点击的文字");

        int timeout = Math.Clamp(request.TimeoutMs, 0, 600_000);
        var result = TextLocator.Locate(needle, request.Region, request.Rect, request.OcrEngine,
            timeout, TextLocator.DefaultIntervalMs, ctx);

        if (!result.Found)
            throw new InvalidOperationException(TextLocator.MissMessage(request.Name, needle, result.Seen, timeout));

        int x = result.X, y = result.Y;
        MouseOps.Move(x, y);
        Thread.Sleep(10);   // 让光标先落定，再发按键（与「鼠标操作」一致）
        MouseOps.Perform(request.MouseAction, request.WheelDelta);

        AppLog.Info("TextClick", $"「{request.Name}」点到「{result.Matched}」：{x},{y}");
        return new VarValue { Text = $"{x},{y}", X = x, Y = y, Flag = true };
    }
}

/// <summary>
/// 「文字点击」的入参。功能单元把参数求值后装成它，不再为了复用这套实现去拼一个
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

    /// <summary>识别引擎（<c>null</c> / <c>windows</c> = 系统 OCR；<c>v6tiny</c> / <c>v6small</c> = PP-OCRv6）。</summary>
    public string? OcrEngine { get; init; }

    /// <summary>等目标出现的最长时间（ms，0 = 只找一次）。</summary>
    public int TimeoutMs { get; init; }

    /// <summary>命中后的鼠标动作（默认单击）。</summary>
    public MouseActionKind MouseAction { get; init; } = MouseActionKind.click;

    /// <summary>滚轮刻度（默认 120）。</summary>
    public int WheelDelta { get; init; } = 120;
}
