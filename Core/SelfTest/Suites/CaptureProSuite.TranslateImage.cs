using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 翻译的版面处理与「译文渲进原图」（照上游 <c>screenshot_translation/layout_processing</c>、
/// <c>original_image_translation</c>、<c>copy_translation_results_as_image</c>）。
/// </summary>
/// <remarks>
/// 纯数据 + 一张内存位图（<see cref="RenderTargetBitmap"/> 在 STA 上跑），**不开窗口**：
/// 结果窗的按钮 / 复制分支由仓库外 <c>capture-overlay-smoke</c> 端到端断言。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void TranslationImageCases()
    {
        TranslationLayoutOptionCases();
        TranslationLayoutPrepareCases();
        TranslationRenderCases();
    }

    private static void TranslationLayoutOptionCases()
    {
        Check.Equal(2, TranslationLayout.Keys.Count, "版面处理两个选项");
        Check.Equal("smart_merge", TranslationLayout.Keys[0], "第一项是智能合并（上游默认）");
        Check.Equal("original", TranslationLayout.Keys[1], "第二项是原始行");

        Check.Equal(TranslationLayout.SmartMerge, TranslationLayout.Normalize(null), "空值 → 智能合并");
        Check.Equal(TranslationLayout.SmartMerge, TranslationLayout.Normalize("  "), "空白值 → 智能合并");
        Check.Equal(TranslationLayout.SmartMerge, TranslationLayout.Normalize("乱写"), "认不出的值 → 智能合并");
        Check.Equal(TranslationLayout.Original, TranslationLayout.Normalize(" ORIGINAL "),
            "键大小写不敏感 + 去空白");

        Check.Equal("智能合并", TranslationLayout.LabelOf("smart_merge"), "智能合并的标签");
        Check.Equal("按原行", TranslationLayout.LabelOf("original"), "按原行的标签");
        Check.True(TranslationLayout.Describe("smart_merge").Contains("段落"), "智能合并的说明提到段落");
        Check.True(TranslationLayout.Describe("original").Contains("逐行"), "按原行的说明提到逐行");
    }

    private static void TranslationLayoutPrepareCases()
    {
        var boxes = new List<OcrLineBox>
        {
            Line("Hello", 10, 10, 100),
            Line("world", 10, 32, 100),
            Line("Third", 10, 80, 120),
        };
        IReadOnlyList<OcrTextLine> layer = OcrTextLayer.FromLines(boxes);
        var texts = new List<string> { "Hello", "world", "Third" };

        // 一、按原行：一行不少、一行不动
        TranslationOverlay original = TranslationLayout.Prepare(texts, layer, "original");
        Check.Equal(3, original.Texts.Count, "按原行仍是 3 行");
        Check.Equal(3, original.Layer.Count, "按原行的落点也是 3 行");
        Check.Equal("world", original.Texts[1], "按原行文本原样");
        Check.Equal(32, original.Layer[1].Box.Y, "按原行行框原样");
        Check.True(original.Layer[1].FontSize == null, "单行不带合并字号");

        // 二、智能合并：紧挨着的两行接成一段，段框是并集，字号取成员中位
        TranslationOverlay merged = TranslationLayout.Prepare(texts, layer, "smart_merge");
        Check.Equal(2, merged.Texts.Count, "智能合并出 2 段");
        Check.Equal("Hello world", merged.Texts[0], "第一段接起来了（英文补空格）");
        Check.Equal(2, merged.Layer.Count, "落点也变 2 段");
        Check.Equal(10, merged.Layer[0].Box.Y, "第一段从第一行开始");
        Check.Equal(42, merged.Layer[0].Box.Height, "第一段的框 = 两行并集");
        Check.True(Math.Abs((merged.Layer[0].FontSize ?? 0) - 16.4) < 0.001,
            "合并段字号 = 成员行字号（槽高 82%）的中位");
        Check.True(merged.Layer[1].FontSize == null, "单行段不带合并字号");
        Check.Equal(80, merged.Layer[1].Box.Y, "第二段是自己的位置");

        // 三、空层不炸
        TranslationOverlay empty = TranslationLayout.Prepare(Array.Empty<string>(),
            Array.Empty<OcrTextLine>(), "smart_merge");
        Check.Equal(0, empty.Texts.Count, "空层不产出文本");
        Check.Equal(0, empty.Layer.Count, "空层不产出落点");

        // 四、ReplaceTexts：换字不换框，颜色 / 字号跟着走
        TranslationOverlay replaced = TranslationLayout.ReplaceTexts(merged,
            new List<string> { "你好 世界", "第三行" });
        Check.Equal("你好 世界", replaced.Texts[0], "第一段换成译文");
        Check.Equal("第三行", replaced.Texts[1], "第二段换成译文");
        Check.Equal(merged.Layer[0].Box, replaced.Layer[0].Box, "译文落在同一个段框上");
        Check.Equal(merged.Layer[0].FontSize, replaced.Layer[0].FontSize, "段字号原样带过去");
        Check.Equal(0, replaced.Layer[0].Index, "落点序号按顺序重编");

        // 五、译文比落点少（模型少给一行）：多出来的段落画空，不越界
        TranslationOverlay shortResult = TranslationLayout.ReplaceTexts(merged,
            new List<string> { "只有一段" });
        Check.Equal(2, shortResult.Layer.Count, "落点仍按原段落数");
        Check.Equal("", shortResult.Layer[1].Text, "没有译文的段留空");
    }

    private static void TranslationRenderCases()
    {
        // 一行不画字、只铺底色：断言像素时不用管字形
        var green = new Rgb(0, 255, 0);
        var line = new OcrTextLine
        {
            Index = 0,
            Text = "",
            Box = new Int32Rect(5, 5, 20, 10),
            TextColor = Rgb.Black,
            BackgroundColor = green,
        };
        IReadOnlyList<OcrTextLine> layer = new[] { line };
        BitmapSource source = Solid(40, 30, Colors.Red);

        // 一、画进原图：尺寸 = 源图，框外仍是原图，框内是行底色
        BitmapSource? onImage = OcrImageRenderer.Render(source, layer, new[] { "" },
            includeSource: true, fillBackground: true);
        Check.True(onImage != null, "画进原图能渲出图");
        Check.Equal(40, onImage?.PixelWidth ?? 0, "画进原图的宽 = 源图宽");
        Check.Equal(30, onImage?.PixelHeight ?? 0, "画进原图的高 = 源图高");
        Check.Equal(Colors.Red, onImage == null ? Colors.Black : PixelAt(onImage, 39, 29),
            "框外像素仍是原图");
        Check.Equal(green.ToColor(), onImage == null ? Colors.Black : PixelAt(onImage, 10, 8),
            "框内铺的是这一行估出来的底色");
        // 二、纯文字图：尺寸按行框外接，白底
        BitmapSource? plain = OcrImageRenderer.Render(source, layer, new[] { "" },
            includeSource: false, fillBackground: false);
        Check.Equal(25, plain?.PixelWidth ?? 0, "纯文字图宽 = 最右行框的右边界");
        Check.Equal(15, plain?.PixelHeight ?? 0, "纯文字图高 = 最下行框的下边界");
        Check.Equal(Colors.White, plain == null ? Colors.Black : PixelAt(plain, 0, 0),
            "纯文字图没有原图（白底）");

        // 三、空层：没有可画的，返回 null（调用方退回复制文本）
        Check.True(OcrImageRenderer.Render(source, Array.Empty<OcrTextLine>(),
            Array.Empty<string>(), includeSource: true, fillBackground: true) == null,
            "空层渲不出图");

        // 四、合并段的字号：Fit 用给定字号，不再按单行宽度缩
        var mergedLine = new OcrTextLine
        {
            Box = new Int32Rect(0, 0, 200, 60),
            FontSize = 16,
            TextColor = Rgb.Black,
            BackgroundColor = Rgb.White,
        };
        (FormattedText fitted, double size) = OcrTextPaint.Fit(
            "一段很长的译文，需要在这个段框里折行，字号保持不变。", mergedLine, 1.0);
        Check.True(Math.Abs(size - 16) < 0.001, "合并段按给的字号排版");
        Check.True(Math.Abs(fitted.MaxTextWidth - 200) < 0.001, "合并在段框宽度里折行");
    }

    private static Color PixelAt(BitmapSource image, int x, int y)
    {
        var pixels = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixels, 4, 0);
        return Color.FromArgb(pixels[3], pixels[2], pixels[1], pixels[0]);
    }

    private static BitmapSource Solid(int width, int height, Color color)
    {
        int stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = color.A;
        }

        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null,
            pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
