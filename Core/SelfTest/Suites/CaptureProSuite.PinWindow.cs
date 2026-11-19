using System;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图窗口设置（上游 <c>pin_to_screen/*</c> 的外观 / 滚轮那十二项）的纯逻辑断言：
/// 归一化 / 色值解析 / 滚轮步长与档位 / 六种缩放锚点 / 阴影带宽。
/// </summary>
/// <remarks>
/// 全部不读盘、不建窗口；窗口层的接线（边框显隐、阴影窗口跟随、右键菜单、设置页控件）
/// 在 <c>capture-overlay-smoke</c> 的 <c>pinsettings</c> 段。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinWindowCases()
    {
        PinSettingsNormalize();
        PinWheelSteps();
        PinScaleAnchors();
        PinShadowGeometry();
        PinRecordDefaults();
        PinRecognitionSettings();
        PinPlacementAutoResize();
        PinContentIdentityCases();
        PinAttentionShake();
    }

    /// <summary>
    /// P9-d2 的四项设置：划选模式两个键、重复内容四个键、自动缩小 / 自动识别默认值。
    /// </summary>
    private static void PinRecognitionSettings()
    {
        // 划选：只认两个键，其余（含空）= 「始终」（与上游默认一致）
        Check.Equal(PinSettings.TextSelectionAlways, PinSettings.NormalizeTextSelection("always"),
            "always 认得出");
        Check.Equal(PinSettings.TextSelectionOnlyWhenDisplayed,
            PinSettings.NormalizeTextSelection(" ONLY_WHEN_DISPLAYED "), "大小写 / 空格都能认");
        Check.Equal(PinSettings.TextSelectionAlways, PinSettings.NormalizeTextSelection(null),
            "空 = 始终");
        Check.Equal(PinSettings.TextSelectionAlways, PinSettings.NormalizeTextSelection("sometimes"),
            "乱写 = 始终");
        Check.Equal(2, PinSettings.TextSelectionModes.Length, "两个划选模式");
        Check.Equal("始终（在图片上直接划选）",
            PinSettings.TextSelectionLabel(PinSettings.TextSelectionAlways), "划选显示名");

        // 重复内容：四个键，其余 = 抖一下
        Check.Equal(PinSettings.DuplicateNone, PinSettings.NormalizeDuplicateAction("none"), "none");
        Check.Equal(PinSettings.DuplicateShakeWindow,
            PinSettings.NormalizeDuplicateAction("SHAKE_WINDOW"), "shake_window");
        Check.Equal(PinSettings.DuplicateRestoreLastClosed,
            PinSettings.NormalizeDuplicateAction("restore_last_closed_window"), "贴回最近关闭");
        Check.Equal(PinSettings.DuplicateRepeatAction,
            PinSettings.NormalizeDuplicateAction("repeat_action"), "照常再贴");
        Check.Equal(PinSettings.DuplicateShakeWindow, PinSettings.NormalizeDuplicateAction(null),
            "空 = 抖一下");
        Check.Equal(PinSettings.DuplicateShakeWindow, PinSettings.NormalizeDuplicateAction("other"),
            "乱写 = 抖一下");
        Check.Equal(4, PinSettings.DuplicateActions.Length, "四个重复动作");
        Check.Equal("抖一下已有的那张",
            PinSettings.DuplicateLabel(PinSettings.DuplicateShakeWindow), "重复动作显示名");

        // 枚举映射（窗口按它分派）
        Check.Equal(PinDuplicateAction.None,
            PinSettings.DuplicateActionOf(PinSettings.DuplicateNone), "none → None");
        Check.Equal(PinDuplicateAction.RestoreLastClosedWindow,
            PinSettings.DuplicateActionOf(PinSettings.DuplicateRestoreLastClosed), "恢复最近关闭");
        Check.Equal(PinDuplicateAction.RepeatAction,
            PinSettings.DuplicateActionOf(PinSettings.DuplicateRepeatAction), "照常再贴");
        Check.Equal(PinDuplicateAction.ShakeWindow,
            PinSettings.DuplicateActionOf("garbage"), "兜底 = 抖一下");
    }

    /// <summary>自动缩小（<c>pin_to_screen/auto_resize_window</c>）：关掉按 100% 贴出，允许越界。</summary>
    private static void PinPlacementAutoResize()
    {
        var logical = new Size(4000, 3000);
        var work = new Rect(0, 0, 1920, 1020);

        var (shrunk, shrinkZoom) = PinPlacement.Center(logical, work);
        Check.True(shrinkZoom < 1, "装不下要缩小");
        Check.True(shrunk.Width <= work.Width, "缩小后不超宽");
        Check.True(shrunk.Height <= work.Height, "缩小后不超高");

        var (raw, rawZoom) = PinPlacement.Center(logical, work, PinPlacement.Margin, autoResize: false);
        Check.Equal(1.0, rawZoom, "关掉自动缩小 = 100%");
        Check.Equal(4000.0, raw.Width, "宽度按原始尺寸");
        Check.Equal(3000.0, raw.Height, "高度按原始尺寸");
        Check.Equal(-1040.0, raw.Left, "居中摆，允许超出左边");
        Check.Equal(-990.0, raw.Top, "居中摆，允许超出上边");

        var (small, smallZoom) = PinPlacement.Center(new Size(400, 300), work, PinPlacement.Margin, false);
        Check.Equal(1.0, smallZoom, "装得下也是 100%");
        Check.Equal(760.0, small.Left, "装得下就居中（横向）");
        Check.Equal(360.0, small.Top, "装得下就居中（纵向）");
    }

    /// <summary>内容身份（照上游 <c>PinnedSourceIdentity</c>）：文件路径 / 剪贴板序号。</summary>
    private static void PinContentIdentityCases()
    {
        string file = PinContentIdentity.FileKey(@"C:\Temp\Shot.PNG");
        Check.True(PinContentIdentity.IsFileKey(file), "文件身份带前缀");
        Check.Equal(file, PinContentIdentity.FileKey(@"c:/temp/shot.png"), "大小写 / 斜杠归一后同一条身份");
        Check.True(PinContentIdentity.IsValid(file), "有身份");
        Check.Equal("", PinContentIdentity.FileKey(""), "空路径没有身份");
        Check.Equal("", PinContentIdentity.FileKey(null), "null 没有身份");
        Check.False(PinContentIdentity.IsValid(""), "空串不算身份");

        string clipboard = PinContentIdentity.ClipboardKey(42);
        Check.True(PinContentIdentity.IsClipboardKey(clipboard), "剪贴板身份带前缀");
        Check.True(clipboard.EndsWith(":42", StringComparison.Ordinal), "带上剪贴板序号");
        Check.Equal(clipboard, PinContentIdentity.ClipboardKey(42), "同一次复制 = 同一条身份");
        Check.NotEqual(clipboard, PinContentIdentity.ClipboardKey(43), "又复制一次就是新身份");
        Check.False(PinContentIdentity.IsFileKey(clipboard), "剪贴板身份不是文件身份");
    }

    /// <summary>抖动提醒的曲线（照上游 shakeForAttention 的 300ms / 6 DIP 关键帧）。</summary>
    private static void PinAttentionShake()
    {
        Check.Equal(300, PinMath.ShakeDurationMs, "抖动 300ms");
        Check.Equal(6.0, PinMath.ShakeAmplitude, "幅度 6 DIP");

        Check.Equal(0.0, PinMath.ShakeOffset(0), "起点 0");
        Check.Equal(-6.0, PinMath.ShakeOffset(1.0 / 6.0), "1/6 拍 = −1");
        Check.Equal(6.0, PinMath.ShakeOffset(2.0 / 6.0), "2/6 拍 = +1");
        Check.Equal(-4.5, PinMath.ShakeOffset(3.0 / 6.0), "3/6 拍 = −0.75");
        Check.Equal(3.0, PinMath.ShakeOffset(4.0 / 6.0), "4/6 拍 = +0.5");
        Check.Equal(-1.5, PinMath.ShakeOffset(5.0 / 6.0), "5/6 拍 = −0.25");
        Check.Equal(0.0, PinMath.ShakeOffset(1), "终点 0");
        Check.Equal(0.0, PinMath.ShakeOffset(2), "超出范围按 0");
        Check.Equal(0.0, PinMath.ShakeOffset(double.NaN), "NaN 按 0");

        // 两拍之间线性插值：1/12 在 0 与 −1 之间 = −3 DIP
        Check.Equal(-3.0, PinMath.ShakeOffset(1.0 / 12.0), "两拍之间线性");
        Check.True(PinMath.ShakeOffset(1.0 / 12.0) > PinMath.ShakeOffset(1.0 / 6.0),
            "1/12 还没到最低点");
    }

    /// <summary>六个锚点键、步长夹取、色值解析、显示名。</summary>
    private static void PinSettingsNormalize()
    {
        // 锚点：六个键一字不差，其余落回光标位置
        Check.Equal(PinSettings.ZoomTopLeft, PinSettings.NormalizeZoomMode("top_left"), "左上角认得出");
        Check.Equal(PinSettings.ZoomTopRight, PinSettings.NormalizeZoomMode(" TOP_RIGHT "), "大小写 / 空格都能认");
        Check.Equal(PinSettings.ZoomBottomLeft, PinSettings.NormalizeZoomMode("bottom_left"), "左下角认得出");
        Check.Equal(PinSettings.ZoomBottomRight, PinSettings.NormalizeZoomMode("bottom_right"), "右下角认得出");
        Check.Equal(PinSettings.ZoomCenter, PinSettings.NormalizeZoomMode("center"), "中心认得出");
        Check.Equal(PinSettings.ZoomMousePosition, PinSettings.NormalizeZoomMode("mouse_position"), "光标位置认得出");
        Check.Equal(PinSettings.ZoomMousePosition, PinSettings.NormalizeZoomMode(null), "空 = 光标位置");
        Check.Equal(PinSettings.ZoomMousePosition, PinSettings.NormalizeZoomMode("corner"), "乱写 = 光标位置");

        // 步长：1–50，没填 / 负数 / 0 用缺省值
        Check.Equal(10, PinSettings.NormalizeStep(null, 10), "缺项 = 缺省");
        Check.Equal(10, PinSettings.NormalizeStep(0, 10), "0 = 缺省");
        Check.Equal(10, PinSettings.NormalizeStep(-5, 10), "负数 = 缺省");
        Check.Equal(1, PinSettings.NormalizeStep(1, 10), "下限保留");
        Check.Equal(50, PinSettings.NormalizeStep(50, 10), "上限保留");
        Check.Equal(50, PinSettings.NormalizeStep(90, 10), "超出夹到 50");
        Check.Equal(7, PinSettings.NormalizeStep(7, 10), "手填的 7 原样保留");

        // 色值：#RRGGBBAA（Qt 写法），6 位简写补 FF
        Color border = PinSettings.ParseColor("#DBDBDBFF", Colors.Black);
        Check.Equal(Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB), border, "8 位色值解析");
        Check.Equal(Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF),
            PinSettings.ParseColor("#69B1FF", Colors.Black), "6 位补成不透明");
        Check.Equal(Colors.Black, PinSettings.ParseColor("not-a-color", Colors.Black), "坏值回落");
        Check.Equal(Colors.Black, PinSettings.ParseColor(null, Colors.Black), "空值回落");

        // 默认色与上游 configurationschema.cpp 一致
        Check.Equal(Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB), PinSettings.DefaultBorderColor, "默认边框 #DBDBDB");
        Check.Equal(Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF), PinSettings.DefaultBorderActiveColor,
            "默认活动边框 #69B1FF");
        Check.Equal(Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14), PinSettings.DefaultLockedBorderColor,
            "默认锁定边框 #FAAD14");
        Check.Equal(Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF), PinSettings.DefaultShadowColor,
            "默认阴影 #BFBFBF");
        Check.Equal(Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF), PinSettings.DefaultShadowActiveColor,
            "默认活动阴影 #69B1FF");
        Check.Equal(Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14), PinSettings.DefaultLockedShadowColor,
            "默认锁定阴影 #FAAD14");

        // 下拉显示名
        Check.Equal("窗口中心", PinSettings.ZoomModeLabel(PinSettings.ZoomCenter), "中心显示名");
        Check.Equal("光标位置", PinSettings.ZoomModeLabel("whatever"), "未知键按缺省显示");
        Check.Equal(6, PinSettings.ZoomModes.Length, "六个锚点");
    }

    /// <summary>滚轮步长：缩放走「档位」（上游 applyWheelScaleSteps），不透明度直接加减。</summary>
    private static void PinWheelSteps()
    {
        // 缩放默认 10%：整档 +10 / -10，上下限停住
        Check.Equal(1.1, PinMath.NextZoom(1.0, 120), "放大一档 = 110%");
        Check.Equal(0.9, PinMath.NextZoom(1.0, -120), "缩小一档 = 90%");
        Check.Equal(PinMath.ZoomMin, PinMath.NextZoom(0.1, -120), "缩放下限停住");
        Check.Equal(PinMath.ZoomMax, PinMath.NextZoom(4.0, 120), "缩放上限停住");
        Check.Equal(1.0, PinMath.NextZoom(1.0, 0), "没有滚动就不变");

        // 非整档（拖边框得到的 137%）：向上取到 140%，向下取到 130%
        Check.Equal(1.4, PinMath.NextZoom(1.37, 120), "137% 向上 = 140%");
        Check.Equal(1.3, PinMath.NextZoom(1.37, -120), "137% 向下 = 130%");

        // 改步长：25% 一档
        Check.Equal(1.25, PinMath.NextZoom(1.0, 120, 25), "25% 档放大 = 125%");
        Check.Equal(1.5, PinMath.NextZoom(1.3, 120, 25), "130% 向上 = 150%");
        Check.Equal(1.25, PinMath.NextZoom(1.3, -120, 25), "130% 向下 = 125%");
        Check.Equal(1.05, PinMath.NextZoom(1.0, 120, 5), "5% 档放大 = 105%");

        // 不透明度默认 5%：直接加减
        Check.Equal(0.5, PinMath.NextOpacity(0.45, 120), "不透明度一档 = 50%");
        Check.Equal(PinMath.OpacityMin, PinMath.NextOpacity(0.2, -120), "不透明度下限停住");
        Check.Equal(PinMath.OpacityMax, PinMath.NextOpacity(1.0, 120), "不透明度上限停住");
        Check.Equal(1.0, PinMath.NextOpacity(1.0, 0), "没有滚动就不变");

        // 改步长：10%
        Check.Equal(0.57, PinMath.NextOpacity(0.47, 120, 10), "10% 档 = 57%");
        Check.Equal(0.85, PinMath.NextOpacity(0.95, -120, 10), "95% 向下 = 85%");
    }

    /// <summary>六种缩放锚点：固定角 / 中心不动，光标位置按比例。</summary>
    private static void PinScaleAnchors()
    {
        var origin = new Point(100, 100);
        var oldSize = new Size(200, 100);
        var newSize = new Size(100, 50);
        var at = new Point(50, 50);

        Check.Equal(new Point(100, 100),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomTopLeft), "左上角不动");
        Check.Equal(new Point(200, 100),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomTopRight), "右上角不动");
        Check.Equal(new Point(100, 150),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomBottomLeft), "左下角不动");
        Check.Equal(new Point(200, 150),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomBottomRight), "右下角不动");
        Check.Equal(new Point(150, 125),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomCenter), "中心不动");

        // 光标位置：按光标在旧窗口里的比例（50/200 = 25%，50/100 = 50%）
        Check.Equal(new Point(125, 125),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at, PinSettings.ZoomMousePosition),
            "光标下的点不动");
        Check.Equal(new Point(125, 125),
            PinMath.AnchoredOrigin(origin, oldSize, newSize, at), "缺省 = 光标位置");

        // 锚点本身（取景框里当缩放锚点用）
        Check.Equal(new Point(0, 0), PinMath.AnchorPoint(oldSize, PinSettings.ZoomTopLeft, at), "左上角锚点");
        Check.Equal(new Point(200, 100), PinMath.AnchorPoint(oldSize, PinSettings.ZoomBottomRight, at),
            "右下角锚点");
        Check.Equal(new Point(100, 50), PinMath.AnchorPoint(oldSize, PinSettings.ZoomCenter, at), "中心锚点");
        Check.Equal(new Point(50, 50), PinMath.AnchorPoint(oldSize, PinSettings.ZoomMousePosition, at),
            "光标锚点就是光标所在的位置");

        // 窗口还没量出尺寸时按中心算，不除零
        Check.Equal(new Point(0, 0),
            PinMath.AnchorPoint(new Size(0, 0), PinSettings.ZoomMousePosition, new Point(30, 40)),
            "零尺寸按中心");

        // 锚点比例：光标位置给 NaN（由调用方算）
        var (rx, ry) = PinSettings.AnchorRatio(PinSettings.ZoomMousePosition);
        Check.True(double.IsNaN(rx) && double.IsNaN(ry), "光标位置不是固定比例");
        Check.Equal((1.0, 0.0), PinSettings.AnchorRatio(PinSettings.ZoomTopRight), "右上角比例");
        Check.Equal((0.5, 0.5), PinSettings.AnchorRatio(PinSettings.ZoomCenter), "中心比例");
        Check.True(PinSettings.IsMouseAnchor("mouse_position"), "认得出光标锚点");
        Check.False(PinSettings.IsMouseAnchor("center"), "中心不是光标锚点");
    }

    /// <summary>阴影带宽：16 物理像素换 DIP（缩放 125% = 12.8），太小兜底 6，峰值 0.36。</summary>
    private static void PinShadowGeometry()
    {
        Check.Equal(16.0, PinShadowWindow.MarginPhysical, "上游 pinnedShadowMargin = 16 物理像素");
        Check.Equal(16.0, PinShadowWindow.MarginDip(1), "100% = 16 DIP");
        Check.Equal(12.8, PinShadowWindow.MarginDip(1.25), "125% = 12.8 DIP");
        Check.Equal(8.0, PinShadowWindow.MarginDip(2), "200% = 8 DIP");
        Check.Equal(6.0, PinShadowWindow.MarginDip(10), "极小带宽兜底 6 DIP");
        Check.Equal(16.0, PinShadowWindow.MarginDip(0), "缩放未知按 100%");
        Check.Equal(0.36, PinShadowWindow.PeakAlpha, "上游 peakAlphaScale = 0.36");
    }

    /// <summary>贴图记录的边框 / 阴影默认值：老记录（缺字段）按「显示边框、不显示阴影」。</summary>
    private static void PinRecordDefaults()
    {
        var record = new PinRecord();
        Check.True(record.ShowBorder, "缺字段 = 显示边框");
        Check.False(record.ShowShadow, "缺字段 = 不显示阴影");
    }
}
