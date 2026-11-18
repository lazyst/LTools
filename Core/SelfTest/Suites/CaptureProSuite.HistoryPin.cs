using System.Windows;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 采集历史「按原选区 + 原外观贴回」：外观模型、落位几何、能不能贴的判定。
/// </summary>
/// <remarks>
/// 纯逻辑，不碰窗口也不读盘；真窗口贴回的位置 / 底色与描边的显隐由仓库外
/// <c>capture-overlay-smoke</c> 的 <c>HistoryPinSection</c> 端到端验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void HistoryPinCases()
    {
        // ============ 从选区样式算出外观：长度换算成成品图像素 ============
        var shadow = new SelectionStyle { CornerRadius = 8, ShadowWidth = 10 }
            .WithBorderType(SelectionBorderType.Shadow);
        var look = PinAppearance.FromStyle(shadow, 1.25, 200, 100);
        Check.Equal(10, look.RadiusPx, "圆角按屏幕缩放换成图像素（8 × 1.25）");
        Check.Equal(12, look.EffectPx, "阴影宽度按屏幕缩放换成图像素（10 × 1.25 = 12.5，四舍六入五成双）");
        Check.Equal(PinAppearance.DecorationShadow, look.Decoration, "阴影记成 shadow");
        Check.True(look.HasShape, "有圆角 / 效果 = 图自带透明外形");
        Check.True(look.SuppressesFrame, "图里已经画了阴影，窗口不再叠自己的描边");

        var border = new SelectionStyle { BorderWidth = 4 }
            .WithBorderType(SelectionBorderType.Border);
        var edge = PinAppearance.FromStyle(border, 1.0, 100, 100);
        Check.Equal(PinAppearance.DecorationBorder, edge.Decoration, "边框记成 border");
        Check.Equal(4, edge.EffectPx, "边框宽度照原样");

        var plain = PinAppearance.FromStyle(SelectionStyle.None, 1.5, 300, 200);
        Check.Equal(0, plain.RadiusPx, "直角 = 0");
        Check.Equal(0, plain.EffectPx, "没有效果 = 0");
        Check.Equal(PinAppearance.DecorationNone, plain.Decoration, "没有效果不记装饰");
        Check.False(plain.HasShape, "直角无效果 = 不透明外形");
        Check.False(plain.SuppressesFrame, "直角无效果时窗口照常画 1px 描边");

        // 圆角不超过短边的一半（与上游 std::clamp 同口径）
        var round = PinAppearance.FromStyle(
            new SelectionStyle { CornerRadius = 100 }, 2.0, 30, 300);
        Check.Equal(15, round.RadiusPx, "圆角被夹到短边的一半（30 / 2）");

        // 只有圆角、没有效果：仍自带透明外形，但窗口描边要留着（跟着圆角走）
        var onlyRound = PinAppearance.FromStyle(new SelectionStyle { CornerRadius = 6 }, 1.0, 120, 80);
        Check.True(onlyRound.HasShape, "只有圆角也算自带外形");
        Check.False(onlyRound.SuppressesFrame, "没有阴影 / 边框时窗口仍画描边");

        // scale 传 0 / 负数按 100%
        var badScale = PinAppearance.FromStyle(new SelectionStyle { ShadowWidth = 6 }, 0, 100, 100);
        Check.Equal(6, badScale.EffectPx, "缩放比不合法时按 100% 算");

        // ============ 夹一遍：负数归零，圆角按图尺寸收，认不出的装饰按「没有」 ============
        var wild = new PinAppearance
        {
            RadiusPx = 40,
            EffectPx = -5,
            Decoration = "  ",
        }.Clamp(20, 30);
        Check.Equal(10, wild.RadiusPx, "圆角被夹到短边的一半（20 / 2）");
        Check.Equal(0, wild.EffectPx, "负的效果宽归零");
        Check.Equal(PinAppearance.DecorationNone, wild.Decoration, "认不出的装饰按「没有」");

        var zeroSize = new PinAppearance { RadiusPx = 12 }.Clamp(0, 0);
        Check.Equal(12, zeroSize.RadiusPx, "尺寸为 0（还没解码）时不夹圆角，留给之后 SetAppearance");

        Check.Equal(PinAppearance.DecorationBorder,
            PinAppearance.NormalizeDecoration(PinAppearance.DecorationBorder), "border 原样");
        Check.Equal(PinAppearance.DecorationNone,
            PinAppearance.NormalizeDecoration("Shadow"), "大小写不同不算（枚举值就是小写）");

        // ============ 窗口矩形：内容正好落回选区，图大出来的内边距往四周让 ============
        var content = new Int32Rect(100, 200, 300, 150);
        var box = new PinAppearance { EffectPx = 18 }.WindowRect(content, 336, 186);
        Check.Equal(82, box.X, "左边让出内边距（100 − 18）");
        Check.Equal(182, box.Y, "上边让出内边距（200 − 18）");
        Check.Equal(336, box.Width, "窗口宽 = 图宽");
        Check.Equal(186, box.Height, "窗口高 = 图高");
        Check.Equal(content.X, box.X + (box.Width - content.Width) / 2, "内容左边界落回选区左边界");
        Check.Equal(content.Y, box.Y + (box.Height - content.Height) / 2, "内容上边界落回选区上边界");

        var exact = PinAppearance.None.WindowRect(content, 300, 150);
        Check.Equal(content, exact, "图与选区一样大 = 原样摆回");

        var smaller = new PinAppearance { EffectPx = 8 }.WindowRect(content, 200, 100);
        Check.Equal(new Int32Rect(150, 225, 200, 100), smaller,
            "图比选区小（不该发生）时也按中心对齐，不倒扣位置");

        var none = new PinAppearance { EffectPx = 18 }.WindowRect(content, 0, 0);
        Check.Equal(content, none, "尺寸为 0（图还没解码）时退回选区");

        var half = new PinAppearance { EffectPx = 17 }.WindowRect(content, 334, 184);
        Check.Equal(83, half.X, "图比「内容 + 2 × 效果」小 1 像素时按整除算内边距");

        // 圆角在窗口里的 DIP 半径：随屏幕缩放与贴图缩放一起变
        var radius = new PinAppearance { RadiusPx = 10 };
        Check.Equal(8.0, radius.RadiusDip(1.25, 1.0), "10 图像素在 125% 屏上 = 8 DIP");
        Check.Equal(4.0, radius.RadiusDip(1.25, 0.5), "贴图缩到 50% 后 = 4 DIP");
        Check.Equal(0.0, PinAppearance.None.RadiusDip(1.25, 1.0), "没有圆角就不画圆角");

        // ============ 截图落位：始终记选区；长截图不记外观 ============
        var area = new Int32Rect(120, 240, 400, 300);
        var placed = CapturePlacement.Of(area, shadow, 1.25, scrolling: false);
        Check.Equal(area, placed.Selection, "落位记下当初那块选区");
        Check.Equal(12, placed.Appearance.EffectPx, "外观一起记下");
        Check.False(placed.Scrolling, "普通截图不是长截图");

        var scrolling = CapturePlacement.Of(area, shadow, 1.25, scrolling: true);
        Check.Equal(PinAppearance.None, scrolling.Appearance, "长截图不记外观（拼出来的长图没有那圈效果）");
        Check.True(scrolling.Scrolling, "长截图标记要记下");

        // ============ 一条记录能不能按原选区贴回 ============
        static CaptureEntry Shot(int w, int h, bool scrolling = false) => new()
        {
            Kind = CaptureCatalog.KindShot,
            SelectionX = 50,
            SelectionY = 60,
            SelectionW = w,
            SelectionH = h,
            RadiusPx = 6,
            EffectPx = 9,
            Decoration = PinAppearance.DecorationShadow,
            Scrolling = scrolling,
        };

        Check.True(HistoryPinPlacement.SupportsPin(Shot(200, 100)), "截图 + 记过选区 = 能贴回");
        Check.False(HistoryPinPlacement.SupportsPin(null), "没有记录不能贴");
        Check.False(HistoryPinPlacement.SupportsPin(Shot(0, 100)), "没记过宽不能按原位置贴");
        Check.False(HistoryPinPlacement.SupportsPin(Shot(200, 0)), "没记过高不能按原位置贴");
        Check.False(HistoryPinPlacement.SupportsPin(Shot(200, 100, scrolling: true)),
            "长截图只能居中贴");

        var recognize = Shot(200, 100);
        recognize.Kind = CaptureCatalog.KindRecognize;
        Check.True(HistoryPinPlacement.SupportsPin(recognize), "识别出来的截图同样能贴回");

        var record = Shot(200, 100);
        record.Kind = CaptureCatalog.KindRecord;
        Check.False(HistoryPinPlacement.SupportsPin(record), "录屏没有截图可以贴");

        var entry = Shot(200, 100);
        Check.True(entry.CanPin, "CanPin 跟着 SupportsPin 走");
        Check.Equal(new Int32Rect(50, 60, 200, 100), HistoryPinPlacement.Selection(entry)!,
            "选区按记录还原");
        Check.Equal(6, HistoryPinPlacement.Appearance(entry).RadiusPx, "外观按记录还原");
        Check.Equal(9, HistoryPinPlacement.Appearance(entry).EffectPx, "效果内边距按记录还原");
        Check.Equal(PinAppearance.None, HistoryPinPlacement.Appearance((CaptureEntry?)null),
            "没有记录就没有外观");
        Check.True(HistoryPinPlacement.Selection(null) == null, "没有记录就没有选区");

        var restored = HistoryPinPlacement.WindowRect(entry, 218, 118);
        Check.Equal(new Int32Rect(41, 51, 218, 118), restored!,
            "贴图窗口按内边距让开，内容正好盖住原选区");
        var longShot = Shot(200, 100, scrolling: true);
        Check.True(HistoryPinPlacement.WindowRect(longShot, 200, 100) == null,
            "长截图没有原位置落点");

        // 老记录（P5-h 之前）只有图片：外观全 0，贴回走回落
        var legacy = new CaptureEntry { Kind = CaptureCatalog.KindShot };
        Check.False(legacy.CanPin, "老记录不能按原位置贴");
        Check.Equal(PinAppearance.None, HistoryPinPlacement.Appearance(legacy), "老记录没有外观");

        // 认不出的装饰值按「没有」处理
        var odd = Shot(100, 100);
        odd.Decoration = "whatever";
        Check.Equal(PinAppearance.DecorationNone, HistoryPinPlacement.Appearance(odd).Decoration,
            "认不出的装饰按「没有」");

        // ============ 选区跟当前桌面求交（显示器拔了 / 分辨率改了就别硬摆） ============
        var monitors = new[]
        {
            new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(1920, 0, 1920, 1080),
        };

        Check.True(HistoryPinPlacement.MeetsDesktop(new Int32Rect(100, 100, 200, 200), monitors),
            "整块在桌面里");
        Check.True(HistoryPinPlacement.MeetsDesktop(new Int32Rect(2400, 100, 400, 300), monitors),
            "跨到第二块屏也算");
        Check.True(HistoryPinPlacement.MeetsDesktop(new Int32Rect(-100, -50, 200, 200), monitors),
            "部分越过屏幕左上角仍算有交集");
        Check.False(HistoryPinPlacement.MeetsDesktop(new Int32Rect(4000, 4000, 200, 200), monitors),
            "所有屏幕都碰不上");
        Check.True(HistoryPinPlacement.MeetsDesktop(new Int32Rect(1920, 0, 100, 100), monitors),
            "正好从两块屏的缝开始也算（落在右屏里）");
        Check.False(HistoryPinPlacement.MeetsDesktop(new Int32Rect(2000, 0, 100, 100),
                new[] { monitors[0] }),
            "只有左屏时右屏那块就不算");
        Check.False(HistoryPinPlacement.MeetsDesktop(new Int32Rect(0, 0, 0, 100), monitors),
            "空矩形不算");
        Check.False(HistoryPinPlacement.MeetsDesktop(new Int32Rect(100, 100, 200, 200), null),
            "没有显示器列表就退回居中");
        Check.False(HistoryPinPlacement.MeetsDesktop(new Int32Rect(100, 100, 200, 200), []),
            "显示器列表为空就退回居中");
    }
}
