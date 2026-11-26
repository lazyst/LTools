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

/// <summary>CaptureProSuite 的「选区形状 / 文字度量 / 对比度」部分。</summary>
internal static partial class CaptureProSuite
{
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

        // 多块选区里按下拖动：命中哪一块就只挪那一块（照上游 beginMoveDrag → beginRegionMove）；
        // 按住「移动整个选区」（Space）才整体挪。重叠时取列表最后一块（叠放顺序：后画的在上）。
        Check.Equal(0, SelectionSet.TopmostAt(set, new Point(1, 1)), "命中最上面那块加区");
        Check.Equal(2, SelectionSet.TopmostAt(set, new Point(22, 6)), "命中第二块加区");
        Check.Equal(-1, SelectionSet.TopmostAt(set, new Point(4, 4)), "挖掉的洞里命中不到任何一块");
        Check.Equal(-1, SelectionSet.TopmostAt(set, new Point(-5, 50)), "选区外命中不到");
        var overlap = new[]
        {
            SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 20, 20)),
            SelectionRegion.FromRect(RegionMode.Add, new Rect(10, 10, 20, 20)),
        };
        Check.Equal(1, SelectionSet.TopmostAt(overlap, new Point(15, 15)), "重叠处命中后画的那一块（最上层）");
        Check.Equal(0, SelectionSet.TopmostAt(overlap, new Point(5, 5)), "只有下面那块盖住的地方还是命中它");
        Check.Equal(-1, SelectionSet.TopmostAt(new[] { cut }, new Point(3, 3)), "只有减区：命中不到任何一块");

        Check.True(SelectionSet.MovesSingleRegion(3, 1, false), "多块 + 没按住 Space：只挪命中的那一块");
        Check.False(SelectionSet.MovesSingleRegion(3, 1, true), "按住 Space：挪整个选区");
        Check.False(SelectionSet.MovesSingleRegion(1, 0, false), "单块选区：没有「哪一块」，整体动");
        Check.False(SelectionSet.MovesSingleRegion(3, -1, false), "没命中任何一块：别当成单块拖动");

        var (edgesX, edgesY) = SelectionSet.OtherEdges(set, 2);
        Check.Equal(4, edgesX.Length, "吸附参考边只取别的区域（两块 × 左右）");
        Check.True(Array.IndexOf(edgesX, 0.0) >= 0 && Array.IndexOf(edgesX, 10.0) >= 0,
            "参考边里有第一块的左右");
        Check.True(Array.IndexOf(edgesY, 0.0) >= 0 && Array.IndexOf(edgesY, 10.0) >= 0,
            "参考边里有第一块的上下");
        Check.Equal(0, SelectionSet.OtherEdges(new[] { add }, 0).Xs.Length, "只有一块时没有别的参考边");

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
}
