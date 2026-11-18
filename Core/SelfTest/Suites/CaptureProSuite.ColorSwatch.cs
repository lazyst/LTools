using System;
using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图颜色取样板（照上游 <c>tests/pinned_color_swatch_content_tests.h</c>）：
/// 认 / 拒的颜色写法、三行规范文案、alpha 精度、色卡版面度量、出场居中与等比缩小。
/// </summary>
/// <remarks>
/// 卡片渲染（真实位图、复制热区命中、点复制进剪贴板）由仓库外 <c>capture-overlay-smoke</c> 端到端验；
/// 这里只断言纯函数，任何一条红都说明「这段文字该不该认成颜色」或「复制出来是什么」变了。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ColorSwatchCases()
    {
        // ============ 认：参考色与等价写法 ============
        var green = ColorSwatch.Parse("#7DD353");
        Check.True(green != null, "认得出参考 HEX");
        Check.Equal(Colors2(125, 211, 83, 255), green!.Color, "参考 HEX 的通道");
        Check.Equal("#7DD353", green.Display[0], "第一行显示 HEX");
        Check.Equal("125, 211, 83", green.Display[1], "第二行显示 RGB");
        Check.Equal("100.3, 59.3%, 57.6%", green.Display[2], "第三行显示 HSL");
        Check.Equal("#7DD353", green.Clipboard[0], "复制 HEX");
        Check.Equal("rgb(125, 211, 83)", green.Clipboard[1], "复制 RGB 规范 CSS");
        Check.Equal("hsl(100.3, 59.3%, 57.6%)", green.Clipboard[2], "复制 HSL 规范 CSS");
        Check.Equal("HEX:", green.Labels[0], "不透明时第二行标签是 RGB:");
        Check.Equal("RGB:", green.Labels[1], "不透明时第二行标签是 RGB:");
        Check.Equal("HSL:", green.Labels[2], "不透明时第三行标签是 HSL:");

        foreach (string text in new[]
                 {
                     " #7dd353 \n", "7DD353", "RGB(125, 211, 83)", "rgb(125 211 83)",
                     "hsl(100.3deg, 59.3%, 57.6%)", "hsl(100.3 59.3% 57.6%)",
                 })
        {
            var value = ColorSwatch.Parse(text);
            Check.True(value != null && value.Color == green.Color, "等价写法：" + text.Trim());
        }

        // ============ 认：alpha 在最后 ============
        foreach (string text in new[]
                 {
                     "#F008", "#FF000088", "FF000088", "rgba(255,0,0,0.533)",
                     "rgb(100% 0% 0% / 53.333%)", "hsla(-360deg,100%,50%,0.533)",
                     "hsl(720 100% 50% / 53.333%)",
                 })
        {
            var value = ColorSwatch.Parse(text);
            Check.True(value != null && value.Color == Colors2(255, 0, 0, 136), "CSS alpha 写法：" + text);
            Check.Equal("#FF000088", value!.Clipboard[0], "半透明 HEX 带 alpha：" + text);
            Check.Equal("rgba(255, 0, 0, 0.533)", value.Clipboard[1], "半透明 RGB 用 rgba + 三位 alpha");
            Check.Equal("hsla(0.0, 100.0%, 50.0%, 0.533)", value.Clipboard[2], "半透明 HSL 用 hsla");
            Check.Equal("0.533", value.AlphaText, "alpha 文本三位小数");
            Check.Equal("RGBA:", value.Labels[1], "半透明第二行标签是 RGBA:");
            Check.Equal("HSLA:", value.Labels[2], "半透明第三行标签是 HSLA:");
        }

        // ============ 认：短 HEX 扩展 / 百分比 / 混合通道 ============
        Check.Equal("#AABBCC", ColorSwatch.Parse("#abc")!.Clipboard[0], "三位 HEX 双写扩展");
        Check.Equal(Colors2(255, 0, 0, 255), ColorSwatch.Parse("rgb(100% 0 0)")!.Color,
            "空格写法允许百分比与数值混用");
        Check.Equal(Colors2(128, 128, 128, 255), ColorSwatch.Parse("rgb(50%, 50%, 50%)")!.Color,
            "百分比中点四舍五入到 128（不是银行家舍入）");
        Check.Equal(0, ColorSwatch.Parse("#0000")!.Color.A, "四位 HEX 的 alpha 可以为 0");
        Check.Equal("hsl(0.0, 0.0%, 100.0%)", ColorSwatch.Parse("#FFFFFF")!.Clipboard[2],
            "无色相的颜色写成 0.0");

        // ============ 拒：这一切仍当普通文字 ============
        foreach (string text in new[]
                 {
                     "red", "abc", "value: #7DD353", "#7DD353\n#fff", "#12345", "#123456789",
                     "125, 211, 83", "rgb(256,0,0)", "rgb(-1,0,0)", "rgb(10%,2,3)",
                     "rgba(1,2,3,2)", "rgb(1 2 3 /)", "hsl(10,20,30)", "hsl(0,101%,50%)",
                     "rgb(1,2,3 / .5)", "rgb(nan,0,0)", "rgb(1,2,3) trailing",
                     "hsl(-+1, 50%, 50%)", "rgb(1,2,3,)",
                 })
        {
            Check.True(ColorSwatch.Parse(text) == null, "拒的形状：" + text.Replace("\n", "\\n"));
            Check.True(!ColorSwatch.LooksLikeColor(text), "拒的形状（LooksLikeColor）：" + text.Replace("\n", "\\n"));
        }

        // ============ 版面度量 ============
        Check.Equal(342, ColorSwatch.CompactWidth, "紧凑色卡宽 342");
        Check.Equal(210, ColorSwatch.CompactHeight, "紧凑色卡高 210");
        Check.Equal(470, ColorSwatch.WideWidth, "旧版色卡宽 470");
        Check.Equal(288, ColorSwatch.WideHeight, "旧版色卡高 288");
        Check.Equal(342, ColorSwatch.SizeOf(0).Width, "认不出的宽度按紧凑色卡");
        Check.Equal(470, ColorSwatch.SizeOf(470).Width, "470 = 旧版尺寸");
        Check.True(Math.Abs(ColorSwatch.ScaleFor(342) - 16.0 / 22.0) < 1e-9, "紧凑色卡按 16/22 缩放");
        Check.Equal(1.0, ColorSwatch.ScaleFor(470), "旧版色卡不缩放");
        Check.Equal(16.0, ColorSwatch.FontSize, "色卡字号 16");

        var layout = ColorCardLayout.Of(342);
        Check.Equal(342, layout.Width, "版面宽度取卡片宽度");
        Check.True(layout.RowTop(1) > layout.RowTop(0), "行自上而下排");
        Check.True(layout.ValueX > layout.Margin, "值列在标签列右边");
        Check.True(layout.CopySize > 0, "复制图标有尺寸");
        foreach (double top in new[] { layout.RowTop(0), layout.RowTop(1), layout.RowTop(2), layout.RowTop(3) })
        {
            Check.True(top >= 0 && top < layout.Height, "行顶落在卡片内");
        }

        // ============ 出场：居中 / 装不下等比缩 ============
        var work = new Rect(0, 0, 1920, 1040);
        var (box, zoom) = PinPlacement.Center(new Size(342, 210), work);
        Check.Equal(1.0, zoom, "放得下时不缩放");
        Check.Equal(342.0, box.Width, "放得下时保持原宽");
        Check.Equal(Math.Round((1920 - 342) / 2.0), Math.Round(box.X), "水平居中");

        var huge = new Size(4000, 2000);
        var (fitBox, fitZoom) = PinPlacement.Center(huge, work);
        Check.True(fitZoom < 1 && fitZoom > 0, "装不下时缩小");
        Check.True(fitBox.Width <= work.Width - PinPlacement.Margin * 2 + 0.5, "缩小后不超出左右边距");
        Check.True(fitBox.Height <= work.Height - PinPlacement.Margin * 2 + 0.5, "缩小后不超出上下边距");
        Check.True(Math.Abs(fitBox.Width / fitBox.Height - huge.Width / huge.Height) < 0.01, "缩小保持宽高比");

        // ============ 贴图历史里的种类 ============
        Check.Equal(PinStore.KindImage, PinStore.NormalizeKind(null), "缺字段按图片");
        Check.Equal(PinStore.KindImage, PinStore.NormalizeKind("nope"), "认不出的种类按图片");
        Check.Equal(PinStore.KindText, PinStore.NormalizeKind(PinStore.KindText), "文字种类");
        Check.Equal(PinStore.KindColor, PinStore.NormalizeKind(PinStore.KindColor), "色卡种类");
        Check.True(!PinStore.IsTextual(new PinRecord { Kind = PinStore.KindImage }), "图片贴图不是文字卡");
        Check.True(PinStore.IsTextual(new PinRecord { Kind = PinStore.KindText }), "文字卡从原文重画");
        Check.True(PinStore.IsTextual(new PinRecord { Kind = PinStore.KindColor }), "色卡从原文重画");

        // ============ 复制热区：两笔 OddEven ============
        var copy = new PinCardCopy("rgb(1, 2, 3)", "复制 RGB 颜色值",
            new Rect(0, 0, 20, 20), new Rect(10, 10, 30, 30));
        Check.True(copy.Hit(new Point(2, 2)), "只落在第一笔 = 命中");
        Check.True(copy.Hit(new Point(25, 25)), "只落在第二笔 = 命中");
        Check.True(!copy.Hit(new Point(15, 15)), "两笔重叠处不算命中（OddEven）");
        Check.True(!copy.Hit(new Point(50, 50)), "两笔之外不算命中");
    }

    private static Color Colors2(byte r, byte g, byte b, byte a) => Color.FromArgb(a, r, g, b);
}
