using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

internal static partial class CaptureProSuite
{
    /// <summary>
    /// P2-c：文字族（上游 <c>buildTextFamily</c> + <c>text_fill_outset</c>）——字号 / 字体族 /
    /// 粗斜 / 水平对齐 / 文字描边 / 背景胶囊（填充色、填充方式、圆角）。纯数据断言。
    /// </summary>
    private static void TextStyles()
    {
        // ---- 参数表：文字族挂哪几格 ----
        foreach (var param in new[]
                 {
                     ToolParam.Color, ToolParam.TextSize, ToolParam.FontFamily, ToolParam.TextEmphasis,
                     ToolParam.TextAlign, ToolParam.TextStrokeWidth, ToolParam.TextStrokeColor,
                     ToolParam.FillColor, ToolParam.FillStyle, ToolParam.CornerRadius,
                 })
            Check.True(ToolCatalog.Uses(CaptureTool.Text, param), $"文字有 {param}");

        Check.False(ToolCatalog.Uses(CaptureTool.Text, ToolParam.Width), "文字没有线宽");
        Check.False(ToolCatalog.Uses(CaptureTool.Text, ToolParam.StrokeStyle), "文字没有描边线型");
        Check.False(ToolCatalog.Uses(CaptureTool.Shape, ToolParam.TextSize), "形状没有字号");
        Check.False(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.TextStrokeWidth), "水印没有文字描边（仍在 P2-d）");
        Check.Equal(StyleFamilies.Text, StyleFamilies.Of(CaptureTool.Text), "文字有自己的样式族");

        // ---- 默认值：照上游 screenshotCanvasStyleDefaults() 的 text 段 ----
        var fresh = new ToolStyle();
        Check.Equal(30d, fresh.TextSize, "默认字号 30（上游 text.fontSize）");
        Check.Equal(string.Empty, fresh.FontFamily, "默认字体族为空（= 项目默认）");
        Check.False(fresh.TextBold, "默认不加粗");
        Check.False(fresh.TextItalic, "默认不倾斜");
        Check.Equal(TextAlign.Left, fresh.TextAlign, "默认左对齐（上游 horizontalAlign）");
        Check.Equal(0d, fresh.TextStrokeWidth, "默认不描边（上游 strokeWidth = 0）");
        Check.Equal(Color.FromRgb(0xFF, 0xCC, 0xC7), fresh.StrokeColor, "默认描边色 #FFCCC7");
        Check.Equal(FillKind.None, fresh.FillStyle, "默认没有背景（上游 fillStyle = Border）");
        Check.Equal(6d, fresh.CornerRadius, "默认圆角 6");

        // ---- 排版口径：行高 × 1.2、内边距 0.32 / 0.1、字号 6–256 ----
        Check.Equal(36d, Math.Round(TextInk.LineHeight(30), 6), "行高 = 字号 × 1.2");
        Check.Equal(11.52d, Math.Round(TextInk.PadX(30), 6), "横向内边距 = 行高 × 0.32");
        Check.Equal(3.6d, Math.Round(TextInk.PadY(30), 6), "纵向内边距 = 行高 × 0.1");
        Check.Equal(TextInk.MinFontSize, TextInk.SizeOf(Spec(1)), "字号下限 6");
        Check.Equal(TextInk.MaxFontSize, TextInk.SizeOf(Spec(999)), "字号上限 256");

        // ---- 填充 / 描边的可见性 ----
        Check.False(TextInk.HasFill(Spec(30)), "FillStyle = None 时没有背景胶囊");
        Check.False(TextInk.HasFill(Spec(30) with { FillStyle = FillKind.Solid, Fill = Colors.Transparent }),
            "透明色即便选了实心也看不见");
        Check.True(TextInk.HasFill(Spec(30) with { FillStyle = FillKind.Solid, Fill = Colors.White }),
            "实心 + 不透明 = 有背景");
        Check.False(TextInk.HasStroke(Spec(30)), "描边宽 0 不画");
        Check.True(TextInk.HasStroke(Spec(30) with { StrokeWidth = 2 }), "描边宽 2 要画");

        // ---- 逐行排版：总宽取最长行，按对齐在其中定位 ----
        const string content = "截图\nW";
        var left = TextInk.Layout(content, Spec(30));
        Check.Equal(2, left.Count, "换行切成两行");
        var widest = left.Max(l => l.Size.Width);
        Check.Equal(0d, Math.Round(left[0].X, 6), "左对齐第一行贴 0");
        Check.Equal(0d, Math.Round(left[1].X, 6), "左对齐第二行也贴 0");

        var center = TextInk.Layout(content, Spec(30) with { Align = TextAlign.Center });
        var narrow = center.OrderBy(l => l.Size.Width).First();
        Check.Equal(Math.Round((widest - narrow.Size.Width) / 2, 6), Math.Round(narrow.X, 6),
            "居中：窄行左右各让一半");

        var right = TextInk.Layout(content, Spec(30) with { Align = TextAlign.Right });
        var rightNarrow = right.OrderBy(l => l.Size.Width).First();
        Check.Equal(Math.Round(widest - rightNarrow.Size.Width, 6), Math.Round(rightNarrow.X, 6),
            "右对齐：窄行靠右");

        Check.Equal(Math.Round(widest, 6), Math.Round(TextInk.Measure(content, Spec(30)).Width, 6),
            "墨迹宽 = 最长行");
        Check.True(TextInk.Measure(content, Spec(30)).Height > TextInk.LineHeight(30),
            "两行的高度大于一行行高");

        // ---- 命中 / 选中框：背景内边距与描边外溢都算进来 ----
        var plain = TextInk.BoundsOf(new Point(0, 0), "截图", Spec(30));
        var withFill = TextInk.BoundsOf(new Point(0, 0), "截图",
            Spec(30) with { FillStyle = FillKind.Solid, Fill = Colors.White });
        var withStroke = TextInk.BoundsOf(new Point(0, 0), "截图", Spec(30) with { StrokeWidth = 8 });
        Check.True(withFill.Width > plain.Width && withFill.Height > plain.Height, "背景内边距撑大命中框");
        Check.True(withStroke.Width > plain.Width, "描边外溢撑大命中框");

        // ---- 字体族 / 粗斜 ----
        var bold = TextInk.TypefaceOf(Spec(30) with { Bold = true });
        Check.Equal(FontWeights.Bold, bold.Weight, "粗体走 FontWeight.Bold");
        Check.Equal(FontStyles.Italic, TextInk.TypefaceOf(Spec(30) with { Italic = true }).Style, "斜体走 FontStyles.Italic");
        Check.Equal(new FontFamily(TextInk.FallbackFamily).Source,
            TextInk.TypefaceOf(Spec(30)).FontFamily.Source, "空字体族回落到项目默认");

        // ---- 样式 → 文字：一处换算，且能原样落盘再读回来 ----
        var style = new ToolStyle
        {
            TextSize = 42,
            FontFamily = "Consolas",
            TextBold = true,
            TextItalic = true,
            TextAlign = TextAlign.Right,
            Color = Colors.DarkOrange,
            TextStrokeWidth = 3,
            StrokeColor = Colors.SteelBlue,
            FillColor = Colors.LightYellow,
            FillStyle = FillKind.CrossLine,
            CornerRadius = 12,
        };
        var spec = style.TextSpecOf();
        Check.Equal("Consolas", spec.FontFamily, "字体族带过去");
        Check.Equal(42d, spec.FontSize, "字号带过去");
        Check.True(spec.Bold && spec.Italic, "粗斜带过去");
        Check.Equal(TextAlign.Right, spec.Align, "对齐带过去");
        Check.Equal(Colors.DarkOrange, spec.Color, "字色带过去");
        Check.Equal(3d, spec.StrokeWidth, "描边宽带过去");
        Check.Equal(Colors.SteelBlue, spec.Stroke, "描边色带过去");
        Check.Equal(Colors.LightYellow, spec.Fill, "背景色带过去");
        Check.Equal(FillKind.CrossLine, spec.FillStyle, "填充方式带过去");
        Check.Equal(12d, spec.CornerRadius, "圆角带过去");

        var dto = style.ToDto();
        Check.Equal(42d, dto.TextSize, "落盘有字号");
        Check.Equal("Consolas", dto.FontFamily, "落盘有字体族");
        Check.Equal(true, dto.TextBold, "落盘有粗体");
        Check.Equal(true, dto.TextItalic, "落盘有斜体");
        Check.Equal("Right", dto.TextAlign, "落盘的对齐是枚举名");
        Check.Equal(3d, dto.TextStrokeWidth, "落盘有描边宽");
        Check.Equal("#4682B4", dto.StrokeColor, "落盘有描边色");

        var back = ToolStyle.FromDto(dto);
        Check.Equal(42d, back.TextSize, "读回字号");
        Check.Equal("Consolas", back.FontFamily, "读回字体族");
        Check.True(back.TextBold && back.TextItalic, "读回粗斜");
        Check.Equal(TextAlign.Right, back.TextAlign, "读回对齐");
        Check.Equal(3d, back.TextStrokeWidth, "读回描边宽");
        Check.Equal(Colors.SteelBlue, back.StrokeColor, "读回描边色");

        // 越界与认不出的值：钳回 / 用默认，读坏不炸
        var broken = ToolStyle.FromDto(new CaptureToolStyleDto
        {
            TextSize = 1000,
            TextStrokeWidth = -5,
            TextAlign = "斜着",
            CornerRadius = 999,
            StrokeColor = "不是颜色",
        });
        Check.Equal(TextInk.MaxFontSize, broken.TextSize, "字号超上限钳回 256");
        Check.Equal(0d, broken.TextStrokeWidth, "描边宽负数钳回 0");
        Check.Equal(TextAlign.Left, broken.TextAlign, "认不出的对齐回默认");
        Check.Equal(TextInk.MaxCornerRadius, broken.CornerRadius, "圆角超上限钳回 83");
        Check.Equal(TextInk.DefaultStroke, broken.StrokeColor, "认不出的描边色回默认");

        // ---- 标注：移动不改样式，缩放改字号并钳在 6–256 ----
        var mark = new Mark.Text(new Point(10, 10), "截图", spec);
        var movedText = (Mark.Text)mark.MoveBy(new Vector(5, 5));
        Check.Equal(new Point(15, 15), movedText.Origin, "文字跟着移动");
        Check.Equal(spec, movedText.Style, "移动不改样式");
        Check.Equal(mark.Measured, movedText.Measured, "移动不改排版尺寸");

        var frame = mark.Frame;
        var scaled = (Mark.Text)mark.ScaleTo(frame, new Rect(0, 0, frame.Width, frame.Height * 2));
        Check.True(scaled.Style.FontSize > mark.Style.FontSize, "纵向放大字号变大");
        Check.Equal(mark.Style.FontFamily, scaled.Style.FontFamily, "缩放不改字体族");

        var huge = (Mark.Text)mark.ScaleTo(frame, new Rect(0, 0, frame.Width * 100, frame.Height * 100));
        Check.Equal(TextInk.MaxFontSize, huge.Style.FontSize, "缩放也不会超过字号上限");

        // ---- 枚举文案（参数行 chip 与 tooltip 共用一份）----
        foreach (var align in Enum.GetValues<TextAlign>())
        {
            Check.True(!string.IsNullOrWhiteSpace(ShapeLabel.Chip(align)), align + " 有 chip 标签");
            Check.True(ShapeLabel.Name(align).StartsWith("对齐：", StringComparison.Ordinal), align + " 的 tooltip");
        }

        Check.Equal("左", ShapeLabel.Chip(TextAlign.Left), "左对齐写作「左」");
        Check.Equal("中", ShapeLabel.Chip(TextAlign.Center), "居中写作「中」");
        Check.Equal("右", ShapeLabel.Chip(TextAlign.Right), "右对齐写作「右」");
    }

    private static TextSpec Spec(double size) => new(
        string.Empty, size, false, false, TextAlign.Left,
        Colors.Red, 0, Colors.Black, Colors.Transparent, FillKind.None, 6);
}
