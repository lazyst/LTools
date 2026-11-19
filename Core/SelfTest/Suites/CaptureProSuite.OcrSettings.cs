using System.Windows;
using LTools.Config;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 文字识别全局设置（P9-e，照上游 <c>text_recognition/*</c>）：默认值、归一化、
/// 「识别后自动执行」六值分发、填充样式与模糊核。
/// </summary>
/// <remarks>
/// 真窗口那一段（设置卡片回显 / 落盘、结果窗自动动作、原图预览、保存图片）在仓库外
/// <c>capture-overlay-smoke</c> 的 <c>ocrsettings</c> 段；这里只碰纯逻辑。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void OcrSettingsCases()
    {
        // —— 默认值：逐条对齐 configurationschema.cpp ——
        var file = new TextRecognitionFile();
        Check.Equal("none", file.DefaultFormatting, "默认排版 = 不排版");
        Check.Equal("none", file.DefaultPunctuation, "默认标点 = 不转换");
        Check.Equal("small", file.ModelType, "默认模型档 = small");
        Check.Equal("max", file.DetectorResizePolicy, "默认检测缩放 = 限制长边");
        Check.Equal("accuracy_first", file.TextDetectionProcessing, "默认检测处理 = 精度优先");
        Check.Equal(true, file.ResidentProcess, "默认常驻识别进程");
        Check.Equal(false, file.ModelHotStart, "默认不热启动");
        Check.Equal("background_fill", file.FillStyle, "默认填充样式 = 背景填充");
        Check.Equal(false, file.ShowTextHighlighting, "默认不加遮罩");
        Check.Equal(true, file.SaveRecognitionResultAsImage, "默认保存结果图");
        Check.Equal(true, file.ShowOriginalImagePreview, "默认显示原图预览");

        // —— 模型档归一 ——
        Check.Equal("extra_small", TextRecognitionStore.NormalizeModelType("extra_small"), "extra_small 保留");
        Check.Equal("small", TextRecognitionStore.NormalizeModelType("small"), "small 保留");
        Check.Equal("medium", TextRecognitionStore.NormalizeModelType("medium"), "medium 保留");
        Check.Equal("small", TextRecognitionStore.NormalizeModelType("SMALL"), "大小写不敏感");
        Check.Equal("medium", TextRecognitionStore.NormalizeModelType(" medium "), "首尾空白也认");
        Check.Equal("small", TextRecognitionStore.NormalizeModelType(null), "null 回 small");
        Check.Equal("small", TextRecognitionStore.NormalizeModelType("huge"), "认不出回 small");
        Check.Equal(3, TextRecognitionStore.ModelTypes.Length, "模型档三档");
        Check.Equal("extra_small", TextRecognitionStore.ModelTypes[0], "下拉第一项是超小号");
        Check.Equal("medium", TextRecognitionStore.ModelTypes[2], "下拉最后一项是中号");

        // —— 其余枚举归一 ——
        Check.Equal("min", TextRecognitionStore.NormalizeResizePolicy("min"), "检测缩放 min");
        Check.Equal("min", TextRecognitionStore.NormalizeResizePolicy("MIN"), "检测缩放大小写不敏感");
        Check.Equal("max", TextRecognitionStore.NormalizeResizePolicy(""), "检测缩放空 = max");
        Check.Equal("speed_first", TextRecognitionStore.NormalizeDetectionProcessing("speed_first"),
            "检测处理 speed_first");
        Check.Equal("accuracy_first", TextRecognitionStore.NormalizeDetectionProcessing("fast"),
            "检测处理认不出 = 精度优先");
        Check.Equal("blur", TextRecognitionStore.NormalizeFillStyle("blur"), "填充样式 blur");
        Check.Equal("background_fill", TextRecognitionStore.NormalizeFillStyle(null), "填充样式空 = 背景填充");

        // —— 识别后自动执行（六值）——
        Check.Equal("no_action", ScreenshotBehavior.DefaultOcrAction, "默认不执行操作");
        Check.Equal("no_action", ScreenshotBehavior.NormalizeOcrAction(null), "null = 不执行");
        Check.Equal("copy_text", ScreenshotBehavior.NormalizeOcrAction("copy_text"), "copy_text");
        Check.Equal("copy_text_and_end_screenshot",
            ScreenshotBehavior.NormalizeOcrAction("COPY_TEXT_AND_END_SCREENSHOT"), "大小写不敏感");
        Check.Equal("quick_copy_text", ScreenshotBehavior.NormalizeOcrAction("quick_copy_text"), "quick_copy_text");
        Check.Equal("quick_copy_text_and_end_screenshot",
            ScreenshotBehavior.NormalizeOcrAction("quick_copy_text_and_end_screenshot"), "quick 并结束");
        Check.Equal("enable_edit_mode", ScreenshotBehavior.NormalizeOcrAction("enable_edit_mode"), "启用编辑模式");
        Check.Equal("no_action", ScreenshotBehavior.NormalizeOcrAction("copy"), "认不出 = 不执行");

        Check.True(ScreenshotBehavior.IsQuickOnlyOcrAction("quick_copy_text"), "quick_copy_text 只在快速入口");
        Check.True(ScreenshotBehavior.IsQuickOnlyOcrAction("quick_copy_text_and_end_screenshot"),
            "quick 并结束只在快速入口");
        Check.False(ScreenshotBehavior.IsQuickOnlyOcrAction("copy_text"), "copy_text 两个入口都生效");
        Check.False(ScreenshotBehavior.IsQuickOnlyOcrAction("enable_edit_mode"), "编辑模式两个入口都生效");

        Check.True(ScreenshotBehavior.CopiesOcrText("copy_text"), "copy_text 复制");
        Check.True(ScreenshotBehavior.CopiesOcrText("copy_text_and_end_screenshot"), "并结束也复制");
        Check.True(ScreenshotBehavior.CopiesOcrText("quick_copy_text"), "quick 也复制");
        Check.True(ScreenshotBehavior.CopiesOcrText("quick_copy_text_and_end_screenshot"), "quick 并结束也复制");
        Check.False(ScreenshotBehavior.CopiesOcrText("enable_edit_mode"), "编辑模式不复制");
        Check.False(ScreenshotBehavior.CopiesOcrText("no_action"), "不执行不复制");

        Check.True(ScreenshotBehavior.EndsAfterOcr("copy_text_and_end_screenshot"), "并结束会结束");
        Check.True(ScreenshotBehavior.EndsAfterOcr("quick_copy_text_and_end_screenshot"), "quick 并结束会结束");
        Check.False(ScreenshotBehavior.EndsAfterOcr("copy_text"), "只复制不结束");
        Check.False(ScreenshotBehavior.EndsAfterOcr("enable_edit_mode"), "编辑模式不结束");

        // —— 填充样式（照上游 kBackgroundBlendAmount / logicalSigma）——
        Check.Equal("background_fill", OcrFillStyle.Default, "填充样式默认背景填充");
        Check.Equal("blur", OcrFillStyle.Normalize("blur"), "blur 保留");
        Check.Equal("blur", OcrFillStyle.Normalize("BLUR"), "大小写不敏感");
        Check.Equal("background_fill", OcrFillStyle.Normalize(""), "空 = 背景填充");
        Check.True(OcrFillStyle.IsBlur("blur"), "IsBlur(blur)");
        Check.False(OcrFillStyle.IsBlur("background_fill"), "IsBlur(background_fill)");
        Check.Equal(0.5, OcrFillStyle.BackgroundBlendAmount, "混合比例 0.5");
        Check.Equal(8.0, OcrFillStyle.LogicalSigma, "逻辑 sigma 8");
        Check.Equal(8.0, OcrFillStyle.DeviceSigma(0.5), "DPR 夹到 ≥1");
        Check.Equal(8.0, OcrFillStyle.DeviceSigma(1.0), "DPR 1 不放大");
        Check.Equal(16.0, OcrFillStyle.DeviceSigma(2.0), "DPR 2 翻倍");

        // —— 模糊核（三次盒式逼近高斯，sigma 8 → 7/7/8）——
        var none = OcrBlur.BoxRadii(0);
        Check.Equal((0, 0, 0), none, "sigma 0 不模糊");
        Check.Equal((0, 0, 0), OcrBlur.BoxRadii(double.NaN), "sigma NaN 不模糊");
        Check.Equal((7, 7, 8), OcrBlur.BoxRadii(8), "sigma 8 的核半径");
        Check.Equal((0, 0, 1), OcrBlur.BoxRadii(1), "sigma 1 的核半径");
        Check.True(OcrBlur.BoxRadii(4).First < OcrBlur.BoxRadii(8).First, "sigma 越大核越大");

        // —— 模糊本身：冲激被摊平、平场不变、alpha 不动 ——
        const int width = 9, height = 9, stride = width * 4;
        var pixels = new byte[stride * height];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0;
            pixels[i + 1] = 0;
            pixels[i + 2] = 0;
            pixels[i + 3] = 255;
        }

        int center = (height / 2) * stride + (width / 2) * 4;
        pixels[center] = 255;
        pixels[center + 1] = 255;
        pixels[center + 2] = 255;

        OcrBlur.BlurBgra(pixels, width, height, stride, 2);

        Check.True(pixels[center] < 255, "冲激中心被摊平");
        Check.True(pixels[center - 4] > 0, "左边邻居被染上");
        Check.True(pixels[center + 4] > 0, "右边邻居被染上");
        Check.True(pixels[center - stride] > 0, "上边邻居被染上");
        Check.True(pixels[center + stride] > 0, "下边邻居被染上");
        Check.Equal((byte)255, pixels[center + 3], "alpha 不被模糊");

        var flat = new byte[stride * height];
        for (int i = 0; i < flat.Length; i += 4)
        {
            flat[i] = 40;
            flat[i + 1] = 80;
            flat[i + 2] = 120;
            flat[i + 3] = 255;
        }

        OcrBlur.BlurBgra(flat, width, height, stride, 8);
        bool same = true;
        for (int i = 0; i < flat.Length; i += 4)
            if (flat[i] != 40 || flat[i + 1] != 80 || flat[i + 2] != 120) same = false;

        Check.True(same, "平场模糊后仍是原色（含边缘）");

        // —— 坏参数不炸、不改像素 ——
        var guard = new byte[4 * 2 * 2];
        for (int i = 0; i < guard.Length; i += 4) guard[i] = 200;
        OcrBlur.BlurBgra(guard, 2, 2, 4, 8);
        Check.Equal((byte)200, guard[0], "stride 小于 width×4 时直接返回");
        OcrBlur.BlurBgra(guard, 2, 3, 8, 8);
        Check.Equal((byte)200, guard[0], "像素不足时直接返回");

        // —— 成品图：填充样式与文字遮罩（OcrImageRenderer 的真实像素）——
        RenderStyleCases();
    }

    /// <summary>
    /// 导图渲染的填充样式 / 反向遮罩：同一份文字层用三种画法各渲一张，
    /// 按像素确认「背景填充 = 估出的底色」「模糊 = 原图块 + 50% 底」「遮罩 = 整体压暗」。
    /// </summary>
    private static void RenderStyleCases()
    {
        const int width = 40, height = 20;
        var red = new byte[width * height * 4];
        for (int i = 0; i < red.Length; i += 4)
        {
            red[i] = 0;
            red[i + 1] = 0;
            red[i + 2] = 255;   // BGRA：红
            red[i + 3] = 255;
        }

        var source = System.Windows.Media.Imaging.BitmapSource.Create(width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, red, width * 4);
        source.Freeze();

        var layer = new[]
        {
            new OcrTextLine
            {
                Index = 0,
                Text = "A",
                Box = new Int32Rect(0, 0, width, height),
                TextColor = Rgb.Black,
                BackgroundColor = Rgb.White,
            },
        };
        var texts = new[] { "A" };

        (byte B, byte G, byte R, byte A) Pixel(System.Windows.Media.Imaging.BitmapSource? bitmap, int x, int y)
        {
            if (bitmap == null) return (0, 0, 0, 0);
            var buffer = new byte[4];
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
            return (buffer[0], buffer[1], buffer[2], buffer[3]);
        }

        var filled = OcrImageRenderer.Render(source, layer, texts, includeSource: true,
            fillBackground: true);
        var fillPixel = Pixel(filled, 1, 1);
        Check.True(fillPixel.R > 240 && fillPixel.G > 240 && fillPixel.B > 240,
            "背景填充：估出的白底盖住原图");

        var blurred = OcrImageRenderer.Render(source, layer, texts, includeSource: true,
            fillBackground: true, fillStyle: OcrFillStyle.Blur);
        var blurPixel = Pixel(blurred, 1, 1);
        Check.True(blurPixel.R > 200 && blurPixel.G is > 90 and < 190 && blurPixel.B is > 90 and < 190,
            $"模糊：原图红 + 50% 白 ≈ (255,127,127)，实际 ({blurPixel.R},{blurPixel.G},{blurPixel.B})");
        Check.True(blurPixel.G < fillPixel.G, "模糊与背景填充确实画得不一样");

        var plain = OcrImageRenderer.Render(source, layer, texts, includeSource: true,
            fillBackground: false);
        var plainPixel = Pixel(plain, 1, 1);
        Check.True(plainPixel.R > 240 && plainPixel.G < 10, "关掉填充只剩原图");

        var masked = OcrImageRenderer.Render(source, layer, texts, includeSource: true,
            fillBackground: false, highlight: true);
        var maskPixel = Pixel(masked, width - 2, height - 2);
        int plainSum = plainPixel.R + plainPixel.G + plainPixel.B;
        int maskSum = maskPixel.R + maskPixel.G + maskPixel.B;
        Check.True(maskSum < plainSum, $"文字遮罩把非文字区域压暗（{plainSum} → {maskSum}）");
        Check.True(maskPixel.R < 240, "遮罩覆盖到画布右下角");

        Check.True(OcrImageRenderer.Render(source, Array.Empty<OcrTextLine>(), texts,
            includeSource: true, fillBackground: true) == null, "空文字层不产图");
    }
}
