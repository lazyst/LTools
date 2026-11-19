using System;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图文字层所处的阶段（照上游 <c>m_ocrMode</c> / <c>m_recognitionSession</c> 的几态，
/// 收成一份菜单与状态条都能直接用的枚举）。
/// </summary>
internal enum PinOcrPhase
{
    /// <summary>还没识别过（菜单可点，点了开始识别）。</summary>
    Idle,

    /// <summary>正在识别（菜单写「正在识别…」，不能再点）。</summary>
    Busy,

    /// <summary>识别结果已经铺在贴图上（菜单勾上，再次点击退出）。</summary>
    Ready,

    /// <summary>识别跑完了但一个字都没找到（菜单仍可点，用来重试）。</summary>
    Empty,

    /// <summary>图片超过识别上限（照上游 <c>screenshotOcrImageWithinPixelLimit</c>，菜单置灰）。</summary>
    TooLarge,
}

/// <summary>
/// 贴图「显示文字识别结果」（P4-j，照 Snow Shot 的 <c>show_text_recognition_results</c>）的纯逻辑：
/// 尺寸门槛、阶段推导、菜单 / 状态条的文案与可用性。
/// </summary>
/// <remarks>
/// 真窗口那一段（识别 → 铺文字层 → 拖动选择 → 复制、OCR 模式里禁用拖动 / 滚轮）在
/// <c>CapturePinWindow.Ocr.cs</c>；这里只放可以脱开 WPF 断言的部分。
/// </remarks>
internal static class PinOcr
{
    /// <summary>识别上限（上游 <c>kScreenshotOcrMaximumPixels = 3840 × 2160</c>）。</summary>
    public const long MaxPixels = 3840L * 2160L;

    public const string ShowLabel = "显示文字";
    public const string BusyLabel = "正在识别…";
    public const string TooLargeLabel = "图片尺寸过大";
    public const string CopySelectionLabel = "复制所选文字";
    public const string CopyAllLabel = "复制全部文字";
    public const string EmptyHint = "没有识别到文字";

    /// <summary>宽高都要有像素，且总像素不超过上限（照上游 <c>screenshotOcrImageWithinPixelLimit</c>）。</summary>
    public static bool WithinPixelLimit(int width, int height) =>
        width >= 1 && height >= 1 && (long)width * height <= MaxPixels;

    /// <summary>把「能不能识别 / 在识别 / 已有结果」收成一个阶段。</summary>
    public static PinOcrPhase PhaseOf(bool withinLimit, bool busy, bool ready) =>
        !withinLimit ? PinOcrPhase.TooLarge
        : busy ? PinOcrPhase.Busy
        : ready ? PinOcrPhase.Ready
        : PinOcrPhase.Idle;

    /// <summary>菜单项标题：识别中 / 尺寸过大有自己的说法，其余都是「显示文字」。</summary>
    public static string MenuLabel(PinOcrPhase phase) => phase switch
    {
        PinOcrPhase.TooLarge => TooLargeLabel,
        PinOcrPhase.Busy => BusyLabel,
        _ => ShowLabel,
    };

    /// <summary>菜单项能不能点：只有「尺寸过大」与「正在识别」不能。</summary>
    public static bool MenuEnabled(PinOcrPhase phase) =>
        phase is PinOcrPhase.Idle or PinOcrPhase.Ready or PinOcrPhase.Empty;

    /// <summary>菜单项是否勾上（只有真的铺着文字层时勾）。</summary>
    public static bool MenuChecked(PinOcrPhase phase) => phase == PinOcrPhase.Ready;

    /// <summary>
    /// 贴图上那条小状态条的字：识别中 / 结果行数 / 已选几个字 / 没找到 / 尺寸过大。
    /// </summary>
    public static string BarText(PinOcrPhase phase, int lines, int selectedChars)
    {
        if (phase == PinOcrPhase.Busy) return BusyLabel;
        if (phase == PinOcrPhase.TooLarge) return TooLargeLabel + "（上限 3840×2160）";
        if (phase == PinOcrPhase.Empty) return EmptyHint;
        if (phase != PinOcrPhase.Ready) return "";

        return selectedChars > 0
            ? $"已选 {selectedChars} 字 · 点「复制所选」或右键复制"
            : $"{Math.Max(0, lines)} 行 · 拖动选择文字";
    }
}
