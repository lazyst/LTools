using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro.Collage;

namespace LTools.Core.SelfTest;

/// <summary>
/// 图像拼接的纯逻辑：布局几何（纵向 / 横向 / 宫格 / 统一与原始尺寸 / 边距间距）、设置与背景色、
/// 文档的增删换序与手摆位置、成品渲染的逐像素结果。
/// </summary>
/// <remarks>
/// 照上游 <c>collagedocument.cpp</c> / <c>collagewidget.cpp</c> 的口径。窗口本体（真实鼠标拖图、
/// 拖到另一张交换、取色弹层、导出落盘）在仓库外冒烟里跑。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void CollageLayoutCases()
    {
        var settings = CollageSettings.Default;
        var red = CollageImage(100, 50, (_, _) => Colors.Red);
        var blue = CollageImage(50, 100, (_, _) => Colors.Blue);
        var items = new List<CollageItem>
        {
            new(1, red, "a"),
            new(2, blue, "b"),
        };

        // ---- 纵向 + 统一尺寸：参考首图宽，第二张等比拉到同宽 ----
        var vertical = CollageLayoutCalculator.Calculate(items, settings);
        Check.True(vertical.IsValid, "纵向两张是一份有效布局");
        Check.Equal(100, vertical.Width, "纵向统一尺寸：宽 = 首图宽 100");
        Check.Equal(250, vertical.Height, "纵向统一尺寸：高 50 + 200");
        Check.Equal(new Int32Rect(0, 0, 100, 50), vertical.ImageRects[0], "第 1 张在最上面");
        Check.Equal(new Int32Rect(0, 50, 100, 200), vertical.ImageRects[1], "第 2 张按 2:1 拉高到 200");

        var bordered = CollageLayoutCalculator.Calculate(items, settings with { Border = 10, Spacing = 6 });
        Check.Equal(120, bordered.Width, "外边距 10：宽 100 + 20");
        Check.Equal(276, bordered.Height, "外边距 10 + 间距 6：50 + 200 + 6 + 20");
        Check.Equal(new Int32Rect(10, 10, 100, 50), bordered.ImageRects[0], "有边距时第 1 张从 10,10 起");
        Check.Equal(new Int32Rect(10, 66, 100, 200), bordered.ImageRects[1], "间距把第 2 张推到 66");
        Check.Equal(new Int32Rect(10, 10, 100, 50), bordered.Clips[0], "裁剪格跟着边距走");

        // ---- 横向 + 统一尺寸：参考首图高 ----
        var horizontal = CollageLayoutCalculator.Calculate(items, settings with
        {
            Direction = CollageDirection.Horizontal,
        });
        Check.Equal(125, horizontal.Width, "横向统一尺寸：100 + 25");
        Check.Equal(50, horizontal.Height, "横向统一尺寸：高 = 首图高 50");
        Check.Equal(new Int32Rect(0, 0, 100, 50), horizontal.ImageRects[0], "横向第 1 张在左");
        Check.Equal(new Int32Rect(100, 0, 25, 50), horizontal.ImageRects[1], "横向第 2 张按 1:4 压窄到 25");

        // ---- 原始尺寸：各用各的像素，交叉轴居中 ----
        var original = CollageLayoutCalculator.Calculate(items, settings with
        {
            Sizing = CollageSizing.Original,
        });
        Check.Equal(100, original.Width, "原始尺寸：宽 = 最宽 100");
        Check.Equal(150, original.Height, "原始尺寸：高 50 + 100");
        Check.Equal(new Int32Rect(25, 50, 50, 100), original.ImageRects[1], "窄图在交叉轴上居中");

        // ---- 宫格：2×2，格子 = 首图尺寸 ----
        var four = new List<CollageItem>
        {
            new(1, red, "a"),
            new(2, red, "b"),
            new(3, red, "c"),
            new(4, red, "d"),
        };
        var grid = CollageLayoutCalculator.Calculate(four, settings with
        {
            Direction = CollageDirection.Custom,
            Rows = 2,
            Columns = 2,
        });
        Check.Equal(200, grid.Width, "宫格 2 列：200");
        Check.Equal(100, grid.Height, "宫格 2 行：100");
        Check.Equal(new Int32Rect(0, 0, 100, 50), grid.ImageRects[0], "宫格第 1 张在左上");
        Check.Equal(new Int32Rect(100, 0, 100, 50), grid.ImageRects[1], "宫格第 2 张在右上");
        Check.Equal(new Int32Rect(0, 50, 100, 50), grid.ImageRects[2], "宫格第 3 张在左下");

        // ---- 宫格行列规整（照上游 normalizeCustomGrid）----
        var custom = settings with { Direction = CollageDirection.Custom };
        var normalized = CollageLayoutCalculator.NormalizeCustomGrid(custom, 5, out bool adjusted);
        Check.True(adjusted, "没定过宫格 → 第一次按图片数定行列");
        Check.Equal(3, normalized.Columns, "5 张：列 = ceil(sqrt(5)) = 3");
        Check.Equal(2, normalized.Rows, "5 张 3 列 → 2 行");
        Check.True(normalized.CustomGridInitialized, "定过之后打上标记");

        var wide = CollageLayoutCalculator.NormalizeCustomGrid(
            normalized with { Rows = 3, Columns = 3 }, 5, out adjusted);
        Check.False(adjusted, "3 列已经装得下 5 张 → 不动");
        Check.Equal(3, wide.Columns, "列数只加不减");

        var narrow = CollageLayoutCalculator.NormalizeCustomGrid(
            normalized with { Rows = 3, Columns = 1 }, 5, out adjusted);
        Check.True(adjusted, "3 行 1 列装不下 5 张 → 补列");
        Check.Equal(2, narrow.Columns, "补到 2 列");

        var untouched = CollageLayoutCalculator.NormalizeCustomGrid(settings, 5, out adjusted);
        Check.False(adjusted, "非宫格方向不做行列规整");
        Check.Equal(settings, untouched, "非宫格方向原样返回");

        Check.Equal(2, CollageLayoutCalculator.ColumnsForImages(4, 2), "4 张 2 行 → 2 列");
        Check.Equal(3, CollageLayoutCalculator.ColumnsForImages(5, 2), "5 张 2 行 → 3 列");
        Check.Equal(5, CollageLayoutCalculator.ColumnsForImages(5, 0), "行数为 0 时一行一张");

        // ---- 算不出来 / 太大：给一句话而不是静默空白 ----
        Check.False(CollageLayoutCalculator.Calculate(Array.Empty<CollageItem>(), settings).IsValid,
            "空文档 = 无效布局");
        Check.Equal("拼接设置无效",
            CollageLayoutCalculator.Calculate(items, settings with { Border = -1 }).Error,
            "负边距 → 明说设置无效");

        var huge = new List<CollageItem> { new(1, CollageImage(40000, 1, (_, _) => Colors.Red), "huge") };
        var tooLarge = CollageLayoutCalculator.Calculate(huge, settings);
        Check.False(tooLarge.IsValid, "超过最大边长的拼接算不出来");
        Check.True(tooLarge.Error.Contains("太大"), "失败文案说明太大");

        // ---- 手摆位置：没摆过就是自动矩形，摆过按中心 + 缩放重算 ----
        var placed = new CollageItem(1, red, "a")
        {
            Placement = new CollagePlacement(20, -10, 0.5),
        };
        var resolved = CollageLayoutCalculator.ResolveStaticRect(placed, new Int32Rect(0, 0, 100, 50),
            new Int32Rect(0, 0, 100, 50));
        Check.Equal(new Int32Rect(45, 3, 50, 25), resolved, "中心偏移 20,-10、缩放 0.5 → 50×25，中心落在 70,15");

        var unplaced = CollageLayoutCalculator.ResolveStaticRect(new CollageItem(1, red, "a"),
            new Int32Rect(3, 4, 100, 50), new Int32Rect(0, 0, 100, 50));
        Check.Equal(new Int32Rect(3, 4, 100, 50), unplaced, "没摆过的图用手摆函数仍给自动矩形");
    }

    private static void CollageSettingsCases()
    {
        var defaults = CollageSettings.Default;
        Check.Equal(CollageDirection.Vertical, defaults.Direction, "默认纵向");
        Check.Equal(CollageSizing.Uniform, defaults.Sizing, "默认统一尺寸");
        Check.Equal(0, defaults.Border, "默认没有外边距");
        Check.Equal(0, defaults.Spacing, "默认没有间距");
        Check.Equal(Colors.White, defaults.Background, "默认白底");
        Check.True(defaults.IsValid, "默认设置有效");

        Check.Equal("vertical", defaults.DirectionKey, "纵向的键");
        Check.Equal("horizontal", (defaults with { Direction = CollageDirection.Horizontal }).DirectionKey,
            "水平的键");
        Check.Equal("custom", (defaults with { Direction = CollageDirection.Custom }).DirectionKey, "宫格的键");
        Check.Equal("uniform", defaults.SizingKey, "统一尺寸的键");
        Check.Equal("original", (defaults with { Sizing = CollageSizing.Original }).SizingKey,
            "原始尺寸的键");

        Check.Equal(CollageDirection.Custom, CollageSettings.ParseDirection("custom"), "解析 custom");
        Check.Equal(CollageDirection.Horizontal, CollageSettings.ParseDirection("horizontal"),
            "解析 horizontal");
        Check.Equal(CollageDirection.Vertical, CollageSettings.ParseDirection("nonsense"),
            "认不出的方向回纵向");
        Check.Equal(CollageSizing.Original, CollageSettings.ParseSizing("ORIGINAL"), "尺寸方式不分大小写");

        // ---- 背景色：#RRGGBBAA 嘟上 / 解析回来 ----
        var translucent = Color.FromArgb(0x80, 0x12, 0x34, 0x56);
        Check.Equal("#12345680", CollageColor.Format(translucent), "#RRGGBBAA 顺序");
        Check.Equal(translucent, CollageColor.Parse("#12345680"), "格式化再解析回同一个颜色");
        Check.Equal(Colors.White, CollageColor.Parse("bogus"), "解析不了按白底");
        Check.Equal("#123456", CollageColor.Hex(translucent), "给人看的 #RRGGBB 不带透明度");

        Check.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), CollageColor.TryParseHex("#abc"), "#RGB 展开");
        Check.Equal(Color.FromRgb(0xAA, 0xBB, 0xCC), CollageColor.TryParseHex("AABBCC"), "不带 # 也认");
        Check.Equal(Color.FromArgb(0xAA, 0xBB, 0xCC, 0xDD), CollageColor.TryParseHex("#AABBCCDD"),
            "8 位按 #AARRGGBB 解析（WPF 口径）");
        Check.True(CollageColor.TryParseHex("#12345") == null, "位数不对 → null");

        // ---- 只有背景色不算几何变化（照上游 setSettings 只清手摆位置的条件）----
        Check.True(defaults.GeometryEquals(defaults with { Background = Colors.Black }),
            "只换背景色不算几何变化");
        Check.False(defaults.GeometryEquals(defaults with { Border = 1 }), "改边距算几何变化");
        Check.False(defaults.GeometryEquals(defaults with { CustomGridInitialized = true }),
            "宫格初始化标记也算在比较里");
    }

    private static void CollageDocumentCases()
    {
        var document = new CollageDocument();
        Check.True(document.IsEmpty, "新文档没有图");
        Check.Equal(0L, document.SelectedId, "新文档没有选中项");
        Check.False(document.Layout.IsValid, "空文档布局无效");

        long first = document.Append(CollageImage(100, 50, (_, _) => Colors.Red), "a");
        Check.Equal(first, document.SelectedId, "第一张进来就选中");
        long second = document.Append(CollageImage(50, 100, (_, _) => Colors.Blue), "b");
        Check.Equal(first, document.SelectedId, "第二张不抢选中（照上游 append）");
        Check.Equal(2, document.Items.Count, "两张都在");
        Check.True(document.Layout.IsValid, "两张图能算出布局");

        document.Select(second);
        Check.Equal(second, document.SelectedId, "选中第二张");
        document.Select(9999);
        Check.Equal(0L, document.SelectedId, "选不存在的 id = 取消选中");
        document.Select(second);

        // ---- 替换：保留 id / 顺序 / 变换 ----
        long before = document.Items[0].Id;
        var transformed = document.Items[0];
        transformed.Transform = CollageTransform.Clockwise;
        Check.True(document.Replace(before, CollageImage(20, 30, (_, _) => Colors.Green), "new"), "替换成功");
        Check.Equal(before, document.Items[0].Id, "替换后 id 不变");
        Check.Equal(20, document.Items[0].SourceSize.Width, "像素换成新的");
        Check.Equal((30, 20), document.Items[0].TransformedSize, "替换保留旋转");

        // ---- 旋转 / 翻转 / 重置 ----
        Check.True(document.Rotate(before, true), "顺时针转 90");
        Check.Equal((20, 30), document.Items[0].TransformedSize, "再转一次尺寸换回来");
        Check.True(document.Flip(before, true), "水平翻转");
        Check.True(document.ResetTransform(before), "重置变换");
        Check.Equal((20, 30), document.Items[0].TransformedSize, "重置后回到原始方向");
        Check.True(document.Items[0].Transform.IsIdentity, "重置后矩阵是单位矩阵");

        // ---- 删掉选中项：选中相邻一张 ----
        Check.True(document.Remove(second), "删掉第二张");
        Check.Equal(before, document.SelectedId, "删掉选中项后选相邻的一张");
        Check.Equal(1, document.Items.Count, "只剩一张");

        // ---- 换序：交换 / 移动 / 反转 ----
        long a = document.Append(CollageImage(10, 10, (_, _) => Colors.Red), "a");
        long b = document.Append(CollageImage(10, 10, (_, _) => Colors.Red), "b");
        long c = document.Append(CollageImage(10, 10, (_, _) => Colors.Red), "c");
        Check.Equal(string.Join(",", new[] { before, a, b, c }),
            string.Join(",", document.Items.Select(item => item.Id)),
            "追加顺序就是文档顺序");

        Check.True(document.Swap(a, c), "交换 a 与 c");
        Check.Equal(string.Join(",", new[] { before, c, b, a }),
            string.Join(",", document.Items.Select(item => item.Id)), "交换后顺序换了");
        Check.True(document.Move(a, 1), "把 a 挪到下标 1");
        Check.Equal(string.Join(",", new[] { before, a, c, b }),
            string.Join(",", document.Items.Select(item => item.Id)), "移动按最终下标落位");
        Check.False(document.Move(a, 99), "越界的下标不挪");
        Check.False(document.Swap(a, a), "自己跟自己换 = 不动");

        Check.True(document.Reverse(), "反转顺序");
        Check.Equal(string.Join(",", new[] { b, c, a, before }),
            string.Join(",", document.Items.Select(item => item.Id)), "反转就是倒过来");

        // ---- 手摆位置：几何一变全清，只改背景色保住 ----
        var target = document.Items[0];
        Check.True(document.SetStaticPlacement(target.Id, new CollagePlacement(12, 8, 1.5)), "摆一张图");
        Check.False(document.Items[0].Placement.IsDefault, "手摆位置写进去了");

        Check.True(document.SetSettings(document.Settings with { Background = Colors.Black }),
            "只换背景色");
        Check.False(document.Items[0].Placement.IsDefault, "只换背景色不动手摆位置");

        Check.True(document.SetSettings(document.Settings with { Border = 4 }), "改外边距");
        Check.True(document.Items[0].Placement.IsDefault, "几何一变就清空手摆位置");

        Check.False(document.SetSettings(document.Settings with { Border = -5 }), "非法设置拒绝");
        Check.Equal(4, document.Settings.Border, "拒绝后设置保持原样");

        // ---- 宫格：进自定义方向时自动定行列 ----
        var grid = new CollageDocument();
        for (int i = 0; i < 4; i++) grid.Append(CollageImage(10, 10, (_, _) => Colors.Red), $"i{i}");
        Check.True(grid.SetSettings(grid.Settings with { Direction = CollageDirection.Custom }), "切到宫格");
        Check.Equal(2, grid.Settings.Columns, "4 张 → 2 列");
        Check.Equal(2, grid.Settings.Rows, "4 张 → 2 行");
        Check.True(grid.Settings.CustomGridInitialized, "宫格初始化标记落地");

        // ---- 清空 ----
        grid.Clear();
        Check.True(grid.IsEmpty, "清空后没有图");
        Check.Equal(0L, grid.SelectedId, "清空后取消选中");
        grid.Clear();
        Check.True(grid.IsEmpty, "重复清空不报错");
    }

    private static void CollageRenderCases()
    {
        // ---- 纵向拼接：背景 / 边距 / 间距逐像素 ----
        var items = new List<CollageItem>
        {
            new(1, CollageImage(10, 10, (_, _) => Colors.Red), "red"),
            new(2, CollageImage(10, 10, (_, _) => Colors.Blue), "blue"),
        };
        var settings = CollageSettings.Default with
        {
            Border = 2,
            Spacing = 4,
            Background = Color.FromRgb(0x11, 0x22, 0x33),
        };
        var layout = CollageLayoutCalculator.Calculate(items, settings);
        Check.Equal(new Int32Rect(2, 2, 10, 10), layout.ImageRects[0], "渲染夹具：第 1 张在 2,2");
        Check.Equal(new Int32Rect(2, 16, 10, 10), layout.ImageRects[1], "渲染夹具：第 2 张在 2,16");

        var rendered = CollageRender.Render(items, settings, layout);
        Check.Equal(14, rendered.PixelWidth, "成品宽 10 + 2 × 2");
        Check.Equal(28, rendered.PixelHeight, "成品高 10 + 10 + 4 + 2 × 2");
        Check.Equal(Color.FromRgb(0x11, 0x22, 0x33), CollagePixelAt(rendered, 0, 0), "角落里是背景色");
        Check.Equal(Colors.Red, CollagePixelAt(rendered, 2, 2), "第 1 张左上角是红的");
        Check.Equal(Colors.Red, CollagePixelAt(rendered, 11, 11), "第 1 张右下角也是红的");
        Check.Equal(Color.FromRgb(0x11, 0x22, 0x33), CollagePixelAt(rendered, 7, 13),
            "两张之间是背景色（间距 4）");
        Check.Equal(Colors.Blue, CollagePixelAt(rendered, 2, 16), "第 2 张左上角是蓝的");
        Check.Equal(Colors.Blue, CollagePixelAt(rendered, 11, 25), "第 2 张右下角也是蓝的");

        // ---- 顺时针 90°：左半变上边、右半变下边（左宽 5 的红色）----
        var split = CollageImage(10, 4, (x, _) => x < 5 ? Colors.Red : Colors.Blue);
        var rotated = new CollageItem(1, split, "split");
        rotated.Transform = CollageTransform.Clockwise;
        Check.Equal((4, 10), rotated.TransformedSize, "10×4 转 90° → 4×10");

        var single = CollageSettings.Default with { Background = Colors.Black };
        var rotatedLayout = CollageLayoutCalculator.Calculate(new List<CollageItem> { rotated }, single);
        Check.Equal(new Int32Rect(0, 0, 4, 10), rotatedLayout.ImageRects[0], "旋转后铺满拼接结果");

        var turned = CollageRender.Render(new List<CollageItem> { rotated }, single, rotatedLayout);
        Check.Equal(4, turned.PixelWidth, "转完宽 4");
        Check.Equal(10, turned.PixelHeight, "转完高 10");
        Check.Equal(Colors.Red, CollagePixelAt(turned, 3, 0), "左上看红色（源图左上转到右上）");
        Check.Equal(Colors.Red, CollagePixelAt(turned, 0, 0), "左下也是红色（源图左下转到左上）");
        Check.Equal(Colors.Blue, CollagePixelAt(turned, 0, 9), "右下是蓝色（源图右下转到左下）");
        Check.Equal(Colors.Blue, CollagePixelAt(turned, 3, 9), "右下角是蓝色");

        // ---- 水平翻转：红色从左半跑到右半 ----
        var flipped = new CollageItem(1, split, "split");
        flipped.Transform = CollageTransform.MirrorHorizontal;
        var flippedLayout = CollageLayoutCalculator.Calculate(new List<CollageItem> { flipped }, single);
        var mirror = CollageRender.Render(new List<CollageItem> { flipped }, single, flippedLayout);
        Check.Equal(Colors.Blue, CollagePixelAt(mirror, 0, 0), "水平翻转后左上变蓝");
        Check.Equal(Colors.Blue, CollagePixelAt(mirror, 3, 0), "左半仍是蓝（红色在右半）");
        Check.Equal(Colors.Red, CollagePixelAt(mirror, 9, 0), "水平翻转后右上变红");

        // ---- 宫格 + 间距：4 张各归各格 ----
        var grid = CollageSettings.Default with
        {
            Direction = CollageDirection.Custom,
            Rows = 2,
            Columns = 2,
            Spacing = 2,
            Background = Colors.Black,
        };
        var quad = new List<CollageItem>
        {
            new(1, CollageImage(6, 6, (_, _) => Colors.Red), "r"),
            new(2, CollageImage(6, 6, (_, _) => Colors.Green), "g"),
            new(3, CollageImage(6, 6, (_, _) => Colors.Blue), "b"),
            new(4, CollageImage(6, 6, (_, _) => Colors.White), "w"),
        };
        var quadLayout = CollageLayoutCalculator.Calculate(quad, grid);
        var sheet = CollageRender.Render(quad, grid, quadLayout);
        Check.Equal(14, sheet.PixelWidth, "宫格宽 6 + 2 + 6");
        Check.Equal(14, sheet.PixelHeight, "宫格高 6 + 2 + 6");
        Check.Equal(Colors.Red, CollagePixelAt(sheet, 0, 0), "宫格左上红");
        Check.Equal(Colors.Green, CollagePixelAt(sheet, 8, 0), "宫格右上绿");
        Check.Equal(Colors.Blue, CollagePixelAt(sheet, 0, 8), "宫格左下蓝");
        Check.Equal(Colors.White, CollagePixelAt(sheet, 8, 8), "宫格右下白");
        Check.Equal(Colors.Black, CollagePixelAt(sheet, 6, 6), "格与格之间是间距（背景色）");

        // ---- 布局算不出来时渲染要报错，不假装成功 ----
        bool threw = false;
        try
        {
            CollageRender.Render(Array.Empty<CollageItem>(), single, CollageLayout.Invalid);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Check.True(threw, "无效布局渲染 → 抛错");
    }

    // ============ 小工具 ============

    private static BitmapSource CollageImage(int width, int height, Func<int, int, Color> color)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = color(x, y);
                int i = (y * width + x) * 4;
                pixels[i] = c.B;
                pixels[i + 1] = c.G;
                pixels[i + 2] = c.R;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels,
            width * 4);
        image.Freeze();
        return image;
    }

    private static Color CollagePixelAt(BitmapSource image, int x, int y)
    {
        var one = new byte[4];
        var source = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        source.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return Color.FromArgb(one[3], one[2], one[1], one[0]);
    }
}
