using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features.CapturePro;
using LTools.Features.CapturePro.ImageEdit;

namespace LTools.Core.SelfTest;

/// <summary>
/// 图片编辑窗的纯逻辑：镜头几何（适应 / 滚轮 / 裁剪条带）、像素搬运（剪行 / 剪列）、
/// 文档的裁剪与撤销 / 脏判定。
/// </summary>
/// <remarks>
/// 照上游 <c>ImageEditingCanvas::fit</c> / <c>cropStrip</c> 与
/// <c>ImageEditingDocument::prepareCrop</c>、<c>dirty</c> / <c>markExported</c>。
/// 窗口本体（真实鼠标拖动、棋盘格、导出）在仓库外冒烟里跑。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void ImageEditGeometryCases()
    {
        // ---- 适应窗口：四周留 48，小图也放大（上游不封顶 1）----
        Check.True(Math.Abs(ImageEditGeometry.FitMargin - 48) < 1e-9, "适应窗口的留白是 48");
        Check.True(Math.Abs(ImageEditGeometry.FitZoom(new Size(1000, 500), new Size(1048, 548)) - 1.0) < 1e-9,
            "内容正好等于视口减留白 → 1:1");
        Check.True(Math.Abs(ImageEditGeometry.FitZoom(new Size(100, 50), new Size(1048, 548)) - 10) < 1e-9,
            "小图按空间放大（100 宽 → 10 倍）");
        Check.True(Math.Abs(ImageEditGeometry.FitZoom(new Size(4000, 1000), new Size(1048, 548)) - 0.25) < 1e-9,
            "大图按更紧的那一边缩（1000 高 → 0.25）");
        Check.True(Math.Abs(ImageEditGeometry.FitZoom(Size.Empty, new Size(800, 600)) - 1) < 1e-9,
            "没有内容时按 1 倍");
        Check.True(Math.Abs(ImageEditGeometry.FitZoom(new Size(10, 10), Size.Empty) - 1) < 1e-9,
            "视口还没量出来时按 1 倍");
        Check.True(Math.Abs(ImageEditGeometry.FitZoom(new Size(10_000, 10_000), new Size(40, 30)))
                   - ImageEditGeometry.MinZoom < 1e-9,
            "极小的适应比夹到下限");

        // ---- 滚轮：每格 1.15 倍，以当前值为基准，夹在上下限 ----
        Check.True(Math.Abs(ImageEditGeometry.Wheel(1, 1) - 1.15) < 1e-9, "滚轮上一格 → ×1.15");
        Check.True(Math.Abs(ImageEditGeometry.Wheel(1, -1) - 1 / 1.15) < 1e-9, "滚轮下一格 → ÷1.15");
        Check.True(Math.Abs(ImageEditGeometry.Wheel(2, 2) - 2 * 1.15 * 1.15) < 1e-6, "两格按 1.15² 叠乘");
        Check.True(Math.Abs(ImageEditGeometry.Wheel(1, 0) - 1) < 1e-9, "零格不变");
        Check.True(Math.Abs(ImageEditGeometry.Wheel(1, 200) - ImageEditGeometry.MaxZoom) < 1e-9, "放到头上限");
        Check.True(Math.Abs(ImageEditGeometry.Wheel(1, -200) - ImageEditGeometry.MinZoom) < 1e-9, "缩到底为下限");
        Check.True(Math.Abs(ImageEditGeometry.ClampZoom(double.NaN) - 1) < 1e-9, "NaN 缩放回 1 倍");
        Check.True(Math.Abs(ImageEditGeometry.ClampZoom(0) - 1) < 1e-9, "0 缩放回 1 倍");

        // ---- 拖动起止 → 条带：先夹到 [0, 边长]，再取小的是起点 ----
        var forward = ImageEditGeometry.Resolve(10, 40, 100);
        Check.Equal(new CropSpan(10, 30), forward, "正着拖：起点 10 长 30");
        var backward = ImageEditGeometry.Resolve(40, 10, 100);
        Check.Equal(new CropSpan(10, 30), backward, "反着拖也是同一条带");
        var outside = ImageEditGeometry.Resolve(-20, 500, 100);
        Check.Equal(new CropSpan(0, 100), outside, "拖出图外夹回整条（长度 = 边长 → 不剪）");
        Check.False(outside.IsValid(100), "整条边长不算有效条带");
        Check.True(new CropSpan(0, 1).IsValid(10), "1 像素也算有效");
        Check.False(new CropSpan(9, 2).IsValid(10), "越界的条带无效");
        Check.False(default(CropSpan).IsValid(10), "长度 0 无效");
        Check.Equal(default(CropSpan), ImageEditGeometry.Resolve(5, 9, 0), "没有内容时给空条带");

        // ---- 条带在内容矩形里的位置 ----
        var content = new Rect(10, 20, 200, 100);
        var rows = ImageEditGeometry.Strip(content, CropAxis.Rows, new CropSpan(25, 50), 100);
        Check.True(Math.Abs(rows.X - 10) < 1e-9 && Math.Abs(rows.Width - 200) < 1e-9,
            "剪行：整宽一条横带");
        Check.True(Math.Abs(rows.Y - 45) < 1e-9 && Math.Abs(rows.Height - 50) < 1e-9,
            "剪行：起止按比例落在内容里（25/100 → y=45，50/100 → 高 50）");

        var columns = ImageEditGeometry.Strip(content, CropAxis.Columns, new CropSpan(25, 50), 100);
        Check.True(Math.Abs(columns.Y - 20) < 1e-9 && Math.Abs(columns.Height - 100) < 1e-9,
            "剪列：整高一条纵带");
        Check.True(Math.Abs(columns.X - 60) < 1e-9 && Math.Abs(columns.Width - 100) < 1e-9,
            "剪列：x=10+200×0.25，宽 200×0.5");

        var zero = ImageEditGeometry.Strip(content, CropAxis.Rows, new CropSpan(40, 0), 100);
        Check.True(Math.Abs(zero.Height) < 1e-9 && Math.Abs(zero.Y - 60) < 1e-9,
            "长度 0 时是一条落在起点上的引导线");
        Check.Equal(Rect.Empty, ImageEditGeometry.Strip(Rect.Empty, CropAxis.Rows,
            new CropSpan(0, 1), 10), "内容为空时不给矩形");
    }

    private static void ImageEditBitmapCases()
    {
        // 4×4，颜色 = (R = x*30, G = y*30)：剪完逐像素比对，确认搬的是哪几行 / 列
        var source = EditImage(4, 4);

        var rows = ImageEditBitmap.RemoveStrip(source, CropAxis.Rows, new CropSpan(1, 1));
        Check.Equal(4, rows.PixelWidth, "剪行：宽度不变");
        Check.Equal(3, rows.PixelHeight, "剪行：高度减 1");
        Check.Equal(EditColor(0, 0), EditPixel(rows, 0, 0), "剪行：第 0 行留在第 0 行");
        Check.Equal(EditColor(2, 2), EditPixel(rows, 2, 1), "剪行：原来的第 2 行顶到第 1 行");
        Check.Equal(EditColor(3, 3), EditPixel(rows, 3, 2), "剪行：原来的第 3 行顶到第 2 行");

        var head = ImageEditBitmap.RemoveStrip(source, CropAxis.Rows, new CropSpan(0, 2));
        Check.Equal(2, head.PixelHeight, "从头剪两行：高度减 2");
        Check.Equal(EditColor(2, 2), EditPixel(head, 2, 0), "从头剪两行：原来的第 2 行成了第 0 行");

        var tail = ImageEditBitmap.RemoveStrip(source, CropAxis.Rows, new CropSpan(2, 2));
        Check.Equal(2, tail.PixelHeight, "从尾剪两行：高度减 2");
        Check.Equal(EditColor(1, 1), EditPixel(tail, 1, 1), "从尾剪两行：最后留下的是原来的第 1 行");

        var columns = ImageEditBitmap.RemoveStrip(source, CropAxis.Columns, new CropSpan(1, 1));
        Check.Equal(3, columns.PixelWidth, "剪列：宽度减 1");
        Check.Equal(4, columns.PixelHeight, "剪列：高度不变");
        Check.Equal(EditColor(0, 0), EditPixel(columns, 0, 0), "剪列：第 0 列留在第 0 列");
        Check.Equal(EditColor(2, 2), EditPixel(columns, 1, 2), "剪列：原来的第 2 列顶到第 1 列");
        Check.Equal(EditColor(3, 3), EditPixel(columns, 2, 3), "剪列：原来的第 3 列顶到第 2 列");

        // 不合法 = 原样返回同一个实例（调用方据此判断「没剪」）
        Check.True(ReferenceEquals(source,
                ImageEditBitmap.RemoveStrip(source, CropAxis.Rows, new CropSpan(0, 0))),
            "长度 0 → 原图实例");
        Check.True(ReferenceEquals(source,
                ImageEditBitmap.RemoveStrip(source, CropAxis.Rows, new CropSpan(0, 4))),
            "等于整条边长 → 原图实例");
        Check.True(ReferenceEquals(source,
                ImageEditBitmap.RemoveStrip(source, CropAxis.Columns, new CropSpan(3, 2))),
            "越界 → 原图实例");
    }

    private static void ImageEditDocumentCases()
    {
        var document = new ImageEditDocument();
        Check.False(document.HasImage, "新建的编辑文档是空的");
        Check.False(document.Dirty, "空文档不算改过");
        Check.False(document.Crop(CropAxis.Rows, 0, 1), "没有图时裁剪返回 false");

        document.Load(EditImage(6, 6), "demo.png");
        Check.True(document.HasImage, "装图后 HasImage");
        Check.Equal("demo.png", document.Title, "标题用文件名");
        Check.Equal(6, document.Width, "宽 = 底图宽");
        Check.Equal(6, document.Height, "高 = 底图高");
        Check.False(document.Dirty, "刚打开不算改过");
        Check.False(document.CanUndo, "刚打开没有可撤销的");
        Check.True(document.Doc is { } doc && doc.Area.Width == 6 && doc.Style == SelectionStyle.None,
            "文档区域盖满底图、外观是直角");

        // 标注 → 脏 → 清掉标注 → 又不脏了（签名回得去）
        document.PushUndo();
        document.Doc!.Add(new Mark.Highlight(new Point(0, 0), new Point(10, 10), Colors.Red, 2));
        Check.True(document.Dirty, "加标注算改过");
        Check.True(document.CanUndo, "加标注前记了撤销点");
        Check.True(document.Undo(), "撤销加标注");
        Check.False(document.Dirty, "撤销回打开时的样子 → 不算改过");
        Check.True(document.CanRedo, "撤销后能重做");
        Check.True(document.Redo(), "重做加标注");
        Check.Equal(1, document.Doc!.Marks.Count, "重做后标注回来了");

        document.MarkSaved();
        Check.False(document.Dirty, "导出后重新记基准 → 不算改过");

        // 裁剪：宽高与撤销栈
        Check.True(document.Crop(CropAxis.Rows, 1, 3), "剪掉两行");
        Check.Equal(6, document.Width, "剪行不改宽");
        Check.Equal(4, document.Height, "剪行减高");
        Check.True(document.Dirty, "裁剪算改过（已导出之后又改了）");
        Check.Equal(EditColor(2, 0), EditPixel(document.Image!, 2, 0), "裁剪后第 0 行还是原来的第 0 行");
        Check.Equal(EditColor(2, 3), EditPixel(document.Image!, 2, 1), "裁剪后第 1 行是原来的第 3 行（中间那条没了）");
        Check.True(document.Doc!.Area.Height == 4, "文档区域跟着新底图");

        Check.True(document.Undo(), "撤销裁剪");
        Check.Equal(6, document.Height, "撤销后高度恢复");
        Check.False(document.Dirty, "撤销回导出时的样子 → 不算改过");
        Check.True(document.Redo(), "重做裁剪");
        Check.Equal(4, document.Height, "重做后又是剪完的高度");

        // 不合法的裁剪不动、也不进撤销栈
        int before = document.Height;
        Check.False(document.Crop(CropAxis.Rows, 2, 2), "长度 0 不剪");
        Check.False(document.Crop(CropAxis.Rows, 0, before), "整条边长不剪");
        Check.False(document.Crop(CropAxis.Columns, -5, 400), "拖出图外夹成整条也不剪");
        Check.Equal(before, document.Height, "不合法裁剪不改尺寸");

        Check.True(document.Crop(CropAxis.Columns, 0, 2), "剪掉两列");
        Check.Equal(4, document.Width, "剪列减宽");
        Check.Equal(4, document.Height, "剪列不改高");

        // 换图：撤销栈清空、标题与来源一起换
        document.Load(EditImage(3, 2), "", null);
        Check.Equal("未命名", document.Title, "没有文件名时标题是「未命名」");
        Check.False(document.CanUndo, "换图清空撤销栈");
        Check.False(document.CanRedo, "换图清空重做栈");
        Check.Equal(3, document.Width, "换图换了底图宽");

        document.Clear();
        Check.False(document.HasImage, "清空后回到空态");
        Check.False(document.Dirty, "空态不算改过");
        Check.Equal("", document.FileName, "清空也清掉文件名");
        Check.Equal("未命名", document.Title, "没有文件名时标题是「未命名」");
    }

    /// <summary>造一张 <c>(R = x×30, G = y×30)</c> 的图，剪完能逐像素认出是原来哪一行 / 哪一列。</summary>
    private static BitmapSource EditImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                var color = EditColor(x, y);
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 0xFF;
            }
        }

        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null,
            pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Color EditColor(int x, int y) =>
        Color.FromArgb(0xFF, (byte)(x * 30), (byte)(y * 30), 0);

    private static Color EditPixel(BitmapSource image, int x, int y)
    {
        var one = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), one, 4, 0);
        return Color.FromArgb(one[3], one[2], one[1], one[0]);
    }
}
