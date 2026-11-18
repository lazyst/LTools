using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Windows;
using LTools.Features.CapturePro;
using LTools.Features.Units.Screen;

namespace LTools.Core.SelfTest;

/// <summary>
/// 自动识别二维码：识别图缩放、网址判定、气泡尺寸 / 落点，以及真实二维码的检测
/// （两枚码同图 → 都认出来且不重复计数）。
/// </summary>
/// <remarks>
/// 纯计算 + GDI+ 位图，<b>不开窗口</b>；覆盖层上的标记 / 气泡交互走仓库外
/// <c>capture-overlay-smoke</c>。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void QrGeometryCases()
    {
        // 识别图缩放：min(1, sqrt(1920*1080/(w*h)), 2560/max(w,h))
        Check.Equal(1.0, QrScan.ScaleFor(1920, 1080), "1080p 不缩");
        Check.Equal(1.0, QrScan.ScaleFor(1280, 720), "更小的图也不放大");
        Check.Equal(0.5, QrScan.ScaleFor(3840, 2160), "4K 缩到一半");
        Check.True(Math.Abs(QrScan.ScaleFor(8000, 100) - 0.32) < 1e-9, "超宽按长边缩");
        Check.Equal(1.0, QrScan.ScaleFor(0, 0), "空尺寸兜底 1");

        using (var source = new Bitmap(800, 600, PixelFormat.Format32bppArgb))
        using (var half = QrScan.Prepare(source, 0.5))
        {
            Check.Equal(400, half.Width, "宽度按比例 floor");
            Check.Equal(300, half.Height, "高度按比例 floor");
        }

        using (var source = new Bitmap(40, 30, PixelFormat.Format32bppArgb))
        using (var same = QrScan.Prepare(source, 1.0))
        {
            Check.Equal(40, same.Width, "比例 1 原样");
            Check.False(ReferenceEquals(source, same), "原样也返回新图（调用方统一 Dispose）");
        }

        // 网址判定：整串必须是一个绝对 http/https 地址，正文里带链接一律不算
        Check.Equal("https://example.com/a?b=1", QrScan.WebUrl("https://example.com/a?b=1"), "完整网址");
        Check.Equal("https://example.com", QrScan.WebUrl("  https://example.com  "), "首尾空白先修掉");
        Check.True(QrScan.WebUrl("见 https://example.com") == null, "夹在正文里不算");
        Check.True(QrScan.WebUrl("https://example.com\n下一行") == null, "多行不算");
        Check.True(QrScan.WebUrl("ftp://example.com") == null, "只认 http / https");
        Check.True(QrScan.WebUrl("example.com") == null, "没有 scheme 不算");
        Check.True(QrScan.WebUrl("https://") == null, "没有 host 不算");
        Check.True(QrScan.WebUrl("") == null, "空文本不算");
        Check.True(QrScan.WebUrl(null) == null, "null 不算");

        // 气泡尺寸：总宽 ≤344、文本区扣左右各 24、文本高 48–240 且压到「可用高 − 140」
        Check.Equal(344.0, QrScan.BubbleWidth(1000), "宽屏取上限 344");
        Check.Equal(200.0, QrScan.BubbleWidth(200), "窄屏跟着可用宽");
        Check.Equal(296.0, QrScan.ContentWidth(344), "文本区 = 344 − 48");
        Check.Equal(48.0, QrScan.TextHeight(0, 800), "最矮 48");
        Check.Equal(58.0, QrScan.TextHeight(50, 800), "量出来 + 8");
        Check.Equal(240.0, QrScan.TextHeight(400, 800), "最高 240");
        Check.Equal(60.0, QrScan.TextHeight(400, 200), "可用高不够时压到 60");
        Check.Equal(24.0, QrScan.TextHeight(400, 100), "可用高极小时留 24 下限");

        // 落点：优先放标记右侧（箭头朝左），放不下翻左侧，再夹进可用区
        var available = new Rect(0, 0, 1000, 800);
        var anchor = new Rect(100, 100, 24, 24);
        QrBubbleLayout right = QrScan.PlaceBubble(anchor, available, 344, 200);
        Check.True(right.ArrowOnLeft, "右侧放得下 → 箭头朝左");
        Check.Equal(121.0, right.Left, "左 = 标记右边 + 1 + 8 − 12");
        Check.Equal(12.0, right.Top, "纵向按标记中心居中");
        Check.Equal(100.0, right.ArrowY, "箭头指着标记中心");

        QrBubbleLayout flipped = QrScan.PlaceBubble(new Rect(900, 100, 24, 24), available, 344, 200);
        Check.False(flipped.ArrowOnLeft, "右边放不下 → 翻到左侧");
        Check.Equal(560.0, flipped.Left, "左 = 标记左边 − 8 − 宽 + 12");

        QrBubbleLayout bottom = QrScan.PlaceBubble(new Rect(500, 770, 24, 24), available, 344, 200);
        Check.Equal(601.0, bottom.Top, "贴底时夹进可用区");
        Check.True(bottom.ArrowY <= 200 - QrScan.Pad - QrScan.Radius - 8, "箭头不出圆角");
        Check.True(bottom.ArrowY >= QrScan.Pad + QrScan.Radius + 8, "箭头上限也在圆角内");
    }

    private static void QrDetectCases()
    {
        const string first = "https://example.com/one";
        const string second = "LTools 二维码：第二枚";

        using var canvas = new Bitmap(800, 600, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(canvas))
        {
            graphics.Clear(Color.White);
            using Bitmap a = QrCodeAlgorithms.Generate(first, 220, 4, "M");
            using Bitmap b = QrCodeAlgorithms.Generate(second, 180, 4, "M");
            graphics.DrawImage(a, new Rectangle(40, 40, 220, 220));
            graphics.DrawImage(b, new Rectangle(450, 260, 180, 180));
        }

        List<QrMark> marks = QrScan.Detect(canvas);
        Check.Equal(2, marks.Count, "两枚码都认出来（掩码不会把同一枚数两遍）");

        var texts = new List<string>();
        foreach (var mark in marks) texts.Add(mark.Text);
        Check.Contains(string.Join("\n", texts), first);
        Check.Contains(string.Join("\n", texts), second);

        // 中心要落在画上去的位置附近（ZXing 给的是定位点，外接框会比码本身小一圈）
        QrMark? firstMark = null;
        foreach (var mark in marks)
            if (mark.Text == first) { firstMark = mark; break; }
        Check.True(firstMark.HasValue, "第一枚有结果");
        if (firstMark is { } one)
        {
            Check.True(Math.Abs(one.CenterX - 150) <= 40, $"第一枚中心 x ≈150（实际 {one.CenterX:0.#}）");
            Check.True(Math.Abs(one.CenterY - 150) <= 40, $"第一枚中心 y ≈150（实际 {one.CenterY:0.#}）");
            Check.True(one.Width > 40 && one.Height > 40, "外接框有一定大小");
        }

        QrMark? secondMark = null;
        foreach (var mark in marks)
            if (mark.Text == second) { secondMark = mark; break; }
        Check.True(secondMark.HasValue, "第二枚有结果");
        if (secondMark is { } two)
        {
            Check.True(Math.Abs(two.CenterX - 540) <= 40, $"第二枚中心 x ≈540（实际 {two.CenterX:0.#}）");
            Check.True(Math.Abs(two.CenterY - 350) <= 40, $"第二枚中心 y ≈350（实际 {two.CenterY:0.#}）");
        }

        using var blank = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.White);
        Check.Equal(0, QrScan.Detect(blank).Count, "空白图没有码");
    }
}
