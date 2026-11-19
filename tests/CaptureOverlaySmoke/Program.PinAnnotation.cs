using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 贴图「快速文字标注」（P9-d3，照上游 <c>screenshotquicktextannotationtoolbar.cpp</c> /
/// <c>screenshottextannotationrenderer.cpp</c> / <c>screenshotpinnedtextannotation.cpp</c>）：
/// 划选后出现四个标注开关、点一下加 / 再点去掉、样式面板改默认样式并回写已有标注、
/// 关掉「可划选」时标注照样显示（画布不接鼠标）、导出 png 带上标注、
/// 历史落盘 + 贴回按指纹恢复 / 文本变了整份丢弃。
/// </summary>
/// <remarks>
/// 识别不跑：夹具 <c>OcrFixture</c> 直接灌结果，与真识别走同一条 <c>StoreOcrResult</c>。
/// 划选与按钮点击都是真鼠标 / 真 Click 事件；配置根指到本段临时目录，收尾恢复并删掉本段建的记录。
/// </remarks>
internal static partial class Program
{
    private static void PinAnnotationSection(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var pinSettingsType = asm.GetType("LTools.Features.CapturePro.PinSettings")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var resultType = asm.GetType("LTools.Features.CapturePro.RecognizeResult")!;
        var lineType = asm.GetType("LTools.Features.OcrLineBox")!;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var pinType = asm.GetType("LTools.Views.CapturePro.CapturePinWindow")!;

        static void Set(object target, string name, object value) =>
            target.GetType().GetProperty(name)!.SetValue(target, value);

        object Line(string text, int x, int y, int w, int h)
        {
            var line = Activator.CreateInstance(lineType)!;
            Set(line, "Text", text);
            Set(line, "X", x);
            Set(line, "Y", y);
            Set(line, "W", w);
            Set(line, "H", h);
            return line;
        }

        object Fixture(string text, params object[] lines)
        {
            var array = Array.CreateInstance(lineType, lines.Length);
            for (int i = 0; i < lines.Length; i++) array.SetValue(lines[i], i);
            var result = Activator.CreateInstance(resultType)!;
            Set(result, "Text", text);
            Set(result, "Lines", array);
            return result;
        }

        void PinSet(string method, params object?[] args) =>
            pinSettingsType.GetMethod(method, Stat)!.Invoke(null, args);

        object? PinGet(string property) =>
            pinSettingsType.GetProperty(property, Stat)!.GetValue(null);

        object? PinField(string name) =>
            pinSettingsType.GetField(name, Stat)!.GetValue(null);

        var toolType = asm.GetType("LTools.Features.CapturePro.PinTextAnnotationTool")!;

        void Invalidate() => pinSettingsType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "pinannotate-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");

        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

        var created = new List<string>();
        Window? pin = null;
        Window? restored = null;
        Window? mismatch = null;
        bool savedAutoOcr = (bool)pinType.GetField("AutoOcrEnabled", Stat)!.GetValue(null)!;

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        try
        {
            PinSet("SetTextSelectionMode", "always");
            PinSet("SetAutomaticTextRecognition", true);
            PinSet("SetQuickAnnotationForSelectedText", true);
            Invalidate();

            Check((bool)PinGet("QuickAnnotationForSelectedText")!, "快速标注：设置默认开");
            Check(((Array)PinField("AnnotationTools")!).Length == 4, "快速标注：四种工具");

            // ============ ① 贴一张图并灌识别结果 ============
            pin = manager.GetMethod("Pin")!.Invoke(null,
                    new object[] { image, new Int32Rect(300, 200, image.PixelWidth, image.PixelHeight), "screenshot", null })
                as Window;
            Check(pin != null && pin.IsVisible, "快速标注：先贴一张图");
            if (pin == null) return;

            var type = pin.GetType();
            created.Add((string)type.GetProperty("PinId", Inst)!.GetValue(pin)!);

            object Call(string name, params object?[] args) => type.GetMethod(name, Inst)!.Invoke(pin, args)!;
            string Payload() => (string)type.GetProperty("AnnotationsPayload", Inst)!.GetValue(pin)!;
            bool Active(string tool) =>
                (bool)type.GetMethod("AnnotationActive", Inst)!.Invoke(pin,
                    new[] { Enum.Parse(toolType, tool) })!;
            string Phase() => type.GetProperty("OcrPhase", Inst)!.GetValue(pin)!.ToString()!;

            void Wait(int ms)
            {
                long until = Environment.TickCount64 + ms;
                while (Environment.TickCount64 < until)
                {
                    pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                    Thread.Sleep(12);
                }
            }

            void Layout()
            {
                pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                pin.UpdateLayout();
            }

            var canvas = (FrameworkElement)Field(pin, "OcrCanvas")!;
            var hiddenBar = (FrameworkElement)Field(pin, "HiddenBar")!;
            var stylePanel = (FrameworkElement)Field(pin, "HiddenStylePanel")!;
            var styleBody = (StackPanel)Field(pin, "HiddenStyleBody")!;
            var copyBtn = (Button)Field(pin, "HiddenCopyBtn")!;
            var highlightBtn = (Button)Field(pin, "HiddenHighlightBtn")!;
            var wavyBtn = (Button)Field(pin, "HiddenWavyBtn")!;
            var underlineBtn = (Button)Field(pin, "HiddenUnderlineBtn")!;
            var strikeBtn = (Button)Field(pin, "HiddenStrikeBtn")!;
            var styleBtn = (Button)Field(pin, "HiddenStyleBtn")!;

            type.GetField("OcrFixture", Inst)!.SetValue(pin,
                Fixture("第一行文字\n第二行文字",
                    Line("第一行文字", 24, 40, 200, 28),
                    Line("第二行文字", 24, 90, 220, 28)));
            pinType.GetField("AutoOcrEnabled", Stat)!.SetValue(null, true);

            Call("StartAutoRecognize");
            Wait(200);
            Layout();
            Check(Phase() == "Ready", $"快速标注：识别结果就位（实际 {Phase()}）");
            Check(highlightBtn.Visibility == Visibility.Collapsed, "快速标注：还没选区时四个开关不出现");
            Check(styleBtn.Visibility == Visibility.Collapsed, "快速标注：还没选区时「样式」不出现");

            // ============ ② 真鼠标拖选 ============
            double scale = (double)Field(pin, "_scale")!;
            if (scale <= 0) scale = 1;
            int ox = (int)Math.Round(pin.Left * scale);
            int oy = (int)Math.Round(pin.Top * scale);

            SetCursorPos(ox + 60, oy + 54);
            Wait(60);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Wait(40);
            SetCursorPos(ox + 180, oy + 104);
            Wait(80);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Wait(180);
            Layout();

            var selection = (System.Collections.IEnumerable)type.GetProperty("AnnotationSelection", Inst)!
                .GetValue(pin)!;
            Check(selection.Cast<object>().Any(), "快速标注：拖过之后有选区区间");
            Check(hiddenBar.Visibility == Visibility.Visible, "快速标注：工具栏出现");
            Check(highlightBtn.Visibility == Visibility.Visible && wavyBtn.Visibility == Visibility.Visible &&
                  underlineBtn.Visibility == Visibility.Visible && strikeBtn.Visibility == Visibility.Visible,
                "快速标注：四个标注开关都出现");
            Check(styleBtn.Visibility == Visibility.Visible, "快速标注：「样式」入口出现");
            var barText = (TextBlock)Field(pin, "HiddenBarText")!;
            Check(barText.Text.StartsWith("已选 "), $"快速标注：工具栏报字数（{barText.Text}）");

            // ============ ③ 点一下加、再点去掉 ============
            highlightBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(100);
            Check(Payload().Length > 0, "快速标注：点高亮写出载荷");
            Check(Active("Highlight"), "快速标注：高亮按钮进入激活态");
            Check((string?)highlightBtn.Tag == "Active", "快速标注：激活态走 Tag=Active");

            highlightBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(100);
            Check(Payload().Length == 0, "快速标注：再点一次高亮 = 去掉（载荷清空）");
            Check(!Active("Highlight"), "快速标注：按钮退出激活态");
            Check(highlightBtn.Tag == null, "快速标注：Tag 复位");

            // ============ ④ 高亮 + 下划线可同时存在 ============
            highlightBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(60);
            underlineBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(100);
            Check(Active("Highlight") && Active("Underline"), "快速标注：两种标注可同时挂在同一段上");
            string payload = Payload();
            Check(payload.Contains("highlight") || payload.Contains("Highlight"), "快速标注：载荷里有高亮");
            Check(payload.Contains("LineTool"), "快速标注：载荷里有线标注");

            // ============ ⑤ 样式面板：改默认色并回写已有标注 ============
            styleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(100);
            Layout();
            Check(stylePanel.Visibility == Visibility.Visible, "快速标注：点「样式」展开面板");
            Check(styleBody.Children.Count >= 5, $"快速标注：样式面板有颜色 + 线宽 / 线型 / 笔触（{styleBody.Children.Count} 行）");

            var swatchRow = styleBody.Children.OfType<WrapPanel>().FirstOrDefault();
            Check(swatchRow != null && swatchRow.Children.Count >= 2, "快速标注：样式面板有颜色预设");
            var swatch = swatchRow!.Children.OfType<Button>().Skip(1).First();
            Color expected = ((SolidColorBrush)((Border)swatch.Content).Background).Color;
            swatch.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(120);
            object styleValue = pinSettingsType.GetMethod("AnnotationStyle", Stat)!
                .Invoke(null, new[] { Enum.Parse(toolType, "Underline") })!;
            Color actual = (Color)styleValue.GetType().GetProperty("Color")!.GetValue(styleValue)!;
            Check(actual == expected, $"快速标注：样式面板改了下划线的默认色（{expected}）");
            Check(Payload() != payload, "快速标注：已有下划线跟着换色（载荷变了）");

            styleBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(80);
            Check(stylePanel.Visibility == Visibility.Collapsed, "快速标注：再点「样式」收起面板");

            // ============ ⑥ 关掉「可划选」：标注照样显示，画布不接鼠标 ============
            PinSet("SetTextSelectionMode", "only_when_displayed");
            Invalidate();
            Call("SyncHiddenTextLayer");
            Wait(80);
            Check((bool)type.GetProperty("HiddenTextSelection", Inst)!.GetValue(pin)!,
                "快速标注：关掉可划选后隐形层仍铺着（标注要看得见）");
            Check(canvas.Visibility == Visibility.Visible, "快速标注：标注层画布可见");
            Check(!canvas.IsHitTestVisible, "快速标注：不能划选时画布不接鼠标（点击照旧拖动窗口）");

            PinSet("SetTextSelectionMode", "always");
            Invalidate();
            Call("SyncHiddenTextLayer");
            Wait(80);
            Check(canvas.IsHitTestVisible, "快速标注：切回「始终」后又能划选");

            // ============ ⑦ 关掉快速标注：开关消失，复制仍可用 ============
            PinSet("SetQuickAnnotationForSelectedText", false);
            Invalidate();
            type.GetMethod("RefreshHiddenBar", Inst)!.Invoke(pin, null);
            Wait(60);
            Check(highlightBtn.Visibility == Visibility.Collapsed, "快速标注：设置关掉后四个开关收起");
            Check(copyBtn.Visibility == Visibility.Visible, "快速标注：复制按钮仍在（与标注无关）");
            Check(Payload().Length > 0, "快速标注：关掉开关不会删掉已有标注");

            PinSet("SetQuickAnnotationForSelectedText", true);
            Invalidate();
            type.GetMethod("RefreshHiddenBar", Inst)!.Invoke(pin, null);
            Wait(60);

            // ============ ⑧ 导出带上标注（逐像素） ============
            var composed = (BitmapSource)type.GetMethod("ComposeContent", Inst)!.Invoke(pin, null)!;
            Check(!ReferenceEquals(composed, Field(pin, "_content")), "快速标注：有标注时导出合成新位图");
            Check(composed.PixelWidth == image.PixelWidth && composed.PixelHeight == image.PixelHeight,
                $"快速标注：导出尺寸与原图一致（{composed.PixelWidth}×{composed.PixelHeight}）");

            int stride = composed.PixelWidth * 4;
            var original = new byte[stride * composed.PixelHeight];
            var merged = new byte[original.Length];
            ((BitmapSource)Field(pin, "_content")).CopyPixels(original, stride, 0);
            composed.CopyPixels(merged, stride, 0);
            int changed = 0;
            for (int i = 0; i < original.Length; i += 4)
                if (original[i] != merged[i] || original[i + 1] != merged[i + 1] || original[i + 2] != merged[i + 2])
                    changed++;
            Check(changed > 100, $"快速标注：导出图里画上了标注（{changed} 个像素变化）");
            Save(composed, Path.Combine(_outDir, "pin-annotate-export.png"));

            // 没有标注时导出就是原图（不白渲染）
            var plainPin = manager.GetMethod("Pin")!.Invoke(null,
                    new object[] { image, new Int32Rect(700, 200, image.PixelWidth, image.PixelHeight), "screenshot", null })
                as Window;
            if (plainPin != null)
            {
                created.Add((string)plainPin.GetType().GetProperty("PinId", Inst)!.GetValue(plainPin)!);
                Pump(plainPin);
                var plain = (BitmapSource)plainPin.GetType().GetMethod("ComposeContent", Inst)!.Invoke(plainPin, null)!;
                Check(ReferenceEquals(plain, Field(plainPin, "_content")), "快速标注：没有标注时导出直接给原图");
                plainPin.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(plainPin, null);
            }

            // ============ ⑨ 落盘：记录里带上载荷 ============
            string id = (string)type.GetProperty("PinId", Inst)!.GetValue(pin)!;
            var record = ((System.Collections.IEnumerable)store.GetMethod("Load", Stat)!.Invoke(null, null)!)
                .Cast<object>()
                .FirstOrDefault(item => (string)item.GetType().GetProperty("Id")!.GetValue(item)! == id);
            Check(record != null, "快速标注：贴图历史里有这条记录");
            string stored = record == null
                ? ""
                : (string)record.GetType().GetProperty("Annotations")!.GetValue(record)!;
            Check(stored == Payload(), "快速标注：载荷写进了贴图历史");

            // ============ ⑩ 贴回：按指纹恢复 / 文本变了丢弃 ============
            string saved = Payload();
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            Wait(100);

            Window RestoreWith(string text, object[] lines)
            {
                // 自动识别先关掉：否则 Show() → Loaded 会抢先用真 OCR 跑一次（夹具还没灌），
                // 那一轮空结果会在夹具到达前就把隐形层状态定死。真实贴回是「先 Show、后交载荷」，
                // 这里把顺序固定成「关自动识别 → 灌夹具 → 交载荷 → 开自动识别 → 跑一次」。
                pinType.GetField("AutoOcrEnabled", Stat)!.SetValue(null, false);
                var w = manager.GetMethod("Pin")!.Invoke(null,
                        new object[] { image, new Int32Rect(900, 200, image.PixelWidth, image.PixelHeight), "screenshot", null })
                    as Window;
                if (w == null) return null!;
                created.Add((string)w.GetType().GetProperty("PinId", Inst)!.GetValue(w)!);
                var wt = w.GetType();
                wt.GetField("OcrFixture", Inst)!.SetValue(w, Fixture(text, lines));
                // 真实贴回顺序：先交标注载荷（还没识别），识别结果回来再校验
                wt.GetMethod("RestoreAnnotations", Inst)!.Invoke(w, new object?[] { saved });
                pinType.GetField("AutoOcrEnabled", Stat)!.SetValue(null, true);
                wt.GetMethod("StartAutoRecognize", Inst)!.Invoke(w, null);
                Pump(w);
                return w;
            }

            restored = RestoreWith("第一行文字\n第二行文字",
                new object[] { Line("第一行文字", 24, 40, 200, 28), Line("第二行文字", 24, 90, 220, 28) });
            Wait(220);
            Check(restored != null, "快速标注：贴回一张图");
            if (restored != null)
            {
                var rt = restored.GetType();
                string payloadBack = (string)rt.GetProperty("AnnotationsPayload", Inst)!.GetValue(restored)!;
                string phaseBack = rt.GetProperty("OcrPhase", Inst)!.GetValue(restored)!.ToString()!;
                Check(payloadBack == saved,
                    $"快速标注：同一份文字 → 标注原样贴回（{payloadBack.Length} vs {saved.Length}，阶段 {phaseBack}）");
                Check((bool)rt.GetProperty("HiddenTextSelection", Inst)!.GetValue(restored)!,
                    "快速标注：贴回的标注画在隐形层上");
                restored.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(restored, null);
            }

            mismatch = RestoreWith("完全不同的文字",
                new object[] { Line("完全不同的文字", 24, 40, 200, 28) });
            Wait(220);
            Check(mismatch != null, "快速标注：再贴一张文字不同的图");
            if (mismatch != null)
            {
                string payloadBack = (string)mismatch.GetType().GetProperty("AnnotationsPayload", Inst)!
                    .GetValue(mismatch)!;
                Check(payloadBack.Length == 0, "快速标注：文字变了 → 标注整份丢弃（不画到别的字上）");
                mismatch.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(mismatch, null);
            }
        }
        finally
        {
            pinType.GetField("AutoOcrEnabled", Stat)!.SetValue(null, savedAutoOcr);

            foreach (Window? window in new[] { pin, restored, mismatch })
            {
                try
                {
                    if (window != null && window.IsVisible)
                        window.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(window, null);
                }
                catch { /* 已经关了 */ }
            }

            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

            foreach (string id in created.Where(id => id.Length > 0).Distinct())
            {
                try { store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id }); }
                catch { /* 记录可能已经不在了 */ }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
            SetCursorPos(40, 40);
        }
    }
}
