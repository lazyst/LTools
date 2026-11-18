using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 多选（P2-e3）：选区切族 / 显示值是哪一条、Shift+点选与框选的命中规则。
/// </summary>
/// <remarks>
/// 照上游 <c>selection.rs</c> 的 <c>toggle_selection</c> / <c>marquee_candidate_elements</c>
/// （<c>selection_ids_intersecting_bounds</c>）与 <c>style.rs</c> 的 <c>style_toolbar_source</c>
/// （选区按固定优先顺序挑族，显示值优先主选中项）。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void SelectionMulti()
    {
        // 一份混合文档：形状（低优先）、文字（高优先）、箭头、水印（不归族）
        var shapeSpec = new ShapeSpec(ShapeKind.Rectangle, Colors.Red, 2, StrokeKind.Solid,
            StrokeRendering.Artist, Colors.Transparent, FillKind.None, 0, 1);
        var shape = new Mark.Shape(new Point(0, 0), new Point(20, 20), shapeSpec);
        var text = new Mark.Text(new Point(40, 0), "多选",
            new TextSpec("Segoe UI", 24, false, false, TextAlign.Left, Colors.Black, 0,
                TextInk.DefaultStroke, Colors.Transparent, FillKind.None, 0));
        var arrow = new Mark.Arrow(new[] { new Point(60, 60), new Point(100, 60) },
            new ArrowSpec(Colors.Blue, 2, StrokeKind.Solid, StrokeRendering.Artist, ArrowType.Straight,
                Arrowhead.Triangle, Arrowhead.None, 1, ArrowShaft.Plain, Colors.Transparent, 0, 1, 2));
        var watermark = new Mark.Watermark(WatermarkSpec.Default);

        var marks = new Mark[] { shape, text, arrow, watermark };

        // ============ 选区切族：按固定优先顺序挑，显示值优先主选中项 ============
        // 只选一条 → 就是它那一族
        Check.True(MarkStyler.TrySelectionFamily(marks, new[] { 2 }, 2,
                out string family, out CaptureTool tool, out int representative),
            "单选箭头：有族");
        Check.Equal(StyleFamilies.Arrow, family, "单选箭头：族名");
        Check.Equal(CaptureTool.Arrow, tool, "单选箭头：参数行按箭头排");
        Check.Equal(2, representative, "单选箭头：显示值就是它自己");

        // 选区里有更高优先的族（文字 > 箭头）：即便主选中项是箭头，也切到文字族，
        // 显示值取那个族里的一员（上游 style_toolbar_source 的顺序 + Selected* 来源）
        Check.True(MarkStyler.TrySelectionFamily(marks, new[] { 2, 1 }, 2,
                out family, out tool, out representative),
            "箭头 + 文字：有族");
        Check.Equal(StyleFamilies.Text, family, "箭头 + 文字：显示优先级更高的文字族");
        Check.Equal(CaptureTool.Text, tool, "箭头 + 文字：参数行按文字排");
        Check.Equal(1, representative, "箭头 + 文字：显示值是文字那一条");

        // 主选中项本身就在那一族里 → 显示值就是主选中项（不是「第一个」
        Check.True(MarkStyler.TrySelectionFamily(marks, new[] { 1, 2 }, 2,
                out family, out _, out representative),
            "文字 + 箭头（主 = 箭头）：有族");
        Check.Equal(StyleFamilies.Text, family, "文字 + 箭头（主 = 箭头）：族仍是文字");
        Check.Equal(1, representative, "主选中项不在该族：退回该族第一个");

        // 同族多条 + 另一族混着：显示值取主选中项
        var text2 = text;   // 同一份数据即可（只比「挑哪一条」）
        var mixed = new Mark[] { shape, text, text2, arrow };
        Check.True(MarkStyler.TrySelectionFamily(mixed, new[] { 0, 1, 2 }, 2,
                out family, out _, out representative),
            "形状 + 两条文字：有族");
        Check.Equal(StyleFamilies.Text, family, "形状 + 两条文字：文字族优先");
        Check.Equal(2, representative, "主选中项在同一族：显示它自己的值");

        // 选区全是水印（不归族）→ 没有族，参数行退回当前工具
        Check.False(MarkStyler.TrySelectionFamily(marks, new[] { 3 }, 3, out _, out _, out _),
            "只有水印：没有样式族");
        Check.False(MarkStyler.TrySelectionFamily(marks, System.Array.Empty<int>(), -1, out _, out _, out _),
            "空选区：没有样式族");

        // InFamily：多选时「只写回同一族的那几条」
        Check.True(MarkStyler.InFamily(text, StyleFamilies.Text), "文字属于文字族");
        Check.False(MarkStyler.InFamily(arrow, StyleFamilies.Text), "箭头不属于文字族");
        Check.False(MarkStyler.InFamily(watermark, StyleFamilies.Text), "水印不属于任何族");

        // ============ 框选：相交即命中（上游 selection_ids_intersecting_bounds） ============
        // shape 占 (0,0)-(20,20)，text 在 (40,0) 起，arrow 在 (60,60)-(100,60)
        var box = MarkMarquee.RectOf(new Point(30, -10), new Point(10, 10));
        Check.Equal(new Rect(10, -10, 20, 20), box, "框选矩形：反向拖也规范化");

        var hits = MarkMarquee.Hits(marks, new Rect(15, 15, 10, 10));
        Check.True(hits.Contains(0), "只碰到形状右下角也算命中（相交）");
        Check.False(hits.Contains(1), "没碰到的文字不命中");
        Check.False(hits.Contains(3), "水印不参与框选（它不归族，也不该被批量删）");

        // 完全包住：命中的都进来，顺序按绘制顺序（小的在前）
        hits = MarkMarquee.Hits(marks, new Rect(-5, -5, 200, 200));
        Check.True(hits.Contains(0), "整屏框选：命中形状");
        Check.True(hits.Contains(1), "整屏框选：命中文字");
        Check.True(hits.Contains(2), "整屏框选：命中箭头");
        Check.False(hits.Contains(3), "整屏框选：水印不进选区");
        Check.Equal(3, hits.Count, "整屏框选：三条标注全部命中（不重复）");
        Check.Equal(0, hits[0], "命中顺序按绘制顺序");
        Check.Equal(2, hits[2], "命中顺序按绘制顺序（最后一条）");

        // 加选：Shift 拖着框=在原有选区上追加
        var additive = MarkMarquee.Hits(marks, new Rect(-5, -5, 30, 30), new[] { 2 });
        Check.True(additive.Contains(2), "加选：原有选区保留");
        Check.True(additive.Contains(0), "加选：新框到的追加进来");
        Check.Equal(2, additive.Count, "加选：不重复、不多加");

        // 点一下（没拖动）不产生选区
        Check.Equal(0, MarkMarquee.Hits(marks, new Rect(50, 50, 0, 0)).Count, "没拖开就不命中任何东西");

        // 原地小抖动（< 0.5 DIP）也别当成框
        Check.Equal(0, MarkMarquee.Hits(marks, new Rect(0, 0, 0.4, 0.4)).Count, "抖动不到半像素不算框选");

        // ============ Shift 干不干「加减选区」：照上游 allow_shift_toggle ============
        foreach (var info in ToolCatalog.All)
        {
            var each = info.Tool;
            bool allowed = LTools.Views.CapturePro.CaptureOverlayWindow.AllowsShiftToggle(each);

            // 只有这几个工具上 Shift 另有含义，别的地方 Shift 都该能加减选区
            if (each is CaptureTool.AutoFilter or CaptureTool.RectEraser or CaptureTool.BrushEraser
                or CaptureTool.Watermark or CaptureTool.Eraser)
                Check.False(allowed, each + "：Shift 另有含义，不做加减选区");
            else
                Check.True(allowed, each + "：Shift 可以加减选区");
        }
    }
}
