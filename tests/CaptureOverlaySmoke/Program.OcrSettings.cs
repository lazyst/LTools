using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 文字识别设置（P9-e，照上游 <c>text_recognition/*</c> + <c>screenshot/auto_execute_after_text_recognition</c>）：
/// 默认值 / 归一 / 落盘、设置卡片回显与改动、模型下载按钮状态、
/// 以及结果窗「识别后自动执行」六值在真窗口里的实际效果。
/// </summary>
/// <remarks>
/// 与 <c>BehaviorSection</c> 同一套做法：把 <c>ConfigStore.ConfigPath</c> 指到本段自己的临时目录，
/// 断言完恢复原路径并 <c>Invalidate()</c>，不给后面的段留脏配置。
/// 自动动作那几条用真窗口 + 系统 OCR 跑一遍（识别一张自己画的文字图），
/// 所以验的是「真的识别完 → 真的复制 / 真的关窗」。
/// </remarks>
internal static partial class Program
{
    private static void OcrSettingsSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.TextRecognitionStore")!;
        var behaviorType = asm.GetType("LTools.Features.CapturePro.ScreenshotBehavior")!;
        var paddleType = asm.GetType("LTools.Features.PaddleOcr")!;
        var modelStoreType = asm.GetType("LTools.Features.OcrModelStore")!;
        var tierType = asm.GetType("LTools.Features.PaddleOcr+Tier")!;
        var resultType = asm.GetType("LTools.Views.CapturePro.CaptureResultWindow")!;
        var nativeClipType = asm.GetType("LTools.Native.NativeClipboard")!;

        string ClipText()
        {
            var args = new object?[] { null };
            bool ok = (bool)nativeClipType.GetMethod("TryGetText", Stat)!.Invoke(null, args)!;
            return ok ? (string?)args[0] ?? "" : "";
        }

        void ClearClip() => nativeClipType.GetMethod("Clear", Stat)!.Invoke(null, null);

        object Get(string name) => storeType.GetProperty(name, Stat)!.GetValue(null)!;
        bool Flag(string name) => (bool)Get(name);
        string Text(string name) => (string)Get(name);
        void Set(string method, params object?[] args) =>
            storeType.GetMethod(method, Stat)!.Invoke(null, args);
        void Invalidate() => storeType.GetMethod("Invalidate", Stat)!.Invoke(null, null);

        object Tier(string name) => Enum.Parse(tierType, name);
        bool Ready(string name) => (bool)paddleType.GetMethod("IsReady", Stat)!
            .Invoke(null, new[] { Tier(name) })!;
        string TierName(string name) => (string)paddleType.GetMethod("DisplayName", Stat)!
            .Invoke(null, new[] { Tier(name) })!;
        bool CanDownload(string name) => (bool)modelStoreType.GetMethod("CanDownload", Stat)!
            .Invoke(null, new[] { Tier(name) })!;

        string OcrAction() => (string)behaviorType.GetProperty("AutoExecuteAfterTextRecognition", Stat)!
            .GetValue(null)!;
        void SetOcrAction(string? value) => behaviorType
            .GetMethod("SetAutoExecuteAfterTextRecognition", Stat)!.Invoke(null, new object?[] { value });

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "ocrsettings-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        Window? settings = null;
        var windows = new List<Window>();
        try
        {
            // ============ ① 默认值（逐条对齐 configurationschema.cpp） ============
            Check(Text("Formatting") == "none", "默认排版 = 不排版：" + Text("Formatting"));
            Check(Text("Punctuation") == "none", "默认标点 = 不转换");
            Check(Text("ModelType") == "small", "默认模型档 = small");
            Check(Text("DetectorResizePolicy") == "max", "默认检测缩放 = 限制长边");
            Check(Text("DetectionProcessing") == "accuracy_first", "默认检测处理 = 精度优先");
            Check(Text("FillStyle") == "background_fill", "默认填充样式 = 背景填充");
            Check(Flag("ResidentProcess"), "默认常驻识别进程");
            Check(!Flag("ModelHotStart"), "默认不热启动");
            Check(!Flag("ShowTextHighlighting"), "默认不加遮罩");
            Check(Flag("SaveResultAsImage"), "默认保存结果图");
            Check(!(bool)storeType.GetField("DefaultShowOriginalPreview", Stat)!.GetValue(null)!,
                "默认不显示原图（上游 show_original_image 默认关）");
            Check(OcrAction() == "no_action", "默认识别后不执行操作");
            Check(!File.Exists(settingsFile), "只读默认值不会平白落一份 settings.json");

            // ============ ② 写入即落盘 + 归一化 ============
            Set("SetModelType", "medium");
            Check(File.Exists(settingsFile), "改一项才出现 settings.json");
            Check(File.ReadAllText(settingsFile).Contains("\"ModelType\": \"medium\""),
                "settings.json 的 TextRecognition 段写着 ModelType = medium");
            Set("SetModelType", "huge");
            Invalidate();
            Check(Text("ModelType") == "small", "认不出的模型档回落 small");

            Set("SetFillStyle", "blur");
            Invalidate();
            Check(Text("FillStyle") == "blur", "填充样式可以切到 blur");
            Set("SetFillStyle", "paint");
            Invalidate();
            Check(Text("FillStyle") == "background_fill", "认不出的填充样式回落背景填充");

            Set("SetResidentProcess", false);
            Set("SetModelHotStart", true);
            Set("SetShowTextHighlighting", true);
            Set("SetSaveResultAsImage", false);
            Set("SetShowOriginalPreview", false);
            Invalidate();
            Check(!Flag("ResidentProcess") && Flag("ModelHotStart") && Flag("ShowTextHighlighting") &&
                  !Flag("SaveResultAsImage") && !Flag("ShowOriginalPreview"), "五个开关都能读回来");
            Check(File.ReadAllText(settingsFile).Contains("\"ResidentProcess\": false"),
                "常驻开关落盘");

            SetOcrAction("copy_text");
            Check(OcrAction() == "copy_text", "识别后自动执行可以写 copy_text");
            Check(File.ReadAllText(settingsFile).Contains("\"AutoExecuteAfterTextRecognition\": \"copy_text\""),
                "自动动作落在 Screenshot 段");
            SetOcrAction("COPY_TEXT_AND_END_SCREENSHOT");
            Check(OcrAction() == "copy_text_and_end_screenshot", "自动动作大小写不敏感");
            SetOcrAction("nonsense");
            Check(OcrAction() == "no_action", "认不出的自动动作回落不执行");

            // ============ ③ 设置页：回显 + 改动即落盘 + 模型下载按钮 ============
            Set("SetFormatting", "smart");
            Set("SetPunctuation", "full");
            Set("SetModelType", "extra_small");
            Set("SetDetectorResizePolicy", "min");
            Set("SetDetectionProcessing", "speed_first");
            Set("SetFillStyle", "blur");
            Set("SetResidentProcess", false);
            Set("SetModelHotStart", true);
            Set("SetShowTextHighlighting", true);
            Set("SetSaveResultAsImage", false);
            Set("SetShowOriginalPreview", false);
            Invalidate();

            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);
            settings.GetType().GetField("_initializing", Inst)!.SetValue(settings, false);
            settings.GetType().GetMethod("PopulateOcr", Inst)!.Invoke(settings, null);
            Pump(settings);

            ComboBox? Combo(string name) => settings!.FindName(name) as ComboBox;
            CheckBox? Box(string name) => settings!.FindName(name) as CheckBox;
            Button? Btn(string name) => settings!.FindName(name) as Button;
            TextBlock? Label(string name) => settings!.FindName(name) as TextBlock;

            Check(Combo("OcrFormattingCombo")?.SelectedIndex == 3, "排版回显「智能排版」");
            Check(Combo("OcrPunctuationCombo")?.SelectedIndex == 2, "标点回显「全角标点」");
            Check(Combo("OcrModelCombo")?.SelectedIndex == 0, "模型档回显「超小号 V6」");
            Check(Combo("OcrResizeCombo")?.SelectedIndex == 1, "检测缩放回显「放大短边」");
            Check(Combo("OcrProcessingCombo")?.SelectedIndex == 1, "检测处理回显「速度优先」");
            Check(Combo("OcrFillCombo")?.SelectedIndex == 1, "填充样式回显「模糊」");
            Check(Box("OcrResidentBox")?.IsChecked == false, "常驻回显为不勾");
            Check(Box("OcrHotStartBox")?.IsChecked == true, "热启动回显为勾上");
            Check(Box("OcrHighlightBox")?.IsChecked == true, "文字遮罩回显为勾上");
            Check(Box("OcrSaveImageBox")?.IsChecked == false, "保存结果图回显为不勾");
            Check(Box("OcrPreviewBox")?.IsChecked == false, "原图预览回显为不勾");

            bool tinyReady = Ready("Tiny");
            Check((Btn("OcrDownloadBtn")?.Content as string) == "随包内置",
                "超小号档随包，按钮写「随包内置」（" + (Btn("OcrDownloadBtn")?.Content as string) + "）");
            Check(Btn("OcrDownloadBtn")?.IsEnabled == false, "超小号档不需要下载，按钮置灰");
            Check((Label("OcrModelStatus")?.Text ?? "").Contains("随包内置"),
                "状态行写明超小号档随包（" + (Label("OcrModelStatus")?.Text ?? "") + "）");
            Check(tinyReady, "超小档在安装包里就位");

            Combo("OcrModelCombo")!.SelectedIndex = 1;   // small
            Pump(settings);
            Check(Text("ModelType") == "small", "下拉切「小号 V6」立刻写进设置");
            bool smallReady = Ready("Small");
            Check(!CanDownload("Small"), "小号档随包内置，不再提供下载");
            Check(smallReady, "小号档在安装包里就位（打包漏了模型会被这条抓住）");
            Check((Btn("OcrDownloadBtn")?.Content as string) == "随包内置",
                "小号档的按钮写「随包内置」（" + (Btn("OcrDownloadBtn")?.Content as string) + "）");
            Check(Btn("OcrDownloadBtn")?.IsEnabled == false, "小号档随包，按钮置灰");
            Check((Label("OcrModelStatus")?.Text ?? "").Contains("随包内置"),
                "状态行写明小号档随包（" + (Label("OcrModelStatus")?.Text ?? "") + "）");
            Check((Label("OcrModelStatus")?.Text ?? "").Contains(TierName("Small")),
                "状态行写着小号档的名字");

            // 默认档（small）必须能直接给出引擎键，不用退到超小号
            var engineForModel = paddleType.GetMethod("EngineKey", Stat)!;
            var defaultTier = paddleType.GetMethod("TierOf", Stat)!
                .Invoke(null, new object?[] { Text("ModelType") })!;
            Check((string)engineForModel.Invoke(null, new[] { defaultTier })! == "v6small",
                "默认模型档 = 小号 V6（引擎键 v6small）");
            var dirOf = modelStoreType.GetMethod("DirOf", Stat)!;
            string bundledDir = Path.Combine(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                "Assets", "Ocr", "ppocrv6");
            Check(dirOf.Invoke(null, new[] { defaultTier })!.ToString() == bundledDir,
                "小号档就用安装包里那份目录，不去数据根找");

            Combo("OcrModelCombo")!.SelectedIndex = 2;   // medium
            Pump(settings);
            Check(Text("ModelType") == "medium", "下拉切「中号 V6」立刻写进设置");
            Check(CanDownload("Medium"), "中号档可以下载");
            Check((Label("OcrModelStatus")?.Text ?? "").Contains(TierName("Medium")),
                "状态行写着中号档的名字");

            Combo("OcrFillCombo")!.SelectedIndex = 0;
            Pump(settings);
            Check(Text("FillStyle") == "background_fill", "填充样式下拉切回背景填充");

            Combo("OcrFormattingCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(Text("Formatting") == "keep", "排版下拉切「保留换行」立刻写进设置");

            Box("OcrHighlightBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("ShowTextHighlighting"), "取消文字遮罩立刻写进设置");

            Box("OcrResidentBox")!.IsChecked = true;
            Pump(settings);
            Check(Flag("ResidentProcess"), "勾上常驻立刻写进设置");

            settings.Close();
            settings = null;

            // ============ ④ 结果窗「识别后自动执行」六值（真窗口 + 系统 OCR） ============
            Set("SetShowOriginalPreview", false);   // 别让预览窗干扰本段
            Set("SetShowTextHighlighting", false);
            Invalidate();

            var image = BuildTextShot(320, 120, "Hello LTools 123");

            // 4.1 enable_edit_mode：识别完自动切到文字页
            SetOcrAction("enable_edit_mode");
            var edit = OpenResult(resultType, image, quick: false);
            windows.Add(edit);
            Check(WaitUntil(() => Done(edit, Inst) || !edit.IsVisible, 15000), "识别在 15 秒内完成");
            Pump(edit);
            Check(TabOf(edit, Inst) == "text", "「启用编辑模式」把页签切到文字");
            Check(edit.IsVisible, "编辑模式不关窗");

            // 「保存识别结果图片」= 整张截图 + 原位替换的文字（不是只有文字、裁到文字外接框的一张图）
            var saved = (BitmapSource?)resultType.GetMethod("BuildSavableImage", Inst)!
                .Invoke(edit, null);
            Check(saved != null && saved.PixelWidth == image.PixelWidth &&
                  saved.PixelHeight == image.PixelHeight,
                $"保存识别结果图片 = 整张截图（{saved?.PixelWidth}×{saved?.PixelHeight}，原图 {image.PixelWidth}×{image.PixelHeight}）");
            if (saved != null)
            {
                var probe = new byte[4];
                saved.CopyPixels(new Int32Rect(1, 1, 1, 1), probe, 4, 0);
                Check(probe[3] == 255, "成品图角落不透明（铺了原图，不是只有文字的透明图）");
            }

            edit.Close();

            // 4.2 copy_text_and_end_screenshot：复制文本并关窗
            ClearClip();
            SetOcrAction("copy_text_and_end_screenshot");
            bool closed = false;
            var copied = OpenResult(resultType, image, quick: false,
                window => window.Closed += (_, _) => closed = true);
            windows.Add(copied);
            Check(WaitUntil(() => closed, 15000), "「复制文本并结束截图」识别完自己关窗");
            Check(ClipText().Length > 0, "关窗之前真的复制了文本：" + ClipText());

            // 4.3 quick_copy_text_and_end_screenshot：非快速入口不生效
            SetOcrAction("quick_copy_text_and_end_screenshot");
            var normal = OpenResult(resultType, image, quick: false);
            windows.Add(normal);
            Check(WaitUntil(() => Done(normal, Inst), 15000), "普通入口识别在 15 秒内完成");
            Pump(normal);
            Check(normal.IsVisible, "quick_* 在普通入口不生效，窗口留着");
            normal.Close();

            // 4.4 quick_copy_text_and_end_screenshot：快速入口生效
            ClearClip();
            bool quickClosed = false;
            var quick = OpenResult(resultType, image, quick: true,
                window => window.Closed += (_, _) => quickClosed = true);
            windows.Add(quick);
            Check(WaitUntil(() => quickClosed, 15000), "快速入口的 quick_* 识别完关窗");
            Check(ClipText().Length > 0, "快速入口也复制了文本：" + ClipText());

            // 4.5 no_action：识别完什么都不做
            SetOcrAction("no_action");
            var idle = OpenResult(resultType, image, quick: true);
            windows.Add(idle);
            Check(WaitUntil(() => Done(idle, Inst), 15000), "不执行操作时识别也正常完成");
            Pump(idle);
            Check(idle.IsVisible, "不执行操作时不关窗");
            idle.Close();
        }
        finally
        {
            settings?.Close();
            foreach (Window window in windows)
            {
                try { if (window.IsVisible) window.Close(); }
                catch { /* 已经关掉 */ }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
            SetOcrAction(null);
        }
    }

    /// <summary>结果窗「自动动作已经跑过」的标志（识别完成的那一刻置位）。</summary>
    private static bool Done(Window window, BindingFlags flags) =>
        (bool)window.GetType().GetField("_autoActionDone", flags)!.GetValue(window)!;

    /// <summary>结果窗当前的页签键（text / table / qr / …）。</summary>
    private static string TabOf(Window window, BindingFlags flags) =>
        (string)window.GetType().GetField("_tab", flags)!.GetValue(window)!;

    /// <summary>用内部构造函数开一扇结果窗（不跑 <c>Show()</c> 之前的识别流程也能设 quick）。</summary>
    /// <param name="beforeShow">在 <c>Show()</c> 之前跑一次（订阅 <c>Closed</c> 必须赶在识别完成前）。</param>
    private static Window OpenResult(Type type, BitmapSource image, bool quick,
        Action<Window>? beforeShow = null)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var window = (Window)Activator.CreateInstance(type, Any, null,
            new object?[] { image, null, null, new Int32Rect(0, 0, 0, 0), false, false, quick },
            CultureInfo.InvariantCulture)!;
        beforeShow?.Invoke(window);
        window.Show();
        window.Activate();
        Pump(window);
        return window;
    }

    /// <summary>画一张白底黑字的图（系统 OCR 能认出来）。</summary>
    private static BitmapSource BuildTextShot(int width, int height, string content)
    {
        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            var text = new FormattedText(content, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 36, Brushes.Black, 1.0);
            dc.DrawText(text, new Point(12, (height - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
