using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>识别文字层里的一行：原始框、估出来的字色 / 底色，以及可编辑的文字。</summary>
internal sealed class OcrTextLine
{
    /// <summary>在识别结果里的序号（0 起）。</summary>
    public int Index { get; init; }

    /// <summary>原始文字（<see cref="OcrTextEditModel"/> 的编辑不写回这里）。</summary>
    public string Text { get; init; } = "";

    /// <summary>这一行在源图片里的像素矩形。</summary>
    public Int32Rect Box { get; init; }

    /// <summary>估出来的字色（估不出时取对比色）。</summary>
    public Rgb TextColor { get; init; } = Rgb.Black;

    /// <summary>字色是**从像素里估出来的**（false = 用了对比色兜底）。</summary>
    public bool HasTextColor { get; init; }

    /// <summary>估出来的底色（文字层铺在它上面）。</summary>
    public Rgb BackgroundColor { get; init; } = Rgb.White;
}

/// <summary>
/// 把 OCR 的行框变成「可重排的文字层」：每行一个槽位，字色 / 底色从原图像素里估。
/// </summary>
/// <remarks>
/// <para>
/// 上游 <c>screenshotocrvisuals.cpp</c> 是「逐行拟合并估算颜色」重排文字，本文件只负责
/// **数据**（槽位 + 颜色）；字号拟合在界面层（<c>CaptureOcrCanvas</c>）用真实
/// <see cref="System.Windows.Media.FormattedText"/> 量宽，因为字体度量必须跑在 UI 线程。
/// </para>
/// <para>
/// 颜色估算失败（花底 / 证据不足）不报错：字色退回「浅底黑字、深底白字」的对比色，
/// 底色退回白色。上游在这种情况下用主题字色，这里改对比色是因为文字层会**铺满底色**，
/// 主题字色可能正好和底色融在一起。
/// </para>
/// </remarks>
internal static class OcrTextLayer
{
    /// <summary>按行框估色，产出文字层数据（在后台线程跑：只碰像素，不碰 WPF）。</summary>
    public static IReadOnlyList<OcrTextLine> Build(IReadOnlyList<OcrLineBox> lines, OcrImage image)
    {
        if (lines.Count == 0) return Array.Empty<OcrTextLine>();

        var boxes = new Int32Rect[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            OcrLineBox line = lines[i];
            boxes[i] = Clip(new Int32Rect(line.X, line.Y,
                Math.Max(1, line.W), Math.Max(1, line.H)), image.Width, image.Height);
        }

        var result = new List<OcrTextLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            Int32Rect box = boxes[i];
            OcrLineBox line = lines[i];

            if (box.Width <= 0 || box.Height <= 0)
            {
                result.Add(new OcrTextLine
                {
                    Index = i,
                    Text = line.Text,
                    Box = new Int32Rect(line.X, line.Y, Math.Max(1, line.W), Math.Max(1, line.H)),
                    BackgroundColor = Rgb.White,
                    TextColor = Rgb.Black,
                });
                continue;
            }

            Rgb background = OcrPalette.EstimateBackground(image, box, boxes);
            Rgb? text = OcrPalette.EstimateTextColor(image, box, background, boxes);

            result.Add(new OcrTextLine
            {
                Index = i,
                Text = line.Text,
                Box = box,
                BackgroundColor = background,
                TextColor = text ?? (background.Luma > 150 ? Rgb.Black : Rgb.White),
                HasTextColor = text.HasValue,
            });
        }

        return result;
    }

    /// <summary>
    /// 没有像素可估时（冒烟 / 自检直接灌结果）的一层：白底黑字，框照行框。
    /// </summary>
    public static IReadOnlyList<OcrTextLine> FromLines(IReadOnlyList<OcrLineBox> lines)
    {
        var result = new List<OcrTextLine>(lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            OcrLineBox line = lines[i];
            result.Add(new OcrTextLine
            {
                Index = i,
                Text = line.Text,
                Box = new Int32Rect(line.X, line.Y, Math.Max(1, line.W), Math.Max(1, line.H)),
                BackgroundColor = Rgb.White,
                TextColor = Rgb.Black,
            });
        }

        return result;
    }

    /// <summary>
    /// 一行的**起始字号**（源图像素）：槽高的 82%，再按量出来的宽度收回。
    /// </summary>
    /// <remarks>
    /// 上游是「整段行内缩放 + 保持行距」，本项目的识别结果没有段落信息（只有逐行框），
    /// 所以逐行拟合：字号先按槽高给，量出来太宽就等比缩，最小 6 px。
    /// </remarks>
    public static double FitFontSize(double baseSize, double measuredWidth, double availableWidth)
    {
        if (baseSize <= 0) return 6;
        if (measuredWidth <= 0 || availableWidth <= 0) return baseSize;
        double limit = availableWidth * 0.98;
        return measuredWidth <= limit ? baseSize : Math.Max(6, baseSize * limit / measuredWidth);
    }

    private static Int32Rect Clip(Int32Rect rect, int width, int height)
    {
        int x = Math.Clamp(rect.X, 0, width);
        int y = Math.Clamp(rect.Y, 0, height);
        int right = Math.Clamp(rect.X + rect.Width, x, width);
        int bottom = Math.Clamp(rect.Y + rect.Height, y, height);
        return new Int32Rect(x, y, right - x, bottom - y);
    }
}
