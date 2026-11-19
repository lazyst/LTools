using System;

namespace LTools.Features.CapturePro;

/// <summary>
/// 文字区域的填充样式（照上游 <c>text_recognition/fill_style</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游两种取值：<c>background_fill</c> 用逐行估出来的底色盖住原文（默认），
/// <c>blur</c> 把文字区域高斯模糊后叠一层 50% 的容器底色
/// （<c>screenshotocrvisuals.cpp</c> 的 <c>renderScreenshotOcrFilteredImage</c>，
/// <c>kBackgroundBlendAmount = 0.5</c>、<c>parameters.logicalSigma = 8</c>）。
/// </para>
/// <para>
/// 键归一与「模糊要多粗」都放这里（纯函数），<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class OcrFillStyle
{
    /// <summary>填估出来的底色。</summary>
    public const string BackgroundFill = "background_fill";

    /// <summary>高斯模糊原图再叠半透明容器底色。</summary>
    public const string Blur = "blur";

    /// <summary>默认值（照上游 <c>configurationschema.cpp</c>）。</summary>
    public const string Default = BackgroundFill;

    /// <summary>模糊后叠上去的容器底色比例（上游 <c>kBackgroundBlendAmount</c>）。</summary>
    public const double BackgroundBlendAmount = 0.5;

    /// <summary>逻辑像素下的高斯 sigma（上游 <c>parameters.logicalSigma</c>）。</summary>
    public const double LogicalSigma = 8.0;

    /// <summary>键归一：只认 <c>blur</c>，其余一律按背景填充。</summary>
    public static string Normalize(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == Blur ? Blur : BackgroundFill;

    /// <summary>是不是模糊模式。</summary>
    public static bool IsBlur(string? key) => Normalize(key) == Blur;

    /// <summary>
    /// 设备像素比 → 实际 sigma（上游把 DPR 夹到 ≥1 再乘逻辑 sigma）。
    /// </summary>
    public static double DeviceSigma(double devicePixelRatio) =>
        LogicalSigma * Math.Max(1.0, devicePixelRatio);
}
