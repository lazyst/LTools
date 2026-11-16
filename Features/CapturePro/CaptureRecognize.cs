using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Features;
using LTools.Features.Units.Screen;

namespace LTools.Features.CapturePro;

/// <summary>一次识别的结果：文字、词框、近似表格、二维码。</summary>
internal sealed class RecognizeResult
{
    public string Text { get; init; } = "";
    public IReadOnlyList<OcrLineBox> Lines { get; init; } = Array.Empty<OcrLineBox>();
    public TableGrid Table { get; init; } = TableGrid.Empty;

    /// <summary>二维码内容；没找到为空串（不是错误）。</summary>
    public string QrText { get; init; } = "";

    /// <summary>二维码格式（`QR_CODE`）；没找到为空串。</summary>
    public string QrType { get; init; } = "";

    /// <summary>用到的 OCR 引擎（给人看的名字）。</summary>
    public string Engine { get; init; } = "";

    public long ElapsedMs { get; init; }

    /// <summary>识别失败的原因（如系统没装 OCR 语言包）；成功为空串。</summary>
    public string Error { get; init; } = "";

    public bool Ok => Error.Length == 0;
}

/// <summary>
/// 截图 Pro 的识别管线：文字（OCR）+ 二维码 + 近似表格，一次跑完。
/// </summary>
/// <remarks>
/// <para>
/// 与「文字识别」取值共用同一套底层（<see cref="OcrLayout"/> / <see cref="QrCodeAlgorithms"/>），
/// 不另写识别实现；这里的差别只有「一次要三样结果」和「失败要能显示在窗口里」。
/// </para>
/// <para>
/// **绝不抛异常**：调用方是后台任务（识别要一两秒），出错要变成窗口里的一行提示，
/// 而不是把任务线程炸掉。二维码找不到只是空文本；OCR 真挂了才写 <see cref="RecognizeResult.Error"/>。
/// </para>
/// </remarks>
internal static class CaptureRecognize
{
    /// <summary>识别一张图（<paramref name="engine"/> 同「文字识别」的引擎参数；null = 系统 OCR）。</summary>
    public static RecognizeResult Run(BitmapSource image, string? engine)
    {
        var watch = Stopwatch.StartNew();

        IReadOnlyList<OcrLineBox> lines = Array.Empty<OcrLineBox>();
        string error = "";

        try
        {
            using Bitmap bitmap = ToBitmap(image);
            lines = OcrLayout.Read(bitmap, engine);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Recognize", "识别失败：" + ex.Message);
            error = ex.Message;
        }

        string qrText = "", qrType = "";
        try
        {
            using Bitmap bitmap = ToBitmap(image);
            var qr = QrCodeAlgorithms.Read(bitmap);
            qrText = qr.Text ?? "";
            qrType = qr.Text == null ? "" : qr.Type;
        }
        catch (Exception ex)
        {
            // 二维码识别失败从来不算错误（截图里没有码是常态）
            AppLog.Warn("CapturePro.Recognize", "二维码识别失败：" + ex.Message);
        }

        return new RecognizeResult
        {
            Text = string.Join("\n", lines.Select(l => l.Text)),
            Lines = lines,
            Table = OcrTable.Build(lines),
            QrText = qrText,
            QrType = qrType,
            Engine = EngineLabel(engine),
            ElapsedMs = watch.ElapsedMilliseconds,
            Error = error,
        };
    }

    /// <summary>引擎参数 → 界面上的名字（与设置页下拉的叫法一致）。</summary>
    public static string EngineLabel(string? engine) => engine?.Trim().ToLowerInvariant() switch
    {
        "v6tiny" => "PP-OCRv6 快速",
        "v6small" => "PP-OCRv6 精确",
        "windows" or null or "" => "系统 OCR",
        _ => engine!.Trim(),
    };

    /// <summary>
    /// 把成品位图转成 GDI+ 位图（OCR 与二维码两套底层都吃 <see cref="Bitmap"/>）。
    /// 走 png 内存流最省事，也避免锁住 WPF 的位图。
    /// </summary>
    public static Bitmap ToBitmap(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));

        using var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;

        // Bitmap 不能在源流被释放后再读，所以这里复制一份出来
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded);
    }
}
