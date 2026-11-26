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

/// <summary>CaptureProSuite 的「工具条布局 / 识别排版」部分。</summary>
internal static partial class CaptureProSuite
{
    private static void ToolLayouts()
    {
        int all = ToolCatalog.All.Length;

        var def = ToolLayout.Default();
        Check.Equal(17, def.Groups.Count,
            "默认布局：照上游 toolbar_layout::defaultOrder() 排 17 格（选择 / 形状 / 气泡 / 箭头 / 测距 / 测角 / 括号 / 直线 / 画笔 / 荧光 / 聚光 / 放大镜 / 文字 / 序号 / 滤镜 / 橡皮 / 水印）");
        Check.Equal(all - 2, def.Groups.SelectMany(g => g).Distinct().Count(),
            "23 个工具各占一格（折线 / 取色默认收起）");
        Check.Equal(2, def.HiddenTools.Count, "默认收起的只有上游没有的扩展工具（折线 / 取色）");
        Check.Equal("Select", def.ToLines()[0], "第一格是「选择」");
        Check.Equal("Shape", def.ToLines()[1], "第二格是形状（矩形 / 椭圆 / 菱形共用一个工具）");
        Check.Equal("SpeechBubble", def.ToLines()[2], "第三格是气泡（上游独立按钮）");
        Check.Equal("Arrow", def.ToLines()[3], "第四格是箭头");
        Check.Equal("Line", def.ToLines()[7], "第八格是直线（与箭头分开，上游就是两个按钮）");
        Check.Equal("Highlighter,HighlightRect", def.ToLines()[9], "荧光一格（矩形 / 画笔两种变体，照上游 highlighter）");
        Check.Equal("Spotlight", def.ToLines()[10], "聚光单独一格");
        Check.Equal("Magnifier", def.ToLines()[11], "放大镜单独一格");
        Check.Equal("Mosaic,Filter,BrushFilter,AutoFilter", def.ToLines()[14],
            "滤镜一格（马赛克 / 矩形 / 画笔 / 自动，照上游 filter）");
        Check.Equal("Eraser,RectEraser,BrushEraser", def.ToLines()[15], "三把橡皮同格（照上游 eraser）");
        Check.Equal("Watermark", def.ToLines()[16], "水印单独一格（最后一格）");

        // 并到左边一格：格内顺序保持原样，第一格不再动
        var merge = def.MergeLeft(CaptureTool.Pen);          // 「画笔」并进「直线」
        Check.Equal(16, merge.Groups.Count, "并到左边一格：少一格");
        Check.Equal(CaptureTool.Select, merge.Groups[0][0], "第一格还是「选择」");
        Check.True(merge.Groups[7].SequenceEqual(new[] { CaptureTool.Line, CaptureTool.Pen }),
            "整格并进左边那格（顺序不变）");
        Check.True(merge.MergeLeft(CaptureTool.Select).SameAs(merge), "第一格左边没东西：并过去什么都不变");

        // 拆出来：回到默认的格数与位置
        var split = merge.Split(CaptureTool.Pen);
        Check.Equal(17, split.Groups.Count, "拆成单独一格：格子数回到默认");
        Check.Equal(1, split.Groups[8].Count, "拆出来的格子里只有它自己");
        Check.Equal(CaptureTool.Pen, split.Groups[8][0], "拆出来的格子落在原位置");

        // 收起 / 放回（拿单独一格的「聚光」试：整格消失）
        var hidden = def.Hide(CaptureTool.Spotlight);
        Check.True(hidden.IsHidden(CaptureTool.Spotlight), "收起「聚光」");
        Check.Equal(-1, hidden.CellOf(CaptureTool.Spotlight), "收起后它不在任何格子里");
        Check.Equal(16, hidden.Groups.Count, "收起一个单格工具就少一格");
        Check.True(hidden.ToHidden().Contains("Spotlight"), "落盘写的是工具键");

        var back = hidden.Insert(CaptureTool.Spotlight);
        Check.False(back.IsHidden(CaptureTool.Spotlight), "放回去就不再是收起的");
        Check.Equal(17, back.Groups.Count, "放回去还是单独一格");
        Check.Equal(1, back.Groups[back.CellOf(CaptureTool.Spotlight)].Count, "放回去单独占一格");
        Check.True(back.CellOf(CaptureTool.Spotlight) > back.CellOf(CaptureTool.Pen)
                   && back.CellOf(CaptureTool.Spotlight) < back.CellOf(CaptureTool.Highlighter),
            "放回去按目录顺序插回（画笔之后、荧光之前）");

        // 多工具格里收起一个：格子还在，只是少一个
        var trimmed = def.Hide(CaptureTool.Eraser);
        Check.Equal(17, trimmed.Groups.Count, "多工具格收起一个：格子保留");
        Check.Equal(2, trimmed.Groups[15].Count, "那一格剩两把橡皮");

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
        Check.Equal("PP-OCRv6 高精度", CaptureRecognize.EngineLabel("v6small"), "v6small 的显示名");
        Check.Equal("PP-OCRv6 最高精度", CaptureRecognize.EngineLabel("v6medium"), "v6medium 的显示名");

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
}
