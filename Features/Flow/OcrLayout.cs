using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using LTools.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LTools.Features;

/// <summary>OCR 识别到的一个词 / 一个字：文字 + 它在这张图里的像素矩形。</summary>
internal sealed class OcrWordBox
{
    public string Text { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }

    /// <summary>这个词的置信度（0..1）；系统 OCR 不提供，恒为 0。</summary>
    public double Score { get; init; }
}

/// <summary>OCR 识别到的一行：文字 + 行矩形 + 词框。</summary>
internal sealed class OcrLineBox
{
    public string Text { get; init; } = "";
    public int X { get; init; }
    public int Y { get; init; }
    public int W { get; init; }
    public int H { get; init; }

    /// <summary>整行置信度（0..1）；系统 OCR 不提供，恒为 0。</summary>
    public double Score { get; init; }

    /// <summary>整行是否够可信（PP-OCRv6 要求 ≥ 0.5；系统 OCR 恒为 true）。</summary>
    public bool Confident { get; init; }

    /// <summary>词框，用于算出「这几个字在哪儿」；可能为空（引擎没给词级结果）。</summary>
    public IReadOnlyList<OcrWordBox> Words { get; init; } = Array.Empty<OcrWordBox>();
}

/// <summary>
/// 带坐标的 OCR：「文字识别」只要文本，而「文字点击」还要知道那几个字在图里的位置。
/// 两个引擎在这里汇成同一份 <see cref="OcrLineBox"/> 列表；坐标是这张图的像素（左上角为原点），
/// 加上截图区域的左上角就是虚拟桌面物理像素坐标。
/// </summary>
/// <remarks>
/// 系统 OCR 的引擎不保证可跨线程，每次现建；PP-OCRv6 由 <see cref="PaddleOcr"/> 懒加载并跨步骤复用。
/// 识别是阻塞调用，调用方在流程的后台线程上。
/// </remarks>
internal static class OcrLayout
{
    /// <summary>
    /// 识别一张图，返回带坐标的行 / 词。<paramref name="engine"/> 与取值器的 <c>OcrEngine</c> 同义：
    /// <c>null</c> / <c>windows</c> = 系统 OCR，<c>v6tiny</c> / <c>v6small</c> = PP-OCRv6。
    /// </summary>
    public static IReadOnlyList<OcrLineBox> Read(Bitmap bmp, string? engine)
    {
        PaddleOcr.Tier? tier = PaddleOcr.ParseTier(engine);
        if (tier is PaddleOcr.Tier level) return PaddleOcr.ReadLines(bmp, level);

        OcrEngine windows = CreateEngine()
            ?? throw new FlowException("系统没有可用的 OCR 语言包，请在 Windows 设置里安装「中文(简体)」或「English」的 OCR 组件。");
        return ReadWindows(windows, bmp);
    }

    // ============ 系统 OCR ============

    private static IReadOnlyList<OcrLineBox> ReadWindows(OcrEngine engine, Bitmap bmp)
    {
        OcrResult result = RecognizeWindows(engine, bmp);
        var lines = new List<OcrLineBox>(result.Lines.Count);

        foreach (OcrLine line in result.Lines)
        {
            string text = OcrText.Clean(line.Text ?? "");
            if (text.Length == 0) continue;

            var words = new List<OcrWordBox>(line.Words.Count);
            double left = double.MaxValue, top = double.MaxValue;
            double right = double.MinValue, bottom = double.MinValue;

            foreach (OcrWord word in line.Words)
            {
                string wt = word.Text ?? "";
                if (wt.Length == 0) continue;

                var r = word.BoundingRect;
                int x = (int)Math.Round(r.X);
                int y = (int)Math.Round(r.Y);
                int w = (int)Math.Round(r.Width);
                int h = (int)Math.Round(r.Height);
                if (w <= 0 || h <= 0) continue;

                words.Add(new OcrWordBox { Text = wt, X = x, Y = y, W = w, H = h });
                left = Math.Min(left, r.X);
                top = Math.Min(top, r.Y);
                right = Math.Max(right, r.X + r.Width);
                bottom = Math.Max(bottom, r.Y + r.Height);
            }

            // 只给行文本、没有词框的行也算数（点整行中心），所以矩形退化时退回 0 尺寸。
            int lx = words.Count > 0 ? (int)Math.Round(left) : 0;
            int ly = words.Count > 0 ? (int)Math.Round(top) : 0;
            int lw = words.Count > 0 ? (int)Math.Round(right - left) : 0;
            int lh = words.Count > 0 ? (int)Math.Round(bottom - top) : 0;

            lines.Add(new OcrLineBox
            {
                Text = text,
                X = lx, Y = ly, W = lw, H = lh,
                Confident = true,
                Words = words,
            });
        }

        return lines;
    }

    /// <summary>
    /// 把位图交给系统 OCR。WinRT 的软件位图只能从流 / 像素缓冲构造，用 PNG 内存流最省事，
    /// 也避免 P/Invoke 锁住位图。
    /// </summary>
    public static OcrResult RecognizeWindows(OcrEngine engine, Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        byte[] bytes = ms.ToArray();

        var ras = new InMemoryRandomAccessStream();
        try
        {
            // DataWriter 写完后必须 DetachStream，否则 ras 释放时会连带关闭流。
            var writer = new DataWriter(ras.GetOutputStreamAt(0));
            try
            {
                writer.WriteBytes(bytes);
                writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            }
            finally
            {
                writer.DetachStream();
            }

            ras.Seek(0);
            var decoder = BitmapDecoder.CreateAsync(ras).AsTask().GetAwaiter().GetResult();
            using var bitmap = decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
                .AsTask().GetAwaiter().GetResult();

            return engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            ras.Dispose();
        }
    }

    /// <summary>建一个系统 OCR 引擎；没有语言包时返回 null。</summary>
    public static OcrEngine? CreateEngine()
    {
        // 优先用户配置的 OCR 语言（中文系统即 zh-Hans-CN）。
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine != null) return engine;
        }
        catch (Exception ex)
        {
            AppLog.Warn("OCR", "按用户语言创建识别引擎失败，改用 en-US：" + ex.Message);
        }

        // 用户语言没有 OCR 组件时退回英文，至少能识别数字 / 英文。
        foreach (string tag in new[] { "zh-Hans-CN", "zh-Hans", "en-US" })
        {
            try
            {
                var language = new Language(tag);
                var engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine != null) return engine;
            }
            catch
            {
                // 语言标签不可用：继续试下一个
            }
        }
        return null;
    }
}
