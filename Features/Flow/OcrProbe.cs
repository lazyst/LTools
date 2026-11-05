using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Core;
using LTools.Native;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LTools.Features;

/// <summary>
/// 截图 OCR：截取前台窗口 / 整个屏幕 / 固定区域，用 Windows 自带的
/// <see cref="OcrEngine"/> 识别文字，结果作为文本变量。
/// </summary>
/// <remarks>
/// <para>
/// 用系统 OCR（Windows.Media.Ocr）而不是引入 Tesseract / Paddle：零额外运行时与模型文件，
/// 中文识别能力随系统语言包提供（Windows 10/11 中文环境默认就有 zh-Hans）。
/// </para>
/// <para>
/// 识别是异步 WinRT 调用，本方法在流程的后台线程上同步等待结果；不回到 UI 线程，
/// 因此不会与 WPF 的 Dispatcher 互相等待。截图坐标与鼠标注入 / 图像匹配同一坐标系。
/// </para>
/// </remarks>
internal static class OcrProbe
{
    /// <summary>汉字之间被 OCR 插入的空格（Windows OCR 常把中文按词切开发空格）。</summary>
    private static readonly Regex CjkSpaces = new(
        @"(?<=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])\s+(?=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>识别指定区域的屏幕文字。没有文字时返回空文本 + <c>found=false</c>。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        if (ctx.IsCancelled) throw new FlowAbortException("已停止", userCancelled: true);

        // 每次现建引擎：OcrEngine 不保证可跨线程，流程与编辑器测试可能在不同后台线程调用。
        var engine = CreateEngine()
            ?? throw new FlowException("系统没有可用的 OCR 语言包，请在 Windows 设置里安装「中文(简体)」或「English」的 OCR 组件。");

        var (x, y, w, h) = ScreenRegion.Resolve(probe);
        string? indicator = null;
        try
        {
            // 自动化指示器是置顶窗口，会被一起截进画面；截图前收起，识别完再恢复。
            indicator = LTools.Views.RecordingIndicatorWindow.HideAutomationForCapture();
            if (indicator != null && !ctx.Wait(80))
                throw new FlowAbortException("已停止", userCancelled: true);

            using Bitmap bmp = ScreenCapture.Capture(x, y, w, h);
            string text = Recognize(engine, bmp);

            return new VarValue
            {
                Text = text,
                Flag = text.Length > 0,
            };
        }
        finally
        {
            LTools.Views.RecordingIndicatorWindow.RestoreAutomation(indicator);
        }
    }

    private static string Recognize(OcrEngine engine, Bitmap bmp)
    {
        // WinRT 的软件位图只能从流 / 像素缓冲构造；用 PNG 内存流最省事，也避免 P/Invoke 锁位图。
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

            OcrResult result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
            return Tidy(result.Lines);
        }
        finally
        {
            ras.Dispose();
        }
    }

    /// <summary>按行拼接并清理 OCR 噪声：去掉中文之间多余的空格、空行与首尾空白。</summary>
    private static string Tidy(IEnumerable<OcrLine> lines)
    {
        var sb = new StringBuilder();
        foreach (OcrLine line in lines)
        {
            string text = (line.Text ?? "").Trim();
            if (text.Length == 0) continue;

            text = CjkSpaces.Replace(text, "");
            if (text.Length == 0) continue;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    private static OcrEngine? CreateEngine()
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
