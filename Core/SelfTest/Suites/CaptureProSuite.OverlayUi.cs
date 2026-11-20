using System;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图界面与参考线（上游 <c>screenshot_ui/*</c> 那 18 项）的纯逻辑断言：
/// 归一化 / 单位换算 / 信息条显隐 / 取色显示时机 / 色值格式 / 参考线几何与反色。
/// </summary>
/// <remarks>
/// 全部是不读盘、不建窗口的纯函数（<see cref="OverlayUiSettings"/> 的 <c>*For</c> 重载 +
/// <see cref="GuideLines"/> + <see cref="PickerColor"/>）。窗口层的接线在
/// <c>capture-overlay-smoke</c> 的 <c>overlayui</c> 段。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void OverlayUiCases()
    {
        OverlayUiNormalize();
        OverlayUiUnits();
        OverlayUiVisibility();
        OverlayUiColorText();
        GuideLineGeometry();
        GuideLineAdaptive();
    }

    /// <summary>每个设置项的归一化：认得出的一字不差，认不出的落回上游默认值。</summary>
    private static void OverlayUiNormalize()
    {
        // 工具条尺寸：只有 small 是「小」，其余（含 null / 空 / 乱写）都是正常
        Check.Equal(OverlayUiSettings.ToolbarSmall, OverlayUiSettings.NormalizeToolbarSize("small"), "small 认得出");
        Check.Equal(OverlayUiSettings.ToolbarSmall, OverlayUiSettings.NormalizeToolbarSize(" SMALL "), "大小写 / 空格都能认");
        Check.Equal(OverlayUiSettings.ToolbarNormal, OverlayUiSettings.NormalizeToolbarSize(null), "空 = 正常");
        Check.Equal(OverlayUiSettings.ToolbarNormal, OverlayUiSettings.NormalizeToolbarSize("big"), "乱写 = 正常");
        Check.Equal(0.8, OverlayUiSettings.SmallToolbarScale, "小档 = 0.8 倍");

        // 过渡动画：存的是毫秒（0 / 32 / 72），认不出的按柔和
        Check.Equal(SelectionTransition.None, OverlayUiSettings.NormalizeTransition(0), "0 = 不做动画");
        Check.Equal(SelectionTransition.Fast, OverlayUiSettings.NormalizeTransition(72), "72 = 快速");
        Check.Equal(SelectionTransition.Soft, OverlayUiSettings.NormalizeTransition(32), "32 = 柔和");
        Check.Equal(SelectionTransition.Soft, OverlayUiSettings.NormalizeTransition(null), "缺项 = 柔和");
        Check.Equal(SelectionTransition.Soft, OverlayUiSettings.NormalizeTransition(999), "乱写 = 柔和");
        Check.Equal(0, OverlayUiSettings.TransitionValue(SelectionTransition.None), "None 写 0");
        Check.Equal(32, OverlayUiSettings.TransitionValue(SelectionTransition.Soft), "Soft 写 32");
        Check.Equal(72, OverlayUiSettings.TransitionValue(SelectionTransition.Fast), "Fast 写 72");

        // 显示单位
        Check.Equal(OverlayUiSettings.UnitLogical, OverlayUiSettings.NormalizeUnit("logical_pixels"), "逻辑像素认得出");
        Check.Equal(OverlayUiSettings.UnitPhysical, OverlayUiSettings.NormalizeUnit(""), "空 = 物理像素");
        Check.Equal(OverlayUiSettings.UnitPhysical, OverlayUiSettings.NormalizeUnit("dp"), "乱写 = 物理像素");

        // 取色显示时机
        Check.Equal(OverlayUiSettings.PickerAlwaysShow,
            OverlayUiSettings.NormalizePickerMode("always_show"), "总是显示认得出");
        Check.Equal(OverlayUiSettings.PickerAlwaysHide,
            OverlayUiSettings.NormalizePickerMode("always_hide"), "总是不显示认得出");
        Check.Equal(OverlayUiSettings.PickerHideOutsideSelection,
            OverlayUiSettings.NormalizePickerMode(null), "缺项 = 选区外不显示");

        // 坐标口径 / 色值格式
        Check.Equal(OverlayUiSettings.CoordRelative,
            OverlayUiSettings.NormalizeCoordinateMode("relative"), "相对坐标认得出");
        Check.Equal(OverlayUiSettings.CoordGlobal,
            OverlayUiSettings.NormalizeCoordinateMode("screen"), "乱写 = 全局");
        Check.Equal(OverlayUiSettings.FormatHsl, OverlayUiSettings.NormalizeColorFormat("HSL"), "格式不区分大小写");
        Check.Equal(OverlayUiSettings.FormatHex, OverlayUiSettings.NormalizeColorFormat("cmyk"), "乱写 = HEX");

        // 提示不透明度：0–100，缺项 = 100
        Check.Equal(0, OverlayUiSettings.NormalizeOpacity(0), "0% 保留");
        Check.Equal(100, OverlayUiSettings.NormalizeOpacity(100), "100% 保留");
        Check.Equal(100, OverlayUiSettings.NormalizeOpacity(null), "缺项 = 100%");
        Check.Equal(0, OverlayUiSettings.NormalizeOpacity(-40), "负数夹到 0");
        Check.Equal(100, OverlayUiSettings.NormalizeOpacity(400), "超出夹到 100");
        Check.Equal(0.55, OverlayUiSettings.NormalizeOpacity(55) / 100.0, "百分比换成 0–1");
    }

    /// <summary>信息条单位：物理像素原样、逻辑像素按 DPI 换算，滚轮一格跟着单位走。</summary>
    private static void OverlayUiUnits()
    {
        const string physical = OverlayUiSettings.UnitPhysical;
        const string logical = OverlayUiSettings.UnitLogical;

        // 125% 缩放下 125 物理像素 = 100 dp
        Check.Equal(125, OverlayUiSettings.DisplayValue(physical, 125, 1.25), "物理像素原样显示");
        Check.Equal(100, OverlayUiSettings.DisplayValue(logical, 125, 1.25), "逻辑像素按 DPI 换算");
        Check.Equal(125, OverlayUiSettings.DisplayValue(logical, 125, 0), "scale = 0 不炸（按 1 算）");

        // 物理像素下一格就是 1 px
        Check.Equal(1, OverlayUiSettings.StepPixels(physical, 125, 1, 1.25), "物理像素 +1");
        Check.Equal(-1, OverlayUiSettings.StepPixels(physical, 125, -1, 1.25), "物理像素 -1");
        Check.Equal(0, OverlayUiSettings.StepPixels(physical, 125, 0, 1.25), "方向 0 = 不动");

        // 逻辑像素下一格是 1 dp = 1.25 px（从 125 px 走 +1 落到 126，-1 落到 124）
        Check.Equal(1, OverlayUiSettings.StepPixels(logical, 125, 1, 1.25), "dp +1（125 → 126）");
        Check.Equal(-1, OverlayUiSettings.StepPixels(logical, 125, -1, 1.25), "dp -1（125 → 124）");

        // 150% 下 3 px 一格：100 px = 66.7 dp → 67 dp → 100.5 px
        Check.Equal(2, OverlayUiSettings.StepPixels(logical, 100, 1, 1.5), "dp 步长按 DPI 放大");
        Check.Equal(-1, OverlayUiSettings.StepPixels(logical, 100, -1, 1.5), "dp 步长向下取整");

        // 单位文字（上游 screenshotSelectionDisplayUnitText）
        Check.Equal("px", OverlayUiSettings.UnitLabelFor(physical), "物理像素写 px");
        Check.Equal("dp", OverlayUiSettings.UnitLabelFor(logical), "逻辑像素写 dp");
    }

    /// <summary>信息条显隐（两个开关）与取色器显示时机（三档 × 选区内外）。</summary>
    private static void OverlayUiVisibility()
    {
        // 总开关优先：隐藏了就不管工具
        Check.False(OverlayUiSettings.ShouldShowSelectionBar(true, true, CaptureTool.Select), "总开关关 → 不显示");
        Check.False(OverlayUiSettings.ShouldShowSelectionBar(true, true, CaptureTool.Pen), "总开关关 → 绘图工具也不显示");

        // 「选择」工具下要不要显示，默认不显示（上游 show_edit_selection_toolbar 默认 false）
        Check.False(OverlayUiSettings.ShouldShowSelectionBar(false, false, CaptureTool.Select), "选择工具默认不显示");
        Check.True(OverlayUiSettings.ShouldShowSelectionBar(false, true, CaptureTool.Select), "打开后选择工具也显示");
        Check.True(OverlayUiSettings.ShouldShowSelectionBar(false, false, CaptureTool.Shape), "其它工具一直显示");

        const string outside = OverlayUiSettings.PickerHideOutsideSelection;
        const string alwaysShow = OverlayUiSettings.PickerAlwaysShow;
        const string alwaysHide = OverlayUiSettings.PickerAlwaysHide;

        // 总是不显示：任何情况都不画色卡
        Check.False(OverlayUiSettings.PickerVisible(alwaysHide, true, true, false), "总是不显示（选区内）");
        Check.False(OverlayUiSettings.PickerVisible(alwaysHide, false, false, false), "总是不显示（没选区）");

        // 总是显示：没选区也画
        Check.True(OverlayUiSettings.PickerVisible(alwaysShow, false, false, false), "总是显示（没选区）");

        // 选区外不显示：选区外藏、选区内画、没选区时（手动选择中）画
        Check.False(OverlayUiSettings.PickerVisible(outside, true, false, false), "选区外不显示");
        Check.True(OverlayUiSettings.PickerVisible(outside, true, true, false), "选区内显示");
        Check.True(OverlayUiSettings.PickerVisible(outside, false, false, false), "没选区时照常显示");
        Check.True(OverlayUiSettings.PickerVisible(outside, true, false, false, dragging: true), "拖动中照常显示");
    }

    /// <summary>色值四种格式与坐标那一行（照上游 <c>formatColor</c> / <c>currentPositionText</c>）。</summary>
    private static void OverlayUiColorText()
    {
        var color = Color.FromRgb(0x40, 0x96, 0xFF);

        Check.Equal("#4096FF", PickerColor.Format(color, OverlayUiSettings.FormatHex), "HEX");
        Check.Equal("4096FF", PickerColor.Format(color, OverlayUiSettings.FormatHexWithoutHash), "无井号");
        Check.Equal("rgb(64, 150, 255)", PickerColor.Format(color, OverlayUiSettings.FormatRgb), "rgb()");
        Check.Equal("hsl(213, 100.0%, 62.5%)", PickerColor.Format(color, OverlayUiSettings.FormatHsl), "hsl()");

        // HSL 三原色（Qt 的 toHsl：色相取整、饱和 / 亮度保留一位小数）
        Check.Equal((0, 100.0, 50.0), PickerColor.ToHsl(Color.FromRgb(255, 0, 0)), "红 = 0°");
        Check.Equal((120, 100.0, 50.0), PickerColor.ToHsl(Color.FromRgb(0, 255, 0)), "绿 = 120°");
        Check.Equal((240, 100.0, 50.0), PickerColor.ToHsl(Color.FromRgb(0, 0, 255)), "蓝 = 240°");
        var gray = PickerColor.ToHsl(Color.FromRgb(128, 128, 128));
        Check.True(gray.Hue == 0 && gray.Saturation == 0 && Math.Abs(gray.Lightness - 50.2) < 0.2,
            $"灰没有饱和（实际 {gray}）");

        // 格式轮换：hex → 无井号 → rgb → hsl → hsb → hex
        Check.Equal(OverlayUiSettings.FormatHexWithoutHash,
            OverlayUiSettings.NextColorFormat(OverlayUiSettings.FormatHex), "hex → 无井号");
        Check.Equal(OverlayUiSettings.FormatRgb,
            OverlayUiSettings.NextColorFormat(OverlayUiSettings.FormatHexWithoutHash), "无井号 → rgb");
        Check.Equal(OverlayUiSettings.FormatHsl,
            OverlayUiSettings.NextColorFormat(OverlayUiSettings.FormatRgb), "rgb → hsl");
        Check.Equal(OverlayUiSettings.FormatHsb,
            OverlayUiSettings.NextColorFormat(OverlayUiSettings.FormatHsl), "hsl → hsb");
        Check.Equal(OverlayUiSettings.FormatHex,
            OverlayUiSettings.NextColorFormat(OverlayUiSettings.FormatHsb), "hsb → hex");

        // HSB 写法（上游取色弹窗的第三种；等于 HSV）
        Check.Equal("hsb(213, 75%, 100%)", PickerColor.Format(color, OverlayUiSettings.FormatHsb),
            "hsb()");
        Check.Equal("hsb(0, 100%, 100%)", ColorText.Format(Color.FromRgb(255, 0, 0), ColorFormatKind.Hsb),
            "红 = hsb(0, 100%, 100%)");
        Check.Equal((120.0, 100.0, 100.0), ColorText.ToHsb(Color.FromRgb(0, 255, 0)), "绿 = 120° / 100% / 100%");
        var dark = ColorText.ToHsb(Color.FromRgb(0, 0, 128));
        Check.True(Math.Abs(dark.Hue - 240) < 0.01 && Math.Abs(dark.Saturation - 100) < 0.01 &&
                   Math.Abs(dark.Brightness - 50.196) < 0.2, $"蓝的亮度 = 128/255（实际 {dark}）");

        // 半透明：hex 补 AA、rgb 用 rgba(…, 0.533)、hsb 用百分比刻度里的 hsb(…, 0.533)
        var translucent = Color.FromArgb(0x88, 0x40, 0x96, 0xFF);
        Check.Equal("#4096FF88", ColorText.Format(translucent, ColorFormatKind.Hex), "HEX 带 alpha");
        Check.Equal("rgba(64, 150, 255, 0.533)", ColorText.Format(translucent, ColorFormatKind.Rgb),
            "rgba() 的 alpha 是分数");
        Check.Equal("hsb(213, 75%, 100%, 0.533)", ColorText.Format(translucent, ColorFormatKind.Hsb),
            "hsb() 带 alpha");

        // 解析：hex 3/4/6/8 位、rgb/rgba、hsl/hsla、hsb/hsv（逗号与空格斜杠两种写法）
        Check.True(ColorText.TryParse("#4096FF", out var parsed) && parsed == Color.FromRgb(0x40, 0x96, 0xFF),
            "#RRGGBB");
        Check.True(ColorText.TryParse("#49F", out parsed) && parsed == Color.FromRgb(0x44, 0x99, 0xFF),
            "#RGB 展开");
        Check.True(ColorText.TryParse("4096FF", out parsed) && parsed == Color.FromRgb(0x40, 0x96, 0xFF),
            "无井号 6 位");
        Check.True(ColorText.TryParse("#4096FF88", out parsed) && parsed.A == 0x88, "#RRGGBBAA");
        Check.True(ColorText.TryParse("rgba(64, 150, 255, 0.5)", out parsed) && parsed.A is 127 or 128,
            "rgba()");
        Check.True(ColorText.TryParse("hsl(213, 100%, 62.5%)", out parsed) &&
                   Math.Abs(parsed.R - 0x40) <= 1 && Math.Abs(parsed.B - 0xFF) <= 1, "hsl()");
        Check.True(ColorText.TryParse("hsb(213, 75%, 100%)", out parsed) &&
                   Math.Abs(parsed.R - 0x40) <= 1 && Math.Abs(parsed.G - 0x96) <= 1 &&
                   Math.Abs(parsed.B - 0xFF) <= 1, "hsb() 与 rgb() 同一档");
        Check.True(ColorText.TryParse("hsv(213, 75%, 100%)", out parsed) && Math.Abs(parsed.R - 0x40) <= 1,
            "hsv() 也认");
        Check.True(ColorText.TryParse("hsb(213 75% 100%)", out parsed) && Math.Abs(parsed.R - 0x40) <= 1,
            "空格写法");
        Check.False(ColorText.TryParse("hsb(213, 75)", out _), "少一个分量不认");
        Check.False(ColorText.TryParse("无聊的一段文字", out _), "普通文字不认");

        // 坐标：X: … Y: …（四舍五入，不含小数）
        Check.Equal("X: 128 Y: -3", PickerColor.PositionText(127.6, -3.4), "坐标文本");
        Check.Equal("#RRGGBB", PickerColor.FormatHint(OverlayUiSettings.FormatHex), "提示：HEX");
        Check.Equal("hsl(h, s%, l%)", PickerColor.FormatHint(OverlayUiSettings.FormatHsl), "提示：HSL");
        Check.Equal("hsb(h, s%, b%)", PickerColor.FormatHint(OverlayUiSettings.FormatHsb), "提示：HSB");
    }

    /// <summary>参考线几何：像素中心吸附、右边取外沿、十字落在视口内、取样点留空。</summary>
    private static void GuideLineGeometry()
    {
        // 100% 缩放：x.5 是像素中心
        Check.Equal(10.5, GuideLines.PixelCenter(10.2, 1.0), "100% 吸附到像素中心");
        Check.Equal(10.5, GuideLines.PixelCenter(10.7, 1.0), "100% 同上（同一个像素）");
        Check.Equal(19.5, GuideLines.Right(19.2, 1.0), "右边 = ceil - 0.5");

        // 125% 缩放：物理像素 1 个 = 0.8 DIP
        Check.Equal(0.4, GuideLines.PixelCenter(0.3, 1.25), "125% 像素中心");
        Check.Equal(1.2, GuideLines.Right(0.9, 1.25), "125% 右边");

        // 十字：竖线 x 取光标吸附值，横线 y 取光标吸附值，四条边取视口
        var viewport = new Rect(0, 0, 200, 100);
        var cross = GuideLines.Cross(viewport, new Point(50.2, 30.7), 1.0);
        Check.True(cross.HasVertical && cross.HasHorizontal, "十字两笔都有");
        Check.Equal(50.5, cross.VerticalStart.X, "竖线落在光标所在像素中心");
        Check.Equal(30.5, cross.HorizontalStart.Y, "横线落在光标所在像素中心");
        Check.Equal(0.5, cross.VerticalStart.Y, "竖线从视口顶边开始");
        Check.Equal(99.5, cross.VerticalEnd.Y, "竖线到视口底边结束");
        Check.Equal(0.5, cross.HorizontalStart.X, "横线从视口左边开始");
        Check.Equal(199.5, cross.HorizontalEnd.X, "横线到视口右边结束");

        // 空视口不画
        Check.True(GuideLines.Cross(new Rect(0, 0, 0, 0), new Point(10, 10), 1.0).IsEmpty, "空视口 = 空十字");

        // 取样点：中间留 1 个物理像素的小方块，四条线各自从视口边走到它旁边
        var reticle = GuideLines.PickerReticle(viewport, new Point(50.2, 30.7), 1.0);
        Check.Equal(new Rect(50, 30, 1, 1), reticle.Marker, "取样像素 = 1×1 小方块");
        Check.Equal(50.5, reticle.TopStart.X, "上段与取样点同列");
        Check.Equal(30.0, reticle.TopEnd.Y, "上段停在取样像素上沿");
        Check.Equal(31.0, reticle.BottomStart.Y, "下段从取样像素下沿开始");
        Check.Equal(99.5, reticle.BottomEnd.Y, "下段走到视口底边");
        Check.Equal(50.0, reticle.LeftEnd.X, "左段停在取样像素左沿");
        Check.Equal(51.0, reticle.RightStart.X, "右段从取样像素右沿开始");
        Check.Equal(199.5, reticle.RightEnd.X, "右段走到视口右边");

        // 光标贴在视口边上时那一段长度为 0（不画反方向的线）
        var edge = GuideLines.PickerReticle(viewport, new Point(0.5, 0.5), 1.0);
        Check.True(edge.TopEnd.Y <= edge.TopStart.Y, "贴顶时上段不反向");
    }

    /// <summary>参考线反色：跟背景太像才反、透明 / 没取样原样返回；取样方块描边取反差最大的一侧。</summary>
    private static void GuideLineAdaptive()
    {
        var black = Color.FromArgb(0xFF, 0, 0, 0);
        var white = Color.FromArgb(0xFF, 255, 255, 255);
        var blue = Color.FromArgb(0xFF, 0x40, 0x96, 0xFF);

        // 黑线压在黑底上 → 反成白（保留 alpha）
        Check.Equal(white, GuideLines.Adaptive(black, black), "黑压黑 → 反色");
        // 黑线压在白底上 → 原样（差得够远）
        Check.Equal(black, GuideLines.Adaptive(black, white), "黑压白 → 原样");
        // 跟背景差一点点也算「太像」（三通道欧氏距离 ≤ 64）
        Check.Equal(Color.FromArgb(0xFF, 255, 255, 255),
            GuideLines.Adaptive(black, Color.FromRgb(30, 30, 30)), "差 30 → 太像，反色");
        // 差 100 就够远了
        Check.Equal(black, GuideLines.Adaptive(black, Color.FromRgb(100, 100, 100)), "差 100 → 原样");
        // 透明色不参与（= 用户明确关掉这一条）
        Check.Equal(Colors.Transparent, GuideLines.Adaptive(Colors.Transparent, black), "透明原样返回");
        // 没取到样（越界）也原样
        Check.Equal(blue, GuideLines.Adaptive(blue, null), "没取样原样返回");

        // 取样像素的小方框：亮底用黑框、暗底用白框（照上游 readableTextColor）
        Check.Equal(Color.FromArgb(224, 0, 0, 0), GuideLines.Readable(white), "亮底 → 半透明黑框");
        Check.Equal(Color.FromArgb(217, 255, 255, 255), GuideLines.Readable(black), "暗底 → 半透明白框");
        Check.Equal(Color.FromArgb(224, 0, 0, 0), GuideLines.Readable(Color.FromRgb(200, 10, 10)), "亮红 → 黑框");
    }
}
