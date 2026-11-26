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

/// <summary>CaptureProSuite 的「上游对齐：标注 / 智能擦除 / 还原 / 预览摆位」部分。</summary>
internal static partial class CaptureProSuite
{
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

    /// <summary>
    /// 「原文」预览的摆位与尺寸（照上游 <c>ScreenshotOriginalImagePreviewWindow::placement</c>，
    /// 但**不再按图片原始像素开窗**）：右 → 左 → 上 → 下，四周都不够才贴识别窗内侧右边。
    /// </summary>
    private static void PreviewPlacement()
    {
        var work = new Rect(0, 0, 1920, 1040);
        var host = new Rect(400, 200, 600, 500);
        var size = new Size(360, 270);

        Rect right = OriginalPreviewPlacement.Place(host, work, size);
        Check.Equal(host.Right + OriginalPreviewPlacement.Gap, right.Left, "右边放得下 → 贴识别窗右侧");
        Check.Equal(host.Top, right.Top, "纵向对齐识别窗顶边（再夹进工作区）");
        Check.True(!OriginalPreviewPlacement.Overlaps(right, host), "贴在外面不算压住识别窗");

        var wide = new Rect(1500, 200, 400, 500);       // 右边只剩 20，放不下
        Rect left = OriginalPreviewPlacement.Place(wide, work, size);
        Check.Equal(wide.Left - OriginalPreviewPlacement.Gap - size.Width, left.Left,
            "右边放不下 → 翻到左侧");

        var full = new Rect(20, 400, 1880, 500);        // 左右都放不下，上方够
        Rect above = OriginalPreviewPlacement.Place(full, work, size);
        Check.Equal(full.Top - OriginalPreviewPlacement.Gap - size.Height, above.Top,
            "左右都不够 → 放到上方");

        var tall = new Rect(20, 20, 1880, 700);         // 上方也放不下，下方够
        Rect below = OriginalPreviewPlacement.Place(tall, work, size);
        Check.Equal(tall.Bottom + OriginalPreviewPlacement.Gap, below.Top,
            "上方也不够 → 放到下方");

        // 四周都不够（识别窗几乎满屏）→ 贴内侧右边、垂直居中，像一张画中画
        var huge = new Rect(0, 0, 1920, 1040);
        Rect inside = OriginalPreviewPlacement.Place(huge, work, size);
        Check.Equal(huge.Right - OriginalPreviewPlacement.Inset - size.Width, inside.Left,
            "四周都不够 → 贴识别窗内侧右边");
        Check.True(inside.Top >= work.Top && inside.Bottom <= work.Bottom + 0.5,
            "内侧摆放也夹进工作区");
        Check.True(OriginalPreviewPlacement.Overlaps(inside, huge),
            "内侧摆放确实压在识别窗上（投影要加重）");

        Rect preferLeft = OriginalPreviewPlacement.Place(host, work, size, preferLeft: true);
        Check.Equal(host.Left - OriginalPreviewPlacement.Gap - size.Width, preferLeft.Left,
            "preferLeft = 优先左侧");

        // 尺寸：工作区的 22%，夹在 300–420 宽；高 = 宽 × 0.75，不超过工作区高的 45%
        Check.Equal(420d, Math.Round(OriginalPreviewPlacement.DefaultSize(work).Width),
            "1920 宽 → 卡片夹到 420");
        Check.Equal(300d, Math.Round(OriginalPreviewPlacement.DefaultSize(new Rect(0, 0, 1024, 768)).Width),
            "1024 宽 → 卡片夹到 300");
        Size shortWork = OriginalPreviewPlacement.DefaultSize(new Rect(0, 0, 1920, 600));
        Check.True(shortWork.Height <= 600 * 0.45 + 0.5, "矮屏幕时卡片高不超过工作区 45%");

        Check.True(OriginalPreviewPlacement.Place(host, work, new Size(0, 0)).IsEmpty,
            "尺寸为 0 时返回空矩形（不摆位）");
    }

    /// <summary>
    /// 原文栏的 1:1 布局（照「能 1:1 就 1:1」的规则）：宽度够 → 左右并列；宽度不够但高度够 →
    /// 上下并列；两样都不够 → 退回「右侧一栏 + 缩放」。
    /// </summary>
    private static void PreviewLayout()
    {
        var chrome = new Size(26, 170);
        var work = new Size(1920, 1040);

        // 858×748：2×858 + 缝 + 开销 ≤ 1920，748 + 开销 ≤ 1040 → 左右并列
        var wide = OriginalPreviewLayout.Plan(new Size(858, 748), work, chrome);
        Check.Equal(OriginalPaneSide.Right, wide.Side, "宽度够 → 左右并列");
        Check.True(wide.OneToOne, "左右并列时两个都按 100% 显示全");
        Check.True(wide.Window.Width >= 858 * 2, "窗口宽到能放下两栏");
        Check.Equal(860d, wide.Pane, "原图栏 = 图宽 + 2");

        // 1280 宽放不下 2×700，但高度够 → 上下并列
        var narrow = OriginalPreviewLayout.Plan(new Size(700, 400), new Size(1280, 1040), chrome);
        Check.Equal(OriginalPaneSide.Bottom, narrow.Side, "宽度不够、高度够 → 上下并列");
        Check.True(narrow.OneToOne, "上下并列也是 1:1");
        Check.True(narrow.Window.Height >= 400 * 2, "窗口高到能放下两栏");
        Check.Equal(402d, narrow.Pane, "下面那一栏 = 图高 + 2");

        // 3000×1600 的截图放在 1080p 屏上：两样都不够 → 退回缩放
        var huge = OriginalPreviewLayout.Plan(new Size(3000, 1600), work, chrome);
        Check.True(!huge.OneToOne, "左右上下都放不下 → 退回缩放");
        Check.Equal(OriginalPaneSide.Right, huge.Side, "退回缩放时贴右侧");
        Check.True(huge.Pane >= OriginalPreviewLayout.MinPane &&
                   huge.Pane <= OriginalPreviewLayout.MaxPane, "缩放时那一栏有上下限（别把文字层挤没）");

        // 高度是瓶颈（宽够、高不够）也不能硬上左右并列
        var tall = OriginalPreviewLayout.Plan(new Size(800, 1000), work, chrome);
        Check.True(!tall.OneToOne, "高度不够时不做 1:1（800×1000 在 1040 高的屏上放不下两栏）");
    }
}
