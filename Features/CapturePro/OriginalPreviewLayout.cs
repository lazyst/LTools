using System;
using System.Windows;

namespace LTools.Features.CapturePro;

/// <summary>原文那一栏摆在哪边。</summary>
internal enum OriginalPaneSide
{
    /// <summary>识别窗右侧（文字在左、原图在右）。</summary>
    Right,

    /// <summary>识别窗下方（文字在上、原图在下）。</summary>
    Bottom,
}

/// <summary>一次摆位方案：放哪边、是不是两个都按 100% 显示全、窗口该多大、那一栏多大。</summary>
/// <param name="Side">原图那一栏摆在哪边。</param>
/// <param name="OneToOne">true = 文字层和原图都按 100%（1 源像素 1 物理像素）完整显示，不用滚也不用缩。</param>
/// <param name="Window">要 1:1 时窗口该有的尺寸（DIP，含标题栏 / 页签 / 底栏这些固定开销）。</param>
/// <param name="Pane">那一栏的尺寸：<see cref="OriginalPaneSide.Right"/> 是宽、<see cref="OriginalPaneSide.Bottom"/> 是高。</param>
internal readonly record struct OriginalPanePlan(
    OriginalPaneSide Side,
    bool OneToOne,
    Size Window,
    double Pane);

/// <summary>
/// 「原文」那一栏怎么摆、窗口要多大——纯函数，<c>--selftest</c> 直接断言。
/// </summary>
/// <remarks>
/// <para>
/// 目标是**能 1:1 就 1:1**：文字层和原图都按 100% 完整显示，不用滚也不用缩。
/// 顺序照用户的要求：**宽度够 → 左右并列（窗口约 2 倍图宽）**；宽度不够但高度够 → 上下并列
/// （窗口约 2 倍图高）；两样都不够 → **仍然左右对照**，窗口撑满可用区、原图尽量大。
/// </para>
/// <para>
/// <paramref name="work"/> 是**内容区**可用尺寸（DIP），<paramref name="chrome"/> 是窗口里
/// 除内容区之外的固定开销（标题栏 / 页签 / 底栏 / 外边距）；返回的 <see cref="OriginalPanePlan.Window"/>
/// 已经含 chrome（调用方再按工作区夹一次）。100% = 1 源像素 1 物理像素。
/// </para>
/// </remarks>
internal static class OriginalPreviewLayout
{
    /// <summary>两栏之间的缝（和分隔条同宽）。</summary>
    public const double Gap = 7;

    /// <summary>多留一点余量，别因为 4px 内边距刚好卡出一个滚动条。</summary>
    public const double Slack = 8;

    /// <summary>退回「缩放」时那一栏的宽度范围。</summary>
    public const double MinPane = 240;
    public const double MaxPane = 420;

    public static OriginalPanePlan Plan(Size image, Size work, Size chrome)
    {
        double iw = Math.Max(1, image.Width);
        double ih = Math.Max(1, image.Height);

        // ① 左右并列：文字 + 缝 + 原图（内容区）
        var sideBySide = new Size(iw * 2 + Gap + Slack, ih + Slack);

        // 宽度够就左右并列；高度也够才是真 1:1，不够就把窗口高度用满（仍左右对照）
        if (sideBySide.Width <= work.Width + 0.5)
        {
            bool full = sideBySide.Height <= work.Height + 0.5;
            var content = new Size(sideBySide.Width, Math.Min(sideBySide.Height, work.Height));
            return new OriginalPanePlan(OriginalPaneSide.Right, full,
                new Size(content.Width + chrome.Width, content.Height + chrome.Height), iw + 2);
        }

        // ② 宽度不够但高度够：上下并列（窗口约 2 倍图高）
        var stacked = new Size(iw + Slack, ih * 2 + Gap + Slack);
        if (stacked.Height <= work.Height + 0.5)
        {
            return new OriginalPanePlan(OriginalPaneSide.Bottom, true,
                new Size(stacked.Width + chrome.Width, stacked.Height + chrome.Height), ih + 2);
        }

        // ③ 宽度、高度都不够：**仍然左右对照**，窗口撑满可用区；原图栏最多占 45%，
        //    放得下就 1:1（pane = 图宽 + 2），放不下才缩。
        double pane = Math.Min(iw + 2, Math.Max(MinPane, work.Width * 0.45));
        return new OriginalPanePlan(OriginalPaneSide.Right, false,
            new Size(work.Width + chrome.Width, work.Height + chrome.Height), pane);
    }
}
