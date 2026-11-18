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
        Check.Equal(Color.FromRgb(0xF5, 0x22, 0x2D), set.For(CaptureTool.Arrow).Color,
            "箭头默认色（上游 defaults.arrow = rectangle 的红）");
        Check.Equal(2d, set.For(CaptureTool.Arrow).Width, "箭头默认线宽 2");
        Check.Equal(ArrowType.Curve, set.For(CaptureTool.Arrow).ArrowKind, "箭头默认曲线");
        Check.Equal(Arrowhead.None, set.For(CaptureTool.Arrow).StartArrowhead, "箭头起点默认无端点");
        Check.Equal(Arrowhead.Arrow, set.For(CaptureTool.Arrow).EndArrowhead, "箭头终点默认三角箭头");
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
        serial.Badge = SerialBadge.OutlinedSquare;
        serial.Numbering = SerialNumbering.Chinese;
        var filter = set.For(CaptureTool.Filter);
        filter.FilterMode = FilterKind.Emboss;
        filter.FilterStrength = 73;
        var water = set.For(CaptureTool.Watermark);
        water.WatermarkText = "验收";
        water.WatermarkAngle = -45;
        water.WatermarkGap = 200;
        water.FontSize = 24;
        water.WatermarkOpacity = 0.4;

        // 放大镜族（P2-d2 新增的连接方式 / 源轮廓 / 箭头比例）与序号的下一个号码
        serial.SerialNumber = 7;
        serial.FontSize = 30;
        var magnifier = set.For(CaptureTool.Magnifier);
        magnifier.Factor = 3.5;
        magnifier.Connection = MagnifierConnection.LeaderLine;
        magnifier.SourceOutline = false;
        magnifier.ArrowRatio = 2.2;
        magnifier.ShapeKind = ShapeKind.Ellipse;

        set.AddWatermarkTemplate("带日期", "{text} {YYYY}-{MM}-{DD}");
        set.AddWatermarkTemplate("机密", "机密 {HH}:{mm}");

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
        Check.Equal(SerialBadge.OutlinedSquare, back.For(CaptureTool.Serial).Badge, "徽标类型按名字存盘");
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
        Check.Equal(24d, back.For(CaptureTool.Watermark).FontSize, "水印字号往返");
        Check.True(Math.Abs(back.For(CaptureTool.Watermark).WatermarkOpacity - 0.4) < 0.001, "水印不透明度往返");
        Check.Equal(7, back.For(CaptureTool.Serial).SerialNumber, "下一个序号往返");
        Check.Equal(30d, back.For(CaptureTool.Serial).FontSize, "序号字号往返");
        Check.Equal(3.5, back.For(CaptureTool.Magnifier).Factor, "放大镜倍率往返");
        Check.Equal(MagnifierConnection.LeaderLine, back.For(CaptureTool.Magnifier).Connection,
            "连接方式按名字存盘");
        Check.False(back.For(CaptureTool.Magnifier).SourceOutline, "源轮廓开关往返");
        Check.Equal(2.2, back.For(CaptureTool.Magnifier).ArrowRatio, "箭头比例往返");
        Check.Equal(2, back.WatermarkTemplates.Count, "水印模板往返（条数）");
        Check.Equal("带日期", back.WatermarkTemplates[0].Name, "模板名字往返");
        Check.Equal("{text} {YYYY}-{MM}-{DD}", back.WatermarkTemplates[0].Value, "模板内容往返");
        Check.True(back.RemoveWatermarkTemplate("机密"), "删模板删得到");
        Check.Equal(1, back.WatermarkTemplates.Count, "删完只剩一条");

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
        Check.Equal(6d, pen.FontSize, "字号低于下限钳到 6");
        Check.Equal(24d, pen.Block, "块大小超上限钳到 24");
        Check.True(Math.Abs(pen.Opacity - 0.9) < 0.001, "不透明度超上限钳到 0.9");
        Check.Equal(100d, pen.FilterStrength, "强度超上限钳到 100");
        Check.Equal(10d, pen.Factor, "倍率超上限钳到 10（上游 1–10）");
        Check.Equal(FilterKind.Mosaic, pen.FilterMode, "认不出的滤镜类型用默认");
        Check.Equal(SerialBadge.OutlinedCircle, pen.Badge, "认不出的徽标类型用默认");
        Check.Equal(ShapeKind.Rectangle, pen.ShapeKind, "认不出的形状用默认");
        Check.Equal(StrokeKind.Solid, pen.StrokeStyle, "认不出的线型用默认");
        Check.Equal(StrokeRendering.Architect, pen.Rendering, "认不出的笔触风格用默认");
        Check.Equal(FillKind.None, pen.FillStyle, "认不出的填充方式用默认");
        Check.Equal(ToolStyle.DefaultFillColor, pen.FillColor, "坏填充色回默认");
        Check.Equal(83d, pen.CornerRadius, "圆角半径超上限钳到 83（上游 kMaxRectangleCornerRadius）");
        Check.Equal(LineType.Curve, pen.LineType, "认不出的线型开关用默认");
        Check.Equal(BrushMode.Regular, pen.BrushMode, "认不出的画笔模式用默认");

        var empty = ToolStyleSet.FromFile(new CaptureStylesFile { Families = new() { ["已退役的工具"] = new() } });
        Check.Equal(4d, empty.For(CaptureTool.Pen).Width, "认不出的族名丢掉，按默认");
    }

    private static Color ParseHex(string text) =>
        ToolStyleFormat.TryParseColor(text, out var color) ? color : Colors.Transparent;
}
