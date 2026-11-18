using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 识别文字层：像素估色（背景 / 字色）、行层构建、字号拟合、编辑模型（选区 / 撤销）、画布命中。
/// </summary>
/// <remarks>
/// 只碰纯数据与 GDI+ 位图，<b>不开窗口</b>：画布 / 结果窗的结构与真实鼠标走仓库外冒烟
/// （<c>capture-overlay-smoke</c>）。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void OcrPaletteCases()
    {
        // ---- 白底黑笔画 ----
        using (Bitmap plain = Bars(120, 32, Color.White, Color.Black, 4))
        {
            OcrImage image = OcrImage.From(plain);
            var box = new Int32Rect(0, 0, 120, 32);
            Rgb background = OcrPalette.EstimateBackground(image, box, Array.Empty<Int32Rect>());
            Rgb? text = OcrPalette.EstimateTextColor(image, box, background,
                Array.Empty<Int32Rect>());

            Check.True(background.Luma > 240, "白底估色应是浅色");
            Check.True(text.HasValue, "白底黑字应该估得出字色");
            Check.True(text!.Value.Luma < 80, "字色应该接近黑");
        }

        // ---- 深底白笔画（不能被平均成灰）----
        using (Bitmap dark = Bars(120, 32, Color.FromArgb(30, 30, 30), Color.White, 4))
        {
            OcrImage image = OcrImage.From(dark);
            var box = new Int32Rect(0, 0, 120, 32);
            Rgb background = OcrPalette.EstimateBackground(image, box, Array.Empty<Int32Rect>());
            Rgb? text = OcrPalette.EstimateTextColor(image, box, background,
                Array.Empty<Int32Rect>());

            Check.True(background.Luma < 60, "深底估色应是深色");
            Check.True(text.HasValue, "深底白字应该估得出字色");
            Check.True(text!.Value.Luma > 210, "字色应该接近白");
        }

        // ---- 彩色字：红笔画不能被拆成灰 ----
        using (Bitmap red = Bars(120, 32, Color.White, Color.FromArgb(208, 32, 32), 4))
        {
            OcrImage image = OcrImage.From(red);
            var box = new Int32Rect(0, 0, 120, 32);
            Rgb background = OcrPalette.EstimateBackground(image, box, Array.Empty<Int32Rect>());
            Rgb? text = OcrPalette.EstimateTextColor(image, box, background,
                Array.Empty<Int32Rect>());

            Check.True(text.HasValue, "红字应该估得出字色");
            Check.True(text!.Value.R > text.Value.G + 60 && text.Value.R > text.Value.B + 60,
                "字色应该偏红（而不是把彩边平均成灰）");
        }

        // ---- 花底（棋盘格）：宁可估不出，也不要把纹理当字 ----
        using (Bitmap noise = Checkerboard(120, 32))
        {
            OcrImage image = OcrImage.From(noise);
            var box = new Int32Rect(0, 0, 120, 32);
            Rgb background = OcrPalette.EstimateBackground(image, box, Array.Empty<Int32Rect>());
            Rgb? text = OcrPalette.EstimateTextColor(image, box, background,
                Array.Empty<Int32Rect>());
            Check.True(text == null, "花底应该放弃估字色（返回 null 走对比色兜底）");
        }

        // ---- 文字区域应被排除在背景采样之外 ----
        using (Bitmap row = Bars(120, 32, Color.White, Color.Black, 4))
        {
            OcrImage image = OcrImage.From(row);
            var box = new Int32Rect(0, 0, 120, 32);
            Rgb withExclusion = OcrPalette.EstimateBackground(image, box,
                new[] { new Int32Rect(4, 6, 108, 20) });
            Check.True(withExclusion.Luma > 240, "排除文字框后仍应是白底");
        }

        // ---- OKLab：同色距离为 0，黑白距离大 ----
        double same = OcrPalette.Perceptual(new Rgb(200, 40, 40))
            .DistanceSquared(OcrPalette.Perceptual(new Rgb(200, 40, 40)));
        double apart = OcrPalette.Perceptual(new Rgb(0, 0, 0))
            .DistanceSquared(OcrPalette.Perceptual(new Rgb(255, 255, 255)));
        Near(same, 0, 0.00001, "同色 OKLab 距离为 0");
        Check.True(apart > 0.5, "黑白在 OKLab 里差得很开");
    }

    private static void OcrTextLayerCases()
    {
        // 行层：框 / 文本照搬，颜色退到对比色
        var lines = new[]
        {
            OcrRow(10, OcrWord("Hello", 10), OcrWord("World", 100)),
            OcrRow(50, OcrWord("second", 10)),
        };

        IReadOnlyList<OcrTextLine> layer = OcrTextLayer.FromLines(lines);
        Check.Equal(2, layer.Count, "两行就出两条");
        Check.Equal("Hello World", layer[0].Text, "文本照搬");
        Check.True(layer[0].Box.Width > 0 && layer[0].Box.Height > 0, "框要有面积");

        // 有像素时：估色结果写进层里
        using (Bitmap bars = Bars(160, 80, Color.White, Color.Black, 4))
        {
            OcrImage image = OcrImage.From(bars);
            IReadOnlyList<OcrTextLine> estimated = OcrTextLayer.Build(lines, image);
            Check.Equal(2, estimated.Count, "构建行层不改行数");
            Check.True(estimated[0].BackgroundColor.Luma > 200, "白底行估出浅底");
        }

        // 字号拟合：超高给 82%，太宽等比缩，最小 6
        Near(OcrTextLayer.FitFontSize(40, 100, 400), 40, 0.001, "放得下就用起始字号");
        Near(OcrTextLayer.FitFontSize(40, 400, 200), 19.6, 0.001, "超宽按比例缩到 98% 槽宽");
        Near(OcrTextLayer.FitFontSize(4, 1000, 10), 6, 0.001, "再窄也不小于 6 px");
        Near(OcrTextLayer.FitFontSize(40, 0, 0), 40, 0.001, "量不出宽度就用起始字号");
    }

    private static void OcrEditCases()
    {
        var model = new OcrTextEditModel();
        model.Load(new[] { "hello world", "second line" });

        Check.Equal("hello world\nsecond line", model.AllText, "载入后全文");
        Check.True(!model.HasSelection, "载入后没有选区");

        model.SelectLine(0);
        Check.True(model.HasSelection, "选整行后有选区");
        Check.Equal("hello world", model.SelectionText(), "选整行取到整行");

        model.InsertText("hi");
        Check.Equal("hi\nsecond line", model.AllText, "替换整行");
        Check.Equal(0, model.CaretLine, "光标留在这行");
        Check.Equal(2, model.CaretChar, "光标在插入内容之后");

        Check.True(model.Undo(), "可以撤销");
        Check.Equal("hello world\nsecond line", model.AllText, "撤销回到替换前");
        Check.True(model.Redo(), "可以重做");
        Check.Equal("hi\nsecond line", model.AllText, "重做回到替换后");
        Check.True(model.Undo(), "再撤一次");

        // 跨行选区 + 删除
        model.Load(new[] { "abc", "def", "ghi" });
        model.SetCaret(0, 1);
        model.ExtendCaret(2, 2);
        Check.Equal("bc\ndef\ngh", model.SelectionText(), "跨行选区文本（中间整行一起选中）");
        Check.Equal(9, model.SelectionText().Length, "跨行选区长度");
        Check.True(model.DeleteSelection(), "跨行删除成功");
        Check.Equal("ai", model.AllText, "跨行删除后只剩选区外的首尾");

        // 插入换行 = 拆行
        model.Load(new[] { "abcd" });
        model.SetCaret(0, 2);
        model.InsertText("X\nY");
        Check.Equal(2, model.LineCount, "插入换行后拆成两行");
        Check.Equal("abX", model.Lines[0], "第一行前半 + 插入前半");
        Check.Equal("Ycd", model.Lines[1], "插入后半 + 原后半");
        Check.Equal(1, model.CaretLine, "光标落在最后一行");
        Check.Equal(1, model.CaretChar, "光标落在最后一行插入内容之后");

        // 退格：跨行合并
        model.Load(new[] { "ab", "cd" });
        model.SetCaret(1, 0);
        model.Backspace();
        Check.Equal(1, model.LineCount, "退格跨行合并成一行");
        Check.Equal("abcd", model.AllText, "合并后文字");
        Check.Equal(2, model.CaretChar, "光标落在合并点");

        // 没有选区时 Delete 删后面一个字符 / 在行尾合并下一行
        model.Load(new[] { "ab", "cd" });
        model.SetCaret(0, 1);
        model.DeleteForward();
        Check.Equal("a", model.Lines[0], "Delete 删后一个字符");
        Check.Equal(2, model.LineCount, "只删字符时不跨行");
        model.SetCaret(0, 1);
        model.DeleteForward();
        Check.Equal(1, model.LineCount, "行尾 Delete 合并下一行");
        Check.Equal("acd", model.AllText, "合并后文字");

        // 全选 / 清选区 / 左右移动
        model.Load(new[] { "one", "two" });
        model.SelectAll();
        Check.Equal("one\ntwo", model.SelectionText(), "全选取到全部");
        model.ClearSelection();
        Check.True(!model.HasSelection, "清选区");

        model.SetCaret(0, 0);
        model.MoveCaret(-1, false);
        Check.Equal(0, model.CaretLine, "行首再往左不动");
        model.MoveCaret(1, false);
        Check.Equal(1, model.CaretChar, "往右走一格");
        model.MoveLine(1, false);
        Check.Equal(1, model.CaretLine, "往下走一行");
        Check.Equal(1, model.CaretChar, "列号尽量保留");

        // 替换某一行（双击整行改写）
        model.Load(new[] { "keep", "old" });
        model.ReplaceLine(1, "new");
        Check.Equal("keep\nnew", model.AllText, "整行替换");
        Check.True(model.Undo(), "整行替换能撤销");
        Check.Equal("keep\nold", model.AllText, "撤销回原行");

        // 撤销上限：连改 80 次后仍能撤回最近 50 次
        model.Load(new[] { "0" });
        for (int i = 1; i <= 80; i++) model.ReplaceLine(0, i.ToString());
        int undos = 0;
        while (model.Undo()) undos++;
        Check.Equal(50, undos, "撤销栈只保留最近 50 步");
    }

    private static void OcrCanvasHitCases()
    {
        var prefix = new[] { 0.0, 10.0, 20.0, 30.0 };
        Check.Equal(0, CaptureOcrCanvas.CharIndexAt(prefix, -5), "左侧外 = 行首");
        Check.Equal(0, CaptureOcrCanvas.CharIndexAt(prefix, 5), "第一个字符内");
        Check.Equal(1, CaptureOcrCanvas.CharIndexAt(prefix, 10), "正好在边界上算后一个字符的前面");
        Check.Equal(2, CaptureOcrCanvas.CharIndexAt(prefix, 25), "第三个字符内");
        Check.Equal(3, CaptureOcrCanvas.CharIndexAt(prefix, 999), "右侧外 = 行尾");
        Check.Equal(0, CaptureOcrCanvas.CharIndexAt(Array.Empty<double>(), 5), "空行落在行首");
    }

    /// <summary>浮点近似断言（<see cref="Check"/> 里只有精确相等）。</summary>
    private static void Near(double expected, double actual, double tolerance, string what) =>
        Check.True(Math.Abs(expected - actual) <= tolerance,
            $"{what}（期望 {expected:0.###}，实际 {actual:0.###}）");

    private static OcrWordBox OcrWord(string text, int x) =>
        new() { Text = text, X = x, Y = 0, W = 60, H = 30 };

    private static OcrLineBox OcrRow(int y, params OcrWordBox[] words) => new()
    {
        Text = string.Join(" ", words.Select(w => w.Text)),
        X = words[0].X,
        Y = y,
        W = 300,
        H = 30,
        Confident = true,
        Words = words.Select(w => new OcrWordBox
        {
            Text = w.Text, X = w.X, Y = y, W = w.W, H = w.H,
        }).ToArray(),
    };

    /// <summary>白 / 深底 + 竖条「笔画」：比真字形更可控，几何与颜色都是确定的。</summary>
    private static Bitmap Bars(int width, int height, Color background, Color ink, int barWidth)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(background);
            graphics.SmoothingMode = SmoothingMode.None;

            using var brush = new SolidBrush(ink);
            for (int x = 6; x + barWidth < width - 6; x += barWidth * 3)
            {
                graphics.FillRectangle(brush, x, 6, barWidth, height - 12);
            }
        }

        return bitmap;
    }

    /// <summary>两色棋盘格：模拟纹理底，估字色应该放弃。</summary>
    private static Bitmap Checkerboard(int width, int height)
    {
        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.White);
            using var brush = new SolidBrush(Color.FromArgb(90, 90, 90));
            for (int y = 0; y < height; y += 4)
            {
                for (int x = 0; x < width; x += 4)
                {
                    if ((x / 4 + y / 4) % 2 == 0) graphics.FillRectangle(brush, x, y, 4, 4);
                }
            }
        }

        return bitmap;
    }
}
