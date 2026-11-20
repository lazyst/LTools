using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 采集历史的「重新编辑截图」：把当时的标注（19 种）与选区外观落盘再读回来，画面必须逐像素相同。
/// </summary>
/// <remarks>
/// 这是 <see cref="MarkCodec"/> 的验收：光「读出来是个对象」不够——形状族带抖动 seed、滤镜 / 马赛克 /
/// 还原块吃底图像素、文字要重新排版，任何一处参数丢了、或者走了「简化构造」（会重抽 seed），
/// 成品图就会不一样。所以这里是「原列表」与「读回来的列表」各渲染一张，逐像素对账。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void MarkCodecCases()
    {
        var ink = Color.FromArgb(0xC8, 0x2B, 0x7C, 0xF0);
        var points = new[] { new Point(20, 20), new Point(60, 40), new Point(90, 25) };

        Mark[] marks =
        {
            new Mark.Stroke(points, ink, 5, highlighter: false, opacity: 0.9),
            new Mark.Stroke(points, ink, 14, highlighter: true, opacity: 0.45),
            new Mark.Shape(new Point(10, 10), new Point(80, 60), new ShapeSpec(ShapeKind.Ellipse, ink, 5,
                StrokeKind.Dotted, StrokeRendering.Cartoonist, Colors.Transparent, FillKind.CrossLine,
                9, 1234)),
            new Mark.Arrow(points, ArrowSpec.Default with
            {
                Stroke = ink, Width = 4, StrokeStyle = StrokeKind.Dashed,
                Rendering = StrokeRendering.Artist, Type = ArrowType.Elbow,
                Start = Arrowhead.Diamond, End = Arrowhead.Arrow, Ratio = 1.4,
                Shaft = ArrowShaft.Tapered, OutlineColor = Colors.Black, OutlineWidth = 1.5,
                Opacity = 0.8, Seed = 77,
            }),
            new Mark.Polyline(points, ink, 3, dashed: true),
            new Mark.Text(new Point(12, 90), "标注 123", new TextSpec("Microsoft YaHei UI", 18,
                Bold: true, Italic: true, TextAlign.Center, ink, 1.2, Colors.White,
                Color.FromArgb(120, 0, 0, 0), FillKind.Solid, 6)),
            new Mark.Serial(new Point(140, 40), 7, new SerialSpec(SerialBadge.SolidSquare,
                SerialNumbering.Roman, ink, Colors.White, FillKind.Solid, 20, "Microsoft YaHei UI", 2, 0.9)),
            new Mark.Mosaic(new Point(120, 70), new Point(200, 120), 8),
            new Mark.Filter(new Point(20, 130), new Point(90, 170), FilterKind.Grayscale, 60, 3),
            new Mark.Spotlight(new Point(150, 20), new Point(220, 80), 0.4),
            new Mark.Line(new Point(10, 175), new Point(90, 130), ink, 4, StrokeKind.Dotted,
                LineType.Curve, StrokeRendering.Artist, 555),
            new Mark.Distance(new Point(30, 60), new Point(150, 60), ink, 3,
                new DistanceSpec(DistanceUnit.Pt, 2.5, 2, 1.4, Arrowhead.Square)),
            new Mark.Angle(new Point(60, 120), 40, 0.6, 72, ink, 3,
                new AngleSpec(AngleKind.Interior, AngleUnit.Radians, 2)),
            new Mark.Bracket(new Point(200, 10), new Point(230, 50), new Point(200, 90), ink, 4),
            new Mark.SpeechBubble(new Rect(100, 130, 80, 40), new ShapeSpec(ShapeKind.ThoughtBubble, ink, 3,
                StrokeKind.Solid, StrokeRendering.Artist, Colors.White, FillKind.Solid, 12, 99)),
            new Mark.Magnifier(new Rect(150, 130, 60, 40), new MagnifierSpec(ShapeKind.Ellipse, ink, 3, 2.5,
                MagnifierConnection.Gradient, true, Arrowhead.Arrow, 1.2, 10, 0.9)),
            new Mark.Watermark(new WatermarkSpec("内部", Color.FromArgb(90, 0, 0, 0), 16,
                "Microsoft YaHei UI", 30, 60, 0.3)),
            new Mark.Highlight(new Point(20, 100), new Point(120, 110),
                Color.FromArgb(160, 255, 220, 0), 0.35),
            new Mark.Restore(new Point(30, 30), new Point(70, 70), points, 9),
            new Mark.BrushFilter(points, 12, FilterKind.Blur, 40),
        };

        // ============ ① 结构往返：19 种都在，顺序不变 ============
        var json = MarkCodec.Write(marks);
        Check.Equal(marks.Length, json.Count, "19 种标注都写进 JSON");

        var back = MarkCodec.Read(json);
        Check.Equal(marks.Length, back.Count, "读回来还是 19 条");

        for (int i = 0; i < marks.Length; i++)
            Check.Equal(marks[i].GetType().Name, back[i].GetType().Name, $"第 {i + 1} 条类型不变");

        // 具体参数抽查（枚举按名字、seed / 不透明度 / 编号都得原样）
        var arrow = (Mark.Arrow)back[3];
        Check.Equal(ArrowType.Elbow, arrow.Spec.Type, "箭头：类型");
        Check.Equal((uint)77, arrow.Spec.Seed, "箭头：抖动 seed（丢了线会自己动）");
        Check.Equal(Arrowhead.Diamond, arrow.Spec.Start, "箭头：起点端点");
        Check.Equal(1.5, arrow.Spec.OutlineWidth, "箭头：外描边宽");

        var text = (Mark.Text)back[5];
        Check.Equal("标注 123", text.Content, "文字：正文（构造参数 text → 属性 Content）");
        Check.True(text.Style.Bold && text.Style.Italic, "文字：粗斜体");
        Check.Equal(FillKind.Solid, text.Style.FillStyle, "文字：背景填充方式");

        var line = (Mark.Line)back[10];
        Check.Equal(StrokeKind.Dotted, line.StrokeStyle, "直线：线型（构造参数 style → 属性 StrokeStyle）");
        Check.Equal((uint)555, line.Seed, "直线：seed");

        var serial = (Mark.Serial)back[6];
        Check.Equal(7, serial.Number, "序号：数值");
        Check.Equal(SerialNumbering.Roman, serial.Spec.Numbering, "序号：写法");

        var magnifier = (Mark.Magnifier)back[15];
        Check.Equal(2.5, magnifier.Spec.Factor, "放大镜：倍率");
        Check.Equal(magnifier.SourceWidth, ((Mark.Magnifier)marks[15]).SourceWidth, "放大镜：源宽原样");
        Check.Equal(magnifier.SourceHeight, ((Mark.Magnifier)marks[15]).SourceHeight, "放大镜：源高原样");

        var angle = (Mark.Angle)back[12];
        Check.Equal(72.0, angle.Degrees, "测角：张角");
        Check.Equal(AngleKind.Interior, angle.Spec.Kind, "测角：内角模式");

        var distance = (Mark.Distance)back[11];
        Check.Equal(DistanceUnit.Pt, distance.Spec.Unit, "测距：单位");
        Check.Equal(2, distance.Spec.Decimals, "测距：小数位");

        // ============ ② 逐像素：原列表与读回来的列表画出来必须一模一样 ============
        static WriteableBitmap Base()
        {
            const int width = 240, height = 180;
            var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            var bytes = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                // 斜条纹：还原块 / 马赛克 / 滤镜 / 放大镜是不是真的取了这块像素，一眼能看出来
                bytes[i] = (byte)((x * 3 + y) & 0xFF);
                bytes[i + 1] = (byte)((y * 2 + x / 2) & 0xFF);
                bytes[i + 2] = (byte)(((x / 8 + y / 8) % 2 == 0) ? 0x20 : 0xD0);
                bytes[i + 3] = 0xFF;
            }

            bitmap.WritePixels(new Int32Rect(0, 0, width, height), bytes, width * 4, 0);
            bitmap.Freeze();
            return bitmap;
        }

        static byte[] Render(IReadOnlyList<Mark> list)
        {
            var doc = new CaptureDocument(Base(), new Int32Rect(0, 0, 240, 180), 1)
            {
                Area = new Int32Rect(0, 0, 240, 180),
                Style = SelectionStyle.None,
            };
            doc.ImportHistory(list, Array.Empty<Mark[]>(), Array.Empty<Mark[]>());

            var image = doc.Render();
            var bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
            image.CopyPixels(bytes, image.PixelWidth * 4, 0);
            return bytes;
        }

        var before = Render(marks);
        var after = Render(back);
        Check.Equal(before.Length, after.Length, "两张成品图尺寸一致");

        int firstDiff = -1;
        for (int i = 0; i < before.Length && i < after.Length; i++)
        {
            if (before[i] == after[i]) continue;
            firstDiff = i;
            break;
        }

        Check.True(firstDiff < 0, firstDiff < 0
            ? "19 种标注落盘再读回来逐像素相同"
            : $"第 {firstDiff / 4} 个像素对不上（通道 {firstDiff % 4}）：" +
              $"{before[firstDiff]} vs {after[firstDiff]}");

        // ============ ③ 撤销 / 重做栈：每份快照都是完整的一份列表 ============
        var history = MarkCodec.WriteHistory(new[]
        {
            marks.Take(2).ToArray(),
            marks.Take(5).ToArray(),
        });
        var backHistory = MarkCodec.ReadHistory(history);
        Check.Equal(2, backHistory.Count, "撤销栈两份快照");
        Check.Equal(2, backHistory[0].Length, "第一份只有前 2 条");
        Check.Equal(5, backHistory[1].Length, "第二份有前 5 条");
        Check.Equal("Stroke", backHistory[1][0].GetType().Name, "快照里的类型也对");

        // ============ ④ 坏数据只丢那一条，不挡整份历史 ============
        var mixed = (JsonArray)JsonNode.Parse(
            """[{"t":"Nope","a":{}},{"t":"Mosaic","a":{"a":"1,2","b":"3,4","block":6}}]""")!;
        var survived = MarkCodec.Read(mixed);
        Check.Equal(1, survived.Count, "不认识的一条被跳过，认识的照读");
        Check.Equal("Mosaic", survived[0].GetType().Name, "留下来的是马赛克");
        Check.Equal(0, MarkCodec.Read(null).Count, "null 当空列表");

        // ============ ⑤ 选区外观：圆角 / 阴影 / 边框 / 背景不透明度 ============
        var style = new SelectionStyle
        {
            CornerRadius = 12,
            ShadowWidth = 6,
            ShadowColor = Colors.SteelBlue,
            BorderWidth = 3,
            BorderColor = Colors.Orange,
            BorderType = SelectionBorderType.Border,
            BackgroundOpacity = 40,
        }.Clamp();

        var styleJson = JsonSerializer.Serialize(CaptureStyleDto.Of(style));
        var styleBack = JsonSerializer.Deserialize<CaptureStyleDto>(styleJson)!.ToStyle();

        Check.Equal(style.CornerRadius, styleBack.CornerRadius, "外观：圆角");
        Check.Equal(style.ShadowWidth, styleBack.ShadowWidth, "外观：阴影宽");
        Check.Equal(style.ShadowColor, styleBack.ShadowColor, "外观：阴影色");
        Check.Equal(style.BorderWidth, styleBack.BorderWidth, "外观：边框宽");
        Check.Equal(style.BorderColor, styleBack.BorderColor, "外观：边框色");
        Check.Equal(SelectionBorderType.Border, styleBack.BorderType, "外观：边框方式");
        Check.Equal(40, styleBack.BackgroundOpacity, "外观：背景不透明度");

        // 空对象（老记录只有选区没有外观）读成默认的直角无效果，不抛
        var plain = JsonSerializer.Deserialize<CaptureStyleDto>("{}")!.ToStyle();
        Check.True(plain.IsPlain, "空外观读成「直角无效果」");
    }
}
