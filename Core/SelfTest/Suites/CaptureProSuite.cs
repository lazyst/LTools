using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 截图 Pro 的标注模型：几何（命中 / 外接框 / 箭头）、不可变语义、撤销栈、
/// 选区换算与导出前置条件、文字量宽、反色字。
/// </summary>
/// <remarks>
/// 这里只碰纯数据与 <see cref="BitmapSource"/>，**不开窗口**：覆盖层本体与工具栏走
/// 仓库外冒烟（<c>capture-pro-smoke</c>）。
/// </remarks>
internal static class CaptureProSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("capturePro", "几何：矩形 / 外接框 / 折线命中", Geometry));
        cases.Add(new("capturePro", "几何：箭头三角头", ArrowHead));
        cases.Add(new("capturePro", "标注：移动返回新实例", ImmutableMarks));
        cases.Add(new("capturePro", "文档：撤销 / 重做 / 上限", UndoRedo));
        cases.Add(new("capturePro", "文档：序号撤销后不跳号", SerialNumbers));
        cases.Add(new("capturePro", "文档：选区换算与导出", Selection));
        cases.Add(new("capturePro", "文字：量宽随字号增长", TextMetrics));
        cases.Add(new("capturePro", "样式：底色对比取字色", Contrast));
    }

    private static void Geometry()
    {
        // 反着拖也要得到规范化矩形
        Check.Equal(new Rect(10, 20, 40, 20), MarkGeometry.RectOf(new Point(50, 40), new Point(10, 20)),
            "拖成右下 → 左上");

        // 外接框按线宽外扩（描边会出界）
        var stroke = new[]
        {
            new Point(0, 0), new Point(10, 0), new Point(10, 6),
        };
        Check.Equal(new Rect(-2, -2, 14, 10), MarkGeometry.BoundsOf(stroke, 4), "线宽 4 外扩一半");

        var shape = new Mark.Shape(new Point(10, 10), new Point(50, 40), Colors.Red, 2, false, false);
        Check.True(shape.HitTest(new Point(10, 25), 2), "左边框上命中");
        Check.True(shape.HitTest(new Point(50, 40), 3), "角上命中");
        Check.False(shape.HitTest(new Point(30, 25), 2), "矩形内部不算命中（只有描边）");
        Check.False(shape.HitTest(new Point(80, 25), 2), "离得远不命中");

        var poly = new Mark.Polyline(new[] { new Point(0, 0), new Point(10, 0) }, Colors.Red, 2, false);
        Check.True(poly.HitTest(new Point(5, 1), 2), "折线附近命中");
        Check.False(poly.HitTest(new Point(5, 10), 2), "折线外面不命中");

        // 单点折线（按下就松手）也要能命中
        var dot = new Mark.Polyline(new[] { new Point(3, 3) }, Colors.Red, 2, false);
        Check.True(dot.HitTest(new Point(4, 4), 2), "单点折线按距离命中");
    }

    private static void ArrowHead()
    {
        var head = MarkGeometry.ArrowHead(new Point(0, 0), new Point(100, 0), 4);
        Check.Equal(3, head.Length, "三角头三个顶点");
        Check.Equal(new Point(100, 0), head[0], "尖端在终点");
        double size = MarkGeometry.HeadSize(4);
        Check.Equal(100 - size, Math.Round(head[1].X, 6), "底边在后方 HeadSize 处");
        Check.Equal(0d, Math.Round((head[1].Y + head[2].Y) / 2, 6), "两个底角上下对称");

        // 起点终点重合时不给箭头（否则会画出一个 NaN 多边形）
        Check.Equal(0, MarkGeometry.ArrowHead(new Point(5, 5), new Point(5, 5), 4).Length, "重合点不出头");
    }

    private static void ImmutableMarks()
    {
        var shape = new Mark.Shape(new Point(0, 0), new Point(10, 10), Colors.Red, 2, false, true);
        var moved = (Mark.Shape)shape.MoveBy(new Vector(5, -3));
        Check.Equal(new Point(5, -3), moved.A, "新实例跟着移动");
        Check.Equal(new Point(0, 0), shape.A, "原实例不动（撤销栈靠这个）");

        var text = new Mark.Text(new Point(0, 0), "截图", 20, Colors.White, 0, Colors.Black, 0, 4);
        var movedText = (Mark.Text)text.MoveBy(new Vector(10, 10));
        Check.Equal(new Point(10, 10), movedText.Origin, "文字移动");
        Check.Equal(text.Measured, movedText.Measured, "移动不改排版尺寸");
    }

    private static void UndoRedo()
    {
        var doc = NewDocument();
        Check.False(doc.CanUndo, "新文档不能撤销");

        doc.Add(new Mark.Serial(new Point(20, 20), doc.NextSerial(), Colors.Red, false));
        doc.Add(new Mark.Arrow(new Point(0, 0), new Point(10, 10), Colors.Red, 2));
        Check.Equal(2, doc.Marks.Count, "加了两条");

        doc.Undo();
        Check.Equal(1, doc.Marks.Count, "撤销掉一条");
        Check.True(doc.CanRedo, "可以重做");
        doc.Redo();
        Check.Equal(2, doc.Marks.Count, "重做回来");

        // 上限：只留最近 MaxUndo 份快照
        var many = NewDocument();
        for (int i = 0; i < CaptureDocument.MaxUndo + 10; i++)
            many.Add(new Mark.Serial(new Point(10, 10), many.NextSerial(), Colors.Red, false));
        Check.Equal(CaptureDocument.MaxUndo + 10, many.Marks.Count, "都加进去了");
        for (int i = 0; i < CaptureDocument.MaxUndo + 10; i++) many.Undo();
        Check.False(many.CanUndo, "撤销栈到底");
        Check.Equal(10, many.Marks.Count, "超过上限的早期快照被丢掉（剩 10 条退不回去）");
    }

    private static void SerialNumbers()
    {
        var doc = NewDocument();
        int first = doc.NextSerial();
        doc.Add(new Mark.Serial(new Point(20, 20), first, Colors.Red, false));
        int second = doc.NextSerial();
        doc.Add(new Mark.Serial(new Point(40, 20), second, Colors.Red, false));
        Check.Equal(1, first, "从 1 开始");
        Check.Equal(2, second, "递增");

        doc.Undo();
        Check.Equal(2, doc.NextSerial(), "撤销之后下一个序号接着用 2，不跳号");
    }

    private static void Selection()
    {
        var doc = NewDocument();
        Check.False(doc.HasArea, "新文档没有选区");
        Check.Equal(Rect.Empty, doc.SelectionDip, "没选区时 DIP 矩形为空");
        Check.Throws<InvalidOperationException>(() => doc.Render(), "没选区不能导出");

        // 8×8 物理像素的底图，缩放 2 → 4×4 DIP；选区取右下 4×4 物理像素
        doc.Area = new Int32Rect(102, 102, 4, 4);
        Check.True(doc.HasArea, "有选区");
        Check.Equal(new Rect(1, 1, 2, 2), doc.SelectionDip, "选区换算成 DIP");

        var image = doc.Render();
        Check.Equal(4, image.PixelWidth, "导出宽 = 选区物理宽");
        Check.Equal(4, image.PixelHeight, "导出高 = 选区物理高");
    }

    private static void TextMetrics()
    {
        var small = MarkGeometry.MeasureText("截图 Pro", 20);
        var big = MarkGeometry.MeasureText("截图 Pro", 40);
        Check.True(small.Width > 0 && small.Height > 0, "量得出尺寸");
        Check.True(big.Width > small.Width, "字号变大宽度变大");
        Check.True(MarkGeometry.MeasureText("", 20).Width > 0, "空文本给一个最小宽度（不塌成 0）");
        Check.Equal(15, MarkGeometry.SerialFontSize(7), "个位数的字号");
        Check.Equal(13, MarkGeometry.SerialFontSize(12), "两位数的字号");
        Check.Equal(11, MarkGeometry.SerialFontSize(120), "三位数缩小一点");
    }

    private static void Contrast()
    {
        Check.Equal(Color.FromRgb(0x18, 0x18, 0x1B), MarkRenderer.Contrast(Colors.White), "浅底用深字");
        Check.Equal(Colors.White, MarkRenderer.Contrast(Colors.Black), "深底用白字");
        Check.Equal((byte)128, MarkRenderer.WithAlpha(Colors.Red, 0.5).A, "透明度乘进颜色");
    }

    /// <summary>8×8 像素的假底图 + 缩放 2 的文档（不开窗口）。</summary>
    private static CaptureDocument NewDocument()
    {
        var pixels = new byte[8 * 8 * 4];
        var baseImage = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Pbgra32, null, pixels, 8 * 4);
        baseImage.Freeze();
        return new CaptureDocument(baseImage, new Int32Rect(100, 100, 8, 8), 2.0);
    }
}
