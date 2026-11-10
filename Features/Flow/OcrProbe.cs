using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Core;
using Windows.Media.Ocr;

namespace LTools.Features;

/// <summary>
/// 文字识别：读一张图片（通常来自「截图」步骤，也可直接填文件路径），识别其中的文字。
/// </summary>
/// <remarks>
/// <para>
/// 取图和识字是两件事：截图步骤负责拿图，这里只负责认字。中途切换窗口、画面变了，
/// 识别的仍是截图当时存下来的那张 png。
/// </para>
/// <para>
/// 两个引擎，默认用系统 OCR（<see cref="OcrEngine"/>）：零额外运行时与模型文件，
/// 中文能力随系统语言包提供（Windows 10/11 中文环境默认就有 zh-Hans）。
/// </para>
/// <para>
/// 对精度有要求时可在取值器里切到 PP-OCRv6（见 <see cref="PaddleOcr"/>）：
/// 同图基准里 12–14px 中文、数字与标点的错误率比系统 OCR 低一个数量级。
/// </para>
/// <para>
/// 识别是异步 / 同步的阻塞调用，本方法在流程的后台线程上执行；不回到 UI 线程，
/// 因此不会与 WPF 的 Dispatcher 互相等待。
/// </para>
/// </remarks>
internal static class OcrProbe
{
    /// <summary>识别图片中的文字。没有文字时返回空文本 + <c>found=false</c>。</summary>
    public static VarValue Run(Probe probe, RunContext ctx)
    {
        if (ctx.IsCancelled) throw new FlowAbortException("已停止", userCancelled: true);

        string path = ResolveImagePath(probe, ctx);

        using Bitmap image = LoadImage(path);
        return RecognizeImage(image, probe.OcrEngine);
    }

    /// <summary>
    /// 识别一张已经在内存里的位图（「文字识别」选屏幕区域时先截一块再认字，不落盘）。
    /// 引擎选择、语言包检查与结果整形与读文件那条路完全一致。
    /// </summary>
    public static VarValue RecognizeImage(Bitmap image, string? engine)
    {
        ArgumentNullException.ThrowIfNull(image);

        PaddleOcr.Tier? tier = PaddleOcr.ParseTier(engine);

        // 系统 OCR 的引擎不保证可跨线程，每次现建；PP-OCRv6 由 PaddleOcr 懒加载并跨步骤复用。
        OcrEngine? windows = tier == null ? OcrLayout.CreateEngine() : null;
        if (tier == null && windows == null)
            throw new FlowException("系统没有可用的 OCR 语言包，请在 Windows 设置里安装「中文(简体)」或「English」的 OCR 组件。");

        return Recognize(tier, windows, image);
    }

    private static VarValue Recognize(PaddleOcr.Tier? tier, OcrEngine? windows, Bitmap bmp) =>
        tier is PaddleOcr.Tier engine
            ? PaddleOcr.Recognize(bmp, engine)
            : RecognizeWindows(windows!, bmp);

    // ============ 图片来源 ============

    /// <summary>
    /// 解析 <see cref="Probe.OcrImage"/>：整段一个变量时优先用它的 <c>file</c> 属性
    /// （截图的 <c>${截图}</c> 自带图片路径），否则按模板渲染成路径。
    /// </summary>
    private static string ResolveImagePath(Probe probe, RunContext ctx)
    {
        string spec = (probe.OcrImage ?? "").Trim();
        if (spec.Length == 0)
            throw new FlowException("「文字识别」还没有指定图片：先用「截图」拿一张图，再把它的变量填进来（例如 ${截图}）。");

        string path;
        if (Template.TrySingleVariable(spec, out string name))
        {
            VarValue value = ctx.ResolvePath(name);
            if (value.File.Length > 0)
            {
                path = value.File;
            }
            else if (value.W != null || value.X != null)
            {
                // 区域 / 坐标变量本身不是图片：多半是截图被取消，或者把「截图」和「找图」弄混了。
                throw new FlowException(
                    $"变量「{name}」里没有图片（截图被取消时不会留下 file；找图的结果是坐标，不是图片）。");
            }
            else
            {
                path = value.Text.Trim();
            }
        }
        else
        {
            path = Template.Render(spec, ctx).Trim();
        }

        if (path.Length == 0)
            throw new FlowException($"「{spec}」里没有图片路径（截图被取消时不会留下 file）。");

        path = ImageLibrary.Resolve(path, ctx.ResourceRoot);
        if (!File.Exists(path))
            throw new FlowException($"要识别的图片不存在：{path}");
        return path;
    }

    private static Bitmap LoadImage(string path)
    {
        try
        {
            return new Bitmap(path);
        }
        catch (Exception ex)
        {
            throw new FlowException($"读不到要识别的图片「{path}」：{ex.Message}");
        }
    }

    private static VarValue RecognizeWindows(OcrEngine engine, Bitmap bmp)
    {
        string text = Tidy(OcrLayout.RecognizeWindows(engine, bmp).Lines);
        return new VarValue { Text = text, Flag = text.Length > 0 };
    }

    /// <summary>按行拼接并清理 OCR 噪声：去掉中文之间多余的空格、空行与首尾空白。</summary>
    private static string Tidy(IEnumerable<OcrLine> lines)
    {
        var sb = new StringBuilder();
        foreach (OcrLine line in lines)
        {
            string text = OcrText.Clean(line.Text ?? "");
            if (text.Length == 0) continue;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }
}

/// <summary>OCR 文本清理：两个引擎共用，保证同一段文字在切换引擎后格式一致。</summary>
internal static class OcrText
{
    /// <summary>汉字之间被 OCR 插入的空格（系统 OCR 常把中文按词切开发空格）。</summary>
    private static readonly Regex CjkSpaces = new(
        @"(?<=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])\s+(?=[\u3400-\u4DBF\u4E00-\u9FFF\uF900-\uFAFF])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>去掉首尾空白与中文之间的空格；全是空白时返回空串。</summary>
    public static string Clean(string text)
    {
        string trimmed = (text ?? "").Trim();
        return trimmed.Length == 0 ? "" : CjkSpaces.Replace(trimmed, "");
    }
}
