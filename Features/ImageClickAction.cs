using System;
using System.Threading;
using LTools.Core;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 「图片点击」：在指定区域里找模板图，找到就点它（找不到按步骤失败策略处理）。
/// </summary>
/// <remarks>
/// <para>
/// 定位复用「截图找图」的 <see cref="ImageProbe"/>（OpenCV 模板匹配 + 多缩放候选 + 可等待），
/// 于是同一张模板在「截图找图」里调好的阈值 / 区域，原样填到本动作也能用。
/// 命中坐标是虚拟桌面物理像素，与鼠标注入、截图一致。
/// </para>
/// <para>
/// 找不到就是失败：不会「假装点过了」——交给步骤的失败策略（继续下一步 / 中止），
/// 报错里带上最高相似度，方便判断是阈值定高了还是模板截得不准。
/// 需要等按钮出现时把「找不到时最多等」调大（内部会反复匹配重试）。
/// </para>
/// </remarks>
internal static class ImageClickAction
{
    public static VarValue Run(ActionDto a, RunContext? ctx)
    {
        if (string.IsNullOrWhiteSpace(a.Image))
            throw new InvalidOperationException($"「{a.Name}」还没有选择模板图片");

        var probe = new Probe
        {
            Kind = ProbeKind.image,
            Image = a.Image,
            Region = a.Region ?? ImageRegion.foreground,
            Rect = a.Rect,
            TimeoutMs = Math.Clamp(a.TimeoutMs ?? 0, 0, 600_000),
            MinScore = Math.Clamp(a.MinScore ?? 0.85, 0.30, 0.99),
        };

        // 自动化指示器是置顶窗口，会盖住目标并被截进画面：找图期间收起，点完再恢复。
        string? indicator = RecordingIndicatorWindow.HideAutomationForCapture();
        VarValue match;
        try
        {
            match = ImageProbe.Run(probe, ctx);
        }
        finally
        {
            RecordingIndicatorWindow.RestoreAutomation(indicator);
        }

        if (match.Flag != true)
            throw new InvalidOperationException(
                $"「{a.Name}」在区域里没有找到模板图（最高相似度 {(match.Score ?? 0):0.00}）；" +
                "可以调低「最低相似度」，或重新框选模板图");

        int x = match.X ?? 0, y = match.Y ?? 0;
        MouseOps.Move(x, y);
        Thread.Sleep(10);   // 让光标先落定，再发按键（与「鼠标操作」一致）
        MouseOps.Perform(a.MouseAction ?? MouseActionKind.click, a.WheelDelta ?? 120);

        AppLog.Info("ImageClick", $"「{a.Name}」点到图片位置：{x},{y}（相似度 {(match.Score ?? 0):0.00}）");
        return new VarValue { Text = $"{x},{y}", X = x, Y = y, Flag = true, Score = match.Score };
    }
}
