using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>CaptureProSuite 的「标注几何 / 不可变 / 缩放 / 撤销 / 滤镜 / 另存」部分。</summary>
internal static partial class CaptureProSuite
{
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

        var text = new Mark.Text(new Point(0, 0), "截图",
            new TextSpec("", 20, false, false, TextAlign.Left, Colors.White, 0, Colors.Black,
                Colors.Transparent, FillKind.Solid, 6));
        var movedText = (Mark.Text)text.MoveBy(new Vector(10, 10));
        Check.Equal(new Point(10, 10), movedText.Origin, "文字移动");
        Check.Equal(text.Measured, movedText.Measured, "移动不改排版尺寸");
    }

    /// <summary>
    /// 标注的缩放手柄：八个把手的命中 / 改框纯函数，以及各标注「按旧框 → 新框」的变形。
    /// 拉成扁的就是扁的（变形），线宽 / 字号 / 块大小按两方向的几何平均放大。
    /// </summary>
    private static void MarkScale()
    {
        // 八个把手：角在四角、边在中点
        Check.Equal(8, MarkGeometry.Handles.Length, "八个把手");
        var frame = new Rect(0, 0, 100, 50);
        Check.Equal(new Point(0, 0), MarkGeometry.GripPoint(frame, FrameHandle.TopLeft), "左上角");
        Check.Equal(new Point(100, 50), MarkGeometry.GripPoint(frame, FrameHandle.BottomRight), "右下角");
        Check.Equal(new Point(50, 0), MarkGeometry.GripPoint(frame, FrameHandle.Top), "上边中点");
        Check.Equal(new Point(100, 25), MarkGeometry.GripPoint(frame, FrameHandle.Right), "右边中点");

        Check.Equal(FrameHandle.TopLeft, MarkGeometry.HitFrame(frame, new Point(2, 1), 8), "角上 8 DIP 内命中");
        Check.Equal(FrameHandle.Right, MarkGeometry.HitFrame(frame, new Point(98, 25), 8), "边上命中");
        // 把手命中半径是 8 DIP：从框**外侧**抓边也得算把手。覆盖层原来还要求 `inside`，
        // 于是光标已经变成 SizeWE、一按却进了「选区外 = 重新框选」，松手时框太小被清掉
        // （用户报的「按住拖动退出了截图」）。
        Check.Equal(FrameHandle.Right, MarkGeometry.HitFrame(frame, new Point(104, 25), 8),
            "贴着 right 边界外 4 DIP 仍是拖右边");
        Check.Equal(FrameHandle.Bottom, MarkGeometry.HitFrame(frame, new Point(50, 54), 8),
            "贴着 bottom 边界外仍是拖下边");
        Check.Equal(FrameHandle.BottomRight, MarkGeometry.HitFrame(frame, new Point(105, 53), 8),
            "右下角外侧仍是拖角");
        Check.Equal(FrameHandle.None, MarkGeometry.HitFrame(frame, new Point(110, 25), 8),
            "离边 10 DIP 就出了命中半径");
        Check.Equal(FrameHandle.Move, MarkGeometry.HitFrame(frame, new Point(50, 25), 8), "框内给「移动」");
        Check.Equal(FrameHandle.None, MarkGeometry.HitFrame(frame, new Point(300, 300), 8), "框外不算");

        // 选区边框：**整条边**都能抓（照 Snow Shot 的边带判定），不再只有八个点周围那点方块
        var sel = new Rect(100, 100, 400, 200);
        Check.Equal(FrameHandle.Top, MarkGeometry.HitFrameEdge(sel, new Point(300, 100), 8),
            "上边中点（远离把手）也能抓");
        Check.Equal(FrameHandle.Right, MarkGeometry.HitFrameEdge(sel, new Point(500, 180), 8),
            "右边靠下那一段也能抓");
        Check.Equal(FrameHandle.Bottom, MarkGeometry.HitFrameEdge(sel, new Point(150, 300), 8),
            "下边靠左那一段也能抓");
        Check.Equal(FrameHandle.Left, MarkGeometry.HitFrameEdge(sel, new Point(100, 150), 8),
            "左边靠上那一段也能抓");
        Check.Equal(FrameHandle.Right, MarkGeometry.HitFrameEdge(sel, new Point(504, 290), 8),
            "框外 4 DIP 的边带仍算拉右边");
        Check.Equal(FrameHandle.BottomRight, MarkGeometry.HitFrameEdge(sel, new Point(496, 296), 8),
            "右下角：两条边的位都命中 → 角优先");
        Check.Equal(FrameHandle.Move, MarkGeometry.HitFrameEdge(sel, new Point(300, 200), 8),
            "边带以内 = 拖动选区");
        Check.Equal(FrameHandle.None, MarkGeometry.HitFrameEdge(sel, new Point(300, 80), 8),
            "框外超过容差 = 不命中（交给「重新框选」）");
        Check.Equal(FrameHandle.None, MarkGeometry.HitFrameEdge(sel, new Point(900, 900), 8),
            "远在框外不会被算成抓某条边");
        Check.Equal(FrameHandle.None, MarkGeometry.HitFrameEdge(Rect.Empty, new Point(0, 0), 8),
            "空框不命中");

        // 比容差还窄的小框：容差按 1/3 收窄，四条边才不会糊在一起（全落进「移动」）
        var tiny = new Rect(50, 50, 12, 12);
        Check.Equal(FrameHandle.Top, MarkGeometry.HitFrameEdge(tiny, new Point(56, 50), 8),
            "小框上边仍能抓");
        Check.Equal(FrameHandle.Left, MarkGeometry.HitFrameEdge(tiny, new Point(50, 56), 8),
            "小框左边仍能抓");
        Check.Equal(FrameHandle.Move, MarkGeometry.HitFrameEdge(tiny, new Point(56, 56), 8),
            "小框正中还是「移动」");

        // 改框：只动被拖的那条边，夹在窗口里，小于最小尺寸原样退回
        var room = new Rect(0, 0, 1000, 1000);
        Check.Equal(new Rect(0, 0, 120, 50),
            MarkGeometry.ResizeFrame(frame, FrameHandle.Right, new Vector(20, 0), 4, room), "拖右边只动右边");
        Check.Equal(new Rect(20, 10, 80, 40),
            MarkGeometry.ResizeFrame(frame, FrameHandle.TopLeft, new Vector(20, 10), 4, room), "拖左上角只动左上");
        Check.Equal(frame,
            MarkGeometry.ResizeFrame(frame, FrameHandle.Right, new Vector(-200, 0), 4, room),
            "拖过头不翻面（原样退回）");
        Check.Equal(new Rect(0, 0, 120, 50),
            MarkGeometry.ResizeFrame(frame, FrameHandle.Right, new Vector(500, 0), 4, new Rect(0, 0, 120, 50)),
            "越过窗口边被夹回来");
        Check.Equal(frame,
            MarkGeometry.ResizeFrame(frame, FrameHandle.Move, new Vector(30, 30), 4, room),
            "「移动」不归改框管");

        // 选中框 = 外接框外扩 FramePad；绘制与命中取同一个矩形
        Check.Equal(new Rect(-3, -3, 106, 56), MarkGeometry.PaddedFrame(frame), "选中框外扩 3 DIP");

        // ScaleOf：两方向几何平均，零宽按 1 算
        Check.Equal(2.0, MarkGeometry.ScaleOf(new Rect(0, 0, 100, 50), new Rect(0, 0, 200, 100)),
            "等比放大一倍 → 线宽 ×2");
        Check.True(Math.Abs(MarkGeometry.ScaleOf(new Rect(0, 0, 100, 50), new Rect(0, 0, 200, 50)) - Math.Sqrt(2)) < 1e-9,
            "只横向拉长 → 线宽 ×√2（不是 ×2）");
        Check.Equal(1.0, MarkGeometry.ScaleOf(new Rect(0, 0, 0, 50), new Rect(0, 0, 50, 50)),
            "退化框（宽 0）不炸");

        // MapPoint：框内相对位置不变
        Check.Equal(new Point(150, 75),
            MarkGeometry.MapPoint(new Point(50, 25), new Rect(0, 0, 100, 50), new Rect(50, 25, 200, 100)),
            "中点映射到中点");

        var to = new Rect(0, 0, 80, 60);

        var shape = new Mark.Shape(new Point(10, 10), new Point(50, 40), Colors.Red, 2, false, false);
        Check.True(shape.Bounds.Width > shape.Frame.Width, "外接框含描边，把手框贴着笔迹");
        var scaledShape = (Mark.Shape)shape.ScaleTo(shape.Frame, to);
        Check.Equal(to, scaledShape.Frame, "矩形按新框变形");
        Check.Equal(4.0, scaledShape.Width, "线宽跟着放大一倍");
        Check.True(shape.CanScale, "矩形能缩放");
        Check.Equal(new Rect(10, 10, 40, 30), shape.Frame, "原实例不动（撤销栈靠这个）");

        var arrow = new Mark.Arrow(new Point(10, 10), new Point(50, 40), Colors.Red, 2);
        var scaledArrow = (Mark.Arrow)arrow.ScaleTo(arrow.Frame, to);
        Check.Equal(new Point(0, 0), scaledArrow.Start, "箭头起点搬进新框");
        Check.Equal(new Point(80, 60), scaledArrow.End, "箭头终点搬进新框");

        var stroke = new Mark.Stroke(new[] { new Point(0, 0), new Point(10, 10) }, Colors.Red, 3, false, 1);
        Check.Equal(new Point(20, 20),
            ((Mark.Stroke)stroke.ScaleTo(stroke.Frame, new Rect(0, 0, 20, 20))).Points[1], "笔迹点按比例搬");

        var text = new Mark.Text(new Point(10, 10), "截图",
            new TextSpec("", 20, false, false, TextAlign.Left, Colors.White, 0, Colors.Black,
                Colors.Transparent, FillKind.Solid, 6));
        var textBox = text.Frame;
        var textTarget = new Rect(0, 0, textBox.Width * 2, textBox.Height * 2);
        var scaledText = (Mark.Text)text.ScaleTo(textBox, textTarget);
        Check.Equal(40.0, scaledText.Style.FontSize, "文字放大是字号变大（不是拉歪）");
        Check.True(textTarget.Contains(scaledText.Origin), "文字原点落在新框里");

        var mosaic = new Mark.Mosaic(new Point(10, 10), new Point(50, 40), 8);
        Check.Equal(16.0, ((Mark.Mosaic)mosaic.ScaleTo(mosaic.Frame, to)).Block, "马赛克方块跟着放大");

        var spot = new Mark.Spotlight(new Point(10, 10), new Point(50, 40), 0.5);
        Check.Equal(to, ((Mark.Spotlight)spot.ScaleTo(spot.Frame, to)).Frame, "聚光框搬过去");

        var serial = new Mark.Serial(new Point(30, 30), 3, SerialSpec.Default);
        Check.False(serial.CanScale, "序号徽标固定尺寸，不给手柄");
        Check.Equal(new Point(40, 30),
            ((Mark.Serial)serial.ScaleTo(serial.Frame, to)).Center, "序号只跟着搬位置");
    }

    /// <summary>
    /// 「物理像素 / DIP」的方向：<see cref="CaptureDocument.Scale"/> 是物理**除以** DIP
    /// （125% 屏 = 1.25），`Canvas = 像素 / Scale`。
    /// 写反了底图会被铺成 1/s² 倍大——125% 屏上 1.56 倍、只看得见屏幕左上角，
    /// 而导出因为「先除以 Scale 再乘回 Scale」看着还是对的（2026-11-17 的真机 bug）。
    /// </summary>
    private static void DocScale()
    {
        var doc = NewDocument();                 // 8×8 像素、Scale = 2
        Check.Equal(2.0, doc.Scale, "构造时给的就是「物理 ÷ DIP」");
        Check.Equal(new Rect(0, 0, 4, 4), doc.Canvas, "8 像素 ÷ 2 = 4 DIP");

        doc.SetScale(1.25);
        Check.Equal(new Rect(0, 0, 6.4, 6.4), doc.Canvas, "125% 屏：8 像素 ÷ 1.25 = 6.4 DIP");

        doc.Area = new Int32Rect(102, 102, 4, 4);
        Check.Equal(new Rect(1.6, 1.6, 3.2, 3.2), doc.SelectionDip, "选区也从物理像素 ÷ Scale 换算");

        doc.SetScale(0.8);                       // 反过来（DIP ÷ 物理）—— 正是那个缩放 bug
        Check.Equal(new Rect(0, 0, 10, 10), doc.Canvas, "比例写反时底图被铺大（1.25 倍屏 → 1.56 倍）");
    }

    private static void UndoRedo()
    {
        var doc = NewDocument();
        Check.False(doc.CanUndo, "新文档不能撤销");

        doc.Add(new Mark.Serial(new Point(20, 20), doc.NextSerial(), SerialSpec.Default));
        doc.Add(new Mark.Arrow(new Point(0, 0), new Point(10, 10), Colors.Red, 2));
        Check.Equal(2, doc.Marks.Count, "加了两条");

        doc.Undo();
        Check.Equal(1, doc.Marks.Count, "撤销掉一条");
        Check.True(doc.CanRedo, "可以重做");
        doc.Redo();
        Check.Equal(2, doc.Marks.Count, "重做回来");

        // 右键取消框选时清标注：进撤销栈，误清还能退回来
        doc.ClearMarks();
        Check.Equal(0, doc.Marks.Count, "清空标注");
        Check.True(doc.CanUndo, "清空是可撤销的一步");
        doc.Undo();
        Check.Equal(2, doc.Marks.Count, "撤销把标注找回来");

        // 上限：只留最近 MaxUndo 份快照
        var many = NewDocument();
        for (int i = 0; i < CaptureDocument.MaxUndo + 10; i++)
            many.Add(new Mark.Serial(new Point(10, 10), many.NextSerial(), SerialSpec.Default));
        Check.Equal(CaptureDocument.MaxUndo + 10, many.Marks.Count, "都加进去了");
        for (int i = 0; i < CaptureDocument.MaxUndo + 10; i++) many.Undo();
        Check.False(many.CanUndo, "撤销栈到底");
        Check.Equal(10, many.Marks.Count, "超过上限的早期快照被丢掉（剩 10 条退不回去）");
    }

    private static void SerialNumbers()
    {
        var doc = NewDocument();
        int first = doc.NextSerial();
        doc.Add(new Mark.Serial(new Point(20, 20), first, SerialSpec.Default));
        int second = doc.NextSerial();
        doc.Add(new Mark.Serial(new Point(40, 20), second, SerialSpec.Default));
        Check.Equal(1, first, "从 1 开始");
        Check.Equal(2, second, "递增");

        doc.Undo();
        Check.Equal(2, doc.NextSerial(), "撤销之后下一个序号接着用 2，不跳号");
    }

    /// <summary>
    /// 序号的五种写法都要有确定结果（照上游 <c>serial_number_format.rs</c>：罗马 1–3999、
    /// 字母表格列名式 aa..、中文带万 / 亿 / 兆 / 京分组），以及五种徽标的直径 / 圆角 /
    /// 「纯圆圈不写数字」。别等真机上看见「VIII」顶破圆圈才发现。
    /// </summary>
    private static void SerialTexts()
    {
        Check.Equal("1", SerialLabel.Of(1, SerialNumbering.Arabic), "阿拉伯数字原样");
        Check.Equal("IV", SerialLabel.Of(4, SerialNumbering.Roman), "4 → IV");
        Check.Equal("IX", SerialLabel.Of(9, SerialNumbering.Roman), "9 → IX（罗马只有大写一种）");
        Check.Equal("a", SerialLabel.Of(1, SerialNumbering.LowercaseLetters), "1 → a");
        Check.Equal("z", SerialLabel.Of(26, SerialNumbering.LowercaseLetters), "26 → z");
        Check.Equal("aa", SerialLabel.Of(27, SerialNumbering.LowercaseLetters), "27 → aa（表格列名式）");
        Check.Equal("AA", SerialLabel.Of(27, SerialNumbering.UppercaseLetters), "27 → AA");
        Check.Equal("aaa", SerialLabel.Of(703, SerialNumbering.LowercaseLetters), "703 → aaa（上游用例）");
        Check.Equal("十", SerialLabel.Of(10, SerialNumbering.Chinese), "10 读「十」而不是「一十」");
        Check.Equal("二十一", SerialLabel.Of(21, SerialNumbering.Chinese), "21 → 二十一");
        Check.Equal("一百零五", SerialLabel.Of(105, SerialNumbering.Chinese), "105 → 一百零五（中间补零）");
        Check.Equal("一万零一", SerialLabel.Of(10001, SerialNumbering.Chinese), "跨组补零：一万零一");
        Check.Equal("一亿零一", SerialLabel.Of(100_000_001, SerialNumbering.Chinese), "空组补零：一亿零一");
        Check.Equal("一亿", SerialLabel.Of(100_000_000, SerialNumbering.Chinese), "亿级分组");
        Check.Equal("一万二千三百四十五", SerialLabel.Of(12345, SerialNumbering.Chinese), "四位一组的读法");
        Check.Equal("二十一亿四千七百四十八万三千六百四十七", SerialLabel.Of(int.MaxValue, SerialNumbering.Chinese),
            "int 上限也要读全（不进位、不塌成空）");
        Check.Equal("4000", SerialLabel.Of(4000, SerialNumbering.Roman), "超范围退回阿拉伯数字，不留空");

        // 徽标直径 = 1.68 × 字号（与写法 / 位数无关）；方形圆角 = 0.2 × 字号
        Check.Equal(24 * 1.68, SerialSpec.Default.Diameter, "默认 24 号 → 直径 40.32");
        Check.Equal(24 * 0.2, SerialSpec.Default.CornerRadius, "方形圆角 = 0.2 × 字号");
        Check.True(SerialSpec.Default.Numbered, "空心圆写数字");
        Check.False((SerialSpec.Default with { Badge = SerialBadge.Circle }).Numbered,
            "纯圆圈徽标不写数字（上游 supports_number()）");
        Check.True((SerialSpec.Default with { Badge = SerialBadge.SolidSquare }).Solid, "实心方是实心");
        Check.True((SerialSpec.Default with { Badge = SerialBadge.OutlinedSquare }).Square, "空心方是方形");

        var spec = SerialSpec.Default with { Badge = SerialBadge.OutlinedSquare, Numbering = SerialNumbering.Roman };
        var serial = new Mark.Serial(new Point(10, 10), 2, spec);
        var moved = (Mark.Serial)serial.MoveBy(new Vector(5, 5));
        Check.Equal(SerialBadge.OutlinedSquare, moved.Badge, "移动不改徽标类型");
        Check.Equal(SerialNumbering.Roman, moved.Numbering, "移动不改数字写法");
        Check.Equal("II", moved.Label, "标签跟着写法走");
        Check.Equal(40.32, moved.Diameter, "直径只由字号决定");
    }

    /// <summary>
    /// 矩形滤镜：框的语义与别的一致（移动 / 缩放 / 命中），像素处理是纯函数直接断言方向。
    /// </summary>
    private static void FilterMark()
    {
        var filter = new Mark.Filter(new Point(10, 10), new Point(50, 40), FilterKind.Grayscale, 40);
        Check.Equal(new Rect(10, 10, 40, 30), filter.Frame, "滤镜框就是拖出来的矩形");
        Check.True(filter.HitTest(new Point(20, 20), 0), "框内命中");
        Check.False(filter.HitTest(new Point(60, 20), 0), "框外不命中");

        var moved = (Mark.Filter)filter.MoveBy(new Vector(4, 6));
        Check.Equal(new Point(14, 16), moved.A, "移动跟着走");
        Check.Equal(FilterKind.Grayscale, moved.Kind, "移动不改类型");
        Check.Equal(new Rect(0, 0, 80, 60),
            ((Mark.Filter)filter.ScaleTo(filter.Frame, new Rect(0, 0, 80, 60))).Frame, "拉手柄改的是框");

        // 1×1 像素：B=0 G=64 R=128 A=255
        var inverted = new byte[] { 0x00, 0x40, 0x80, 0xFF };
        MarkRenderer.Process(FilterKind.Invert, 0, inverted, 1, 1, 1);
        Check.Equal((byte)0xFF, inverted[0], "反相：B 0 → 255");
        Check.Equal((byte)0x7F, inverted[2], "反相：R 128 → 127");

        var gray = new byte[] { 0x00, 0x40, 0x80, 0xFF };
        MarkRenderer.Process(FilterKind.Grayscale, 0, gray, 1, 1, 1);
        Check.Equal(gray[0], gray[2], "灰度：三个通道取同一个值");
        Check.Equal((byte)Math.Round(0.299 * 0x80 + 0.587 * 0x40), gray[0], "灰度按亮度加权");

        var same = new byte[] { 0x10, 0x20, 0x40, 0xFF };
        MarkRenderer.Process(FilterKind.Brighten, 50, same, 1, 1, 1);       // 50 = 原样
        Check.Equal((byte)0x40, same[2], "亮度 50 不变");

        var half = new byte[] { 0x10, 0x20, 0x40, 0xFF };
        MarkRenderer.Process(FilterKind.Brighten, 25, half, 1, 1, 1);
        Check.Equal((byte)0x20, half[2], "亮度 25 → 一半");

        var blurred = new byte[] { 0x10, 0x20, 0x40, 0xFF };
        MarkRenderer.Process(FilterKind.Blur, 100, blurred, 1, 1, 1);
        Check.Equal((byte)0xFF, blurred[3], "模糊不动透明度");
    }

    /// <summary>
    /// 换底图（覆盖层里 <c>,</c> / <c>.</c> 翻采集历史）：标注 / 撤销栈 / 序号 / 选区一起重来，
    /// 底图可以摆在窗口中间（<see cref="CaptureDocument.Origin"/>），返回时再原样放回。
    /// </summary>
    private static void SwapBase()
    {
        var doc = NewDocument();                       // 8×8 底图，monitor = (100,100,8,8)，scale = 2
        doc.Area = new Int32Rect(100, 100, 8, 8);
        doc.Add(new Mark.Shape(new Point(1, 1), new Point(3, 3), Colors.Red, 2, false, false));
        int first = doc.NextSerial();
        doc.Add(new Mark.Serial(new Point(4, 4), first, SerialSpec.Default));
        Check.True(doc.CanUndo, "换之前有撤销可退");

        // 换一张 16×20 的历史图，摆在窗口里的 (5,7)，缩放仍是 2
        var history = NewPage(20);
        doc.LoadBase(history, new Int32Rect(0, 0, 16, 20), 2, new Point(5, 7));

        Check.Equal(0, doc.Marks.Count, "换底图清空标注");
        Check.False(doc.CanUndo, "换底图清空撤销栈");
        Check.False(doc.CanRedo, "换底图也清空重做栈");
        Check.False(doc.HasArea, "换底图清空选区");
        Check.Equal(Rect.Empty, doc.SelectionDip, "没有选区时 DIP 矩形为空");
        Check.Equal(new Rect(5, 7, 8, 10), doc.Canvas, "Canvas 尺寸只由底图算，左上角是 Origin");
        Check.Equal(1, doc.NextSerial(), "换底图后序号从 1 重新开始");

        // 历史图与屏幕没有位置关系：选区相对新底图（0,0），DIP 还要再加 Origin
        doc.Area = new Int32Rect(2, 4, 6, 8);
        Check.Equal(new Rect(6, 9, 3, 4), doc.SelectionDip, "选区按 Origin 偏移换算成 DIP");

        var image = doc.Render();
        Check.Equal(6, image.PixelWidth, "导出宽只看选区（与 Origin 无关）");
        Check.Equal(8, image.PixelHeight, "导出高只看选区（与 Origin 无关）");

        // 退回实时截图：把刚才那份标注放回来，序号接着最大的那个
        doc.ResetMarks(new Mark[]
        {
            new Mark.Serial(new Point(4, 4), 3, SerialSpec.Default),
            new Mark.Serial(new Point(8, 4), 7, SerialSpec.Default),
        });
        Check.Equal(2, doc.Marks.Count, "ResetMarks 原样放回");
        Check.False(doc.CanUndo, "放回标注不产生撤销记录");
        Check.Equal(8, doc.NextSerial(), "序号接着最大的那个（7 → 8）");
    }

    /// <summary>
    /// 保存对话框背后的纯函数：尺寸百分比、编码格式（文件头）与缩小后的像素尺寸。
    /// </summary>
    private static void SaveFormats()
    {
        var page = NewPage(4);                          // 16×4

        Check.Equal((16, 4), CaptureSave.OutputSize(page, 100), "100% 就是原尺寸");
        Check.Equal((8, 2), CaptureSave.OutputSize(page, 50), "50% 四舍五入");
        Check.Equal((4, 1), CaptureSave.OutputSize(page, 25), "25% 至少留 1 像素");
        Check.Equal((16, 4), CaptureSave.OutputSize(page, 0), "0% 按原尺寸兜底");
        Check.True(ReferenceEquals(page, CaptureSave.Scaled(page, 100)), "100% 不复制位图");
        Check.Equal(8, CaptureSave.Scaled(page, 50).PixelWidth, "缩放真的缩小了");

        static bool StartsWith(byte[] bytes, params byte[] head)
        {
            if (bytes.Length < head.Length) return false;
            for (int i = 0; i < head.Length; i++)
                if (bytes[i] != head[i]) return false;
            return true;
        }

        Check.True(StartsWith(CaptureSave.EncodeBytes(page, SaveFormat.Png), 0x89, 0x50, 0x4E, 0x47),
            "PNG 文件头");
        Check.True(StartsWith(CaptureSave.EncodeBytes(page, SaveFormat.Jpeg), 0xFF, 0xD8, 0xFF),
            "JPEG 文件头");
        Check.True(StartsWith(CaptureSave.EncodeBytes(page, SaveFormat.Bmp), 0x42, 0x4D),
            "BMP 文件头");
        Check.True(StartsWith(CaptureSave.EncodeBytes(page, SaveFormat.Tiff), 0x49, 0x49, 0x2A, 0x00) ||
                   StartsWith(CaptureSave.EncodeBytes(page, SaveFormat.Tiff), 0x4D, 0x4D, 0x00, 0x2A),
            "TIFF 文件头");

        Check.Equal(".png", CaptureSave.Extension(SaveFormat.Png), "PNG 扩展名");
        Check.Equal(".jpg", CaptureSave.Extension(SaveFormat.Jpeg), "JPEG 扩展名");
        Check.Equal(".bmp", CaptureSave.Extension(SaveFormat.Bmp), "BMP 扩展名");
        Check.Equal(".tiff", CaptureSave.Extension(SaveFormat.Tiff), "TIFF 扩展名");
        Check.Equal("PNG", CaptureSave.Label(SaveFormat.Png), "格式胶囊文字");

        // 50% 编码出来的 png 解回来就是缩小后的尺寸
        byte[] png = CaptureSave.EncodeBytes(page, SaveFormat.Png, 90, 50);
        using var stream = new MemoryStream(png);
        var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Check.Equal(8, decoded.PixelWidth, "50% 的 png 解回来宽 8");
        Check.Equal(2, decoded.PixelHeight, "50% 的 png 解回来高 2");
    }

}
