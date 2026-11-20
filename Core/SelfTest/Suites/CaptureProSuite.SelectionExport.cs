using System.Windows;
using System.Windows.Media;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 选区背景不透明度（照上游 <c>background_opacity_percent</c>）与版面压缩
/// （照 <c>screenshotlayoutcompaction.h</c>）：纯函数方案 + 导出像素。
/// </summary>
internal static partial class CaptureProSuite
{
    private static void SelectionExportCases()
    {
        // ============ ① 外观夹取 ============
        Check.Equal(100, (SelectionStyle.None with { BackgroundOpacity = 150 }).Clamp().BackgroundOpacity,
            "背景不透明度上限 100");
        Check.Equal(0, (SelectionStyle.None with { BackgroundOpacity = -5 }).Clamp().BackgroundOpacity,
            "背景不透明度下限 0");
        Check.Equal(0, SelectionStyle.None.BackgroundOpacity, "默认不垫背景");
        Check.True((SelectionStyle.None with { BackgroundOpacity = 40 }).IsPlain,
            "只垫背景不影响「直角无效果」快路径");

        // ============ ② 版面压缩方案（纯函数） ============
        var blocks = new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(100, 0, 10, 10),
        };
        var plan = LayoutCompaction.Build(blocks, 110, 10, 4);
        Check.True(plan != null, "两块之间隔着整段空白：算得出方案");
        if (plan != null)
        {
            Check.Equal(24, plan.Width, "输出宽 = 10 + 留白 4 + 10");
            Check.Equal(10, plan.Height, "纵向只有一段：高度不变");
            Check.Equal(2, plan.Columns.Count, "横向两段");
            Check.Equal(0, plan.Columns[0].Destination, "第一段贴到 0");
            Check.Equal(14, plan.Columns[1].Destination, "第二段贴到 14");
            Check.Equal(0, plan.Tiles[0].Dx, "第一块不挪");
            Check.Equal(-86, plan.Tiles[1].Dx, "第二块左移 86（100 → 14）");
            Check.Equal(0, plan.Tiles[1].Dy, "纵向不动");
            Check.Equal(100, plan.Tiles[1].SourceX, "源位置仍是原来的 100");
        }

        var stacked = new[]
        {
            new Rect(0, 0, 10, 10),
            new Rect(0, 100, 10, 10),
        };
        var vertical = LayoutCompaction.Build(stacked, 10, 110, 0);
        Check.True(vertical != null, "上下两块也压得动");
        Check.Equal(10, vertical?.Width ?? 0, "横向一段：宽度不变");
        Check.Equal(20, vertical?.Height ?? 0, "纵向贴紧：10 + 0 + 10");
        Check.Equal(-90, vertical?.Tiles[1].Dy ?? 0, "第二块上移 90（100 → 10）");

        Check.True(LayoutCompaction.Build(new[] { new Rect(0, 0, 10, 10) }, 10, 10, 4) == null,
            "只有一块：没有整段空白可抽");
        Check.True(LayoutCompaction.Build(new[] { new Rect(0, 0, 10, 10), new Rect(5, 0, 10, 10) },
            15, 10, 4) == null, "两块重叠：没有空白可抽");
        Check.True(LayoutCompaction.Build(blocks, 110, 10, 0) != null, "留白 0 也能压（贴着拼）");
        Check.Equal(20, LayoutCompaction.Build(blocks, 110, 10, 0)?.Width ?? 0, "留白 0：输出宽 = 10 + 10");

        var clamped = LayoutCompaction.Build(blocks, 110, 10, 500);
        Check.Equal(10 + LayoutCompaction.MaxSpacing + 10, clamped?.Width ?? 0, "留白夹到 100");

        // 三块：中间那块自己一段，外圈余量照旧
        var three = new[]
        {
            new Rect(2, 2, 6, 6),
            new Rect(40, 2, 6, 6),
            new Rect(80, 2, 6, 6),
        };
        var threePlan = LayoutCompaction.Build(three, 88, 10, 2);
        Check.True(threePlan != null, "三块也算得出方案");
        Check.Equal(3, threePlan?.Columns.Count ?? 0, "横向三段");
        Check.Equal(0, threePlan?.Tiles[0].Dx ?? -1, "第一块不动（外圈 2 像素余量保留）");

        // ============ ③ 导出：背景垫在圆角外 ============
        var white = WhiteBitmap(8, 8);
        var rounded = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 1.0)
        {
            Area = new Int32Rect(100, 100, 8, 8),
            Style = SelectionStyle.None with { CornerRadius = 3, BackgroundOpacity = 50 },
        };
        var shot = rounded.Render();
        Check.Equal(8, shot.PixelWidth, "垫背景不改输出尺寸");
        byte corner = AlphaAt(shot, 0, 0);
        Check.True(corner is > 90 and < 170, $"圆角外透出淡化的底图（alpha {corner}）");
        Check.Equal((byte)0xFF, AlphaAt(shot, 4, 4), "内容仍是实色（不被背景冲淡）");

        var noBackground = new CaptureDocument(white, new Int32Rect(100, 100, 8, 8), 1.0)
        {
            Area = new Int32Rect(100, 100, 8, 8),
            Style = SelectionStyle.None with { CornerRadius = 3 },
        };
        Check.True(AlphaAt(noBackground.Render(), 0, 0) < 32, "不垫背景时圆角外还是透明");

        // ============ ④ 导出：版面压缩真的把两块贴紧 ============
        var wide = WhiteBitmap(110, 10);
        var doc = new CaptureDocument(wide, new Int32Rect(0, 0, 110, 10), 1.0)
        {
            Area = new Int32Rect(0, 0, 110, 10),
            CompactLayout = true,
            CompactSpacing = 4,
            Regions = new[]
            {
                SelectionRegion.FromRect(RegionMode.Add, new Rect(0, 0, 10, 10)),
                SelectionRegion.FromRect(RegionMode.Add, new Rect(100, 0, 10, 10)),
            },
        };
        var compacted = doc.Render();
        Check.Equal(24, compacted.PixelWidth, $"压缩后宽度 = 24（实际 {compacted.PixelWidth}）");
        Check.Equal(10, compacted.PixelHeight, "压缩后高度不变");
        Check.Equal((byte)0xFF, AlphaAt(compacted, 0, 5), "第一块还在最左");
        Check.True(AlphaAt(compacted, 11, 5) < 32, "两块之间的留白是透明的");
        Check.Equal((byte)0xFF, AlphaAt(compacted, 20, 5), "第二块被贴到 14 之后");

        doc.CompactLayout = false;
        var plainDoc = doc.Render();
        Check.Equal(110, plainDoc.PixelWidth, "关掉压缩：宽度回到 110");
        Check.True(AlphaAt(plainDoc, 20, 5) < 32, "关掉压缩：中间那片空白仍在");
    }
}
