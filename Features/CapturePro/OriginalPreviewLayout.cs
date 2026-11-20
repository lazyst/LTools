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
/// 顺序：宽度够 → 左右并列；宽度不够但高度够 → 上下并列；两样都不够才退回「右侧一栏 + 缩放」。
/// </para>
/// <para>
/// 尺寸都按 DIP（100% = 1 源像素 1 物理像素），<paramref name="work"/> 是工作区，
/// <paramref name="chrome"/> 是窗口里除内容区之外的固定开销（标题栏 / 页签 / 底栏 / 外边距）。
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

        // ① 左右并列：文字 + 缝 + 原图
        var sideBySide = new Size(iw * 2 + Gap + chrome.Width + Slack,
            ih + chrome.Height + Slack);
        if (Fits(sideBySide, work))
            return new OriginalPanePlan(OriginalPaneSide.Right, true, sideBySide, iw + 2);

        // ② 上下并列：文字 / 原图
        var stacked = new Size(iw + chrome.Width + Slack, ih * 2 + Gap + chrome.Height + Slack);
        if (Fits(stacked, work))
            return new OriginalPanePlan(OriginalPaneSide.Bottom, true, stacked, ih + 2);

        // ③ 都放不下：右侧一栏 + 原图缩放（文字层自己按可用宽高缩）
        return new OriginalPanePlan(OriginalPaneSide.Right, false, default,
            Math.Clamp(iw, MinPane, MaxPane));
    }

    private static bool Fits(Size size, Size work) =>
        size.Width <= work.Width + 0.5 && size.Height <= work.Height + 0.5;
}
