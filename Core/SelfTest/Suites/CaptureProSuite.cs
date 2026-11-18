using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
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
internal static partial class CaptureProSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("capturePro", "几何：矩形 / 外接框 / 折线命中", Geometry));
        cases.Add(new("capturePro", "几何：箭头三角头", ArrowHead));
        cases.Add(new("capturePro", "标注：移动返回新实例", ImmutableMarks));
        cases.Add(new("capturePro", "标注：把手命中与等比缩放", MarkScale));
        cases.Add(new("capturePro", "文档：撤销 / 重做 / 上限", UndoRedo));
        cases.Add(new("capturePro", "文档：序号撤销后不跳号", SerialNumbers));
        cases.Add(new("capturePro", "标注：序号写法与徽标字号", SerialTexts));
        cases.Add(new("capturePro", "标注：矩形滤镜与像素处理", FilterMark));
        cases.Add(new("capturePro", "文档：换底图（翻采集历史）", SwapBase));
        cases.Add(new("capturePro", "保存：格式 / 尺寸 / 编码", SaveFormats));
        cases.Add(new("capturePro", "文档：选区换算与导出", Selection));
        cases.Add(new("capturePro", "选区形状：自由 / 折线的几何与导出", SelectionShapes));
        cases.Add(new("capturePro", "文档：比例是「物理 ÷ DIP」", DocScale));
        cases.Add(new("capturePro", "文字：量宽随字号增长", TextMetrics));
        cases.Add(new("capturePro", "样式：底色对比取字色", Contrast));
        cases.Add(new("capturePro", "工具：目录 / 参数格 / 默认样式", Tools));
        cases.Add(new("capturePro", "标注：P1 新工具（线 / 尺寸 / 气泡 / 镜头 / 水印 / 画笔滤镜）", ParityMarks));
        cases.Add(new("capturePro", "滤镜：像素级智能擦除（仿射 / 周期 / 多尺度）", SmartEraseCases));
        cases.Add(new("capturePro", "橡皮：恢复原图块（矩形 / 画笔，不可点中）", RestoreMarks));
        cases.Add(new("capturePro", "自动打码：区域检测与上游逐区域对齐", AutoFilterDetect));
        cases.Add(new("capturePro", "自动打码：命中 / 框选 / 填充事务 / 绘制顺序", AutoFilterLogicChecks));
        cases.Add(new("capturePro", "取色：像素颜色与文本", Picker));
        cases.Add(new("capturePro", "贴图：缩放 / 不透明度 / 锚点", Pin));
        cases.Add(new("capturePro", "贴图：分组名与批量另存", PinGrouping));
        cases.Add(new("capturePro", "贴图：实时状态与裁剪", PinLiveCrop));
        cases.Add(new("capturePro", "工具栏：格子布局 / 收起 / 落盘往返", ToolLayouts));
        cases.Add(new("capturePro", "样式：每个工具家族各记一套（落盘往返）", ToolStyles));
        cases.Add(new("capturePro", "样式：形状族（形状 / 描边 / 填充 / 圆角 / 尾巴 / 圆点）", ShapeStyles));
        cases.Add(new("capturePro", "样式：箭头族（三种类型 / 14 端点 / 渐宽杆 / 外描边）", ArrowFamily));
        cases.Add(new("capturePro", "度量：测距端点 / 单位 / 系数 / 测角内角 / 花括号", MeasureStyles));
        cases.Add(new("capturePro", "样式：序号徽标 / 水印模板 / 放大镜几何", SerialWatermarkMagnifier));
        cases.Add(new("capturePro", "识别：表格聚类 / TSV / HTML", Recognize));
        cases.Add(new("capturePro", "识别：文字层像素估色（背景 / 字色 / 花底放弃）", OcrPaletteCases));
        cases.Add(new("capturePro", "识别：文字层构建与字号拟合", OcrTextLayerCases));
        cases.Add(new("capturePro", "识别：文字层编辑（选区 / 跨行 / 撤销）", OcrEditCases));
        cases.Add(new("capturePro", "识别：文字层命中（行内字符）", OcrCanvasHitCases));
        cases.Add(new("capturePro", "识别：表格文档（槽位 / 合并 / 拆分 / 清空 / 选区扩展）", TableDocumentCases));
        cases.Add(new("capturePro", "识别：表格 HTML / TSV 往返（span / 表头 / 嵌套表）", TableHtmlCases));
        cases.Add(new("capturePro", "识别：表格粘贴规则（放不下 / 切合并格）", TablePasteCases));
        cases.Add(new("capturePro", "识别：表格导出 xlsx（part / 引用 / 转义）", TableXlsxCases));
        cases.Add(new("capturePro", "识别：二维码缩放 / 网址 / 气泡落点", QrGeometryCases));
        cases.Add(new("capturePro", "识别：二维码检测（同图两枚 / 空白图）", QrDetectCases));
        cases.Add(new("capturePro", "转换：版面 → 块（标题 / 段落 / 列表 / 表格）", ConversionBlockCases));
        cases.Add(new("capturePro", "转换：Markdown / HTML 源码与转义", ConversionSourceCases));
        cases.Add(new("capturePro", "转换：文档导出（纯文本 / LaTeX / HTML / docx）", DocumentExportCases));
        cases.Add(new("capturePro", "采集历史：剪枝 / 缩略图 / 文案", Catalog));
        cases.Add(new("capturePro", "跨屏：取景并集 / 交集", ScreenArea));
        cases.Add(new("capturePro", "长截图：滚动位移识别", ScrollDetection));
        cases.Add(new("capturePro", "长截图：拼接与面板落点", ScrollBuild));
        cases.Add(new("capturePro", "长截图：固定页脚与就地预览", ScrollSticky));
        cases.Add(new("capturePro", "长截图：横向拼接", ScrollHorizontal));
        cases.Add(new("capturePro", "长截图：自动滚动的自适应节奏", ScrollCadenceRules));
        cases.Add(new("capturePro", "长截图：真实页面形态（snow-shot 夹具）", ScrollRealPages));
        cases.Add(new("capturePro", "长截图：整页切帧回放（逐像素）", ScrollReplay));
        cases.Add(new("capturePro", "选字：候选切分", ScreenTextSplit));
        cases.Add(new("capturePro", "录屏：参数 / 命令行 / 定位", Record));
        cases.Add(new("capturePro", "录屏：暂停计时与历史文案", RecordBookkeeping));
        cases.Add(new("capturePro", "录屏：跟随鼠标（移植上游用例）", RecordFollow));
        cases.Add(new("capturePro", "录屏：空闲区域框的拖拽几何", RecordRegion));
        cases.Add(new("capturePro", "录屏：中断残片的尺寸 / 时长解析", RecordRecoveryProbe));
        cases.Add(new("capturePro", "录屏：选项 / 编码器 / 混音参数", RecordOptionsRules));
        cases.Add(new("capturePro", "录屏：特效几何 / 画中画落点", RecordEffectRules));
        cases.Add(new("capturePro", "录屏：裁剪 / 倍速 / 导出参数", RecordTrimRules));
        cases.Add(new("capturePro", "文字族：字号 / 字体 / 粗斜 / 对齐 / 描边 / 背景胶囊", TextStyles));
        cases.Add(new("capturePro", "样式：选中标注后切族并写回", SelectionStyles));
        cases.Add(new("capturePro", "多选：选区切族 / 框选命中", SelectionMulti));
        cases.Add(new("capturePro", "选区：外观 / 比例锁定 / 阴影曲线 / 导出布局", SelectionStyleRules));
        cases.Add(new("capturePro", "选区：比例锁定的拖动 / 框选几何", SelectionAspectGeometry));
        cases.Add(new("capturePro", "选区：圆角几何与命中", SelectionCornerGeometry));
        cases.Add(new("capturePro", "选区：圆角 / 边框 / 阴影的导出合成（逐像素）", SelectionExportLayout));
        cases.Add(new("capturePro", "选区：信息条摆位", SelectionBarRules));
        cases.Add(new("capturePro", "选区：参数与外观往返 / 两份效果宽保持", SelectionParamsRules));
        cases.Add(new("capturePro", "智能选区：换层过渡（上游 14 组用例）", SmartTransitionCases));
        cases.Add(new("capturePro", "智能选区：命中路径 / 细化 / Tab 切目标", SmartSelectionModelCases));
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

    /// <summary>
    /// 自由 / 折线选区：补点阈值、外接框、平移 / 缩放、DIP → 图像像素、闭合成几何，
    /// 以及「导出按多边形裁」（形状内不透明、形状外透明）。
    /// </summary>
    private static void SelectionShapes()
    {
        // 补点阈值：鼠标没动也有事件，太近的点丢掉
        var points = new List<Point>();
        SelectionShape.Append(points, new Point(0, 0));
        SelectionShape.Append(points, new Point(SelectionShape.MinStep / 2, 0));
        Check.Equal(1, points.Count, "比阈值近的点丢掉");
        SelectionShape.Append(points, new Point(SelectionShape.MinStep, 0));
        Check.Equal(2, points.Count, "够远才补一个点");

        // 外接框（点列的框，不是矩形选区）
        var tri = new[] { new Point(2, 3), new Point(8, 3), new Point(5, 9) };
        Check.Equal(new Rect(2, 3, 6, 6), SelectionShape.Bounds(tri), "点列外接框");
        Check.Equal(Rect.Empty, SelectionShape.Bounds(Array.Empty<Point>()), "空点列 = 空框");

        // 够不够成一个选区：至少三个点 + 外接框到了最小边长
        Check.False(SelectionShape.IsUsable(tri.Take(2).ToArray(), 4), "两个点不是一个面");
        Check.True(SelectionShape.IsUsable(tri, 4), "三个点 + 框够大 = 可用");
        Check.False(SelectionShape.IsUsable(new[] { new Point(0, 0), new Point(1, 0), new Point(0, 1) }, 4),
            "框太小不算选区");
        Check.Equal(3, SelectionShape.MinPoints, "最少三个点（三角形才是面）");

        // 平移 / 外接框缩放（与标注缩放同一口径：按框的比例变形点列）
        var square = new[] { new Point(0, 0), new Point(10, 0), new Point(10, 10), new Point(0, 10) };
        Check.Equal(new Point(3, -2), SelectionShape.Offset(square, new Vector(3, -2))[0], "整体平移");
        var doubled = SelectionShape.Scale(square, new Rect(0, 0, 10, 10), new Rect(5, 5, 20, 20));
        Check.Equal(new Point(5, 5), doubled[0], "缩放：左上跟着框的左上");
        Check.Equal(new Point(25, 25), doubled[2], "缩放：右下按框的比例放大");

        // DIP → 图像像素（相对底图左上角，四舍五入）
        var image = SelectionShape.ToImage(new[] { new Point(10, 10), new Point(5.2, 5.2) },
            new Point(5, 5), 2.0);
        Check.Equal(new Point(10, 10), image[0], "偏移 + 比例换算");
        Check.Equal(new Point(0, 0), image[1], "0.4 像素四舍五入到 0");

        // 闭合成几何：首尾相连、内部填满；命中测试用射线法
        var closed = SelectionShape.Geometry(square, closed: true);
        Check.True(closed.FillContains(new Point(5, 5)), "闭合几何内部填满");
        Check.False(closed.FillContains(new Point(15, 5)), "闭合几何外面不填");
        Check.True(SelectionShape.Contains(square, new Point(5, 5)), "多边形命中：里面");
        Check.False(SelectionShape.Contains(square, new Point(15, 5)), "多边形命中：外面");
        Check.False(SelectionShape.Contains(square.Take(2).ToArray(), new Point(5, 5)), "点数不够不算面");

        // 导出按多边形裁：形状内保留底图，形状外透明
        var pixels = new byte[8 * 8 * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF; pixels[i + 1] = 0xFF; pixels[i + 2] = 0xFF; pixels[i + 3] = 0xFF;
        }
        var white = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Pbgra32, null, pixels, 8 * 4);
        white.Freeze();

        var doc = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 2.0);
        doc.Area = new Int32Rect(100, 100, 8, 8);
        Check.False(doc.HasRegions, "一整块矩形没有区域数据（不用裁）");

        doc.Regions = new[]
        {
            SelectionRegion.FromPoints(RegionMode.Add,
                new[] { new Point(1, 1), new Point(7, 1), new Point(7, 7), new Point(1, 7) }),
        };
        Check.True(doc.HasRegions, "写了区域就按合起来的几何裁");

        var shot = doc.Render();
        var outPixels = new byte[8 * 8 * 4];
        shot.CopyPixels(outPixels, 8 * 4, 0);
        Check.Equal((byte)0xFF, outPixels[(3 * 8 + 3) * 4 + 3], "形状内保留底图（不透明）");
        Check.Equal((byte)0, outPixels[0 * 4 + 3], "左上角在形状外 = 透明");
        Check.Equal((byte)0, outPixels[(7 * 8 + 7) * 4 + 3], "右下角在形状外 = 透明");

        // 选区换成矩形（重新框选）：导出不再裁
        doc.Regions = null;
        var rectShot = doc.Render();
        var rectPixels = new byte[8 * 8 * 4];
        rectShot.CopyPixels(rectPixels, 8 * 4, 0);
        Check.Equal((byte)0xFF, rectPixels[0 * 4 + 3], "矩形选区整块不透明");

        // ============ 多区域：加区 / 减区 ============
        var add = SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 10, 10));
        var add2 = SelectionRegion.FromRect(RegionMode.Add, new Rect(20, 4, 6, 6));
        var cut = SelectionRegion.FromRect(RegionMode.Subtract, new Rect(2, 2, 4, 4));
        var set = new[] { add, cut, add2 };

        Check.Equal(new Rect(0, 0, 26, 10), SelectionSet.Bounds(set), "外接框只并加区（减区不扩大取景）");
        Check.Equal(Rect.Empty, SelectionSet.Bounds(new[] { cut }), "只有减区 = 空外接框");
        Check.True(SelectionSet.Contains(set, new Point(1, 1)), "加区里 = 在选区里");
        Check.False(SelectionSet.Contains(set, new Point(4, 4)), "挖掉的洞里 = 不在选区里");
        Check.True(SelectionSet.Contains(set, new Point(22, 6)), "第二块加区里 = 在选区里");

        var covered = new[]
        {
            SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 10, 10)),
            SelectionRegion.FromRect(RegionMode.Subtract, new Rect(-1, -1, 20, 20)),
        };
        Check.True(SelectionSet.Combine(covered) == null, "减区把加区整个挖空 = 没有可导出的内容");
        Check.True(SelectionSet.Combine(new[] { cut }) == null, "只有减区 = 什么都不剩");

        // 压暗的阴影上按下去：单块矩形 + 选择工具 + 没按修饰键 = 挪整块选区（照 Snow Shot）
        Check.True(SelectionSet.MovesOnOutsideDrag(new[] { add }, true, false, false),
            "单块矩形 + 选择工具：拖阴影 = 挪整块");
        Check.False(SelectionSet.MovesOnOutsideDrag(new[] { add }, false, false, false),
            "别的工具：拖阴影仍是重新框选（要画标注）");
        Check.False(SelectionSet.MovesOnOutsideDrag(new[] { add }, true, true, false),
            "按着 Shift：拖出去是加区，不是移动");
        Check.False(SelectionSet.MovesOnOutsideDrag(new[] { add }, true, false, true),
            "按着 Alt：拖出去是减区，不是移动");
        Check.False(SelectionSet.MovesOnOutsideDrag(set, true, false, false),
            "多块 / 有减区：外面 = 重新框选（上游同口径）");
        Check.False(SelectionSet.MovesOnOutsideDrag(
                new[] { SelectionRegion.FromPoints(RegionMode.Add, new[] { new Point(0, 0), new Point(9, 0), new Point(5, 9) }) },
                true, false, false),
            "自由 / 折线选区：外面 = 重新框选");
        Check.False(SelectionSet.MovesOnOutsideDrag(new[] { cut }, true, false, false),
            "只有减区：没有可挪的整块");

        // 平移 / 缩放 / DIP → 像素 / 夹到底图里
        var moved = add.Offset(new Vector(3, 4));
        Check.Equal(new Rect(3, 4, 10, 10), moved.Bounds, "整块平移");
        Check.Equal(RegionMode.Add, moved.Mode, "平移不改加 / 减");
        Check.Equal(new Rect(0, 0, 20, 30), add.Scale(new Rect(0, 0, 10, 10), new Rect(0, 0, 20, 30)).Bounds,
            "按外接框缩放");
        Check.Equal(new Rect(0, 0, 21, 21), add.ToImage(new Point(0, 0), 2).Bounds,
            "矩形换算成像素：含两端像素（0..20 = 21 个）");
        var polyPixels = SelectionRegion.FromPoints(RegionMode.Add,
            new[] { new Point(0, 0), new Point(10, 0), new Point(0, 10) }).ToImage(new Point(0, 0), 2);
        Check.Equal(new Point(20, 0), polyPixels.Points![1], "点列按点换算成像素");
        Check.Equal(new Rect(0, 0, 8, 8), add.ToImage(new Point(0, 0), 1).Clamp(8, 8).Bounds, "夹到底图里");

        // 多区域导出：加区整块 + 中间挖一个洞
        var holeDoc = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 2.0);
        holeDoc.Area = new Int32Rect(100, 100, 8, 8);
        holeDoc.Regions = new[]
        {
            SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 8, 8)),
            SelectionRegion.FromRect(RegionMode.Subtract, new Rect(2, 2, 4, 4)),
        };
        var holePixels = new byte[8 * 8 * 4];
        holeDoc.Render().CopyPixels(holePixels, 8 * 4, 0);
        Check.Equal((byte)0xFF, holePixels[(0 * 8 + 0) * 4 + 3], "多区域：加区里保留底图");
        Check.Equal((byte)0, holePixels[(3 * 8 + 3) * 4 + 3], "多区域：减区挖出来的洞里透明");

        // 有偏移的选区：裁剪切**不许**再减一次选区偏移（减了右边 / 下边会被裁成透明）
        var offsetDoc = new CaptureDocument(white, new Int32Rect(0, 0, 8, 8), 2.0);
        offsetDoc.Area = new Int32Rect(2, 2, 4, 4);
        offsetDoc.Regions = new[] { SelectionRegion.FromRect(RegionMode.Add, new Rect(2, 2, 4, 4)) };
        var offsetPixels = new byte[4 * 4 * 4];
        offsetDoc.Render().CopyPixels(offsetPixels, 4 * 4, 0);
        Check.Equal((byte)0xFF, offsetPixels[(0 * 4 + 0) * 4 + 3], "有偏移的选区：左上不透明");
        Check.Equal((byte)0xFF, offsetPixels[(3 * 4 + 3) * 4 + 3], "有偏移的选区：右下也不透明");
    }

    private static void TextMetrics()
    {
        var small = MarkGeometry.MeasureText("截图 Pro", 20);
        var big = MarkGeometry.MeasureText("截图 Pro", 40);
        Check.True(small.Width > 0 && small.Height > 0, "量得出尺寸");
        Check.True(big.Width > small.Width, "字号变大宽度变大");
        Check.True(MarkGeometry.MeasureText("", 20).Width > 0, "空文本给一个最小宽度（不塌成 0）");
    }

    private static void Contrast()
    {
        Check.Equal(Color.FromRgb(0x18, 0x18, 0x1B), MarkRenderer.Contrast(Colors.White), "浅底用深字");
        Check.Equal(Colors.White, MarkRenderer.Contrast(Colors.Black), "深底用白字");
        Check.Equal((byte)128, MarkRenderer.WithAlpha(Colors.Red, 0.5).A, "透明度乘进颜色");
    }

    /// <summary>
    /// 工具目录：每个工具都有一行、参数格按用途分配、默认样式与「点一下不留 0 像素标注」的口径。
    /// 新增工具只改 <see cref="ToolCatalog"/>，这里守住它别漏项。
    /// </summary>
    private static void Tools()
    {
        var all = ToolCatalog.All;
        Check.Equal(Enum.GetValues<CaptureTool>().Length, all.Length, "每个工具都在目录里");
        Check.Equal(all.Length, all.Select(t => t.Tool).Distinct().Count(), "工具不重复");
        Check.Equal(all.Length, all.Select(t => t.Name).Distinct().Count(), "名字不重复");
        Check.Equal(all.Length, all.Select(t => t.Icon).Distinct().Count(), "图标键不重复");
        Check.Equal(all.Length, all.Select(t => t.Shortcut).Distinct().Count(), "字母快捷键不重复");

        // 字母 → 工具（覆盖层在没打字时按它切工具）
        Check.Equal(CaptureTool.Spotlight, ToolCatalog.ForKey(System.Windows.Input.Key.S), "S = 聚光（Ctrl+S 是另存为，先判 Ctrl）");
        Check.Equal(CaptureTool.Pen, ToolCatalog.ForKey(System.Windows.Input.Key.P), "P = 画笔");
        Check.Equal(CaptureTool.Select, ToolCatalog.ForKey(System.Windows.Input.Key.Q), "Q = 选择");
        Check.Equal(CaptureTool.Picker, ToolCatalog.ForKey(System.Windows.Input.Key.C), "C = 取色（Ctrl+C 是复制，先判 Ctrl）");
        Check.True(ToolCatalog.ForKey(System.Windows.Input.Key.F13) == null, "没绑的键返回 null");

        foreach (var info in all)
        {
            Check.True(!string.IsNullOrWhiteSpace(info.Name), info.Tool + " 有名字");
            Check.True(!string.IsNullOrWhiteSpace(info.Hint), info.Tool + " 有说明（工具栏 ToolTip 用它）");
            Check.True(!string.IsNullOrWhiteSpace(info.Icon), info.Tool + " 有图标键");
            // 参数格要么没有，要么不重复（重复会让参数条出现两个一样的加号组）
            Check.Equal(info.Params.Length, info.Params.Distinct().Count(), info.Tool + " 参数格不重复");
        }

        // 参数格按工具分：画笔没块大小，马赛克没颜色，文字没线宽，擦除 / 选择一格都不用
        Check.True(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.Color), "画笔有颜色");
        Check.True(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.Width), "画笔有线宽");
        Check.False(ToolCatalog.Uses(CaptureTool.Pen, ToolParam.Block), "画笔没有块大小");
        Check.True(ToolCatalog.Uses(CaptureTool.Highlighter, ToolParam.Opacity), "荧光笔有透明度");
        Check.True(ToolCatalog.Uses(CaptureTool.Text, ToolParam.TextSize), "文字有字号");
        Check.False(ToolCatalog.Uses(CaptureTool.Text, ToolParam.Width), "文字没有线宽");
        Check.True(ToolCatalog.Uses(CaptureTool.Mosaic, ToolParam.Block), "马赛克有块大小");
        Check.False(ToolCatalog.Uses(CaptureTool.Mosaic, ToolParam.Color), "马赛克没有颜色");
        Check.False(ToolCatalog.Uses(CaptureTool.Eraser, ToolParam.Color), "擦除不用任何参数格");
        Check.False(ToolCatalog.Uses(CaptureTool.Select, ToolParam.Width), "选择不用任何参数格");
        Check.Equal(0, ToolCatalog.Of(CaptureTool.Picker).Params.Length, "取色不用任何参数格");

        // 新增的两族参数：序号要形状 / 写法，滤镜要类型 / 强度（马赛克的块大小不给滤镜）
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.BadgeShape), "序号有徽标类型");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.Numbering), "序号有数字写法");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.SerialNumber), "序号有「下一个序号」");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.SerialFontSize), "序号有字号（与文字 / 水印分开）");
        Check.True(ToolCatalog.Uses(CaptureTool.Serial, ToolParam.Width), "序号有线宽（上游 buildSequenceNumberFamily 有 strokeWidth）");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.Connection), "放大镜有连接方式");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.SourceOutline), "放大镜有源轮廓开关");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.ArrowRatio), "放大镜有箭头比例");
        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.LeaderArrowhead), "放大镜有引导线端点");
        Check.True(ToolCatalog.Uses(CaptureTool.Filter, ToolParam.FilterKind), "滤镜有类型");
        Check.True(ToolCatalog.Uses(CaptureTool.Filter, ToolParam.FilterStrength), "滤镜有强度");
        Check.False(ToolCatalog.Uses(CaptureTool.Filter, ToolParam.Block), "滤镜没有马赛克块大小");
        Check.False(ToolCatalog.Uses(CaptureTool.Mosaic, ToolParam.FilterKind), "马赛克没有滤镜类型（两个工具别混）");
        Check.Equal(CaptureTool.Filter, ToolCatalog.ForKey(System.Windows.Input.Key.F), "F = 滤镜");

        // P1 / P2：与 Snow Shot 对齐的新工具（上游 32 项 → 这里 25 项，其余是 P4 的识别类）
        Check.Equal(25, all.Length, "工具目录 25 项");
        Check.Equal(CaptureTool.AutoFilter, ToolCatalog.ForKey(System.Windows.Input.Key.V), "V = 自动打码");
        Check.Equal(CaptureTool.Line, ToolCatalog.ForKey(System.Windows.Input.Key.I), "I = 直线");
        Check.Equal(CaptureTool.Distance, ToolCatalog.ForKey(System.Windows.Input.Key.D), "D = 测距");
        Check.Equal(CaptureTool.Angle, ToolCatalog.ForKey(System.Windows.Input.Key.G), "G = 测角");
        Check.Equal(CaptureTool.Bracket, ToolCatalog.ForKey(System.Windows.Input.Key.B), "B = 括号");
        Check.Equal(CaptureTool.SpeechBubble, ToolCatalog.ForKey(System.Windows.Input.Key.O), "O = 气泡");
        Check.Equal(CaptureTool.Magnifier, ToolCatalog.ForKey(System.Windows.Input.Key.Z), "Z = 放大镜");
        Check.Equal(CaptureTool.Watermark, ToolCatalog.ForKey(System.Windows.Input.Key.W), "W = 水印");
        Check.Equal(CaptureTool.HighlightRect, ToolCatalog.ForKey(System.Windows.Input.Key.J), "J = 矩形荧光");
        Check.Equal(CaptureTool.BrushFilter, ToolCatalog.ForKey(System.Windows.Input.Key.Y), "Y = 画笔滤镜");
        Check.Equal(CaptureTool.RectEraser, ToolCatalog.ForKey(System.Windows.Input.Key.K), "K = 矩形橡皮");
        Check.Equal(CaptureTool.BrushEraser, ToolCatalog.ForKey(System.Windows.Input.Key.U), "U = 画笔橡皮");

        Check.True(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.Factor), "放大镜有倍率");
        Check.False(ToolCatalog.Uses(CaptureTool.Magnifier, ToolParam.Block), "放大镜没有马赛克块大小");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkText), "水印有文字");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkAngle), "水印有角度");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkGap), "水印有间距");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkTemplate), "水印有模板下拉");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.WatermarkOpacity), "水印有不透明度（与荧光笔分开）");
        Check.True(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.FontSize), "水印有字号（12 / 16 / 24 / 30 预设）");
        Check.False(ToolCatalog.Uses(CaptureTool.Watermark, ToolParam.Width), "水印没有线宽");
        Check.True(ToolCatalog.Uses(CaptureTool.BrushFilter, ToolParam.FilterKind), "画笔滤镜有类型");
        Check.True(ToolCatalog.Uses(CaptureTool.BrushFilter, ToolParam.Width), "画笔滤镜有笔头宽度");
        Check.False(ToolCatalog.Uses(CaptureTool.HighlightRect, ToolParam.Width), "矩形荧光没有线宽（只有色 + 透明度）");
        Check.True(ToolCatalog.Uses(CaptureTool.HighlightRect, ToolParam.Opacity), "矩形荧光有透明度");

        // 笔迹类 / 橡皮类的判定只有这一处（手势与最小尺寸都读它）
        foreach (var tool in new[] { CaptureTool.Pen, CaptureTool.Highlighter, CaptureTool.BrushFilter, CaptureTool.BrushEraser })
        {
            Check.True(ToolCatalog.IsFreehand(tool), tool + " 走笔迹");
            Check.Equal(1d, ToolCatalog.MinimumSize(tool), tool + " 的最小尺寸是 1 DIP");
        }
        foreach (var tool in new[] { CaptureTool.Eraser, CaptureTool.RectEraser, CaptureTool.BrushEraser })
            Check.True(ToolCatalog.IsEraser(tool), tool + " 是橡皮");
        Check.False(ToolCatalog.IsFreehand(CaptureTool.RectEraser), "矩形橡皮不是笔迹（拖矩形）");
        Check.False(ToolCatalog.IsEraser(CaptureTool.Pen), "画笔不是橡皮");

        // 一维工具：横着拖也要能画（只判长度，不要求两个方向都动）
        foreach (var tool in new[] { CaptureTool.Line, CaptureTool.Distance, CaptureTool.Bracket, CaptureTool.Arrow })
            Check.True(ToolCatalog.IsLinear(tool), tool + " 是一维工具");
        Check.False(ToolCatalog.IsLinear(CaptureTool.Shape), "形状不是一维工具（宽高都得够）");

        // 每种滤镜都有自己的名字（参数行 chip 与 tooltip 共用，别留空）
        foreach (var kind in Enum.GetValues<FilterKind>())
            Check.True(!string.IsNullOrWhiteSpace(SerialLabel.Name(kind)), kind + " 有中文名");
        foreach (var numbering in Enum.GetValues<SerialNumbering>())
            Check.True(!string.IsNullOrWhiteSpace(SerialLabel.Chip(numbering)), numbering + " 有 chip 标签");
        foreach (var badge in Enum.GetValues<SerialBadge>())
            Check.True(!string.IsNullOrWhiteSpace(SerialLabel.Name(badge)), badge + " 有中文名");

        // 光标：只有选择和文字不是十字
        Check.False(ToolCatalog.WantsCrossCursor(CaptureTool.Select), "选择用箭头");
        Check.False(ToolCatalog.WantsCrossCursor(CaptureTool.Text), "文字用 I 形");
        Check.True(ToolCatalog.WantsCrossCursor(CaptureTool.Shape), "形状用十字");

        // 点一下不该留下 0 像素标注：手绘 1 DIP 就算，形状要 4 DIP
        Check.Equal(1d, ToolCatalog.MinimumSize(CaptureTool.Pen), "手绘的最小尺寸");
        Check.Equal(1d, ToolCatalog.MinimumSize(CaptureTool.Highlighter), "荧光笔的最小尺寸");
        Check.Equal(4d, ToolCatalog.MinimumSize(CaptureTool.Shape), "形状的最小尺寸");

        // 样式：默认值、荧光笔笔头、聚光的压暗换算
        var style = new ToolStyle();
        Check.Equal(ToolStyle.Palette[0], style.Color, "默认颜色是调色盘第一格");
        Check.Equal(4d, style.Width, "默认线宽");
        Check.Equal(20d, style.FontSize, "默认字号");
        Check.Equal(8d, style.Block, "默认块大小");
        Check.Equal(4d, style.WidthFor(CaptureTool.Pen), "画笔用线宽本身");
        Check.True(style.WidthFor(CaptureTool.Highlighter) >= 10, "荧光笔笔头至少 10（否则看着就是普通线）");
        Check.Equal(0.65, Math.Round(style.Dim, 6), "透明度 0.35 → 框外压暗 0.65");
        style.Opacity = 0.95;
        Check.True(style.Dim <= 0.85, "压暗上限 0.85");
        style.Opacity = 1;
        Check.True(style.Dim >= 0, "压暗不为负");
    }

    /// <summary>
    /// 取色：从画面读像素并按 <c>#RRGGBB</c> / <c>rgb()</c> 出文本。
    /// 越界与没有底图都只是 <c>null</c>（光标在窗口边缘时算出界是正常情况，不该报错）。
    /// </summary>
    private static void Picker()
    {
        // 2×1 的假图：左边纯红、右边纯蓝（BGRA 字节序）
        var pixels = new byte[] { 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xFF };
        var image = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
        image.Freeze();

        var left = PickerColor.Read(image, 0, 0);
        var right = PickerColor.Read(image, 1, 0);
        Check.True(left.HasValue && left.Value == Color.FromRgb(0xFF, 0x00, 0x00), "左像素是红（BGRA 没读反）");
        Check.True(right.HasValue && right.Value == Color.FromRgb(0x00, 0x00, 0xFF), "右像素是蓝");

        Check.True(PickerColor.Read(image, 2, 0) == null, "横向越界返回 null");
        Check.True(PickerColor.Read(image, 0, 1) == null, "纵向越界返回 null");
        Check.True(PickerColor.Read(null, 0, 0) == null, "没有底图返回 null");

        var sample = Color.FromRgb(0xFF, 0x80, 0x00);
        Check.Equal("#FF8000", PickerColor.Hex(sample), "十六进制大写补零");
        Check.Equal("rgb(255, 128, 0)", PickerColor.Rgb(sample), "rgb 文本");
        Check.Equal("#000000 · rgb(0, 0, 0)", PickerColor.Describe(Colors.Black), "提示条正文 = 两种写法");
    }

    /// <summary>
    /// 跨屏选区：取景范围 = 所有显示器的并集，逐块抓帧的交集。
    /// 坐标是物理像素，主屏左侧 / 上方的副屏会让 Left / Top 变成负数。
    /// </summary>
    private static void ScreenArea()
    {
        var single = ScreenShot.Union(new[] { new Int32Rect(0, 0, 1920, 1080) });
        Check.Equal(new Int32Rect(0, 0, 1920, 1080), single, "单屏 = 自己");

        // 副屏在右边（高矮不同）
        var side = ScreenShot.Union(new[]
        {
            new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(1920, 0, 1280, 1024),
        });
        Check.Equal(new Int32Rect(0, 0, 3200, 1080), side, "并集取外接矩形");

        // 副屏在左边：Left 为负
        var left = ScreenShot.Union(new[]
        {
            new Int32Rect(0, 0, 1920, 1080),
            new Int32Rect(-1280, -200, 1280, 1024),
        });
        Check.Equal(new Int32Rect(-1280, -200, 3200, 1280), left, "左 / 上方的副屏 → 负坐标");

        // 空表 / 空矩形
        Check.Equal(Int32Rect.Empty, ScreenShot.Union(Array.Empty<Int32Rect>()), "没有显示器 → 空矩形");
        Check.Equal(Int32Rect.Empty,
            ScreenShot.Union(new[] { new Int32Rect(0, 0, 0, 100) }), "宽 0 当空矩形");

        // 交集：逐块抓帧时用
        Check.Equal(new Int32Rect(1800, 500, 120, 580),
            ScreenShot.Intersect(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(1800, 500, 500, 584)),
            "选区跨到副屏 → 主屏只抓相交的那块");
        Check.Equal(new Int32Rect(0, 0, 1920, 1080),
            ScreenShot.Intersect(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(-100, -100, 3000, 3000)),
            "选区比屏幕大 → 取屏幕那一块");
        Check.Equal(Int32Rect.Empty,
            ScreenShot.Intersect(new Int32Rect(0, 0, 1920, 1080), new Int32Rect(1920, 0, 100, 100)),
            "贴边不算相交");
    }

    /// <summary>
    /// 采集历史：保留策略（数量 + 天数）、缩略图尺寸、列表文案。
    /// 全是纯函数——真正落盘的读写由窗口 / 冒烟验证，自检不碰 <c>captures/</c>。
    /// </summary>
    private static void Catalog()
    {
        static CaptureEntry Entry(string id, string createdAt, string kind = CaptureCatalog.KindShot) => new()
        {
            Id = id,
            CreatedAt = createdAt,
            Kind = kind,
            Width = 1200,
            Height = 800,
            Source = "复制",
            State = "done",
        };

        var now = new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);
        var items = new[]
        {
            Entry("a", "2026-11-16T11:00:00"),
            Entry("b", "2026-11-16T10:00:00"),
            Entry("c", "2026-11-15T10:00:00"),
            Entry("d", "2026-11-10T10:00:00"),
            Entry("e", "2026-09-01T10:00:00"),   // 超过 30 天
        };

        var (keep, drop) = CaptureCatalog.PlanPrune(items, now, 3, 30);
        Check.Equal(3, keep.Count, "最多保留 3 条");
        Check.Equal("a,b,c", string.Join(",", keep.Select(e => e.Id)), "按时间倒序保留最新的三条");
        Check.Equal("d,e", string.Join(",", drop.Select(e => e.Id)), "超量 / 超龄都进丢弃");

        var (keepAll, dropOld) = CaptureCatalog.PlanPrune(items, now, 200, 30);
        Check.Equal(4, keepAll.Count, "没超量时只丢超龄的");
        Check.Equal("e", string.Join(",", dropOld.Select(e => e.Id)), "9 月那条超龄");

        var (noLimit, dropNone) = CaptureCatalog.PlanPrune(items, now, 200, 0);
        Check.Equal(0, dropNone.Count, "天数为 0 = 不按天数剪");

        // 缩略图：长边缩到 240，小图不放大
        var big = CaptureCatalog.ThumbnailSize(1200, 800, 240);
        Check.Equal(240, big.Width, "横图缩到长边 240");
        Check.Equal(160, big.Height, "高度按比例");
        var tall = CaptureCatalog.ThumbnailSize(1000, 3000, 240);
        Check.Equal(80, tall.Width, "竖图宽度按比例");
        Check.Equal(240, tall.Height, "竖图缩到长边 240");
        var small = CaptureCatalog.ThumbnailSize(100, 50, 240);
        Check.Equal(100, small.Width, "小图不放大");
        Check.Equal(50, small.Height, "小图高度不变");
        var empty = CaptureCatalog.ThumbnailSize(0, 50, 240);
        Check.Equal(0, empty.Width, "空尺寸返回 0");
        Check.Equal(0, empty.Height, "空尺寸返回 0");

        // 文案
        Check.Equal("截图", CaptureCatalog.KindLabel(CaptureCatalog.KindShot), "shot 的中文名");
        Check.Equal("识别", CaptureCatalog.KindLabel(CaptureCatalog.KindRecognize), "recognize 的中文名");
        Check.Equal("1200 × 800", CaptureCatalog.FormatSize(1200, 800), "尺寸文案");
        Check.Equal("11-16 11:00",
            CaptureCatalog.FormatTime("2026-11-16T11:00:00", new DateTime(2026, 11, 16)), "今年的时间只写月日时分");
        Check.Equal("2025-11-16 11:00",
            CaptureCatalog.FormatTime("2025-11-16T11:00:00", new DateTime(2026, 11, 16)), "跨年补上年份");

        var shot = Entry("s", "2026-11-16T11:00:00");
        Check.Equal("复制", CaptureCatalog.SummaryOf(shot), "截图摘要 = 完成动作");

        var recognize = Entry("r", "2026-11-16T11:00:00", CaptureCatalog.KindRecognize);
        recognize.Text = "第一行文字\r\n第二行";
        Check.Equal("第一行文字", CaptureCatalog.SummaryOf(recognize), "识别摘要 = 第一行");

        recognize.Text = "";
        recognize.State = "pending";
        Check.Equal("识别中…", CaptureCatalog.SummaryOf(recognize), "还没算完的文件说识别中");

        recognize.State = "done";
        Check.Equal("（没有识别到文字）", CaptureCatalog.SummaryOf(recognize), "算完但没字");

        // 长文本截断（列表一行放不下）
        recognize.Text = new string('长', 60);
        Check.True(CaptureCatalog.SummaryOf(recognize).EndsWith("…"), "长文本截断");
    }

    /// <summary>
    /// 贴图：缩放 / 不透明度步进与夹紧、滚轮缩放的光标锚点、出场落点、
    /// 局部显示的内容偏移与取景框尺寸、成组移动的位移夹取。
    /// **光标下的内容不动**是这里最容易被写成「左上角不动」的一条；
    /// 成组移动写成「各自夹」也看不出来，直到拖到屏幕边上组才散架。
    /// </summary>
    private static void Pin()
    {
        Check.Equal(1.1, PinMath.NextZoom(1.0, 120), "滚一格放大 10%");
        Check.Equal(0.9, Math.Round(PinMath.NextZoom(1.0, -120), 4), "反向缩回 10%");
        Check.Equal(PinMath.ZoomMin, PinMath.NextZoom(0.1, -120), "缩到下限就停");
        Check.Equal(PinMath.ZoomMax, PinMath.NextZoom(4.0, 120), "放大到上限就停");
        Check.Equal(1.0, PinMath.NextZoom(1.0, 0), "没有滚动就不动");

        Check.Equal(0.5, Math.Round(PinMath.NextOpacity(0.45, 120), 4), "不透明度一格 5%");
        Check.Equal(PinMath.OpacityMin, PinMath.NextOpacity(0.2, -120), "不透明度下限");
        Check.Equal(PinMath.OpacityMax, PinMath.NextOpacity(1.0, 120), "不透明度上限");
        Check.Equal(1.0, PinMath.NextOpacity(1.0, 0), "没有滚动就不动");

        // 锚点：窗口从 400×300 放大到 800×600，窗口里 (100, 75) 那个点在屏幕上必须还在原处
        var origin = new Point(100, 200);
        var at = new Point(100, 75);
        Point moved = PinMath.AnchoredOrigin(origin, new Size(400, 300), new Size(800, 600), at);
        Check.Equal(0d, moved.X, "放大一倍：左上角左移，光标下的点不动");
        Check.Equal(125d, moved.Y, "纵向同理");
        Check.Equal(origin.X + at.X, moved.X + at.X * 2, "屏幕 X 不变（缩小一半算回去也一样）");

        // 出场落点：物理矩形按屏幕缩放换回 DIP，正好盖住刚截的那块
        var placed = PinMath.PlaceAt(new Rect(200, 100, 400, 300), 2);
        Check.Equal(new Rect(100, 50, 200, 150), placed, "物理矩形 ÷ 缩放 = DIP 落点");
        Check.Equal(1d, PinMath.PlaceAt(new Rect(0, 0, 0, 0), 1).Width, "空区域至少 1 DIP（窗口不能是 0 尺寸）");

        // ============ 局部显示：内容在取景框里平移 ============
        var big = new Size(800, 600);
        var view = new Size(400, 300);

        Check.Equal(new Vector(0, 0), PinMath.ClampPan(new Vector(0, 0), big, view), "内容顶到左上时偏移 0");
        Check.Equal(new Vector(0, 0), PinMath.ClampPan(new Vector(80, 60), big, view),
            "往右 / 下拖不动（会露出左上空白）");
        Check.Equal(new Vector(-400, -300), PinMath.ClampPan(new Vector(-900, -900), big, view),
            "往左 / 上拖到底就停（右下不留空白）");
        Check.Equal(new Vector(-200, -150), PinMath.CenterPan(big, view), "进局部显示先看中间那块");

        var small = new Size(200, 100);
        Check.Equal(new Vector(100, 100), PinMath.ClampPan(new Vector(0, 0), small, view),
            "内容比取景框小就居中");
        Check.Equal(new Vector(100, 100), PinMath.ClampPan(new Vector(-999, -999), small, view),
            "小内容也拖不动（居中钉死）");

        // 换缩放时光标下的内容不动（与 AnchoredOrigin 同一件事，只是挪内容而不是挪窗口）
        Vector anchored = PinMath.ZoomAnchoredPan(new Vector(-200, -150), new Point(200, 150), 1, 2);
        Check.Equal(new Vector(-600, -450), anchored, "放大一倍：锚点下的内容补偿一半偏移");
        Check.Equal(200d, anchored.X + 400 * 2, "取景框中心那个点在屏幕上没动");

        Check.Equal(new Size(800, 600), PinMath.ViewportSize(big, new Rect(0, 0, 1000, 700)),
            "内容装得下就按内容大小");
        Check.Equal(new Size(1000, 700), PinMath.ViewportSize(new Size(3000, 3000), new Rect(0, 0, 1000, 700)),
            "内容比工作区大就按工作区");

        // ============ 成组移动：一个位移，谁都不许被推出桌面 ============
        var desktop = new Rect(0, 0, 1000, 800);
        double margin = PinMath.KeepVisible;

        Check.Equal(new Vector(50, 50), PinMath.ConstrainGroupMove(new Vector(50, 50),
            new[] { new Rect(0, 0, 100, 100) }, desktop, margin), "桌面里随便拖就原样走");

        Check.Equal(960d, PinMath.ConstrainGroupMove(new Vector(2000, 0),
            new[] { new Rect(0, 0, 100, 100) }, desktop, margin).X, "拖到右边界停住（留 40）");

        Check.Equal(-60d, PinMath.ConstrainGroupMove(new Vector(-500, 0),
            new[] { new Rect(0, 0, 100, 100) }, desktop, margin).X, "拖到左边界停住（留 40）");

        // 两张一左一右：整组共用一个位移，右边那张先到就先停，左边那张跟着一起停（相对位置不变）
        var nearLeft = new Rect(0, 0, 100, 100);
        var nearRight = new Rect(900, 0, 100, 100);
        Vector group = PinMath.ConstrainGroupMove(new Vector(500, 0), new[] { nearLeft, nearRight },
            desktop, margin);
        Check.Equal(60d, group.X, "成组移动被最靠边那张卡住");
        Check.Equal(960d, nearRight.Left + group.X, "右边那张正好停在「留 40」的位置");
        Check.Equal(60d, nearLeft.Left + group.X, "左边那张跟着走同一个位移（相对位置不变）");

        Check.Equal(new Vector(10, 10), PinMath.ConstrainGroupMove(new Vector(10, 10),
            Array.Empty<Rect>(), desktop, margin), "一张都没有就没有约束（调用方自己判断要不要动）");

        // ============ 缩略图模式：固定小方块，悬停按钮在小窗口 / 方块里都不给 ============
        Check.Equal(83d, PinMath.ThumbnailSide, "缩略图边长固定 83 DIP（照 Snow Shot）");

        Check.True(PinMath.ShowsHoverButtons(400, 300, false), "正常贴图悬停时给 ✕ / 标注");
        Check.True(PinMath.ShowsHoverButtons(PinMath.HoverButtonMinSide, 80, false),
            "正好卡在最小边长（含）仍给按钮");
        Check.False(PinMath.ShowsHoverButtons(PinMath.HoverButtonMinSide - 1, 80, false),
            "窄一点就不给（按钮会盖住内容）");
        Check.False(PinMath.ShowsHoverButtons(80, PinMath.HoverButtonMinSide - 1, false),
            "矮一点也不给");
        Check.False(PinMath.ShowsHoverButtons(PinMath.ThumbnailSide, PinMath.ThumbnailSide, true),
            "缩略图里一律不给（83 已过尺寸门槛，拦它的是「缩略图」这条）");
    }

    /// <summary>
    /// 实时贴图：源窗口状态判定 / 跟住源窗口里的那一块 / 跟着源窗口挪 / 徽标文案。
    /// </summary>
    /// <remarks>
    /// 关键判据有两条：① 跟的是**窗口坐标里的那一块**（<c>_liveRegion</c> 型的参数），窗口挪到哪儿
    /// <see cref="PinLive.Region"/> 的结果都一样（= 拖源窗口时画面不变样）；② 贴图窗口自己靠
    /// <see cref="LiveAnchor"/> 平移，用户拖它只换「挂」的位置，不改变显示哪一块。
    /// </remarks>
    private static void PinLiveCrop()
    {
        Check.Equal(LiveState.Live, PinLive.StateOf(true, false), "窗口在、没最小化 = 实时");
        Check.Equal(LiveState.Minimized, PinLive.StateOf(true, true), "最小化 = 跳过抓帧");
        Check.Equal(LiveState.Closed, PinLive.StateOf(false, false), "窗口没了 = 关闭贴图");
        Check.True(PinLive.ShouldCapture(LiveState.Live), "实时才抓帧");
        Check.False(PinLive.ShouldCapture(LiveState.Minimized), "最小化时不抓帧");
        Check.False(PinLive.ShouldCapture(LiveState.Closed), "关掉了更不抓");

        Check.Equal("● 实时", PinLive.Badge(LiveState.Live), "徽标：实时");
        Check.Equal("源窗口已最小化", PinLive.Badge(LiveState.Minimized), "徽标：最小化");
        Check.Equal("源窗口已关闭", PinLive.Badge(LiveState.Closed), "徽标：关闭");

        // 跟住的那一块：源窗口 (100,100) 起、400×300 里的 (60,40) 200×150（= 屏幕 160,140 200×150）
        var window = new Int32Rect(100, 100, 400, 300);
        var region = new Int32Rect(60, 40, 200, 150);
        var block = PinLive.Screen(window, region);
        Check.Equal(new Int32Rect(160, 140, 200, 150), block, "那一块在屏幕上的位置 = 窗口左上 + 窗口内偏移");

        // ① 整块都露在窗口里：从 (60,40) 裁，铺到贴图 (0,0)
        Check.Equal(
            new LiveCrop(new Int32Rect(60, 40, 200, 150), new Int32Rect(0, 0, 200, 150)),
            PinLive.Region(window, region),
            "整块可见：从窗口那一块裁、铺满贴图");

        // ② 拖源窗口 / 拖贴图都换不掉内容：同一次截图里「窗口坐标的那一块」是定死的
        var moved = new Int32Rect(350, 230, 400, 300);
        Check.Equal(PinLive.Region(window, region), PinLive.Region(moved, region),
            "源窗口挪到哪儿，裁的都是同一块（拖窗口画面不变样）");

        // ③ 窗口缩到比那块还小：只画露得出来的部分，其余留透明
        Check.Equal(
            new LiveCrop(new Int32Rect(60, 40, 40, 60), new Int32Rect(0, 0, 40, 60)),
            PinLive.Region(new Int32Rect(100, 100, 100, 100), region),
            "窗口比那块小：只画窗口里还看得见的部分");

        // ④ 那块有一部分在窗口外（框选跨出了窗口）：露不出来的边透明，内容按窗口内偏移落位
        Check.Equal(
            new LiveCrop(new Int32Rect(0, 0, 160, 120), new Int32Rect(40, 30, 160, 120)),
            PinLive.Region(window, new Int32Rect(-40, -30, 200, 150)),
            "那块左上在窗口外：贴图对应位置留透明、内容不挪位");

        // ⑤ 整块都在窗口外 / 空矩形：没有可画的东西（调用方交一张全透明画布）
        Check.True(PinLive.Region(window, new Int32Rect(900, 900, 200, 150)) == null, "整块挪到窗口外 = 不画");
        Check.True(PinLive.Region(window, new Int32Rect(0, 0, 0, 0)) == null, "空区块 = 不画");
        Check.True(PinLive.Region(new Int32Rect(100, 100, 0, 0), region) == null, "空窗口矩形 = 不画");

        // ⑥ 跟着源窗口走：锚点相对「那一块」，源窗口挪多少，贴图挪多少，尺寸不变
        var pin = new Int32Rect(160, 140, 200, 150);
        var anchor = LiveAnchor.Of(pin, block);
        Check.Equal(new LiveAnchor(0, 0), anchor, "贴图正好盖在那块上：锚点 = (0,0)");

        Check.Equal(new Int32Rect(410, 270, 200, 150),
            anchor.Place(PinLive.Screen(moved, region), 200, 150),
            "源窗口往右下挪 250 / 130：贴图跟着挪同样的距离");
        Check.Equal(anchor,
            LiveAnchor.Of(new Int32Rect(410, 270, 200, 150), PinLive.Screen(moved, region)),
            "跟完之后锚点不变（再挪一次还是同样的相对位置）");

        // ⑦ 用户自己拖过贴图：新锚点按拖完的位置算，之后继续跟着走（显示内容不受影响）
        var dragged = new Int32Rect(20, 30, 200, 150);
        var retaken = LiveAnchor.Of(dragged, block);
        Check.Equal(new LiveAnchor(-140, -110), retaken, "用户拖完贴图：锚点按拖完的位置重算");
        Check.Equal(new Int32Rect(20 + 250, 30 + 130, 200, 150),
            retaken.Place(PinLive.Screen(moved, region), 200, 150),
            "重定锚点之后源窗口再挪，贴图从新位置继续跟着走");
        Check.Equal(PinLive.Region(window, region), PinLive.Region(moved, region),
            "拖过贴图不改变显示哪一块（画面还是从贴图左上角铺开）");

        // ⑧ 最小化：窗口被挪到 (-32000,-32000) 哨兵位置、尺寸只剩标题栏，不能当基准
        var minimized = new Int32Rect(-32000, -32000, 160, 28);
        var desktop = new Int32Rect(0, 0, 1920, 1080);
        Check.False(PinLive.Trackable(minimized, true), "最小化的窗口矩形不能当「那块在哪」的基准");
        Check.False(PinLive.Trackable(minimized, false, desktop),
            "哨兵矩形本身也不可信（它离桌面十万八千里）");
        Check.False(PinLive.Trackable(new Int32Rect(100, 100, 0, 0), false), "空矩形不可信");
        Check.True(PinLive.Trackable(window, false, desktop), "正常窗口可以当基准");
        Check.True(PinLive.Trackable(new Int32Rect(-200, -100, 400, 300), false, desktop),
            "拖到屏幕外的窗口只要还露着一角就算可信（别把贴边 / 跨屏判成不可信）");

        // 老 bug 的复现：拿哨兵算锚点，贴图会被甩到屏幕外（用户报的「最小化再打开实时贴图没了」）
        var sentinelAnchor = LiveAnchor.Of(pin, PinLive.Screen(minimized, region));
        Check.True(Math.Abs(sentinelAnchor.Place(block, 200, 150).X) > 10000,
            "拿哨兵矩形算锚点会把贴图甩到屏幕外（所以最小化期间必须沿用上一次的好值）");
        // 正确做法：最小化期间 block 沿用上一次的值 → 贴图原地不动，还原之后接着跟
        Check.Equal(pin, anchor.Place(block, 200, 150), "最小化期间沿用旧 block：贴图留在原地");
        var afterRestore = new Int32Rect(300, 260, 400, 300);        // 还原回到别的位置
        Check.Equal(new Int32Rect(360, 300, 200, 150),
            anchor.Place(PinLive.Screen(afterRestore, region), 200, 150),
            "还原之后从原地接着跟（锚点没被哨兵污染）");

        // ⑨ 最小化占位：太小的贴图只留图标（两行字会被裁成一团）
        Check.True(PinLive.CoverShowsText(240, 160), "正常大小的贴图放得下占位文字");
        Check.True(PinLive.CoverShowsText(96, 56), "刚好到门槛也放文字");
        Check.False(PinLive.CoverShowsText(95, 160), "太窄只留图标");
        Check.False(PinLive.CoverShowsText(240, 55), "太矮只留图标");

        // ⑩ 「源窗口没了」只信连续几拍：瞬时读不到就把贴图收掉，正是用户报的
        //    「最小化再打开，实时贴图就自动关闭了」（窗口正在销毁重建的那一拍句柄是失效的）。
        Check.Equal(3, PinLive.GoneTicks, "要连续 3 拍（600ms）都读不到才算真的关了");
        Check.True(PinLive.GoneTicks > 1, "至少两拍：单拍误判不能收贴图");

        // ⑪ 句柄没了要不要改跟同程序的另一个窗口：只认「原地重建」，不是随便挑一个
        var live = new Int32Rect(100, 100, 200, 150);                  // 跟住的那块现在在哪
        Check.True(PinLive.CanSucceed(live, new Int32Rect(90, 90, 260, 200)),
            "原地重建的同尺寸窗口可以接替（整块都在里面）");
        Check.True(PinLive.CanSucceed(live, new Int32Rect(150, 150, 400, 300)),
            "盖住一半以上也接（窗口重建后可能挪了一点）");
        Check.True(!PinLive.CanSucceed(live, new Int32Rect(100, 100, 60, 40)),
            "太小、只盖住一小块的不能接");
        Check.True(!PinLive.CanSucceed(live, new Int32Rect(900, 700, 300, 200)),
            "同程序里另一个不相干的窗口不能接（否则关掉一个 Chrome 窗口会跟到别的窗口上）");
        Check.Equal(0L, PinLive.Overlap(live, new Int32Rect(400, 400, 50, 50)), "不相交 = 0");
        Check.Equal(10000L, PinLive.Overlap(live, new Int32Rect(150, 150, 100, 100)),
            "相交面积只算重叠那部分（100×100）");
    }

    /// <summary>
    /// 贴图分组与批量：组名清洗 / 忽略大小写判重、批量另存落盘（不覆盖同名）。
    /// </summary>
    private static void PinGrouping()
    {
        Check.Equal("默认", PinGroups.Default, "默认组名");
        Check.Equal(16, PinGroups.MaxName, "组名最多 16 字");

        Check.Equal("外部", PinGroups.Clean("  外部  "), "去首尾空白");
        Check.Equal("外部 素材", PinGroups.Clean("外部   素材"), "内部连续空白压成一个");
        Check.True(PinGroups.Clean("   ") == null, "全是空白 = 没有名字");
        Check.True(PinGroups.Clean("") == null, "空串 = 没有名字");
        Check.True(PinGroups.Clean(null) == null, "null = 没有名字");
        Check.Equal("一二三四五六七八九十一二三四五六", PinGroups.Clean("一二三四五六七八九十一二三四五六七八"),
            "超长截到 16 字");

        Check.True(PinGroups.Same("External", "external"), "判重忽略大小写");
        Check.False(PinGroups.Same("外部", "外部2"), "不同名不是一组");

        var groups = new[] { "默认", "外部", "素材" };
        Check.Equal("外部", PinGroups.Find("外部", groups), "查到就返回它原本的写法");
        Check.Equal("素材", PinGroups.Find(" 素材 ", groups), "传进来的名字先清洗，带空白也认得出");
        Check.Equal("外部", PinGroups.Find("外部", new[] { "默认", "外部" }), "多个组里挑对那个");
        Check.True(PinGroups.Find("不存在", groups) == null, "没有就是 null");
        Check.True(PinGroups.Find(null, groups) == null, "null 不匹配任何组");

        // 批量另存：一个文件夹里逐个写 png，同名不覆盖
        string dir = Path.Combine(Path.GetTempPath(), "ltools-selftest-pin-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var first = NewPage(4);                    // 16×4
            var second = NewPage(6);                   // 16×6

            var saved = CaptureSave.SaveManyTo(dir, new[] { first, second });
            Check.Equal(2, saved.Count, "两张各写一个文件");
            Check.True(saved.All(File.Exists), "文件真的落盘了");
            Check.True(saved.All(Path.IsPathRooted), "返回的是完整路径");
            Check.True(saved[0].EndsWith("-1.png", StringComparison.Ordinal) &&
                       saved[1].EndsWith("-2.png", StringComparison.Ordinal), "按序号命名");

            using (var stream = File.OpenRead(saved[0]))
            {
                var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check.Equal(16, decoded.PixelWidth, "写出来的是 png（宽 16）");
                Check.Equal(4, decoded.PixelHeight, "第一张高 4");
            }

            using (var stream = File.OpenRead(saved[1]))
            {
                var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Check.Equal(6, decoded.PixelHeight, "第二张高 6（不是把同一张写两遍）");
            }

            var again = CaptureSave.SaveManyTo(dir, new[] { first });
            Check.True(again[0] != saved[0], "同一秒再存一次自动错开名字");
            Check.True(File.Exists(saved[0]) && File.Exists(again[0]), "前一张没被覆盖");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { /* 清理失败不影响断言 */ }
        }
    }

    /// <summary>
    /// 工具栏布局：一格多工具（并到左边 / 拆出来）、收起与放回、落盘往返、脏数据与新工具。
    /// </summary>
    private static void ToolLayouts()
    {
        int all = ToolCatalog.All.Length;

        var def = ToolLayout.Default();
        Check.Equal(13, def.Groups.Count,
            "默认布局：照 Snow Shot 分成 13 格（选择 / 形状 / 箭直 / 自由笔 / 尺寸 / 气泡文字 / 荧光 / 镜头 / 序号 / 打码 / 水印 / 橡皮 / 取色）");
        Check.Equal(all, def.Groups.SelectMany(g => g).Distinct().Count(), "每个工具都在某一格里（格数少但一个都不少）");
        Check.Equal(0, def.HiddenTools.Count, "默认布局没有收起来的工具");
        Check.Equal("Select", def.ToLines()[0], "第一格是「选择」");
        Check.Equal("Shape", def.ToLines()[1], "第二格是形状（矩形 / 椭圆 / 菱形共用一个工具）");
        Check.Equal("Arrow,Line", def.ToLines()[2], "第三格是箭头与直线");
        Check.Equal("Polyline,Pen", def.ToLines()[3], "第四格是自由笔（折线 / 画笔）");
        Check.Equal("Distance,Angle,Bracket", def.ToLines()[4], "第五格是标注尺寸（测距 / 测角 / 括号）");
        Check.Equal("SpeechBubble,Text", def.ToLines()[5], "第六格是气泡与文字");
        Check.Equal("Highlighter,HighlightRect", def.ToLines()[6], "第七格是两种荧光");
        Check.Equal("Spotlight,Magnifier", def.ToLines()[7], "第八格是聚光与放大镜");
        Check.Equal("Mosaic,AutoFilter,Filter,BrushFilter", def.ToLines()[9], "打码与滤镜同格（马赛克 / 自动打码 / 矩形 / 画笔）");
        Check.Equal("Watermark", def.ToLines()[10], "水印单独一格");
        Check.Equal("Eraser,RectEraser,BrushEraser", def.ToLines()[11], "三把橡皮同格");
        Check.Equal("Picker", def.ToLines()[12], "最后一格是取色");

        // 并到左边一格：格内顺序保持原样，第一格不再动
        var merge = def.MergeLeft(CaptureTool.Pen);          // 「折线 / 画笔」那一格并进「箭头 / 直线」
        Check.Equal(12, merge.Groups.Count, "并到左边一格：少一格");
        Check.Equal(CaptureTool.Select, merge.Groups[0][0], "第一格还是「选择」");
        Check.True(merge.Groups[2].SequenceEqual(new[]
            { CaptureTool.Arrow, CaptureTool.Line, CaptureTool.Polyline, CaptureTool.Pen }),
            "整格并进左边那格（顺序不变）");
        Check.True(merge.MergeLeft(CaptureTool.Select).SameAs(merge), "第一格左边没东西：并过去什么都不变");

        // 拆出来：回到默认的格数与位置
        var split = merge.Split(CaptureTool.Pen);
        Check.Equal(13, split.Groups.Count, "拆成单独一格：格子数回到默认");
        Check.Equal(1, split.Groups[3].Count, "拆出来的格子里只有它自己");
        Check.Equal(CaptureTool.Pen, split.Groups[3][0], "拆出来的格子落在原位置");

        // 收起 / 放回（拿单独一格的「取色」试：整格消失）
        var hidden = def.Hide(CaptureTool.Picker);
        Check.True(hidden.IsHidden(CaptureTool.Picker), "收起「取色」");
        Check.Equal(-1, hidden.CellOf(CaptureTool.Picker), "收起后它不在任何格子里");
        Check.Equal(12, hidden.Groups.Count, "收起一个单格工具就少一格");
        Check.Equal("Picker", hidden.ToHidden()[0], "落盘写的是工具键");

        var back = hidden.Insert(CaptureTool.Picker);
        Check.False(back.IsHidden(CaptureTool.Picker), "放回去就不再是收起的");
        Check.Equal(13, back.Groups.Count, "放回去还是单独一格");
        Check.Equal(1, back.Groups[back.CellOf(CaptureTool.Picker)].Count, "放回去单独占一格");
        Check.True(back.CellOf(CaptureTool.Picker) < back.CellOf(CaptureTool.Spotlight),
            "放回去落在目录顺序里它该在的位置（聚光前面）");

        // 多工具格里收起一个：格子还在，只是少一个
        var trimmed = def.Hide(CaptureTool.Eraser);
        Check.Equal(13, trimmed.Groups.Count, "多工具格收起一个：格子保留");
        Check.Equal(2, trimmed.Groups[11].Count, "那一格剩两把橡皮");

        // 落盘往返：写出去再读回来必须是同一份
        Check.True(ToolLayout.Parse(merge.ToLines(), merge.ToHidden()).SameAs(merge),
            "落盘再读回来是同一份布局");
        Check.Equal("Select", merge.ToLines()[0], "一行一格");
        Check.Equal("Shape", merge.ToLines()[1], "格内就是形状工具本身");

        // 目录里新增、两处都没提到的工具：自动补回来（不许被老布局一直藏着）
        var lines = def.ToLines().Where(l => !l.Contains("Eraser", StringComparison.Ordinal)).ToList();
        var grown = ToolLayout.Parse(lines, def.ToHidden());
        Check.False(grown.IsHidden(CaptureTool.Eraser), "老布局里没有的新工具自动补回（可见）");
        Check.Equal(lines.Count + 3, grown.Groups.Count, "补回来的三把橡皮各占单独一格");

        // 脏数据：未知键丢掉、空行丢掉、同格重复只留一次；其余工具按默认补回
        var dirty = ToolLayout.Parse(new List<string> { "pen,pen", "nope,select", "" }, null);
        int penCell = dirty.CellOf(CaptureTool.Pen);
        Check.Equal(1, dirty.Groups[penCell].Count, "同一格里的重复工具只留一次");
        Check.Equal(penCell + 1, dirty.CellOf(CaptureTool.Select), "未知键「nope」不会变成一格（选择仍在画笔右边）");
        Check.Equal(all, dirty.Groups.SelectMany(g => g).Distinct().Count(), "其余工具按默认补回（一个工具都不会丢）");
        Check.True(ToolLayout.Parse(dirty.ToLines(), dirty.ToHidden()).SameAs(dirty),
            "脏数据修好之后往返稳定");

        // 大小写不敏感（手工改过配置也别崩）
        var lower = ToolLayout.Parse(new List<string> { "PEN,Highlighter" }, null);
        int at = lower.CellOf(CaptureTool.Pen);
        Check.True(at >= 0 && lower.Groups[at].SequenceEqual(new[] { CaptureTool.Pen, CaptureTool.Highlighter }),
            "工具键大小写不敏感（同一格仍在同一格）");
    }

    /// <summary>
    /// 识别：词框 → 近似表格（行 / 列聚类、中文逐字拼接、TSV / HTML），
    /// 以及「引擎名字」与「成品位图转 GDI+」这两处胶水。
    /// </summary>
    private static void Recognize()
    {
        // 词框只关心「横向位置 + 宽度」，纵向由 Line 统一给（模拟同一行）
        static OcrWordBox Word(string text, int x, int w = 60) =>
            new() { Text = text, X = x, Y = 0, W = w, H = 30 };

        static OcrLineBox Line(int y, params OcrWordBox[] words)
        {
            var placed = words
                .Select(w => new OcrWordBox { Text = w.Text, X = w.X, Y = y, W = w.W, H = w.H })
                .ToArray();

            return new OcrLineBox
            {
                Text = string.Join(" ", words.Select(w => w.Text)),
                X = placed[0].X, Y = y, W = 300, H = 30,
                Confident = true,
                Words = placed,
            };
        }

        // 2 行 × 2 列（列间距 10 以上才会分成两列）
        var table = OcrTable.Build(new[]
        {
            Line(0, Word("名称", 10), Word("数量", 130)),
            Line(40, Word("苹果", 10), Word("12", 130)),
        });

        Check.True(table.HasTable, "2 行 2 列算表格");
        Check.Equal(2, table.RowCount, "两行");
        Check.Equal(2, table.Columns, "两列");
        Check.Equal("名称", table.Rows[0][0], "第一行第一列");
        Check.Equal("数量", table.Rows[0][1], "第一行第二列");
        Check.Equal("苹果", table.Rows[1][0], "第二行第一列");
        Check.Equal("12", table.Rows[1][1], "第二行第二列");
        Check.Equal("名称\t数量\r\n苹果\t12", table.ToTsv(), "TSV：单元格制表符、行换行");
        Check.True(table.ToHtml().Contains("<td>苹果</td>"), "HTML 单元格");
        Check.True(table.ToHtml().StartsWith("<table>"), "HTML 以 <table> 开头");

        // 单列 / 单行只是普通文字，不该硬排成表格
        var column = OcrTable.Build(new[]
        {
            Line(0, Word("上", 10)),
            Line(40, Word("下", 10)),
        });
        Check.Equal(1, column.Columns, "同一列只有一个格子列");
        Check.False(column.HasTable, "单列不算表格");

        var single = OcrTable.Build(new[] { Line(0, Word("只有一行", 10)) });
        Check.False(single.HasTable, "单行不算表格");

        // 中文逐字框要贴在一起（间距小于字高），英文词之间有空格才补空格
        var join = OcrTable.Build(new[]
        {
            Line(0, Word("中", 0, 28), Word("文", 30, 28)),
            Line(40, Word("a", 0, 10), Word("b", 26, 10)),
        });
        Check.Equal("中文", join.Rows[0][0], "相邻中文字框直接拼接（不加空格）");

        // 没有词框时把整行当一个格子（系统 OCR 偶尔只给行）
        var fallback = OcrTable.Build(new[]
        {
            new OcrLineBox { Text = "第一行", X = 0, Y = 0, W = 120, H = 30, Confident = true },
            new OcrLineBox { Text = "第二行", X = 0, Y = 40, W = 120, H = 30, Confident = true },
        });
        Check.Equal(2, fallback.RowCount, "整行退化成格子");
        Check.Equal("第一行", fallback.Rows[0][0], "整行文本原样进格子");

        Check.False(OcrTable.Build(Array.Empty<OcrLineBox>()).HasTable, "没有行 → 没有表格");
        Check.Equal("没有表格", OcrTable.Describe(TableGrid.Empty), "空表格的描述");

        // HTML 转义（截图里的表格可能真有尖括号）
        var escape = OcrTable.Build(new[]
        {
            Line(0, Word("<a&b>", 10), Word("x", 130)),
            Line(40, Word("y", 10), Word("z", 130)),
        });
        Check.True(escape.ToHtml().Contains("&lt;a&amp;b&gt;"), "HTML 转义 & < >");

        // 引擎名字（与设置页下拉的叫法一致）
        Check.Equal("系统 OCR", CaptureRecognize.EngineLabel(null), "null = 系统 OCR");
        Check.Equal("系统 OCR", CaptureRecognize.EngineLabel("windows"), "windows = 系统 OCR");
        Check.Equal("PP-OCRv6 快速", CaptureRecognize.EngineLabel("v6tiny"), "v6tiny 的显示名");
        Check.Equal("PP-OCRv6 精确", CaptureRecognize.EngineLabel("v6small"), "v6small 的显示名");

        // 成品位图 → GDI+ 位图（OCR 与二维码两套底层都吃 Bitmap）
        var pixels = new byte[4 * 3 * 4];
        var image = BitmapSource.Create(4, 3, 96, 96, PixelFormats.Bgra32, null, pixels, 16);
        image.Freeze();
        using (var bitmap = CaptureRecognize.ToBitmap(image))
        {
            Check.Equal(4, bitmap.Width, "转出来的宽不变");
            Check.Equal(3, bitmap.Height, "转出来的高不变");
        }
    }

    /// <summary>
    /// 长截图的滚动位移识别（ORB 特征互匹配 + SSIM 候选打分 + 补丁细化）：
    /// 把「整页内容」按不同位置截两帧，位移必须认得出；没动 = 0，内容完全对不上 = 认不出（不瞎接）。
    /// 位移符号与 Snow Shot 一致：**负数 = 往下滚**（内容朝上走）。
    /// </summary>
    private static void ScrollDetection()
    {
        var page = NewScrollPage(420);

        Check.Equal(-50, ShiftOf(Frame(page, 0), Frame(page, 50)), "往下滚 50 行");
        Check.Equal(-5, ShiftOf(Frame(page, 0), Frame(page, 5)), "小幅位移 5 行");
        Check.Equal(0, ShiftOf(Frame(page, 0), Frame(page, 0)), "没动 = 0");
        Check.Equal(-130, ShiftOf(Frame(page, 0), Frame(page, 130)), "大位移 130 行（一次滚过大半屏）");

        // 两页毫无关系的内容：认不出（null）或没动（0）都不接，绝不能把两张无关的图接起来
        int? unrelated = ShiftOf(Frame(NewScrollPage(242, seed: 7), 0), Frame(NewScrollPage(242, seed: 99), 0));
        Check.True(unrelated is null or 0, $"内容对不上 → 不接（实际 {unrelated?.ToString() ?? "null"}）");

        // 帧尺寸变了（换了窗口 / 改了选区）也认不出
        var stitch = new ScrollStitch();
        stitch.Start(Frame(page, 0));
        Check.True(stitch.Append(CropHeight(Frame(page, 0), 200)) is null, "高度不一致 → 认不出");
        Check.Equal(ViewHeight, stitch.Extent, "认不出的帧不会接进画布");

        // 底下一截固定页脚（滚动时不动，但太矮、够不上固定条门槛）：照样认得出位移
        var a = PaintBand(Frame(page, 20), ViewHeight - 4, 4, 0x10);
        var b = PaintBand(Frame(page, 50), ViewHeight - 4, 4, 0x10);
        Check.Equal(-30, ShiftOf(a, b), "页脚不动也能认出位移");

        // 中间一段在动（视频 / 光标闪烁 / 加载中的图）：按候选打分，照样认得位移
        var animated = PaintBand(Frame(page, 60), ViewHeight / 3, 20, 0xFF);
        Check.Equal(-60, ShiftOf(Frame(page, 0), animated), "一段画面在动也能认出位移");
    }

    /// <summary>把两帧喂给新的拼接器，返回第二帧的位移（<c>null</c> = 认不出）。</summary>
    private static int? ShiftOf(BitmapSource first, BitmapSource next)
    {
        var stitch = new ScrollStitch();
        stitch.Start(first);
        return stitch.Append(next);
    }

    /// <summary>
    /// 长截图：拼接只接新露出来的行、往回滚不变长、预览按比例缩、上限会自己停；面板优先落在选区外面。
    /// </summary>
    private static void ScrollBuild()
    {
        var page = NewScrollPage(520);
        var stitch = new ScrollStitch();
        stitch.Start(Frame(page, 0));

        Check.Equal(ViewHeight, stitch.Extent, "起点 = 第一帧高度");
        Check.Equal(ScrollWidth, stitch.OutputWidth, "宽度 = 帧宽");
        Check.Equal(-50, stitch.Append(Frame(page, 50)) ?? 1, "第二帧位移 50（往下滚是负数）");
        Check.Equal(ViewHeight + 50, stitch.Extent, "只接了新露出来的 50 行");
        Check.Equal(-40, stitch.Append(Frame(page, 90)) ?? 1, "第三帧位移 40");
        Check.Equal(ViewHeight + 90, stitch.Extent, "再接 40 行");
        Check.Equal(0, stitch.Append(Frame(page, 90)) ?? 1, "重复帧（到底）位移 0");
        Check.Equal(ViewHeight + 90, stitch.Extent, "没动就不接");

        // 往回滚 10 行：内容已经在画布里（Contained），画布不变长
        Check.Equal(10, stitch.Append(Frame(page, 80)) ?? 1, "往回滚 10 行");
        Check.Equal(ViewHeight + 90, stitch.Extent, "往回滚不改成品长度");

        // 再往下 10 行回到原处：还是落在画布里，不变长
        Check.Equal(-10, stitch.Append(Frame(page, 90)) ?? 1, "再往下 10 行回到原处");
        Check.Equal(ViewHeight + 90, stitch.Extent, "回到原处仍不变长");

        var longImage = stitch.Render();
        Check.Equal(ViewHeight + 90, longImage.PixelHeight, "拼出来的长图高度");
        Check.Equal(ScrollWidth, longImage.PixelWidth, "拼出来的长图宽度");

        // 每一行都是「整页」对应位置的那一行（帧全是同一页的裁切，没有任何偏差）
        for (int y = 0; y < longImage.PixelHeight; y += 37)
            Check.True(RowAt(longImage, y).SequenceEqual(RowAt(page, y)), $"第 {y} 行与整页一致");
        Check.True(RowAt(longImage, longImage.PixelHeight - 1)
                .SequenceEqual(RowAt(page, longImage.PixelHeight - 1)), "最后一行与整页一致");

        var preview = stitch.Preview(30, 200);
        Check.True(preview.PixelWidth <= 30 && preview.PixelHeight <= 200, "预览装进框里");
        Check.True(preview.PixelHeight >= 100, $"预览按比例缩小（{longImage.PixelHeight} 行 → {preview.PixelHeight}）");

        // 就地预览取的是拼接图的**末尾若干行**（不是整张长图重画）
        var tail = stitch.Tail(100);
        Check.Equal(100, tail.PixelHeight, "末尾一屏按行取");
        Check.Equal(ScrollWidth, tail.PixelWidth, "末尾一屏的宽不变");
        Check.Equal(ViewHeight + 90, stitch.Tail(longImage.PixelHeight).PixelHeight, "要的行比拼接图还多 → 给整张");

        Check.True(RowAt(tail, tail.PixelHeight - 1)
                .SequenceEqual(RowAt(longImage, longImage.PixelHeight - 1)), "末尾一屏的最后一行 = 长图的最后一行");

        // 高度上限：到顶就 Full，调用方据此收尾（一直滚下去也不会吃满内存）
        var tall = NewScrollPage(900);
        var limited = new ScrollStitch(StitchAxis.Vertical, 600);
        limited.Start(Frame(tall, 0));
        for (int offset = 100; !limited.Full && offset <= 600; offset += 100)
        {
            if (limited.Append(Frame(tall, offset)) is null) break;
        }
        Check.True(limited.Full, "滚到长度上限后 Full = true");
        Check.True(limited.Extent >= 600, $"停在 {limited.Extent} 行（上限 600）");

        // 小面板落点（画在覆盖层窗口里）：右侧放得下就放右侧，都放不下塞右下角
        var view = new Size(1920, 1040);
        var bar = new Size(400, 320);
        var inRight = ScrollLayout.PlaceIn(new Rect(100, 100, 800, 600), bar, view);
        Check.True(inRight.Left >= 900, $"候选条放选区右侧（Left={inRight.Left:0}）");
        Check.Equal(100d, inRight.Top, "与选区顶边对齐");

        var inBelow = ScrollLayout.PlaceIn(new Rect(0, 0, 400, 300), bar, new Size(420, 1040));
        Check.Equal(0d, inBelow.Left, "右侧不够 → 放到下方");
        Check.Equal(312d, inBelow.Top, "下方留出间距");

        var inClamp = ScrollLayout.PlaceIn(new Rect(0, 0, 1920, 1040), bar, view);
        Check.True(inClamp.Right <= 1920 && inClamp.Bottom <= 1040, "窗口放满 → 夹回右下角");
    }

    /// <summary>
    /// 长截图：页底那条固定条（置顶 / 置底按钮条）只出现一次——认出后每帧先剪掉末尾那几线、成品末尾留一条，
    /// `Extent` 一直把这一截算在内、`Finish` 幂等；纯色空白（只是空白、不是固定条）不能被当成固定条剪掉。
    /// </summary>
    private static void ScrollSticky()
    {
        const int footer = 30;
        var page = NewScrollPage(900);

        var stitch = new ScrollStitch();
        var first = FooterFrame(page, 0, footer);
        stitch.Start(first);
        Check.Equal(0, stitch.StickyBottom, "首帧还不知道有没有固定条");

        var second = FooterFrame(page, 60, footer);
        Check.Equal(-60, stitch.Append(second) ?? 1, "有固定页脚也认得出位移");
        Check.Equal(footer, stitch.StickyBottom, "认出 30 行的固定页脚");
        Check.Equal(ViewHeight + 60, stitch.Extent, "首帧尾部那条已经剪掉，新内容照接（成品末尾仍算固定条）");

        var third = FooterFrame(page, 120, footer);
        Check.Equal(-60, stitch.Append(third) ?? 1, "第三帧位移 60");
        Check.Equal(ViewHeight + 120, stitch.Extent, "每一帧只接固定条上面那一截");

        var footerRow = RowAt(third, ViewHeight - 10);
        Check.Equal(1, CountRows(stitch.Render(), footerRow), "拼到一半时固定条只出现一次（成品末尾）");

        stitch.Finish();
        Check.Equal(ViewHeight + 120, stitch.Extent, "Finish 幂等：固定条已经算在里面，不会再补一条");

        var finished = stitch.Render();
        Check.Equal(1, CountRows(finished, footerRow), "整张长图里固定条只出现一次");
        Check.True(RowAt(finished, finished.PixelHeight - 1).SequenceEqual(RowAt(third, ViewHeight - 1)),
            "最下面那行就是固定条");

        stitch.Finish();
        Check.Equal(ViewHeight + 120, stitch.Extent, "重复调用 Finish 不改长度");

        // 整片纯色的固定条（比如一条纯色底、没有字的页脚）：认不出「有信息」的行，
        // 宁可中间重复一遍也不剪内容（剪错的代价比重复大）
        var plain = new ScrollStitch();
        plain.Start(FooterFrame(page, 0, footer, flat: true));
        Check.Equal(-60, plain.Append(FooterFrame(page, 60, footer, flat: true)) ?? 1,
            "纯色页脚在也不影响位移检测");
        Check.Equal(0, plain.StickyBottom, "纯色页脚不当固定条");
        Check.Equal(ViewHeight + 60, plain.Extent, "没剪内容");
    }

    /// <summary>
    /// 长截图：横向拼接。同一条「按特征对齐」的算法，把「沿轴」换成**列**——
    /// 横向平移对纵向拼接器完全不可见，横向才认得出。
    /// </summary>
    private static void ScrollHorizontal()
    {
        var page = NewWideScrollPage(520);

        // 把横向平移的帧喂给纵向拼接器：认不出（这正是「横向必须单独一个方向」的理由）
        var vertical = new ScrollStitch();
        vertical.Start(ColFrame(page, 0));
        int? sideways = vertical.Append(ColFrame(page, 50));
        Check.True(sideways is null or 0, $"横向平移 → 纵向拼接器不接（实际 {sideways?.ToString() ?? "null"}）");

        // 拼接器：横向模式拼出来的是「更宽的图」，只接新露出来的列
        var stitch = new ScrollStitch(StitchAxis.Horizontal);
        Check.Equal(StitchAxis.Horizontal, stitch.Axis, "方向 = 横向");
        stitch.Start(ColFrame(page, 0));
        Check.Equal(ViewHeight, stitch.Extent, "横向起点 = 一屏的宽");
        Check.Equal(ViewHeight, stitch.OutputWidth, "横向拼图的宽 = 已接的列数");
        Check.Equal(ViewHeight, stitch.OutputHeight, "横向拼图的高 = 帧高");

        Check.Equal(-50, stitch.Append(ColFrame(page, 50)) ?? 1, "横向第二帧位移 50（往右滚是负数）");
        Check.Equal(ViewHeight + 50, stitch.Extent, "横向只接新露出来的 50 列");
        Check.Equal(-40, stitch.Append(ColFrame(page, 90)) ?? 1, "横向第三帧位移 40");
        Check.Equal(ViewHeight + 90, stitch.Extent, "横向再接 40 列");

        var wideImage = stitch.Render();
        Check.Equal(ViewHeight + 90, wideImage.PixelWidth, "横向拼图的宽");
        Check.Equal(ViewHeight, wideImage.PixelHeight, "横向拼图的高");

        // 拼出来的像素要真的来自页面：每一列都对得上整页的对应列
        for (int x = 0; x < wideImage.PixelWidth; x += 41)
            Check.True(ColumnAt(wideImage, x).SequenceEqual(ColumnAt(page, x)), $"第 {x} 列与整页一致");
        Check.True(ColumnAt(wideImage, wideImage.PixelWidth - 1)
                .SequenceEqual(ColumnAt(ColFrame(page, 90), ViewHeight - 1)),
            "最右一列 = 最后一帧的最后一列");

        // 就地预览：横向取的是「末尾若干列」，尺寸按宽高各自缩
        var tail = stitch.Tail(100);
        Check.Equal(100, tail.PixelWidth, "横向末尾一屏按列取");
        Check.Equal(ViewHeight, tail.PixelHeight, "横向末尾一屏的高不变");

        var preview = stitch.Preview(300, 40);
        Check.True(preview.PixelWidth <= 300 && preview.PixelHeight <= 40, "横向预览装进框里");

        // 尺寸变了（换了窗口）认不出
        Check.True(stitch.Append(ColFrame(page, 90, ViewHeight - 20)) == null, "横向帧尺寸变了 → 认不出");
    }

    /// <summary>
    /// 自动滚动的节奏：实测成本越大间隔越长、连续拼不上会退避、接上了立刻恢复、
    /// 上下限夹住（别快过页面落定、也别慢成「一格格挪」）。
    /// </summary>
    private static void ScrollCadenceRules()
    {
        var cadence = new ScrollCadence();

        // 还没有样本：就用用户 / 默认节奏
        Check.Equal(ScrollCadence.MinIntervalMs, cadence.NextInterval(0, 0),
            "没有样本、没有基准 → 夹到下限");
        Check.Equal(200, cadence.NextInterval(200, 0), "没有样本 → 用基准节奏");
        Check.Equal(500, cadence.NextInterval(200, 500), "落定时间比基准还长 → 按落定走");

        // 抓帧慢（160ms × 1.25 = 200）比基准慢：取成本那一侧
        cadence.Reset();
        cadence.RecordCapture(160);
        Check.Equal(200, cadence.NextInterval(50, 0), "成本高于基准 → 按成本走");
        Check.Equal(250, cadence.NextInterval(250, 0), "基准高于成本 → 按基准走");

        // 成本算出来比下限还快：夹到下限（页面来不及落定，滚太快反而拼不上）
        cadence.Reset();
        cadence.RecordCapture(20);
        Check.Equal(ScrollCadence.MinIntervalMs, cadence.NextInterval(0, 0), "成本很低 → 夹到下限");

        // 拼接更慢时按拼接算（两者串行，瓶颈取大的）
        cadence.RecordStitch(400);
        Check.Equal(500, cadence.NextInterval(0, 0), "取抓帧与拼接里更慢的那个");

        // EWMA：第二次采样只挪 25%
        cadence.Reset();
        cadence.RecordCapture(100);
        cadence.RecordCapture(200);
        Check.True(cadence.CaptureMs > 100 && cadence.CaptureMs < 200,
            $"EWMA 平滑（100 → 200 之间，实际 {cadence.CaptureMs:0.#}）");

        // 退避：连续拼不上，间隔逐档变长
        cadence.Reset();
        int plain = cadence.NextInterval(200, 0);
        cadence.NoteProgress(false);
        int once = cadence.NextInterval(200, 0);
        cadence.NoteProgress(false);
        int twice = cadence.NextInterval(200, 0);
        Check.True(once > plain, $"一档退避更慢（{plain} → {once}）");
        Check.True(twice > once, $"两档退避更慢（{once} → {twice}）");

        // 接上了就立刻恢复
        cadence.NoteProgress(true);
        Check.Equal(0, cadence.Penalty, "接上内容 → 退避清零");
        Check.Equal(plain, cadence.NextInterval(200, 0), "退避清零 → 回到原节奏");

        // 退避有上限（再多也不会一直翻倍）
        cadence.Reset();
        for (int i = 0; i < 20; i++) cadence.NoteProgress(false);
        Check.Equal(ScrollCadence.MaxPenalty, cadence.Penalty, "退避最多累计几档");

        // 上下限
        cadence.Reset();
        cadence.RecordCapture(20000);
        Check.Equal(ScrollCadence.MaxIntervalMs, cadence.NextInterval(200, 0), "成本极大 → 夹到上限");

        cadence.Reset();
        cadence.RecordCapture(1);
        Check.True(cadence.NextInterval(0, 0) >= ScrollCadence.MinIntervalMs,
            "无论怎么算都不会快过下限");

        // 非法样本不影响节奏
        cadence.Reset();
        cadence.RecordCapture(double.NaN);
        cadence.RecordCapture(-5);
        Check.Equal(0d, cadence.CaptureMs, "NaN / 负数样本被忽略");
        Check.Equal(200, cadence.NextInterval(200, 0), "没有有效样本 → 仍是基准节奏");
    }

    /// <summary>
    /// 屏幕选字的候选切分：按句末标点、逗号、长度上限逐级切，空白与完全重复的候选丢掉；
    /// 切完的字符总数不变（不许「切着切着少字」）。
    /// </summary>
    private static void ScreenTextSplit()
    {
        var sentences = ScreenTextCandidates.Build(new[] { "第一句话。第二句话！", "  ", "第三行" });
        Check.Equal(3, sentences.Count, "空白行丢掉");
        Check.Equal("第一句话。", sentences[0], "按句末标点切开、标点留在句尾");
        Check.Equal("第二句话！", sentences[1], "第二句照切");
        Check.Equal("第三行", sentences[2], "没有标点的整行就是一条");

        var burst = ScreenTextCandidates.Build(new[] { "真的吗？！不会吧。" });
        Check.Equal("真的吗？！", burst[0], "连续标点算同一句的结尾");
        Check.Equal("不会吧。", burst[1], "后半句照切");

        var dup = ScreenTextCandidates.Build(new[] { "重复", "重复", "不同" });
        Check.Equal(2, dup.Count, "完全相同的候选只留一条");
        Check.Equal("重复", dup[0], "先出现的留着");

        // 超长：先按逗号切，仍超长就硬切；切完不丢字（内容互不相同，免得撞上「相同候选去重」）
        var longLine = new string(Enumerable.Range(0, 300).Select(i => (char)(0x4E00 + i)).ToArray()) + "，后半句";
        var pieces = ScreenTextCandidates.Build(new[] { longLine }, 50);
        Check.True(pieces.Count >= 6, $"超长行切成了 {pieces.Count} 条");
        Check.True(pieces.All(p => p.Length <= 50), $"每条不超过上限（最长 {pieces.Max(p => p.Length)}）");
        Check.Equal(304, pieces.Sum(p => p.Length), "切完字符总数不变");

        Check.Equal(0, ScreenTextCandidates.Build(Array.Empty<string>()).Count, "没有文字 → 没有候选");
        Check.Equal(0, ScreenTextCandidates.Build(new[] { "   ", "\t" }).Count, "只有空白 → 没有候选");
        Check.Equal(1, ScreenTextCandidates.Build(new[] { "a,b" }, 50).Count, "逗号只在超长时才切");
    }

    /// <summary>长截图的「屏幕」高度：一次看到多少行。</summary>
    private const int ViewHeight = 240;

    /// <summary>合成一页「整页内容」：每行颜色固定，按 offset 截出来就是滚动中的某一帧。</summary>
    private static BitmapSource NewPage(int height, int seed = 0)
    {
        const int width = 16;
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = (byte)((y * 7 + seed) & 0xFF);
                pixels[i + 1] = (byte)((x * 11 + y * 3) & 0xFF);
                pixels[i + 2] = (byte)((y * 13) & 0xFF);
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>长截图合成帧的宽度（200 像素：ORB 的特征点要离边缘 31 像素，太窄会一个点都找不到）。</summary>
    private const int ScrollWidth = 200;

    /// <summary>
    /// 合成一页「整页内容」：每 4×4 像素一块，灰度由 (x/4, y/4, seed) 的哈希决定。
    /// 与 snow-shot 自带的合成帧（<c>stitcher_cache_tests.rs</c> 的 <c>texture</c>）同一个做法：
    /// 4 像素粒度的纹理上 ORB 特征最密，按 offset 裁出来就是滚动中的某一帧。
    /// </summary>
    private static BitmapSource NewScrollPage(int height, int seed = 0)
    {
        int stride = ScrollWidth * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < ScrollWidth; x++)
            {
                int i = y * stride + x * 4;
                byte gray = TileHash(x / 4, y / 4, seed);
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(ScrollWidth, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>4×4 块的灰度：同一页里每一块都不同，跨页（换 seed）则毫无关系。</summary>
    private static byte TileHash(int cross, int primary, int seed)
    {
        unchecked
        {
            uint hash = (uint)(cross * 0xc2b2ae35) ^ (uint)(primary * 0x27d4eb2d) ^ (uint)seed;
            hash ^= hash >> 16;
            hash *= 0x7feb352d;
            return (byte)(hash >> 24);
        }
    }

    /// <summary>从整页里截出 offset 开始的 <see cref="ViewHeight"/> 行（= 滚到那里时屏幕上看得到的那一帧）。</summary>
    private static BitmapSource Frame(BitmapSource page, int offset)
    {
        int stride = page.PixelWidth * 4;
        var pixels = new byte[stride * ViewHeight];
        page.CopyPixels(new Int32Rect(0, offset, page.PixelWidth, ViewHeight), pixels, stride, 0);

        var frame = BitmapSource.Create(page.PixelWidth, ViewHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>把一帧沿轴截短到 <paramref name="height"/> 行（验证「尺寸变了就认不出」）。</summary>
    private static BitmapSource CropHeight(BitmapSource frame, int height)
    {
        int stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * height];
        frame.CopyPixels(new Int32Rect(0, 0, frame.PixelWidth, height), pixels, stride, 0);

        var cropped = BitmapSource.Create(frame.PixelWidth, height, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        cropped.Freeze();
        return cropped;
    }

    /// <summary>把一帧的第 [<paramref name="start"/>, +<paramref name="count"/>) 行涂成同一片灰（模拟不动的页脚 / 在动的画面）。</summary>
    private static BitmapSource PaintBand(BitmapSource frame, int start, int count, byte gray)
    {
        int stride = frame.PixelWidth * 4;
        var pixels = new byte[stride * frame.PixelHeight];
        frame.CopyPixels(pixels, stride, 0);

        int end = Math.Min(frame.PixelHeight, start + count);
        for (int y = Math.Max(0, start); y < end; y++)
        {
            for (int x = 0; x < frame.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
                pixels[i + 3] = 0xFF;
            }
        }

        var painted = BitmapSource.Create(frame.PixelWidth, frame.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        painted.Freeze();
        return painted;
    }

    /// <summary>
    /// 滚到 <paramref name="offset"/> 的一帧，底部 <paramref name="footer"/> 行换成「固定条」：
    /// 正常是带花纹的一条（有信息，认得出是固定条），<paramref name="flat"/> 时是一整片纯色（空白，不该当固定条）。
    /// </summary>
    private static BitmapSource FooterFrame(BitmapSource page, int offset, int footer, bool flat = false)
    {
        int stride = page.PixelWidth * 4;
        var pixels = new byte[stride * ViewHeight];
        page.CopyPixels(new Int32Rect(0, offset, page.PixelWidth, ViewHeight), pixels, stride, 0);

        for (int y = ViewHeight - footer; y < ViewHeight; y++)
        {
            for (int x = 0; x < page.PixelWidth; x++)
            {
                int i = y * stride + x * 4;
                if (flat)
                {
                    pixels[i] = 0xF0;
                    pixels[i + 1] = 0xF0;
                    pixels[i + 2] = 0xF0;
                }
                else
                {
                    // 每一行都不一样（同一行里也有灰度跨度）：后面要按「某一行出现了几次」数固定条
                    pixels[i] = (byte)(0x30 + x * 7);
                    pixels[i + 1] = (byte)(0x40 + (y - (ViewHeight - footer)) * 5);
                    pixels[i + 2] = 0xD0;
                }
                pixels[i + 3] = 0xFF;
            }
        }

        var frame = BitmapSource.Create(page.PixelWidth, ViewHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        frame.Freeze();
        return frame;
    }

    /// <summary>位图里第 <paramref name="y"/> 行（Bgra32 字节）。</summary>
    private static byte[] RowAt(BitmapSource image, int y)
    {
        int stride = image.PixelWidth * 4;
        var row = new byte[stride];
        image.CopyPixels(new Int32Rect(0, y, image.PixelWidth, 1), row, stride, 0);
        return row;
    }

    /// <summary>整张图里有多少行与 <paramref name="row"/> 逐字节相同（数固定条重复了几遍）。</summary>
    private static int CountRows(BitmapSource image, byte[] row)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        int count = 0;
        for (int y = 0; y < image.PixelHeight; y++)
        {
            bool same = true;
            for (int i = 0; i < stride; i++)
            {
                if (pixels[y * stride + i] != row[i]) { same = false; break; }
            }
            if (same) count++;
        }
        return count;
    }

    // ============ 横向拼接的合成帧 ============

    /// <summary>合成一张「很宽的页面」：每 4×4 块一块哈希纹理，按 offset 截出来就是横向滚动中的某一帧。</summary>
    private static BitmapSource NewWideScrollPage(int width, int seed = 0)
    {
        int height = ViewHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = y * stride + x * 4;
                byte gray = TileHash(x / 4, y / 4, seed);
                pixels[i] = gray;
                pixels[i + 1] = gray;
                pixels[i + 2] = gray;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        image.Freeze();
        return image;
    }

    /// <summary>从宽页里截出 offset 开始的 <see cref="ViewHeight"/> 列（横向滚动到那里时的一帧）。</summary>
    private static BitmapSource ColFrame(BitmapSource page, int offset, int width = ViewHeight)
    {
        int stride = page.PixelWidth * 4;
        var source = new byte[stride * page.PixelHeight];
        page.CopyPixels(source, stride, 0);

        int outStride = width * 4;
        var pixels = new byte[outStride * page.PixelHeight];
        for (int y = 0; y < page.PixelHeight; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int from = y * stride + (offset + x) * 4;
                int to = y * outStride + x * 4;
                pixels[to] = source[from];
                pixels[to + 1] = source[from + 1];
                pixels[to + 2] = source[from + 2];
                pixels[to + 3] = source[from + 3];
            }
        }

        var frame = BitmapSource.Create(width, page.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, pixels, outStride);
        frame.Freeze();
        return frame;
    }

    /// <summary>位图里第 <paramref name="x"/> 列（Bgra32 字节，按 y 递增）。</summary>
    private static byte[] ColumnAt(BitmapSource image, int x)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        var column = new byte[image.PixelHeight * 4];
        for (int y = 0; y < image.PixelHeight; y++)
        {
            int from = y * stride + x * 4;
            int to = y * 4;
            column[to] = pixels[from];
            column[to + 1] = pixels[from + 1];
            column[to + 2] = pixels[from + 2];
            column[to + 3] = pixels[from + 3];
        }
        return column;
    }

    // ============ 录屏 ============

    /// <summary>
    /// 录屏参数 + ffmpeg 命令行 + 定位顺序。
    /// 命令行是「拼错了要录半天才发现」的那类东西，关键参数逐条断言（顺序也守着）。
    /// </summary>
    private static void Record()
    {
        var plan = RecordPlan.FromValues("window", "gif", 120, 99);
        Check.Equal(RecordMode.Window, plan.Mode, "范围按取值解析");
        Check.Equal(RecordFormat.Gif, plan.Format, "格式按取值解析");
        Check.Equal(RecordPlan.MaxFps, plan.EffectiveFps, "帧率夹到上限");
        Check.Equal(RecordPlan.MaxCountdown, plan.Countdown, "倒计时夹到上限");

        Check.Equal(RecordMode.Region, RecordPlan.FromValues("???", null, 0, -5).Mode, "认不出的范围退回框选");
        Check.Equal(RecordFormat.Mp4, RecordPlan.FromValues(null, "webm", 0, 0).Format, "认不出的格式退回 MP4");
        Check.Equal(RecordPlan.DefaultFps, RecordPlan.FromValues(null, null, 0, 0).EffectiveFps, "帧率 0 = 默认值");
        Check.Equal(RecordPlan.MinFps, RecordPlan.FromValues(null, null, 1, 0).EffectiveFps, "帧率太小夹到下限");
        Check.Equal(0, RecordPlan.FromValues(null, null, 30, -5).Countdown, "倒计时负数夹到 0");

        Check.Equal(RecordPlan.GifMaxFps, RecordPlan.FromValues(null, "gif", 60, 0).GifFps, "GIF 帧率最多 15");
        Check.Equal("gif", RecordPlan.FromValues(null, "gif", 30, 0).Extension, "GIF 扩展名");
        Check.Equal("mp4", RecordPlan.FromValues(null, null, 30, 0).Extension, "MP4 扩展名");

        // 四个格式（上游下拉的同四项）：解析 / 扩展名 / 动图判定 / 显示名
        Check.Equal(RecordFormat.Apng, RecordPlan.ParseFormat("apng"), "APNG 格式解析");
        Check.Equal(RecordFormat.Webp, RecordPlan.ParseFormat("WebP"), "WebP 格式解析不分大小写");
        Check.Equal("apng", RecordPlan.FromValues(null, "apng", 30, 0).Extension, "APNG 扩展名");
        Check.Equal("webp", RecordPlan.FromValues(null, "webp", 30, 0).Extension, "WebP 扩展名");
        Check.True(RecordPlan.FromValues(null, "gif", 30, 0).IsAnimated, "GIF 是动图");
        Check.True(RecordPlan.FromValues(null, "apng", 30, 0).IsAnimated, "APNG 是动图");
        Check.True(RecordPlan.FromValues(null, "webp", 30, 0).IsAnimated, "WebP 是动图");
        Check.False(RecordPlan.FromValues(null, "mp4", 30, 0).IsAnimated, "MP4 不是动图");
        Check.Equal(4, RecordPlan.Formats.Count, "格式下拉 4 项");
        Check.Equal("APNG", RecordPlan.FormatName(RecordFormat.Apng), "APNG 显示名");
        Check.Equal("WebP", RecordPlan.FormatName(RecordFormat.Webp), "WebP 显示名");

        // 面板时长一律 HH:MM:SS（上游 setRecordingDuration）
        Check.Equal("00:00:00", RecordPlan.FormatDuration(0), "时钟空闲态");
        Check.Equal("00:00:59", RecordPlan.FormatDuration(59_999), "时钟未满一分钟");
        Check.Equal("01:02:03", RecordPlan.FormatDuration(3_723_500), "时钟超过一小时");

        Check.Equal("选区", RecordPlan.ModeLabel(RecordMode.Region), "范围文案");
        Check.Equal("全部屏幕", RecordPlan.ModeLabel(RecordMode.All), "全部屏幕文案");
        Check.Equal("MP4", RecordPlan.FormatLabel("mp4"), "格式文案");
        Check.Equal("GIF", RecordPlan.FormatLabel("GIF"), "格式文案不分大小写");
        Check.Equal("APNG", RecordPlan.FormatLabel("apng"), "APNG 文案");
        Check.Equal("WebP", RecordPlan.FormatLabel("webp"), "WebP 文案");

        // 采集历史存成品时不能把动图扩展名改回 mp4（否则 .mp4 里装的是 apng / webp）
        Check.Equal("records/x.apng", CaptureCatalog.RecordRelative("x", "apng"), "APNG 成品保留 .apng");
        Check.Equal("records/x.webp", CaptureCatalog.RecordRelative("x", "webp"), "WebP 成品保留 .webp");
        Check.Equal("records/x.gif", CaptureCatalog.RecordRelative("x", "GIF"), "GIF 成品保留 .gif");
        Check.Equal("records/x.mp4", CaptureCatalog.RecordRelative("x", "bmp"), "不认识的扩展名回到 .mp4");
        Check.Equal("00:07", RecordPlan.FormatClock(7000), "不足一分钟");
        Check.Equal("01:02", RecordPlan.FormatClock(62000), "超过一分钟");
        Check.Equal("1:00:00", RecordPlan.FormatClock(3600000), "超过一小时");

        // MP4：原始 BGRA 从管道进来 → libx264 / yuv420p，奇数尺寸补成偶数，moov 前置
        var mp4 = RecordPlan.Mp4Args(new Int32Rect(0, 0, 1921, 1081), 30, "out.mp4", RecordEncoders.X264);
        string mp4Line = string.Join(" ", mp4);
        Check.True(mp4Line.Contains("rawvideo") && mp4Line.Contains("bgra"), "原始帧格式是 bgra");
        Check.True(mp4Line.Contains("1921x1081"), "尺寸原样交给编码器");
        Check.True(mp4Line.Contains("-framerate 30"), "帧率写进命令行");
        Check.True(mp4Line.Contains("pipe:0"), "从标准输入读原始帧");
        Check.True(mp4Line.Contains("libx264") && mp4Line.Contains("yuv420p"), "H.264 + yuv420p");
        Check.True(mp4Line.Contains("pad=ceil(iw/2)*2:ceil(ih/2)*2"), "奇数宽高补成偶数");
        Check.True(mp4Line.Contains("+frag_keyframe+empty_moov"), "录的过程写分片 MP4（崩溃可恢复）");
        Check.True(!mp4Line.Contains("+faststart"), "边录边写不要求 faststart（收尾再转）");
        Check.Equal("out.mp4", mp4[^1], "输出路径是最后一个参数");

        // 收尾：只搬容器不重编码，转成普通 faststart MP4
        var remux = RecordPlan.RemuxArgs("in.mp4", "out.mp4");
        string remuxLine = string.Join(" ", remux);
        Check.True(remuxLine.Contains("-c copy"), "重封装不重编码");
        Check.True(remuxLine.Contains("+faststart"), "收尾转成 faststart");
        Check.Equal("out.mp4", remux[^1], "重封装输出是最后一个参数");

        // GIF：两遍（调色板 → 编码），帧率与宽度都压下来
        var palette = RecordPlan.GifPaletteArgs("in.mp4", "p.png", 60);
        string paletteLine = string.Join(" ", palette);
        Check.True(paletteLine.Contains("fps=15"), "GIF 抽帧压到 15");
        Check.True(paletteLine.Contains("palettegen"), "第一遍做调色板");
        Check.True(paletteLine.Contains("min(960,iw)"), "宽度上限 960");
        Check.Equal("p.png", palette[^1], "调色板文件是最后一个参数");

        var gif = RecordPlan.GifEncodeArgs("in.mp4", "p.png", "out.gif", 60);
        string gifLine = string.Join(" ", gif);
        Check.True(gifLine.Contains("paletteuse"), "第二遍用调色板");
        Check.True(gifLine.Contains("-loop 0"), "无限循环");
        Check.Equal("out.gif", gif[^1], "GIF 输出是最后一个参数");

        // APNG / WebP：单遍转码（不像 GIF 要两遍调色板），同样压帧率 / 宽度，且不带音轨
        var apng = RecordPlan.AnimatedArgs("in.mp4", "out.apng", RecordFormat.Apng, 60);
        string apngLine = string.Join(" ", apng);
        Check.True(apngLine.Contains("fps=15"), "APNG 帧率也压到 15");
        Check.True(apngLine.Contains("min(960,iw)"), "APNG 宽度上限 960");
        Check.True(apngLine.Contains("-an"), "动图不带音轨");
        Check.True(apngLine.Contains("-c:v apng"), "APNG 用 apng 编码器");
        Check.Equal("out.apng", apng[^1], "APNG 输出是最后一个参数");

        var webp = RecordPlan.AnimatedArgs("in.mp4", "out.webp", RecordFormat.Webp, 30);
        string webpLine = string.Join(" ", webp);
        Check.True(webpLine.Contains("libwebp_anim"), "WebP 动图用 libwebp_anim");
        Check.True(webpLine.Contains("-loop 0"), "WebP 无限循环");
        Check.True(webpLine.Contains("-an"), "WebP 也不带音轨");
        Check.Equal("out.webp", webp[^1], "WebP 输出是最后一个参数");

        // 定位：随包优先，其次 PATH（要剥引号），都没有返回 null
        string bundled = Path.Combine("C:\\app", "runtime", "ffmpeg", "ffmpeg.exe");
        Check.Equal(bundled, RecordPlan.Locate("C:\\app", "D:\\tools", p => p == bundled), "随包优先");

        string fromPath = Path.Combine("D:\\tools", "ffmpeg.exe");
        Check.Equal(fromPath, RecordPlan.Locate("C:\\app", "D:\\tools", p => p == fromPath), "没有随包就用 PATH");

        string quoted = Path.Combine("D:\\with space", "ffmpeg.exe");
        Check.Equal(quoted,
            RecordPlan.Locate("", "D:\\none;\"D:\\with space\"", p => p == quoted), "PATH 里的引号要剥掉");

        Check.True(RecordPlan.Locate("C:\\app", "D:\\none", _ => false) == null, "都没有就返回 null");
    }

    /// <summary>
    /// 跟随鼠标：一比一移植 Snow Shot <c>snow-crates/crates/snow-media/src/follow_mouse.rs</c> 的用例
    /// （immediate_follow / jitter_reduction / clamped_edge / smooth_movement_catches_up /
    /// smooth_follow_catches_large_jumps / combined_options / easing_resumes）。
    /// 时间以毫秒显式传入，断言才是确定的；上游用厘米级的 Point 空间，这里全是物理像素，
    /// 所以「取半」按整像素 floor（与上游 PhysicalPixels 分支一致）。
    /// </summary>
    private static void RecordFollow()
    {
        var still = new RecordFollowOptions(SmoothMovement: false, ReduceJitter: false);
        var bounds = new Int32Rect(-200, -400, 1400, 1800);

        // ① 立即跟随：居中取整、夹在显示器里，光标在中间就不动
        var quick = new RecordFollowMotion(new Int32Rect(-600, 200, 101, 79),
            new Int32Rect(-800, 0, 600, 400), still, 0);

        Check.Equal(new Int32Rect(-600, 200, 101, 79), quick.Advance(-550, 239, 0),
            "立即跟随：光标落在正中间就不动");
        Check.Equal(new Int32Rect(-800, 0, 101, 79), quick.Advance(-799, 1, 0),
            "立即跟随：夹到左上边界");
        Check.Equal(new Int32Rect(-301, 321, 101, 79), quick.Advance(-201, 399, 0),
            "立即跟随：夹到右下边界");

        // ② 抑制抖动：中央 5/6 不动，进了外侧 1/12 才重新居中（两条边都要）
        var initial = new Int32Rect(150, 160, 120, 84);
        (int InX, int InY, int EdgeX, int EdgeY)[] jitter =
        {
            (161, 201, 160, 201),
            (259, 201, 260, 201),
            (211, 168, 211, 167),
            (211, 236, 211, 237),
        };

        foreach (var (inX, inY, edgeX, edgeY) in jitter)
        {
            var motion = new RecordFollowMotion(initial, bounds, new RecordFollowOptions(false, true), 0);
            Check.Equal(initial, motion.Advance(inX, inY, 0), $"抑制抖动：({inX},{inY}) 还在中央 5/6 里");

            var changed = motion.Advance(edgeX, edgeY, 0);
            Check.Equal(new Int32Rect(edgeX - 60, edgeY - 42, 120, 84), changed,
                $"抑制抖动：({edgeX},{edgeY}) 进了外侧 1/12 就重新居中");
            Check.Equal(changed, motion.Advance(edgeX, edgeY, 0), "已经居中后同一位置不再抖");
        }

        // ③ 被夹住的那一轴不会因为另一轴触发而移动（不平滑，目标即位置）
        var edge = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
            new RecordFollowOptions(SmoothMovement: false, ReduceJitter: true), 0);

        var centered = edge.Center(-195, 200, 0);
        Check.Equal(-200, centered.X, "居中时夹到显示器左边界");
        Check.Equal(centered, edge.Advance(-195, 212, 0), "夹住的那一轴不动，另一轴也还没到边缘");

        var moved = edge.Advance(-195, 234, 0);
        Check.Equal(-200, moved.X, "横向仍然夹在边界上");
        Check.Equal(194, moved.Y, "纵向已经重新居中");

        // ④ 平滑移动：按 50ms 半衰期逼近，任何帧率下 1 秒内都收敛到目标
        foreach (int fps in new[] { 15, 30, 60, 120 })
        {
            var smooth = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
                new RecordFollowOptions(true, false), 0);
            var start = smooth.Viewport;
            int at20Percent = 0;

            for (int frame = 1; frame <= fps; frame++)
            {
                long now = (long)Math.Round(frame * 1000.0 / fps);
                var crop = smooth.Advance(240, 200, now);

                if (frame == 1)
                    Check.True(start.X < crop.X && crop.X < 190, $"{fps} fps：第一帧只走一小段（{crop.X}）");

                if (frame * 5 == fps) at20Percent = crop.X;
            }

            Check.Equal(190, smooth.Viewport.X, $"{fps} fps：1 秒内收敛到目标");
            Check.True(Math.Abs(at20Percent - 187.5) <= 1,
                $"{fps} fps：20% 的时间走完 94%（{at20Percent}）");
        }

        // ⑤ 快跳：第一帧先把光标拉进视口（右边留 1 像素），之后才慢慢缓过去
        var jump = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80),
            new Int32Rect(-400, -300, 1400, 1000), new RecordFollowOptions(true, false), 0);

        var jumped = jump.Advance(750, -200, 16);
        Check.Equal(new Int32Rect(651, -200, 100, 80), jumped, "快跳：第一帧就把光标包进视口");

        var caught = jump.Advance(760, -200, 16);
        Check.Equal(new Int32Rect(661, -200, 100, 80), caught, "下一帧补到光标右侧留 1 像素");

        var eased = jump.Advance(760, -200, 66);
        Check.True(eased.X > caught.X && eased.X < 700, $"追平之后才开始缓动（{eased.X}）");
        Check.True(eased.Y < jumped.Y && eased.Y > -240, $"纵轴同时在缓动（{eased.Y}）");

        var reversed = jump.Advance(-400, 699, 82);
        Check.Equal(new Int32Rect(-400, 620, 100, 80), reversed, "反向快跳同样先追上");
        Check.False(jump.IsMoving, "追上就停住（不再抖）");

        // ⑥ 两个开关一起开：判断看「目标」而不是当前位置
        var combined = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
            new RecordFollowOptions(true, true), 0);

        Check.Equal(150, combined.Advance(241, 200, 0).X, "光标还在中央 5/6 里就不动");
        Check.Equal(171, combined.Advance(242, 200, 50).X, "进外侧 1/12：重新居中并走完一半");
        Check.True(Math.Abs(combined.Advance(263, 200, 100).X - 181.5) <= 1,
            "已经处在移动视口的右边缘、但没出目标的中区 → 不重定目标");
        Check.True(Math.Abs(combined.Advance(284, 200, 150).X - 207.75) <= 1,
            "出了目标的中区才重定目标");
        Check.Equal(234, combined.Advance(284, 200, 600).X, "半衰期收敛到新目标");

        // ⑦ 暂停恢复：冻住那一刻的位置接着走，暂停时长不算成缓动
        var paused = new RecordFollowMotion(new Int32Rect(150, 160, 100, 80), bounds,
            new RecordFollowOptions(true, false), 0);

        var frozen = paused.Advance(250, 200, 50);
        Check.Equal(175, frozen.X, "50ms（一个半衰期）走完一半");

        paused.RestartClock();
        Check.Equal(frozen, paused.Advance(265, 200, 3050), "恢复第一帧仍停在冻结位置");
        Check.Equal(195, paused.Advance(265, 200, 3100).X, "恢复后按新间隔继续缓动");

        // ⑧ 视口尺寸：放得下原样、放不下等比缩、缩完居中；太小的区域直接拒绝
        Check.Equal(new Int32Rect(760, 390, 400, 300),
            RecordFollowMotion.Fit(new Int32Rect(100, 50, 400, 300), new Int32Rect(0, 0, 1920, 1080)),
            "放得下就只调位置：尺寸不动、居中到显示器里");

        Check.Equal(new Int32Rect(0, 0, 1920, 1080),
            RecordFollowMotion.Fit(new Int32Rect(0, 0, 3840, 2160), new Int32Rect(0, 0, 1920, 1080)),
            "比整屏还大就等比缩到整屏");

        Check.Equal(new Int32Rect(760, 390, 400, 300),
            RecordFollowMotion.Fit(new Int32Rect(5000, 0, 400, 300), new Int32Rect(0, 0, 1920, 1080)),
            "缩完之后在显示器里居中");

        Check.Equal(Int32Rect.Empty,
            RecordFollowMotion.Normalize(new Int32Rect(0, 0, 1, 1), new Int32Rect(0, 0, 1920, 1080)),
            "1 像素的选区直接拒绝");

        var widened = RecordFollowMotion.Normalize(new Int32Rect(10, 10, 4, 300),
            new Int32Rect(0, 0, 1920, 1080));
        Check.Equal(RecordFollowMotion.MinimumExtent, widened.Width, "太窄的选区补到最小宽度");
        Check.Equal(300, widened.Height, "另一边不动");
    }

    /// <summary>
    /// 空闲录屏区域框：边缘命中 / 拖拽（含翻面）/ 最小尺寸 / 移动 / 把手度量。
    /// </summary>
    /// <remarks>
    /// 规则抄自上游 <c>screenrecordingareawindow.cpp</c> + <c>resizegeometry.h</c>：
    /// 6 逻辑像素的抓取带、按住边翻面、最小 10 物理像素、32 逻辑像素的移动把手内缩 8。
    /// </remarks>
    private static void RecordRegion()
    {
        // ① 窗口比选区四周各大 3 物理像素（2 边框 + 1 透明缝）
        Check.Equal(new Int32Rect(97, 97, 206, 156),
            RecordRegionGeometry.FrameBounds(new Int32Rect(100, 100, 200, 150)),
            "区域框把选区和外面那圈一起盖住");

        // ② 抓边：四边 / 四角 / 中间不抓
        var area = new Int32Rect(100, 100, 200, 150);
        Check.Equal(ResizeEdges.Left | ResizeEdges.Top,
            RecordRegionGeometry.EdgesAt(area, 100, 100, 1), "左上角两条边都抓");
        Check.Equal(ResizeEdges.Right | ResizeEdges.Bottom,
            RecordRegionGeometry.EdgesAt(area, 299, 249, 1), "右下角两条边都抓");
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(area, 103, 180, 1), "左边");
        Check.Equal(ResizeEdges.Right, RecordRegionGeometry.EdgesAt(area, 297, 180, 1), "右边");
        Check.Equal(ResizeEdges.Top, RecordRegionGeometry.EdgesAt(area, 200, 103, 1), "上边");
        Check.Equal(ResizeEdges.Bottom, RecordRegionGeometry.EdgesAt(area, 200, 247, 1), "下边");
        Check.Equal(ResizeEdges.None, RecordRegionGeometry.EdgesAt(area, 200, 175, 1), "中间不抓");
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(area, 98, 180, 1),
            "可见边框在选区外面那 3 像素也算抓边");

        // 小区域：带宽收窄到 1/4（20 → 5）
        var small = new Int32Rect(100, 100, 20, 20);
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(small, 104, 110, 1),
            "小区域带宽收窄到 1/4");
        Check.Equal(ResizeEdges.None, RecordRegionGeometry.EdgesAt(small, 110, 110, 1),
            "收窄之后中间留得出来");

        // 125% DPI：6 逻辑像素 = 7.5 物理像素
        var scaled = new Int32Rect(0, 0, 200, 200);
        Check.Equal(ResizeEdges.Left, RecordRegionGeometry.EdgesAt(scaled, 7, 100, 1.25),
            "抓边带宽跟着 DPI 放大");
        Check.Equal(ResizeEdges.None, RecordRegionGeometry.EdgesAt(scaled, 9, 100, 1.25),
            "放大后 9 像素已经在里面");

        // ③ 拖右边 / 左边 / 角
        var right = RecordRegionGeometry.Resize(area, ResizeEdges.Right, 50, 0, ResizeEdges.Right);
        Check.Equal(new Int32Rect(100, 100, 250, 150), right.Rect, "拖右边：左边不动、变宽");
        Check.Equal(ResizeEdges.Right, right.Effective, "拖右边仍落在右边");

        var left = RecordRegionGeometry.Resize(area, ResizeEdges.Left, 50, 0, ResizeEdges.Left);
        Check.Equal(new Int32Rect(150, 100, 150, 150), left.Rect, "拖左边：右边不动、变窄");

        var corner = RecordRegionGeometry.Resize(area, ResizeEdges.Left | ResizeEdges.Top, 20, 10,
            ResizeEdges.Left | ResizeEdges.Top);
        Check.Equal(new Int32Rect(120, 110, 180, 140), corner.Rect, "拖左上角：宽高同时收");

        // ④ 拖过对边翻面：贴着哪边就换哪边
        var flip = RecordRegionGeometry.Resize(area, ResizeEdges.Left, 250, 0, ResizeEdges.Left);
        Check.Equal(ResizeEdges.Right, flip.Effective, "左边界被拖过右边界 → 换成右边界");
        Check.Equal(new Int32Rect(300, 100, 50, 150), flip.Rect, "翻面后锚点仍是原来的右边");

        var zero = RecordRegionGeometry.Resize(area, ResizeEdges.Left, 200, 0, ResizeEdges.Left);
        Check.Equal(ResizeEdges.Left, zero.Effective, "距离正好为 0：保持上一帧的边（不来回抖）");

        // ⑤ 最小 10 物理像素
        var tiny = RecordRegionGeometry.Resize(area, ResizeEdges.Right, -195, 0, ResizeEdges.Right);
        Check.Equal(RecordRegionGeometry.MinimumPhysical, tiny.Rect.Width, "窄到极限就停在 10 像素");
        var tinyCorner = RecordRegionGeometry.Resize(area, ResizeEdges.Right | ResizeEdges.Bottom,
            -199, -149, ResizeEdges.Right | ResizeEdges.Bottom);
        Check.Equal(RecordRegionGeometry.MinimumPhysical, tinyCorner.Rect.Width, "角上同样有最小宽");
        Check.Equal(RecordRegionGeometry.MinimumPhysical, tinyCorner.Rect.Height, "角上同样有最小高");

        // ⑥ 移动：整块平移、尺寸不变
        Check.Equal(new Int32Rect(80, 130, 200, 150),
            RecordRegionGeometry.Move(area, -20, 30), "拖把手整块挪");

        // ⑦ 抓取带厚度（DIP = 外面那圈 3 像素 + 往里 6 逻辑像素）
        Check.Equal(9.0, Math.Round(RecordRegionGeometry.HitBandDip(area, 1, true), 3),
            "100% 时横向抓取带 3 + 6");
        Check.Equal(7.5, Math.Round(RecordRegionGeometry.HitBandDip(scaled, 2, false), 3),
            "200% 时外面那圈折半、往里仍是 6 逻辑像素");
        Check.Equal(8.0, Math.Round(RecordRegionGeometry.HitBandDip(small, 1, true), 3),
            "小区域按 1/4 收窄");

        // ⑧ 把手：32 逻辑像素、内缩 8；区域太小跟着缩
        var handle = RecordRegionGeometry.HandleMetrics(area, 1);
        Check.Equal(32.0, handle.Side, "把手边长 32");
        Check.Equal(8.0, handle.Inset, "把手离右下角 8");

        var smallHandle = RecordRegionGeometry.HandleMetrics(small, 1);
        Check.Equal(20.0, smallHandle.Side, "区域比把手还小时把手跟着缩");
        Check.Equal(0.0, smallHandle.Inset, "缩到一样大时不再内缩");

        var dpiHandle = RecordRegionGeometry.HandleMetrics(new Int32Rect(0, 0, 200, 200), 2);
        Check.Equal(32.0, dpiHandle.Side, "200% 时把手仍按逻辑像素算");
    }

    /// <summary>
    /// 中断残片：从 <c>ffmpeg -i</c> 的 stderr 里读尺寸与时长（启动抢救靠这个判断能不能救）。
    /// </summary>
    private static void RecordRecoveryProbe()
    {
        const string output =
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'rec.mp4':\n" +
            "  Metadata:\n" +
            "    major_brand     : isom\n" +
            "  Duration: 00:00:12.34, start: 0.000000, bitrate: 812 kb/s\n" +
            "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, progressive), 320x240, 812 kb/s, 30 fps";

        Check.True(RecordRecovery.TryParseProbe(output, out int width, out int height, out int durationMs),
            "从 ffmpeg 输出里读出尺寸与时长");
        Check.Equal(320, width, "宽度");
        Check.Equal(240, height, "高度");
        Check.Equal(12340, durationMs, "时长换成毫秒");

        Check.False(RecordRecovery.TryParseProbe("garbage", out _, out _, out _),
            "读不出来就返回 false（残片会被清掉）");
        Check.False(RecordRecovery.TryParseProbe("Duration: 00:00:01.00\n  Stream: Video: h264, 0x0",
            out _, out _, out _), "尺寸为 0 也算读不出来");
    }

    /// <summary>
    /// 暂停不计时 + 采集历史里录屏条目的形状 / 文案。
    /// 计时断言只比「墙钟 − 活动时长 ≥ 暂停时长」，不给机器快慢留坑。
    /// </summary>
    private static void RecordBookkeeping()
    {
        var wall = Stopwatch.StartNew();
        var controller = new RecordController();
        Check.False(controller.HasStarted, "建出来还没开始计时（面板在等用户点录制）");
        Check.Equal(0, controller.ElapsedMs, "没开始时时长就是 0");
        controller.Start();
        Thread.Sleep(25);
        controller.Pause();
        Thread.Sleep(120);
        controller.Resume();
        Thread.Sleep(25);

        long total = wall.ElapsedMilliseconds;
        long active = controller.ElapsedMs;
        Check.True(total - active >= 100, $"暂停的 120ms 不算进活动时长（差 {total - active}ms）");
        Check.True(active >= 20, "活动时长照常累计");

        Check.False(controller.Finished, "还没结束");
        Check.False(controller.Cancelled, "完成不是取消");
        Check.True(controller.TogglePause(), "切一次变暂停");
        Check.True(controller.IsPaused, "确实是暂停态");
        Check.False(controller.TogglePause(), "再切回继续");

        controller.Stop();
        Check.True(controller.Finished, "点完成后就结束");
        Check.False(controller.Cancelled, "完成 != 取消");

        // 采集历史的录屏条目
        var record = new CaptureEntry
        {
            Id = "r1",
            Kind = CaptureCatalog.KindRecord,
            CreatedAt = "2026-11-16T11:00:00",
            Width = 1920,
            Height = 1080,
            Source = "录屏",
            File = "records/r1.mp4",
            DurationMs = 62000,
            State = "done",
        };

        Check.Equal("录屏", CaptureCatalog.KindLabel(CaptureCatalog.KindRecord), "record 的中文名");
        Check.True(record.IsRecord && !record.IsRecognize, "kind 判定");
        Check.Equal("MP4 · 录屏", CaptureCatalog.SummaryOf(record), "录屏摘要 = 格式 + 完成动作");
        Check.Equal("01:02", record.DurationText, "时长文案");
        Check.Equal("MP4", record.FormatText, "格式文案");
        Check.Equal("records/r1.mp4", CaptureCatalog.RecordRelative("r1", "mp4"), "条目里的相对路径");
        Check.True(CaptureCatalog.FilePath(record).EndsWith("records\\r1.mp4"), "绝对路径拼在数据根下");

        var gif = new CaptureEntry { File = "records/r2.gif", Kind = CaptureCatalog.KindRecord };
        Check.Equal("GIF", gif.FormatText, "GIF 条目");
        Check.Equal("GIF", CaptureCatalog.SummaryOf(gif), "没有完成动作就只写格式");
        Check.Equal("", new CaptureEntry { Kind = CaptureCatalog.KindShot }.FormatText, "截图没有格式");

        Check.Equal("gif", CaptureCatalog.ExtensionOf("shots/a.GIF"), "扩展名统一小写");
        Check.Equal("mp4", CaptureCatalog.ExtensionOf("C:\\a\\b.MP4"), "大写扩展名也认");
        Check.Equal("", CaptureCatalog.ExtensionOf(""), "没有文件名就没有扩展名");

        Check.Equal("820 KB", CaptureCatalog.FormatBytes(840_000), "不到 1 MB 用 KB");
        Check.Equal("3.4 MB", CaptureCatalog.FormatBytes(3_600_000), "MB 保留一位");
        Check.Equal("", CaptureCatalog.FormatBytes(0), "0 字节不写文案");

        // 录屏条目也进保留策略（与截图同一条路）
        var (keep, _) = CaptureCatalog.PlanPrune(
            new[] { record }, new DateTimeOffset(2026, 11, 16, 12, 0, 0, TimeSpan.Zero), 200, 30);
        Check.Equal(1, keep.Count, "当天的录屏不会被剪掉");
    }

    // ============ 录屏：选项 / 编码器 ============

    /// <summary>
    /// 录屏选项：夹取、从内置功能参数覆盖、摘要文案；编码器挑选 / 回退 / 参数表；
    /// 收尾混音参数（无音轨 / 单轨 / 双轨）。
    /// </summary>
    private static void RecordOptionsRules()
    {
        // ---- 夹取 ----
        var clamped = new RecordOptions
        {
            CameraSize = 5000,
            CameraOffsetLeft = -30,
            MouseHighlightRadius = 1,
            MicrophoneDevice = "  ",
        }.Normalized();

        Check.Equal(RecordOptions.MaxCameraSize, clamped.CameraSize, "画中画边长夹到上限");
        Check.Equal(0, clamped.CameraOffsetLeft, "负边距收成 0");
        Check.Equal(RecordOptions.MinHighlightRadius, clamped.MouseHighlightRadius, "高亮半径夹到下限");
        Check.True(clamped.MicrophoneDevice == null, "全空白的设备名当没填");
        Check.Equal(RecordOptions.MinCameraSize,
            new RecordOptions { CameraSize = 1 }.Normalized().CameraSize, "画中画边长夹到下限");

        // ---- 从参数覆盖：没写 / 认不出的项跟随设置 ----
        var baseOptions = new RecordOptions
        {
            SystemAudio = false,
            Microphone = false,
            CameraDevice = -1,
            ShowCursor = false,
            MouseHighlight = false,
            RecordClicks = false,
            RecordScroll = false,
            Keystroke = false,
            LaserColor = "#00000000",
            ClickColor = "#00000000",
            Codec = RecordCodec.h264,
            TrimAfterRecord = false,
        };

        var both = RecordOptions.Apply(baseOptions, audio: "both");
        Check.True(both.SystemAudio && both.Microphone, "audio=both 两路都开");

        var mic = RecordOptions.Apply(baseOptions, audio: "MIC");
        Check.True(!mic.SystemAudio && mic.Microphone, "audio 值不分大小写");

        var untouched = RecordOptions.Apply(baseOptions, audio: "", camera: "???", codec: "???");
        Check.True(!untouched.SystemAudio && !untouched.Microphone, "空值 / 认不出的值不动原选项");
        Check.Equal(RecordCodec.h264, untouched.Codec, "认不出的编码保持原值");

        var cameraOn = RecordOptions.Apply(baseOptions, camera: "on", cameraDevice: 2);
        Check.Equal(2, cameraOn.CameraDevice, "camera=on 用点名设备号");

        var cameraDefault = RecordOptions.Apply(baseOptions, camera: "on");
        Check.Equal(0, cameraDefault.CameraDevice, "没点名就用 0 号摄像头");

        var cameraOff = RecordOptions.Apply(new RecordOptions { CameraDevice = 1 }, camera: "off");
        Check.Equal(-1, cameraOff.CameraDevice, "camera=off 关掉画中画");

        var fxOn = RecordOptions.Apply(baseOptions, effects: "on");
        Check.True(fxOn.HasEffects, "effects=on 打开特效");
        Check.True(fxOn.ShowCursor && fxOn.MouseHighlight && fxOn.RecordClicks && fxOn.Keystroke,
            "光标 / 高亮 / 点击 / 按键逐个都开");
        Check.True(fxOn.HasLaser && fxOn.HasClickRipple, "effects=on 同时给激光笔与点击上色");

        var fxOff = RecordOptions.Apply(new RecordOptions
        {
            ShowCursor = true,
            MouseHighlight = true,
            RecordClicks = true,
            RecordScroll = true,
            Keystroke = true,
            PostProcessing = true,
            LaserColor = "#FFFF4040",
            ClickColor = "#80F5222D",
        }, effects: "off");
        Check.False(fxOff.HasEffects, "effects=off 全部关闭");
        Check.False(fxOff.ShowCursor, "effects=off 连光标覆盖也关");

        Check.Equal(RecordCodec.hevc, RecordOptions.Apply(baseOptions, codec: "h265").Codec,
            "h265 也认作 HEVC");
        Check.Equal(RecordCodec.auto, RecordOptions.Apply(baseOptions, codec: "auto").Codec, "auto");
        Check.True(RecordOptions.Apply(baseOptions, trim: "on").TrimAfterRecord, "trim=on");
        Check.False(RecordOptions.Apply(new RecordOptions { TrimAfterRecord = true }, trim: "off")
            .TrimAfterRecord, "trim=off");

        // ---- 摘要 ----
        Check.Equal("", new RecordOptions { SystemAudio = false }.Summary(), "纯画面没有摘要");
        Check.Equal("系统声音", new RecordOptions().Summary(), "默认只录系统声音（Snow Shot 默认开）");
        Check.Equal("系统声音 + 麦克风", new RecordOptions { SystemAudio = true, Microphone = true }.Summary(),
            "两路声音文案");
        Check.Equal("麦克风", new RecordOptions { SystemAudio = false, Microphone = true }.Summary(), "单路声音文案");
        Check.Equal("HEVC", new RecordOptions { SystemAudio = false, Codec = RecordCodec.hevc }.Summary(), "编码进摘要");
        Check.Equal("录完裁剪", new RecordOptions { SystemAudio = false, TrimAfterRecord = true }.Summary(), "裁剪进摘要");

        var full = new RecordOptions
        {
            SystemAudio = true,
            CameraDevice = 0,
            MouseHighlight = true,
            ClickColor = "#80F5222D",
            Codec = RecordCodec.hevc,
        }.Summary();
        Check.True(full.Contains("系统声音") && full.Contains("摄像头 0") && full.Contains("光标高亮")
            && full.Contains("点击涟漪") && full.Contains("HEVC"), "全开时摘要逐项都在：" + full);
        Check.True(!full.Contains("麦克风") && !full.Contains("拖尾"), "没开的项不进摘要：" + full);
        Check.True(!new RecordOptions().IsPlain, "默认带系统声音，不是纯画面");
        Check.True(new RecordOptions { SystemAudio = false }.IsPlain, "两路都关是纯画面");
        Check.False(new RecordOptions { Keystroke = true }.IsPlain, "有特效就不是纯画面");

        // ---- 编码器挑选（纯函数：硬件清单注入）----
        var nvenc = new[] { "h264_nvenc", "hevc_nvenc" };

        Check.Equal("h264_nvenc", RecordEncoders.Pick(RecordCodec.auto, nvenc), "auto 优先 H.264 硬件");
        Check.Equal("hevc_nvenc", RecordEncoders.Pick(RecordCodec.hevc, nvenc), "指定 HEVC 就用硬件 HEVC");
        Check.Equal("h264_nvenc", RecordEncoders.Pick(RecordCodec.h264, nvenc), "指定 H.264 就用硬件 H.264");
        Check.Equal(RecordEncoders.X264, RecordEncoders.Pick(RecordCodec.auto, Array.Empty<string>()),
            "没有硬件退回 libx264");
        Check.Equal(RecordEncoders.X265, RecordEncoders.Pick(RecordCodec.hevc, Array.Empty<string>()),
            "没有硬件退回 libx265");
        Check.Equal("hevc_nvenc", RecordEncoders.Pick(RecordCodec.auto, new[] { "hevc_nvenc" }),
            "只有 HEVC 硬件时 auto 也用它（别平白掉回软件）");
        Check.Equal("h264_amf", RecordEncoders.Pick(RecordCodec.h264, new[] { "h264_qsv", "h264_amf" }),
            "同代硬件按 NVENC → AMF → QSV 的优先顺序");

        // 回退：硬件开不起来 → 同格式的软件编码器
        Check.Equal(RecordEncoders.X264, RecordEncoders.Fallback("h264_nvenc"), "NVENC H.264 退回 libx264");
        Check.Equal(RecordEncoders.X265, RecordEncoders.Fallback("hevc_qsv"), "QSV HEVC 退回 libx265");
        Check.Equal(RecordEncoders.X264, RecordEncoders.Fallback("h264_amf"), "AMF H.264 退回 libx264");

        Check.True(RecordEncoders.IsHardware("hevc_nvenc"), "硬件编码器判定");
        Check.False(RecordEncoders.IsHardware(RecordEncoders.X264), "libx264 不算硬件");
        Check.False(RecordEncoders.IsHardware("made_up"), "认不出的不算硬件");
        Check.True(RecordEncoders.IsHevc(RecordEncoders.X265), "libx265 是 HEVC");
        Check.True(RecordEncoders.IsHevc("hevc_amf"), "AMF HEVC 是 HEVC");
        Check.False(RecordEncoders.IsHevc("h264_nvenc"), "H.264 不是 HEVC");

        // 参数表：编解码器名必须和「用哪个编码器」一致
        foreach (string encoder in new[]
                 {
                     RecordEncoders.X264, RecordEncoders.X265, "h264_nvenc", "hevc_nvenc",
                     "h264_amf", "hevc_amf", "h264_qsv", "hevc_qsv",
                 })
        {
            var args = RecordEncoders.Arguments(encoder);
            Check.True(args.Count >= 2, encoder + " 至少有 -c:v 和编码器名");
            Check.Equal("-c:v", args[0], encoder + " 第一个参数是 -c:v");
            Check.Equal(encoder, args[1], encoder + " 参数表用的是它自己");
        }

        Check.True(RecordEncoders.Arguments(RecordEncoders.X265).Contains("hvc1"),
            "HEVC 打 hvc1 标签（QuickTime / 浏览器才认）");
        Check.True(RecordEncoders.Arguments("hevc_nvenc").Contains("hvc1"), "硬件 HEVC 也打 hvc1");
        Check.False(RecordEncoders.Arguments(RecordEncoders.X264).Contains("hvc1"),
            "H.264 不打 hvc1");

        Check.Equal("H.264", RecordEncoders.Label(RecordEncoders.X264), "软件 H.264 文案");
        Check.Equal("HEVC · NVENC", RecordEncoders.Label("hevc_nvenc"), "硬件文案带厂商");

        // ---- 收尾混音 ----
        var mute = RecordPlan.FinalizeArgs("in.mp4", "out.mp4");
        string muteLine = string.Join(" ", mute);
        Check.True(muteLine.Contains("-c copy"), "没有音轨就是纯容器搬运");
        Check.True(muteLine.Contains("+faststart"), "收尾转 faststart");
        Check.False(muteLine.Contains("-filter_complex"), "没有音轨不用混流");
        Check.Equal("out.mp4", mute[^1], "输出在最后一个参数");

        var single = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "a.wav" });
        string singleLine = string.Join(" ", single);
        Check.True(singleLine.Contains("-i in.mp4") && singleLine.Contains("-i a.wav"), "原片 + 一条音轨");
        Check.True(singleLine.Contains("-map 0:v") && singleLine.Contains("-map 1:a"), "画面取原片、声音取音轨");
        Check.True(singleLine.Contains("-c:v copy"), "画面不重编码");
        Check.True(singleLine.Contains("-c:a aac") && singleLine.Contains("-b:a 192k"), "声音编成 AAC 192k");
        Check.True(singleLine.Contains("-ar 48000") && singleLine.Contains("-ac 2"), "统一成 48kHz 立体声");
        Check.True(singleLine.Contains("-shortest"), "以画面长度收口");
        Check.False(singleLine.Contains("amix"), "单音轨不用混音");

        var dual = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "sys.wav", "mic.wav" });
        string dualLine = string.Join(" ", dual);
        Check.True(dualLine.Contains("amix=inputs=2"), "两路用 amix 混");
        Check.True(dualLine.Contains("aresample=48000"), "混前先统一采样率（amix 不做格式统一）");
        Check.True(dualLine.Contains("channel_layouts=stereo"), "混前统一声道");
        Check.True(dualLine.Contains("-map [aout]"), "混音结果映射成音轨");
        Check.Equal("out.mp4", dual[^1], "输出在最后一个参数");

        Check.Equal(string.Join(" ", mute), string.Join(" ", RecordPlan.RemuxArgs("in.mp4", "out.mp4")),
            "RemuxArgs 等价于「没有音轨的收尾」");
    }

    // ============ 录屏：特效几何 / 画中画 ============

    /// <summary>
    /// 键鼠特效的纯函数（涟漪 / 拖尾 / 按键 / 坐标映射）+ 摄像头画中画落点。
    /// 参数照上游 <c>snow-recording-effects</c> 与 <c>snow-recording-model/src/webcam.rs</c>。
    /// </summary>
    private static void RecordEffectRules()
    {
        // ---- 涟漪：600ms 内 7 → 40 半径，透明度线性降到 0 ----
        var first = RecordEffectsMath.ClickRipple(0, true);
        Check.True(first != null, "0ms 还在生命周期里");
        Check.Equal(RecordEffectsMath.ClickStartRadius, first!.Value.Radius, "起点半径 7");
        Check.Equal(255, first.Value.Alpha, "起点不透明");
        Check.Equal(3, first.Value.Thickness, "左键描边 3");

        var mid = RecordEffectsMath.ClickRipple(RecordEffectsMath.ClickDurationMs / 2, false);
        Check.True(mid != null, "过半还在生命周期里");
        Check.Equal(23, mid!.Value.Radius, "过半半径 7 + round(16.5) = 23");
        Check.Equal(2, mid.Value.Thickness, "右键描边 2");

        var last = RecordEffectsMath.ClickRipple(RecordEffectsMath.ClickDurationMs, true);
        Check.True(last != null, "最后一刻还在");
        Check.Equal(RecordEffectsMath.ClickEndRadius, last!.Value.Radius, "终点半径 40");
        Check.Equal(0, last.Value.Alpha, "终点完全透明");

        Check.True(RecordEffectsMath.ClickRipple(RecordEffectsMath.ClickDurationMs + 1, true) == null,
            "超过 600ms 就没了");
        Check.True(RecordEffectsMath.ClickRipple(-5, true) == null, "时间倒流当作没有");

        // ---- 拖尾：500ms 线性淡出，上限 50 点 ----
        Check.Equal(220, RecordEffectsMath.TrailAlpha(0), "刚画的点最深");
        Check.Equal(110, RecordEffectsMath.TrailAlpha(RecordEffectsMath.LaserLifeMs / 2), "半程一半深");
        Check.Equal(0, RecordEffectsMath.TrailAlpha(RecordEffectsMath.LaserLifeMs), "到期透明");
        Check.Equal(0, RecordEffectsMath.TrailAlpha(RecordEffectsMath.LaserLifeMs + 1), "过期透明");
        Check.Equal(50, RecordEffectsMath.LaserMaxPoints, "最多 50 个点");

        // ---- 按键浮层：先上浮 180ms，停留到 1200ms，再淡出 400ms ----
        var start = RecordEffectsMath.KeyCapState(0);
        Check.True(start != null, "刚按下还在");
        Check.Equal(255, start!.Value.Alpha, "刚按下不透明");
        Check.Equal(0.0, start.Value.Slide, "刚按下没上浮");

        var risen = RecordEffectsMath.KeyCapState(RecordEffectsMath.KeyMoveMs);
        Check.Equal(1.0, risen!.Value.Slide, "180ms 时浮到位");

        var hold = RecordEffectsMath.KeyCapState(RecordEffectsMath.KeyHoldMs);
        Check.Equal(255, hold!.Value.Alpha, "停留期内仍然不透明");
        Check.Equal(1.0, hold.Value.Slide, "停留期内保持浮起");

        int halfFade = RecordEffectsMath.KeyHoldMs + RecordEffectsMath.KeyFadeMs / 2;
        Check.True(RecordEffectsMath.KeyCapState(halfFade)!.Value.Alpha is > 100 and < 200,
            "淡出过半时透明度落在中间");

        Check.True(RecordEffectsMath.KeyCapState(
            RecordEffectsMath.KeyHoldMs + RecordEffectsMath.KeyFadeMs + 1) == null, "淡出结束就没了");
        Check.True(RecordEffectsMath.KeyCapState(-1) == null, "时间倒流当作没有");

        // ---- 修饰键组合名 ----
        Check.Equal("Ctrl+Shift+A",
            RecordEffectsMath.ComboLabel(new HashSet<int> { 0x11, 0x10 }, "A"), "修饰键拼在前面");
        Check.Equal("A", RecordEffectsMath.ComboLabel(new HashSet<int>(), "A"), "没有修饰键就是键名");
        Check.True(RecordEffectsMath.IsModifier(0x11) && RecordEffectsMath.IsModifier(0x5B), "Ctrl / Win 是修饰键");
        Check.False(RecordEffectsMath.IsModifier(0x41), "字母不是修饰键");
        Check.True(RecordEffectsMath.WatchedKeys.Count > 60, "要轮询的键有全套（字母 / 数字 / 功能键）");

        // ---- 坐标映射（跟随鼠标换区域后仍对得上）----
        var area = new Int32Rect(100, 200, 800, 600);
        var mapped = RecordEffectsMath.Map(500, 500, area, 400, 300);
        Check.Equal(200, mapped.X, "横向按比例缩到一半");
        Check.Equal(150, mapped.Y, "纵向按比例缩到一半");

        Check.Equal(0, RecordEffectsMath.Map(100, 200, area, 400, 300).X, "区域左上角映射到 0");
        Check.Equal(-19, RecordEffectsMath.MapAxis(-40, 800, 400),
            "区域外的点映射成负数（不夹到边界，画面上自然裁掉）");
        Check.Equal(7, RecordEffectsMath.MapAxis(7, 0, 400), "源尺寸为 0 时原样返回（不做除法）");
        Check.Equal(7, RecordEffectsMath.MapAxis(14, 800, 400), "整数四舍五入：14/800*400 = 7");

        // ---- 画中画落点：贴左下，放不下就收进来 ----
        var corner = RecordOptions.CameraPlacement(1920, 1080, 150, 48, 48);
        Check.Equal(48, corner.X, "左边距 48");
        Check.Equal(1080 - 48 - 150, corner.Y, "底边距 48（Y 从上算）");
        Check.Equal(150, corner.Side, "边长照给的值");

        var tight = RecordOptions.CameraPlacement(100, 100, 400, 48, 48);
        Check.Equal(100, tight.Side, "画中画不能比画面还大");
        Check.Equal(0, tight.X, "放不下就贴到左上（不留负边距）");
        Check.Equal(0, tight.Y, "纵向同理");

        var huge = RecordOptions.CameraPlacement(600, 400, 150, 999, 999);
        Check.Equal(600 - 150, huge.X, "边距超过画面时收到右下最大位置");
        Check.Equal(0, huge.Y, "纵向收到 0");
    }

    // ============ 录屏：裁剪 / 倍速 ============

    /// <summary>
    /// 裁剪区间夹取、倍速档位、<c>atempo</c> 串联（单级只接受 0.5–2.0）、
    /// 导出参数与文件名；以及从 ffmpeg 输出里读时长 / 音轨。
    /// </summary>
    private static void RecordTrimRules()
    {
        // ---- 区间夹取 ----
        var all = RecordTrim.Normalize(0, 10_000, 1.0, 10_000);
        Check.Equal(0, all.StartMs, "起点");
        Check.Equal(10_000, all.EndMs, "终点");
        Check.Equal(10_000, all.SourceMs, "源时长");
        Check.Equal(10_000, all.OutputMs, "1.0x 成品时长 = 源时长");
        Check.True(all.IsFullRange(10_000), "整片 + 1.0x 是全片");

        var flipped = RecordTrim.Normalize(8_000, 2_000, 1.0, 10_000);
        Check.True(flipped.EndMs > flipped.StartMs, "终点在起点前面时被推开（不会出现负区间）");
        Check.Equal(RecordTrim.MinDurationMs, flipped.SourceMs, "至少留一帧");

        var past = RecordTrim.Normalize(0, 99_999, 1.0, 10_000);
        Check.Equal(10_000, past.EndMs, "终点不越过片长");

        var negative = RecordTrim.Normalize(-5_000, 5_000, 1.0, 10_000);
        Check.Equal(0, negative.StartMs, "起点负数收到 0");

        var fast = RecordTrim.Normalize(0, 6_000, 2.0, 10_000);
        Check.Equal(3_000, fast.OutputMs, "2.0x 成品时长减半");
        Check.False(fast.IsFullRange(10_000), "剪过就不是全片");

        var slow = RecordTrim.Normalize(0, 6_000, 0.5, 10_000);
        Check.Equal(12_000, slow.OutputMs, "0.5x 成品时长翻倍");

        // 倍速夹取 + 吸附到标准档
        Check.Equal(RecordTrim.MaxSpeed, RecordTrim.Normalize(0, 1000, 99, 1000).Speed, "倍速夹到 3.0");
        Check.Equal(RecordTrim.MinSpeed, RecordTrim.Normalize(0, 1000, 0.01, 1000).Speed, "倍速夹到 0.25");
        Check.Equal(1.5, RecordTrim.Normalize(0, 1000, 1.4, 1000).Speed, "1.4 吸附到 1.5");
        Check.Equal(1.0, RecordTrim.Normalize(0, 1000, double.NaN, 1000).Speed, "NaN 当 1.0");

        // 零长度片（读不出时长的坏文件）也不炸
        var empty = RecordTrim.Normalize(0, 0, 1.0, 0);
        Check.True(empty.EndMs >= empty.StartMs, "零长度片不会算出倒置区间");

        // ---- 文案 ----
        Check.Equal("1.0x", RecordTrim.SpeedText(1.0), "1.0 保留一位");
        Check.Equal("0.25x", RecordTrim.SpeedText(0.25), "0.25 不补零");
        Check.Equal("2.0x", RecordTrim.SpeedText(2.0), "2.0 保留一位");
        Check.Equal("00:00.000", RecordTrim.FormatSeconds(0), "零时码");
        Check.Equal("01:02.345", RecordTrim.FormatSeconds(62_345), "带毫秒的时间码");
        Check.Equal("00:16", RecordPlan.FormatClock(16_000), "摘要用整秒时码");

        // ---- atempo 串联：单级只接受 0.5–2.0 ----
        Check.Equal(0, RecordTrim.AtempoChain(1.0).Count, "1.0x 不挂音频滤镜");
        Check.Equal("2,1.5", string.Join(",", RecordTrim.AtempoChain(3.0)), "3.0x = 2.0 + 1.5");
        Check.Equal("0.5,0.5", string.Join(",", RecordTrim.AtempoChain(0.25)), "0.25x = 0.5 + 0.5");
        Check.Equal("2", string.Join(",", RecordTrim.AtempoChain(2.0)), "2.0x 一级就够");
        Check.Equal("0.5", string.Join(",", RecordTrim.AtempoChain(0.5)), "0.5x 一级就够");
        Check.Equal("1.25", string.Join(",", RecordTrim.AtempoChain(1.25)), "1.25x 一级");
        Check.Equal(0, RecordTrim.AtempoChain(0).Count, "0 不产出（调用方自己判）");

        foreach (double speed in RecordTrim.Speeds)
        {
            double product = 1;
            foreach (double factor in RecordTrim.AtempoChain(speed))
            {
                Check.True(factor is >= 0.5 and <= 2.0, $"每级 atempo 都在 0.5–2.0（{speed}x → {factor}）");
                product *= factor;
            }

            if (Math.Abs(speed - 1.0) > 0.0001)
                Check.True(Math.Abs(product - speed) < 0.001, $"{speed}x 的因子乘积等于倍速（{product}）");
        }

        // ---- 导出参数 ----
        var cut = RecordTrim.Normalize(2_000, 8_000, 1.0, 10_000);
        var cutArgs = RecordTrim.ExportArgs("in.mp4", "out.mp4", cut, hasAudio: true);
        string cutLine = string.Join(" ", cutArgs);
        Check.True(cutLine.Contains("-ss 2") && cutLine.Contains("-to 8"), "按源时间切（秒）");
        Check.True(cutLine.IndexOf("-ss", StringComparison.Ordinal)
            < cutLine.IndexOf("-i in.mp4", StringComparison.Ordinal),
            "-ss / -to 必须放在 -i 之前（否则时间基准变成输出时间轴，倍速会剪错）");
        Check.True(cutLine.Contains("-t 6"), "输出侧 -t 按成品时长收口（1.0x 剪 6 秒 = 6 秒）");
        Check.True(cutLine.IndexOf("-t 6", StringComparison.Ordinal)
            > cutLine.IndexOf("-i in.mp4", StringComparison.Ordinal),
            "-t 是输出选项，要放在 -i 之后");
        Check.True(cutLine.Contains("-c:a aac"), "声音一律重编码（切点不在音频包边界上）");
        Check.False(cutLine.Contains("setpts"), "1.0x 不改画面时间戳");
        Check.False(cutLine.Contains("atempo"), "1.0x 不挂 atempo");
        Check.True(cutLine.Contains("libx264"), "画面重编码（裁剪必须重编）");
        Check.True(cutLine.Contains("+faststart"), "导出也转 faststart");
        Check.Equal("out.mp4", cutArgs[^1], "输出在最后一个参数");

        var sped = RecordTrim.Normalize(1_000, 4_000, 2.0, 10_000);
        var spedArgs = RecordTrim.ExportArgs("in.mp4", "out.mp4", sped, hasAudio: true);
        string spedLine = string.Join(" ", spedArgs);
        Check.True(spedLine.Contains("setpts=(PTS-STARTPTS)/2"), "画面按倍速改时间戳");
        Check.True(spedLine.Contains("atempo=2"), "声音用 atempo 跟速");
        Check.True(spedLine.Contains("-t 1.5"), "成品时长按倍速换算（3 秒剪成 2 倍 = 1.5 秒）");

        var silent = RecordTrim.ExportArgs("in.mp4", "out.mp4", cut, hasAudio: false);
        Check.True(string.Join(" ", silent).Contains("-an"), "没有音轨就明确 -an");

        // ---- 导出文件名：永远不覆盖原片 ----
        string dir = Path.Combine("C:\\v", "records");
        Check.Equal(Path.Combine(dir, "rec-裁剪.mp4"), RecordTrim.OutputPath(dir, "rec", _ => false),
            "默认是「原名字-裁剪」");
        Check.Equal(Path.Combine(dir, "rec-裁剪-2.mp4"),
            RecordTrim.OutputPath(dir, "rec", p => p.EndsWith("rec-裁剪.mp4", StringComparison.Ordinal)),
            "已经存在就加序号");

        // ---- ffmpeg 输出解析（老记录 / 外部文件才需要问）----
        const string info =
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':\n" +
            "  Duration: 00:01:02.34, start: 0.000000, bitrate: 1200 kb/s\n" +
            "  Stream #0:0[0x1](und): Video: h264 (High), yuv420p, 1920x1080, 30 fps\n" +
            "  Stream #0:1[0x2](und): Audio: aac (LC), 48000 Hz, stereo, fltp, 192 kb/s";

        Check.Equal(62_340, RecordTrimSession.ParseDurationMs(info), "从 Duration 行读毫秒");
        Check.True(RecordTrimSession.HasAudioStream(info), "有 Audio 流");
        Check.False(RecordTrimSession.HasAudioStream(
            "  Stream #0:0[0x1](und): Video: h264 (High), yuv420p, 1920x1080, 30 fps"), "只有视频流");
        Check.Equal(0, RecordTrimSession.ParseDurationMs("garbage"), "读不出时长返回 0");
        Check.Equal(0, RecordTrimSession.ParseDurationMs("Duration: N/A, start: 0"), "N/A 也返回 0");
    }

    /// <summary>
    /// P1：与 Snow Shot 对齐的新标注（直线 / 测距 / 测角 / 括号 / 气泡 / 放大镜 / 水印 / 矩形荧光 / 画笔滤镜）
    /// 的几何与命中。**不碰窗口、不碰位图**，纯数据断言。
    /// </summary>
    private static void ParityMarks()
    {
        // ---- 直线 ----
        var line = new Mark.Line(new Point(0, 0), new Point(100, 0), Colors.Red, 4, false);
        Check.Equal(new Rect(-2, -2, 104, 4), line.Bounds, "直线的外接框按线宽外扩");
        Check.Equal(new Rect(0, 0, 100, 0), line.Frame, "直线的选中框贴着笔迹");
        Check.True(line.HitTest(new Point(50, 1), 0), "直线上命中");
        Check.False(line.HitTest(new Point(50, 10), 0), "离得远不命中");
        Check.Equal(new Point(5, 5), ((Mark.Line)line.MoveBy(new Vector(5, 5))).A, "直线整体平移");
        var wide = (Mark.Line)line.ScaleTo(new Rect(0, 0, 100, 100), new Rect(0, 0, 200, 200));
        Check.Equal(8d, wide.Width, "直线缩放时线宽跟着乘 2");
        Check.Equal(new Point(200, 0), wide.B, "直线缩放时终点跟着搬");

        // ---- 测距：标签 = 值 + 单位（上游默认厘米、0 位小数） ----
        var dist = new Mark.Distance(new Point(0, 0), new Point(100, 0), Colors.Red, 4);
        Check.Equal("100 cm", dist.LabelText(1), "测距标签：100 DIP → 100 cm");
        Check.Equal("200 cm", dist.LabelText(2), "测距标签：缩放 2 → 200（物理像素 × 系数）");
        Check.Equal(50d, dist.Midpoint.X, "标签挂在线段中点");
        Check.True(dist.Bounds.Contains(dist.LabelBox(1)), "标签框算进外接框（拖动时不会切掉数字）");
        Check.True(dist.HitTest(dist.Midpoint, 0), "线段中间命中");
        Check.True(dist.HitTest(new Point(50, dist.LabelBox(1).Y + 2), 0), "标签本身也命中（点数字就能选中它）");
        Check.Equal(0, dist.Spec.Decimals, "测距默认 0 位小数");
        Check.Equal(Arrowhead.Bar, dist.Spec.Endpoint, "测距默认横杠端点");
        var scaled = (Mark.Distance)dist.ScaleTo(new Rect(0, 0, 100, 100), new Rect(0, 0, 200, 200));
        Check.Equal("200 cm", scaled.LabelText(1), "缩放后长度按新几何算（200 DIP）");

        // ---- 测角：两条臂 → 角度 / 平分线；滚轮只改张角 ----
        var right = Mark.Angle.FromArms(new Point(0, 0), new Point(100, 0), new Point(0, 100), Colors.Red, 4);
        Check.Equal(90d, Math.Round(right.Degrees, 6), "垂直两臂 = 90°");
        Check.Equal(100d, Math.Round(right.Radius, 6), "臂长取两条边的平均");
        Check.Equal(new Point(100, 0), new Point(Math.Round(right.A.X, 6), Math.Round(right.A.Y, 6)), "第一条臂端点");
        Check.Equal(new Point(0, 100), new Point(Math.Round(right.B.X, 6), Math.Round(right.B.Y, 6)), "第二条臂端点");

        var reflex = Mark.Angle.FromArms(new Point(0, 0), new Point(100, 0), new Point(0, -100), Colors.Red, 4);
        Check.Equal(90d, Math.Round(reflex.Degrees, 6), "顺时针那侧也按「小的那一边」算 90°");

        var sharper = right.WithDegrees(45);
        Check.Equal(45d, sharper.Degrees, "改角度只换张角");
        Check.Equal(right.Vertex, sharper.Vertex, "顶点不动");
        Check.Equal(right.Radius, sharper.Radius, "臂长不动");
        Check.Equal(right.Bisector, sharper.Bisector, "平分线方向不动");
        Check.Equal("45°", sharper.LabelText, "角度标签带度号");
        Check.True(sharper.HitTest(sharper.A, 0), "第一条臂上命中");
        Check.False(sharper.HitTest(new Point(50, 50), 0), "两条臂之间空着的地方不命中");

        // ---- 括号：花括号（三点定义，四段三次贝塞尔）----
        var bracket = new Mark.Bracket(new Point(0, 0), new Point(40, 100), Colors.Red, 4);
        Check.Equal(4, bracket.Segments.Length, "花括号是四段弧");
        Check.Equal(new Point(0, 0), bracket.A, "拖拽形态：起点在起点的 x、纵向最小处");
        Check.Equal(new Point(0, 100), bracket.B, "拖拽形态：终点落回起点的 x");
        Check.Equal(new Point(40, 50), bracket.Apex, "尖在终点的 x 与中线上（开口朝拖动方向）");
        Check.True(bracket.Frame.Contains(bracket.Apex), "尖算进选中框");
        Check.True(bracket.HitTest(new Point(40, 50), 1), "尖上命中");
        Check.False(bracket.HitTest(new Point(0, 200), 0), "括号外不命中");

        // ---- 气泡：身体 + 尾巴 ----
        var bubble = new Mark.SpeechBubble(new Rect(0, 0, 200, 100), Colors.Red, 4);
        Check.Equal(3, bubble.Tail.Length, "尾巴是三角形");
        Check.Equal(136d, bubble.Frame.Bottom, "尾巴尖算进选中框");
        Check.True(bubble.HitTest(new Point(150, 50), 0), "气泡内命中");
        Check.False(bubble.HitTest(new Point(400, 400), 0), "气泡外不命中");
        Check.Equal(24d, bubble.TailInset, "尾巴根部离左边 24 DIP（宽度 200 的 12%）");
        var moved = (Mark.SpeechBubble)bubble.MoveBy(new Vector(10, 20));
        Check.Equal(new Rect(10, 20, 200, 100), moved.Body, "气泡整体平移");

        // ---- 放大镜：镜头 = 源 × 倍率，源与镜头各自定位 ----
        var lensSpec = MagnifierSpec.Default with { Factor = 2, StrokeWidth = 3 };
        var lens = new Mark.Magnifier(new Rect(0, 0, 200, 100), lensSpec);
        Check.Equal(new Rect(50, 25, 100, 50), lens.SourceRect, "2 倍镜头取中心一半大小的区域");
        Check.Equal(new Rect(0, 0, 200, 100), lens.Lens, "镜头就是拖出来的框");
        Check.Equal(new Point(100, 50), lens.LensCenter, "镜头中心");
        Check.Equal(new Point(100, 50), lens.SourceCenter, "新建时源与镜头同心");
        Check.Equal(1d, new Mark.Magnifier(new Rect(0, 0, 10, 10),
            MagnifierSpec.Default with { Factor = 0.2 }).Factor, "倍率下限 1（上游 1–10）");
        Check.Equal(10d, new Mark.Magnifier(new Rect(0, 0, 10, 10),
            MagnifierSpec.Default with { Factor = 99 }).Factor, "倍率上限 10");
        Check.True(lens.HitTest(new Point(100, 50), 0), "镜头里命中");
        Check.False(lens.HitTest(new Point(300, 50), 0), "镜头外不命中");
        Check.Equal(new Rect(10, 10, 200, 100), ((Mark.Magnifier)lens.MoveBy(new Vector(10, 10))).Lens,
            "镜头整体平移");
        Check.Equal(new Rect(150, 110, 100, 50), ((Mark.Magnifier)lens.WithSourceCenter(new Point(200, 135))).SourceRect,
            "源中心单独搬（镜头不动）");

        // ---- 水印：整图效果，不参与选中 / 拖动 ----
        var wmSpec = new WatermarkSpec("保密", Colors.White, 24, string.Empty, -30, 120, 0.15);
        var wm = new Mark.Watermark(wmSpec);
        Check.Equal(Rect.Empty, wm.Bounds, "水印没有外接框");
        Check.Equal(Rect.Empty, wm.Frame, "水印没有选中框");
        Check.False(wm.CanScale, "水印不能缩放");
        Check.False(wm.HitTest(new Point(10, 10), 999), "水印点不中（永远不挡选择）");
        Check.True(ReferenceEquals(wm, wm.MoveBy(new Vector(10, 10))), "水印移动返回自己");
        Check.True(ReferenceEquals(wm, wm.ScaleTo(new Rect(0, 0, 1, 1), new Rect(0, 0, 2, 2))), "水印缩放返回自己");
        Check.Equal(string.Empty, WatermarkSpec.Default.Text, "上游默认水印文字是空（没填就什么都不画）");
        Check.Equal(-30d, wm.Rotation, "水印角度字段（不叫 Angle：那是标注类型名）");
        Check.Equal(216d, wmSpec.Step, "格距 = max(gap 120 + 24×4, 32) = 216");
        Check.Equal(32d, (new WatermarkSpec("x", Colors.Black, 2, "", 0, 10, 1)).Step,
            "格距有 32 的下限（字号小、间距小时别挤成一片）");
        Check.True(Math.Abs(wmSpec.LineHeight - 28.8) < 1e-9, "排版行高 = 1.2 × 字号");

        // ---- 矩形荧光 ----
        var glow = new Mark.Highlight(new Point(0, 0), new Point(100, 50), Colors.Yellow, 0.35);
        Check.Equal(new Rect(0, 0, 100, 50), glow.Bounds, "矩形荧光就是一个矩形");
        Check.True(glow.HitTest(new Point(50, 25), 0), "矩形里命中");
        Check.False(glow.HitTest(new Point(200, 25), 0), "矩形外不命中");
        Check.Equal(1d, new Mark.Highlight(new Point(0, 0), new Point(1, 1), Colors.Yellow, 9).Opacity,
            "不透明度上限 1");
        Check.Equal(0.02, new Mark.Highlight(new Point(0, 0), new Point(1, 1), Colors.Yellow, -1).Opacity,
            "不透明度下限 0.02");

        // ---- 画笔滤镜：笔迹遮罩（纯像素计算）----
        var stroke = new[] { new Point(0, 0), new Point(10, 0) };
        var mask = MarkRenderer.BuildStrokeMask(stroke, 2, 20, 10, 0, 0, 1);
        Check.Equal(200, mask.Length, "遮罩尺寸 = 裁块尺寸");
        Check.True(mask[0 * 20 + 5], "笔迹上的像素在遮罩里");
        Check.True(mask[2 * 20 + 5], "半径内的像素在遮罩里");
        Check.False(mask[3 * 20 + 5], "半径外的像素不在遮罩里");
        Check.False(mask[0 * 20 + 15], "笔迹没经过的地方不在遮罩里");
        Check.True(MarkRenderer.BuildStrokeMask(Array.Empty<Point>(), 2, 20, 10, 0, 0, 1).All(v => !v),
            "没有点就没有遮罩（一个像素都不选）");

        var single = MarkRenderer.BuildStrokeMask(new[] { new Point(5, 5) }, 2, 20, 10, 0, 0, 1);
        Check.True(single[5 * 20 + 5], "单点笔迹也有圆点遮罩");

        // 笔迹滤镜本身：外接框 / 平移 / 缩放 / 命中
        var brush = new Mark.BrushFilter(stroke, 8, FilterKind.Blur, 40);
        Check.Equal(new Rect(-4, -4, 18, 8), brush.Bounds, "画笔滤镜外接框按笔头外扩");
        Check.True(brush.HitTest(new Point(5, 2), 0), "笔迹上命中");
        Check.False(brush.HitTest(new Point(5, 30), 0), "离笔迹远不命中");
        Check.Equal(16d, ((Mark.BrushFilter)brush.ScaleTo(
            new Rect(0, 0, 10, 10), new Rect(0, 0, 20, 20))).Width, "缩放时笔头跟着乘 2");

        // ---- 几何补充：极坐标 / 角度归一 / 夹角 / 三角形 ----
        var polar = MarkGeometry.Polar(new Point(10, 10), 0, 5);
        Check.Equal(new Point(15, 10), new Point(Math.Round(polar.X, 6), Math.Round(polar.Y, 6)),
            "极坐标沿 X 轴取点");
        Check.Equal(0d, Math.Round(MarkGeometry.NormalizeAngle(Math.PI * 2), 6), "2π 归一到 0");
        Check.Equal(Math.Round(Math.PI, 6), Math.Round(MarkGeometry.NormalizeAngle(-Math.PI), 6), "负角归一到正角");
        Check.Equal(Math.Round(Math.PI / 2, 6),
            Math.Round(MarkGeometry.AngleBetween(new Vector(1, 0), new Vector(0, 1)), 6), "垂直向量夹角 90°");
        Check.Equal(0d, MarkGeometry.AngleBetween(new Vector(0, 0), new Vector(1, 0)), "零向量夹角按 0 算");
        Check.True(MarkGeometry.InTriangle(new Point(1, 1), new Point(0, 0), new Point(4, 0), new Point(0, 4)),
            "三角形里");
        Check.False(MarkGeometry.InTriangle(new Point(3, 3), new Point(0, 0), new Point(4, 0), new Point(0, 4)),
            "三角形外");
    }

    /// <summary>8×8 像素的假底图 + 缩放 2 的文档（不开窗口）。</summary>
    /// <summary>
    /// P1-b：像素级智能擦除（对齐上游 <c>FilterType.SmartErase</c>）。
    /// 三级求解各来一遍：纯色 / 渐变走仿射曲面，条纹走周期纹理，噪声走多尺度块匹配；
    /// 再加「没有素材 / 没有洞 / 确定性 / 大洞降采样」四条边界。
    /// </summary>
    private static void SmartEraseCases()
    {
        const int W = 64;
        const int H = 48;

        // BGRA 缓冲（Alpha 全不透明）
        byte[] Buffer(Func<int, int, (byte r, byte g, byte b)> color)
        {
            var buffer = new byte[W * H * 4];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    var (r, g, b) = color(x, y);
                    int at = (y * W + x) * 4;
                    buffer[at] = b;
                    buffer[at + 1] = g;
                    buffer[at + 2] = r;
                    buffer[at + 3] = 0xFF;
                }

            return buffer;
        }

        bool[] Hole(int x0, int y0, int x1, int y1)
        {
            var mask = new bool[W * H];
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    mask[y * W + x] = true;
            return mask;
        }

        int At(byte[] buffer, int x, int y, int channel) => buffer[(y * W + x) * 4 + channel];

        // ---- 第一级：纯色背景（仿射曲面一次命中）----
        var flat = Buffer((_, _) => ((byte)0x40, (byte)0x80, (byte)0xC0));
        var flatHole = Hole(20, 16, 44, 32);
        Check.True(SmartErase.Reconstruct(flat, W, H, flatHole, default), "智能擦除：纯色背景能重建");
        Check.True(Math.Abs(At(flat, 32, 24, 0) - 0xC0) <= 2, $"纯色洞内还原成同一个蓝色（B={At(flat, 32, 24, 0)}）");
        Check.True(Math.Abs(At(flat, 32, 24, 1) - 0x80) <= 2, "纯色洞内绿通道一致");
        Check.True(Math.Abs(At(flat, 32, 24, 2) - 0x40) <= 2, "纯色洞内红通道一致");
        Check.Equal((byte)0xFF, At(flat, 32, 24, 3), "重建只改颜色，Alpha 一个字节都不动");

        // ---- 第一级：竖直渐变（平面拟合把斜坡接上，不是涂成一块）----
        var gradient = Buffer((_, y) => ((byte)Math.Clamp(y * 4, 0, 255), 100, 60));
        var gradientHole = Hole(10, 10, 50, 40);
        Check.True(SmartErase.Reconstruct(gradient, W, H, gradientHole, default), "智能擦除：渐变能重建");
        for (int y = 12; y < 38; y += 6)
            Check.True(Math.Abs(At(gradient, 30, y, 2) - Math.Clamp(y * 4, 0, 255)) <= 6,
                $"渐变第 {y} 行接上了（R={At(gradient, 30, y, 2)} 期望 {Math.Clamp(y * 4, 0, 255)}）");

        // ---- 第二级：周期条纹（8 像素一格，按周期抄回来）----
        var stripes = Buffer((x, _) => x % 8 < 4
            ? ((byte)0xC8, (byte)0xC8, (byte)0xC8)
            : ((byte)0x28, (byte)0x28, (byte)0x28));
        var stripeHole = Hole(24, 8, 40, 40);
        Check.True(SmartErase.Reconstruct(stripes, W, H, stripeHole, default), "智能擦除：周期条纹能重建");
        int stripeError = 0;
        for (int x = 24; x < 40; x++)
        {
            byte want = x % 8 < 4 ? (byte)0xC8 : (byte)0x28;
            if (Math.Abs(At(stripes, x, 20, 0) - want) <= 4) continue;
            stripeError++;
        }

        Check.Equal(0, stripeError, "条纹按原周期接上（每一列都回到它该有的明暗）");

        // ---- 第三级：噪声纹理（前两级都不命中，交给多尺度块匹配）----
        byte[] Noise()
        {
            uint seed = 12345;
            return Buffer((x, y) =>
            {
                seed = seed * 1664525 + 1013904223;
                byte value = (byte)(40 + seed % 180);
                return (value, (byte)(value / 2 + 40), (byte)(x + y % 7));
            });
        }

        var noise = Noise();
        var noiseHole = Hole(18, 14, 46, 34);
        Check.True(SmartErase.Reconstruct(noise, W, H, noiseHole, default), "智能擦除：噪声纹理能重建");
        long sum = 0;
        int count = 0;
        for (int y = 14; y < 34; y++)
            for (int x = 18; x < 46; x++)
            {
                sum += At(noise, x, y, 0);
                count++;
            }

        long expected = 0;
        int ring = 0;
        for (int y = 6; y < 42; y++)
            for (int x = 10; x < 54; x++)
            {
                if (x >= 18 && x < 46 && y >= 14 && y < 34) continue;
                expected += At(noise, x, y, 0);
                ring++;
            }

        long mean = sum / Math.Max(1, count);
        long around = expected / Math.Max(1, ring);
        Check.True(Math.Abs(mean - around) <= 20,
            $"噪声洞内均值贴近周围（洞内 {mean} / 周围 {around}）——不是留一片死黑");

        // ---- 确定性：同一输入跑两次，逐字节一致 ----
        var again = Noise();
        SmartErase.Reconstruct(again, W, H, noiseHole, default);
        bool identical = true;
        for (int i = 0; i < noise.Length; i++)
            if (noise[i] != again[i])
            {
                identical = false;
                break;
            }

        Check.True(identical, "同输入同输出（种子固定，不靠随机结果碰运气）");

        // ---- 边界：没有洞 = 什么都不做 ----
        var untouched = Buffer((x, y) => ((byte)x, (byte)y, 7));
        var before = (byte[])untouched.Clone();
        Check.True(SmartErase.Reconstruct(untouched, W, H, new bool[W * H], default), "没有洞：直接算成功");
        Check.True(before.SequenceEqual(untouched), "没有洞：一个像素都不改");

        // ---- 边界：整块都被遮住 = 没有素材，老实说做不到 ----
        var all = Buffer((_, _) => (10, 20, 30));
        var everyPixel = new bool[W * H];
        Array.Fill(everyPixel, true);
        Check.False(SmartErase.Reconstruct(all, W, H, everyPixel, default), "没有素材：返回失败（不假装擦掉了）");

        // ---- 大洞：自动降采样，仍然出结果（上游这时会交给 GPU）----
        const int BW = 160;
        const int BH = 120;
        var big = new byte[BW * BH * 4];
        for (int y = 0; y < BH; y++)
            for (int x = 0; x < BW; x++)
            {
                int at = (y * BW + x) * 4;
                big[at] = (byte)(x / 2);
                big[at + 1] = (byte)(y / 2);
                big[at + 2] = 90;
                big[at + 3] = 0xFF;
            }

        var bigHole = new bool[BW * BH];
        for (int y = 20; y < 100; y++)
            for (int x = 30; x < 130; x++)
                bigHole[y * BW + x] = true;
        Check.True(SmartErase.Reconstruct(big, BW, BH, bigHole, default), "大洞也能重建（自动降采样）");
        Check.True(big[(60 * BW + 80) * 4 + 3] == 0xFF, "大洞重建保留 Alpha");
    }

    /// <summary>
    /// P1-b：两把橡皮落的是「恢复原图」块（对齐上游 <c>RestoreBackground</c>），不是按对象删除。
    /// 恢复块不可点中（选择 / 点选橡皮都碰不到），渲染时只还原底图像素。
    /// </summary>
    private static void RestoreMarks()
    {
        const int W = 100, H = 60;
        var buffer = new byte[W * H * 4];
        for (int i = 0; i < W * H; i++)
        {
            buffer[i * 4] = 0xE0;      // B
            buffer[i * 4 + 1] = 0x40;  // G
            buffer[i * 4 + 2] = 0x10;  // R
            buffer[i * 4 + 3] = 0xFF;
        }

        var baseImage = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, buffer, W * 4);
        baseImage.Freeze();

        CaptureDocument NewDoc()
        {
            var doc = new CaptureDocument(baseImage, new Int32Rect(0, 0, W, H), 1.0);
            doc.Area = new Int32Rect(0, 0, W, H);
            return doc;
        }

        byte[] Render(CaptureDocument doc)
        {
            var frame = doc.Render();
            var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
            frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
            return pixels;
        }

        (byte b, byte g, byte r) At(byte[] pixels, int x, int y)
        {
            int i = (y * W + x) * 4;
            return (pixels[i], pixels[i + 1], pixels[i + 2]);
        }

        // ---- 命中：恢复块永远点不中（上游不可选、旧橡皮也删不掉）----
        var only = NewDoc();
        only.Add(new Mark.Restore(new Point(10, 10), new Point(50, 40)));
        Check.Equal(-1, only.HitTest(new Point(30, 25), 4), "恢复块点不中：选择 / 橡皮都碰不到它");

        // ---- 矩形橡皮：整块还原成原图，块外仍是盖上去的标注 ----
        var rectDoc = NewDoc();
        rectDoc.Add(new Mark.Highlight(new Point(0, 0), new Point(W, H), Colors.Magenta, 1));
        rectDoc.Add(new Mark.Restore(new Point(20, 10), new Point(80, 50)));
        var rectPixels = Render(rectDoc);
        var restored = At(rectPixels, 50, 30);
        var covered = At(rectPixels, 5, 30);
        Check.True(restored.r < 40 && restored.b > 150, $"恢复块里露出原图（r={restored.r} b={restored.b}）");
        Check.True(covered.r > 200 && covered.b > 200, $"恢复块外还是盖上去的洋红（r={covered.r} b={covered.b}）");

        // ---- 画笔橡皮：只有笔迹压过的那一条还原 ----
        var brushDoc = NewDoc();
        brushDoc.Add(new Mark.Highlight(new Point(0, 0), new Point(W, H), Colors.Magenta, 1));
        brushDoc.Add(new Mark.Restore(new Point(50, 10), new Point(50, 50),
            new[] { new Point(50, 10), new Point(50, 50) }, 12));
        var brushPixels = Render(brushDoc);
        var wiped = At(brushPixels, 50, 30);
        var beside = At(brushPixels, 20, 30);
        Check.True(wiped.r < 40 && wiped.b > 150, $"笔迹压过的地方还原成原图（r={wiped.r} b={wiped.b}）");
        Check.True(beside.r > 200 && beside.b > 200, $"笔迹旁边的洋红没被抹掉（r={beside.r} b={beside.b}）");

        // ---- 几何：外接框按笔迹 + 线宽，平移搬笔迹，缩放放大线宽 ----
        var stroke = new Mark.Restore(new Point(0, 0), new Point(0, 0),
            new[] { new Point(0, 0), new Point(10, 0) }, 4);
        Check.Equal(new Rect(-2, -2, 14, 4), stroke.Bounds, "画笔恢复块的外接框按笔迹 + 线宽算");
        var movedStroke = (Mark.Restore)stroke.MoveBy(new Vector(5, 5));
        Check.Equal(new Point(15, 5), movedStroke.Path![1], "平移把笔迹一起搬走");
        var scaledStroke = (Mark.Restore)stroke.ScaleTo(new Rect(0, 0, 10, 10), new Rect(0, 0, 20, 20));
        Check.Equal(8d, scaledStroke.Width, "缩放时线宽跟着放大");
    }

    private static CaptureDocument NewDocument()
    {
        var pixels = new byte[8 * 8 * 4];
        var baseImage = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Pbgra32, null, pixels, 8 * 4);
        baseImage.Freeze();
        return new CaptureDocument(baseImage, new Int32Rect(100, 100, 8, 8), 2.0);
    }
}

