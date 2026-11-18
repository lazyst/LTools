using System;
using System.Collections.Generic;

namespace LTools.Features.CapturePro;

/// <summary>
/// 覆盖层里「双击 / 中键」能做的事（照 Snow Shot 的 <c>screenshot/double_click_action</c>、
/// <c>screenshot/middle_mouse_button_action</c>）。
/// </summary>
/// <remarks>
/// 只对**已经有选区**的时候有意义（上游 <c>executeConfiguredCompletionAction</c> 先判
/// <c>hasPixelSelection()</c>）：还没框选就单击 / 双击，那是框选的起手式，不是完成动作。
/// </remarks>
internal enum OverlayPointerAction
{
    /// <summary>什么都不做（双击仍是双击，只是没有出口）。</summary>
    None,

    Copy,

    Save,

    QuickSave,

    Pin,

    /// <summary>开识别结果窗。</summary>
    Ocr,

    /// <summary>开识别结果窗，识别完直接把文字复制走。</summary>
    OcrCopy,

    /// <summary>退出这次截图（等于 Esc）。</summary>
    Cancel,
}

/// <summary>
/// 贴图里「双击 / 中键」能做的事（照 Snow Shot 的 <c>pin_to_screen/double_click_action</c>、
/// <c>pin_to_screen/middle_mouse_button_action</c>）。
/// </summary>
internal enum PinPointerAction
{
    None,

    /// <summary>回到 100%（<c>applyScale(100)</c>）。</summary>
    ResetZoom,

    /// <summary>缩略图 / 整张来回切。</summary>
    ThumbnailMode,

    /// <summary>收进屏幕顶部（上游 <c>hide_to_top</c>；LTools 还没做，见 P5-i）。</summary>
    HideToTop,

    /// <summary>显示贴图上的文字识别结果（上游 <c>show_text_recognition_results</c>；见 P4-j）。</summary>
    TextRecognition,

    /// <summary>关掉这张贴图（历史里留一条「已关闭」）。</summary>
    Close,

    /// <summary>连记录带图片一起删掉（历史里也不留）。</summary>
    Destroy,
}

/// <summary>
/// 指针动作的取值表：默认值、解析 / 序列化、界面选项、实时贴图的门禁。
/// </summary>
/// <remarks>
/// <para>
/// 上游配置里存的是英文键（<c>copy</c> / <c>thumbnail_mode</c> …），读进来认不出的值当
/// <see cref="OverlayPointerAction.None"/>——和上游一致：不认识的键在 <c>handleDoubleClick</c> 里
/// 落到最后一支，什么都不做，而不是悄悄退回默认值。
/// </para>
/// <para>
/// **设置里只列已经能做的动作**：<see cref="PinPointerAction.HideToTop"/> 与
/// <see cref="PinPointerAction.TextRecognition"/> 是上游两个独立大功能（P5-i / P4-j），
/// 现在还没实现，所以 <see cref="PinOptions"/> 不列它们（不做假选项）；但解析仍认这两个键，
/// 以后补上实现时老配置直接生效。
/// </para>
/// </remarks>
internal static class PointerActions
{
    public const string DefaultOverlayDoubleClick = "copy";
    public const string DefaultOverlayMiddle = "pin";
    public const string DefaultPinDoubleClick = "thumbnail_mode";
    public const string DefaultPinMiddle = "reset_zoom";

    /// <summary>解析覆盖层动作；空串 / 认不出的键 = <see cref="OverlayPointerAction.None"/>。</summary>
    public static OverlayPointerAction ParseOverlay(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() switch
        {
            "copy" => OverlayPointerAction.Copy,
            "save" => OverlayPointerAction.Save,
            "quick_save" => OverlayPointerAction.QuickSave,
            "pin" => OverlayPointerAction.Pin,
            "ocr" => OverlayPointerAction.Ocr,
            "ocr_copy" => OverlayPointerAction.OcrCopy,
            "cancel" => OverlayPointerAction.Cancel,
            _ => OverlayPointerAction.None,
        };

    /// <summary>解析贴图动作；空串 / 认不出的键 = <see cref="PinPointerAction.None"/>。</summary>
    public static PinPointerAction ParsePin(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() switch
        {
            "reset_zoom" => PinPointerAction.ResetZoom,
            "thumbnail_mode" => PinPointerAction.ThumbnailMode,
            "hide_to_top" => PinPointerAction.HideToTop,
            "show_text_recognition_results" => PinPointerAction.TextRecognition,
            "close" => PinPointerAction.Close,
            "destroy" => PinPointerAction.Destroy,
            _ => PinPointerAction.None,
        };

    /// <summary>写回配置用的英文键（<see cref="OverlayPointerAction.None"/> = <c>none</c>）。</summary>
    public static string Key(OverlayPointerAction action) => action switch
    {
        OverlayPointerAction.Copy => "copy",
        OverlayPointerAction.Save => "save",
        OverlayPointerAction.QuickSave => "quick_save",
        OverlayPointerAction.Pin => "pin",
        OverlayPointerAction.Ocr => "ocr",
        OverlayPointerAction.OcrCopy => "ocr_copy",
        OverlayPointerAction.Cancel => "cancel",
        _ => "none",
    };

    /// <summary>写回配置用的英文键（<see cref="PinPointerAction.None"/> = <c>none</c>）。</summary>
    public static string Key(PinPointerAction action) => action switch
    {
        PinPointerAction.ResetZoom => "reset_zoom",
        PinPointerAction.ThumbnailMode => "thumbnail_mode",
        PinPointerAction.HideToTop => "hide_to_top",
        PinPointerAction.TextRecognition => "show_text_recognition_results",
        PinPointerAction.Close => "close",
        PinPointerAction.Destroy => "destroy",
        _ => "none",
    };

    /// <summary>下拉 / 提示里的中文名（与设置页那一行的取值一一对应）。</summary>
    public static string Describe(OverlayPointerAction action) => action switch
    {
        OverlayPointerAction.Copy => "复制到剪贴板",
        OverlayPointerAction.Save => "另存为…",
        OverlayPointerAction.QuickSave => "快速保存",
        OverlayPointerAction.Pin => "贴到屏幕",
        OverlayPointerAction.Ocr => "文字识别",
        OverlayPointerAction.OcrCopy => "识别并复制",
        OverlayPointerAction.Cancel => "取消这次截图",
        _ => "不处理",
    };

    /// <summary>下拉 / 提示里的中文名（与设置页那一行的取值一一对应）。</summary>
    public static string Describe(PinPointerAction action) => action switch
    {
        PinPointerAction.ResetZoom => "重置缩放",
        PinPointerAction.ThumbnailMode => "缩略图模式",
        PinPointerAction.HideToTop => "收到屏幕顶部",
        PinPointerAction.TextRecognition => "显示文字识别结果",
        PinPointerAction.Close => "关闭贴图",
        PinPointerAction.Destroy => "删除贴图",
        _ => "不处理",
    };

    /// <summary>
    /// 覆盖层的下拉选项（顺序照上游 <c>screenshotPointerActionOptions()</c>）。
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> OverlayOptions { get; } = new[]
    {
        ("copy", "复制到剪贴板"),
        ("save", "另存为…"),
        ("quick_save", "快速保存"),
        ("pin", "贴到屏幕"),
        ("ocr", "文字识别"),
        ("ocr_copy", "识别并复制"),
        ("cancel", "取消这次截图"),
        ("none", "不处理"),
    };

    /// <summary>
    /// 贴图的下拉选项：<b>只列已经能做的</b>（顺序照上游 <c>pinDoubleClickActionItem</c>，
    /// 去掉还没实现的 <c>hide_to_top</c> / <c>show_text_recognition_results</c>）。
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> PinOptions { get; } = new[]
    {
        ("none", "不处理"),
        ("reset_zoom", "重置缩放"),
        ("thumbnail_mode", "缩略图模式"),
        ("close", "关闭贴图"),
        ("destroy", "删除贴图"),
    };

    /// <summary>
    /// 实时贴图只允许这几件（照上游 <c>liveCommandAllowed</c> 的命令表：
    /// <c>reset_zoom</c> 在 <c>handleDoubleClick</c> / <c>handleMiddleClick</c> 里单独放行，
    /// <c>close</c> 在允许列表里，缩略图 / 收顶部 / 识别 / 删除都不行）。
    /// </summary>
    public static bool AllowedInLive(PinPointerAction action) =>
        action is PinPointerAction.None or PinPointerAction.ResetZoom or PinPointerAction.Close;
}
