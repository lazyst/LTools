using System;
using System.Linq;
using System.Windows.Media;
using LTools.Config;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

internal static partial class CaptureProSuite
{
    /// <summary>
    /// 样式记忆（P2-a）：每个工具家族各一套、共享颜色开关、落盘往返、越界钳制、坏配置丢弃。
    /// </summary>
    private static void ToolStyles()
    {
        foreach (var info in ToolCatalog.All)
            Check.True(StyleFamilies.IsKnown(StyleFamilies.Of(info.Tool)),
                $"工具 {info.Tool} 映射到已知样式族（{StyleFamilies.Of(info.Tool)}）");

        Check.Equal(StyleFamilies.Shape, StyleFamilies.Of(CaptureTool.Shape), "形状工具归形状族");
        Check.Equal(StyleFamilies.Filter, StyleFamilies.Of(CaptureTool.AutoFilter),
            "自动打码与矩形滤镜共用强度");
        Check.Equal(StyleFamilies.Filter, StyleFamilies.Of(CaptureTool.Filter), "矩形滤镜归滤镜族");
        Check.Equal(StyleFamilies.Pen, StyleFamilies.Of(CaptureTool.Pen), "画笔归画笔族");
        Check.Equal(StyleFamilies.Highlighter, StyleFamilies.Of(CaptureTool.Highlighter), "荧光笔单独一族");

        // 每族独立：改一支不影响另一支；同一族的两个工具拿到同一个对象
        var set = ToolStyleSet.FromFile(null);
        set.For(CaptureTool.Pen).Width = 12;
        Check.Equal(4d, set.For(CaptureTool.Shape).Width, "改画笔线宽不影响形状");
        Check.True(ReferenceEquals(set.For(CaptureTool.Filter), set.For(CaptureTool.AutoFilter)),
            "同一族的工具拿到同一个对象（不是两份副本）");
        Check.Equal(ToolStyle.Palette[0], set.For(CaptureTool.Arrow).Color, "默认颜色");
        Check.Equal(4d, set.For(CaptureTool.Highlighter).Width, "默认线宽 4");

        // 共享颜色：关着只改一族，开着改一处 = 全改（含还没碰过的工具）
        set.SetColor(StyleFamilies.Pen, Colors.Blue);
        Check.Equal(Colors.Blue, set.For(CaptureTool.Pen).Color, "关着时改自己那一族");
        Check.Equal(ToolStyle.Palette[0], set.For(CaptureTool.Shape).Color, "关着时别的族不变");

        set.SetSyncColors(true);
        Check.Equal(set.SharedColor, set.For(CaptureTool.Shape).Color, "打开共享颜色：已有族立即统一");
        set.SetColor(StyleFamilies.Shape, Colors.Lime);
        Check.Equal(Colors.Lime, set.For(CaptureTool.Pen).Color, "共享颜色开着时改一处 = 全改");
        Check.Equal(Colors.Lime, set.For(CaptureTool.Arrow).Color, "连还没碰过的工具也跟着变");
        Check.Equal(Colors.Lime, set.SharedColor, "共享色记成本次选的色");

        // 落盘往返：颜色 / 数值 / 枚举 / 开关都要一致
        var serial = set.For(CaptureTool.Serial);
        serial.SerialShape = SerialShape.Square;
        serial.Numbering = SerialNumbering.Chinese;
        var filter = set.For(CaptureTool.Filter);
        filter.FilterMode = FilterKind.Emboss;
        filter.FilterStrength = 73;
        var water = set.For(CaptureTool.Watermark);
        water.WatermarkText = "验收";
        water.WatermarkAngle = -45;
        water.WatermarkGap = 200;

        var shape = set.For(CaptureTool.Shape);
        shape.ShapeKind = ShapeKind.Ellipse;
        shape.StrokeStyle = StrokeKind.Dotted;
        shape.Rendering = StrokeRendering.Cartoonist;
        shape.FillStyle = FillKind.CrossLine;
        shape.FillColor = Colors.Gold;
        shape.CornerRadius = 12;
        set.For(CaptureTool.Line).LineType = LineType.Straight;
        set.For(CaptureTool.Pen).BrushMode = BrushMode.PressureSensitive;

        var file = set.ToFile();
        Check.True(file.SyncColors, "落盘带上共享颜色开关");
        Check.Equal(Colors.Lime, ParseHex(file.SharedColor!), "共享色按 #RRGGBB 落盘");
        Check.True(file.Families.ContainsKey(StyleFamilies.Pen), "落盘带上碰过的族");

        var back = ToolStyleSet.FromFile(file);
        Check.True(back.SyncColors, "读回来开关还在");
        Check.Equal(Colors.Lime, back.SharedColor, "读回来共享色一致");
        Check.Equal(12d, back.For(CaptureTool.Pen).Width, "读回来线宽一致");
        Check.Equal(Colors.Lime, back.For(CaptureTool.Pen).Color, "读回来颜色一致");
        Check.Equal(SerialShape.Square, back.For(CaptureTool.Serial).SerialShape, "徽标形状按名字存盘");
        Check.Equal(SerialNumbering.Chinese, back.For(CaptureTool.Serial).Numbering, "数字写法按名字存盘");
        Check.Equal(ShapeKind.Ellipse, back.For(CaptureTool.Shape).ShapeKind, "形状选择按名字存盘");
        Check.Equal(StrokeKind.Dotted, back.For(CaptureTool.Shape).StrokeStyle, "描边线型按名字存盘");
        Check.Equal(StrokeRendering.Cartoonist, back.For(CaptureTool.Shape).Rendering, "笔触风格按名字存盘");
        Check.Equal(FillKind.CrossLine, back.For(CaptureTool.Shape).FillStyle, "填充方式按名字存盘");
        Check.Equal(Colors.Gold, back.For(CaptureTool.Shape).FillColor, "填充色按 #RRGGBB 存盘");
        Check.True(Math.Abs(back.For(CaptureTool.Shape).CornerRadius - 12) < 0.001, "圆角半径往返");
        Check.Equal(LineType.Straight, back.For(CaptureTool.Line).LineType, "直线 / 曲线往返");
        Check.Equal(BrushMode.PressureSensitive, back.For(CaptureTool.Pen).BrushMode, "画笔模式往返");
        Check.Equal(FilterKind.Emboss, back.For(CaptureTool.Filter).FilterMode, "滤镜类型按名字存盘");
        Check.True(Math.Abs(back.For(CaptureTool.Filter).FilterStrength - 73) < 0.001, "滤镜强度往返");
        Check.Equal("验收", back.For(CaptureTool.Watermark).WatermarkText, "水印文字往返");
        Check.True(Math.Abs(back.For(CaptureTool.Watermark).WatermarkAngle + 45) < 0.001, "水印角度往返");
        Check.True(Math.Abs(back.For(CaptureTool.Watermark).WatermarkGap - 200) < 0.001, "水印间距往返");

        // 没碰过的族不落盘（别在 ui.json 里堆一堆全默认的键）
        Check.False(file.Families.ContainsKey(StyleFamilies.SpeechBubble), "没碰过的族不落盘");

        // 坏配置：认不出的族名丢掉、越界数值钳回界面能摆出来的区间、认不出的枚举用默认
        var broken = new CaptureStylesFile
        {
            SharedColor = "不是颜色",
            Families = new()
            {
                ["pen"] = new CaptureToolStyleDto
                {
                    Width = 999, FontSize = -5, Block = 100, Opacity = 9,
                    FilterStrength = 400, Factor = 30, FilterMode = "NotAKind", Shape = "NotAShape",
                    ShapeKind = "NotAKind", StrokeStyle = "NotAKind", Rendering = "NotAKind",
                    FillStyle = "NotAKind", FillColor = "不是颜色", CornerRadius = 999,
                    LineType = "NotAKind", BrushMode = "NotAKind",
                },
                ["已退役的工具"] = new CaptureToolStyleDto { Width = 8 },
            },
        };
        var safe = ToolStyleSet.FromFile(broken);
        Check.Equal(ToolStyleSet.DefaultSharedColor, safe.SharedColor, "坏颜色回默认");
        Check.False(broken.Families.Keys.Any(StyleFamilies.IsKnown) == false, "样例里至少有已知族");
        var pen = safe.For(CaptureTool.Pen);
        Check.Equal(50d, pen.Width, "线宽超上限钳到 50");
        Check.Equal(8d, pen.FontSize, "字号低于下限钳到 8");
        Check.Equal(24d, pen.Block, "块大小超上限钳到 24");
        Check.True(Math.Abs(pen.Opacity - 0.9) < 0.001, "不透明度超上限钳到 0.9");
        Check.Equal(100d, pen.FilterStrength, "强度超上限钳到 100");
        Check.Equal(8d, pen.Factor, "倍率超上限钳到 8");
        Check.Equal(FilterKind.Mosaic, pen.FilterMode, "认不出的滤镜类型用默认");
        Check.Equal(SerialShape.Circle, pen.SerialShape, "认不出的徽标形状用默认");
        Check.Equal(ShapeKind.Rectangle, pen.ShapeKind, "认不出的形状用默认");
        Check.Equal(StrokeKind.Solid, pen.StrokeStyle, "认不出的线型用默认");
        Check.Equal(StrokeRendering.Architect, pen.Rendering, "认不出的笔触风格用默认");
        Check.Equal(FillKind.None, pen.FillStyle, "认不出的填充方式用默认");
        Check.Equal(ToolStyle.DefaultFillColor, pen.FillColor, "坏填充色回默认");
        Check.Equal(40d, pen.CornerRadius, "圆角半径超上限钳到 40");
        Check.Equal(LineType.Curve, pen.LineType, "认不出的线型开关用默认");
        Check.Equal(BrushMode.Regular, pen.BrushMode, "认不出的画笔模式用默认");

        var empty = ToolStyleSet.FromFile(new CaptureStylesFile { Families = new() { ["已退役的工具"] = new() } });
        Check.Equal(4d, empty.For(CaptureTool.Pen).Width, "认不出的族名丢掉，按默认");
    }

    private static Color ParseHex(string text) =>
        ToolStyleFormat.TryParseColor(text, out var color) ? color : Colors.Transparent;
}
