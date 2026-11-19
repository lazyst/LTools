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
