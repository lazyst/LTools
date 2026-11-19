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
/// 贴图的「识别结果可划选」与「重复内容」（P9-d2，照上游 <c>pin_to_screen/*</c>）：
/// 隐形文字层（原图不变、文字能直接拖选 + 复制）、只在文字层里可划选、重复内容四动作、
/// 抖动提醒、关掉自动缩小按 100% 贴出。
/// </summary>
/// <remarks>
/// 识别本身不跑：夹具 <c>OcrFixture</c> 直接灌一份结果，与真识别走同一条 <c>StoreOcrResult</c> 路径。
/// 拖选 / 点 chip 都是真鼠标。配置根指到本段临时目录，收尾恢复并删掉本段建的贴图记录。
/// </remarks>
internal static partial class Program
{
    private static void PinRecognitionSection(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var pinSettingsType = asm.GetType("LTools.Features.CapturePro.PinSettings")!;
        var identity = asm.GetType("LTools.Features.CapturePro.PinContentIdentity")!;
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

        void Invalidate() => pinSettingsType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "pinrecognition-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");

        // 上一节可能还留着贴图：先全关
        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

        var created = new List<string>();
        Window? pin = null;
        Window? dup = null;
        Window? big = null;
        bool savedAutoOcr = (bool)pinType.GetField("AutoOcrEnabled", Stat)!.GetValue(null)!;

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        try
        {
            // 回到上游默认（划选「始终」、自动识别开、自动缩小开、重复内容「抖一下」）
            PinSet("SetTextSelectionMode", "always");
            PinSet("SetAutomaticTextRecognition", true);
            PinSet("SetAutoResizeWindow", true);
            PinSet("SetDuplicateMode", "shake_window");
            Invalidate();

            // ============ ① 隐形文字层：原图不变，文字能拖选 ============
            pin = manager.GetMethod("Pin")!.Invoke(null,
                    new object[] { image, new Int32Rect(300, 200, image.PixelWidth, image.PixelHeight), "screenshot", null })
                as Window;
            Check(pin != null && pin.IsVisible, "贴图划选：先贴一张图");
            if (pin == null) return;
            created.Add((string)pin.GetType().GetProperty("PinId", Inst)!.GetValue(pin)!);

            var type = pin.GetType();
            object Call(string name, params object[] args) => type.GetMethod(name, Inst)!.Invoke(pin, args);
            bool Hidden() => (bool)type.GetProperty("HiddenTextSelection", Inst)!.GetValue(pin)!;
            string HiddenText() => (string)type.GetProperty("HiddenSelectionText", Inst)!.GetValue(pin)!;
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

            string Clip()
            {
                for (int i = 0; i < 20; i++)
                {
                    try { return Clipboard.GetText(); }
                    catch { Thread.Sleep(30); }
                }

                return "";
            }

            var canvas = (FrameworkElement)Field(pin, "OcrCanvas")!;
            var canvasType = canvas.GetType();
            var hiddenBar = (FrameworkElement)Field(pin, "HiddenBar")!;

            Wait(150);
            Layout();

            // 还没识别：不铺隐形层
            Check(!Hidden(), "贴图划选：还没识别时不铺隐形层");
            Check(hiddenBar.Visibility == Visibility.Collapsed, "贴图划选：没选区时 chip 不出现");

            // 灌夹具 + 走自动识别那条路（临时打开全局开关，与真设置同一条 StartAutoRecognize）
            object fixture = Fixture("第一行文字\n第二行文字\n第三行文字",
                Line("第一行文字", 24, 40, 200, 28),
                Line("第二行文字", 24, 90, 220, 28),
                Line("第三行文字", 24, 140, 180, 28));
            type.GetField("OcrFixture", Inst)!.SetValue(pin, fixture);
            pinType.GetField("AutoOcrEnabled", Stat)!.SetValue(null, true);

            Call("StartAutoRecognize");
            Wait(180);
            Layout();

            Check(Phase() == "Ready", $"贴图划选：自动识别跑出结果（实际 {Phase()}）");
            Check(Hidden(), "贴图划选：识别完成后铺了隐形文字层");
            Check(canvas.Visibility == Visibility.Visible, "贴图划选：隐形文字层画布显示出来");
            Check((bool)canvasType.GetProperty("SelectionOnly", Inst)!.GetValue(canvas)!,
                "贴图划选：画布切到「只画选区」模式");
            Check(hiddenBar.Visibility == Visibility.Collapsed, "贴图划选：还没选时 chip 仍不出现");

            // 原图不变：左上角是深色底（白纸页会是白的）
            var shot = Render(pin);
            Save(shot, Path.Combine(_outDir, "pin-recognition-hidden.png"));
            var pagePixel = new byte[4];
            shot.CopyPixels(new Int32Rect(3, 3, 1, 1), pagePixel, 4, 0);
            Check(pagePixel[2] < 120 && pagePixel[1] < 120 && pagePixel[0] < 120,
                $"贴图划选：隐形层不换底，原图照旧（{pagePixel[2]},{pagePixel[1]},{pagePixel[0]}）");

            // ============ ② 真鼠标拖选文字 ============
            double scale = (double)Field(pin, "_scale")!;
            if (scale <= 0) scale = 1;
            int ox = (int)Math.Round(pin.Left * scale);
            int oy = (int)Math.Round(pin.Top * scale);

            double beforeLeft = pin.Left, beforeTop = pin.Top;
            SetCursorPos(ox + 60, oy + 54);          // 第 1 行中间
            Wait(60);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Wait(40);
            SetCursorPos(ox + 180, oy + 104);        // 拖到第 2 行
            Wait(80);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Wait(160);

            Check(Math.Abs(pin.Left - beforeLeft) < 0.5 && Math.Abs(pin.Top - beforeTop) < 0.5,
                $"贴图划选：在文字上拖动不搬窗口（{pin.Left:0.#},{pin.Top:0.#}）");
            string selection = HiddenText();
            Check(selection.Length > 0, $"贴图划选：真鼠标拖过之后选中了「{selection}」");
            Check(hiddenBar.Visibility == Visibility.Visible, "贴图划选：选中后选区旁出现 chip");
            Check(canvas.Visibility == Visibility.Visible, "贴图划选：有选区时画布仍在");

            // ============ ③ chip 上的「复制 / ✕」 ============
            var copyBtn = (Button)Field(pin, "HiddenCopyBtn")!;
            var clearBtn = (Button)Field(pin, "HiddenClearBtn")!;

            try { Clipboard.Clear(); } catch { /* 被别的程序占着 */ }
            copyBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(120);
            Check(Clip() == selection, $"贴图划选：点 chip「复制」拿到「{selection}」");

            clearBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(100);
            Check(HiddenText().Length == 0, "贴图划选：点 ✕ 清掉选区");
            Check(hiddenBar.Visibility == Visibility.Collapsed, "贴图划选：清掉选区后 chip 收起");

            // ============ ④ 只在文字层里可划选 ============
            PinSet("SetTextSelectionMode", "only_when_displayed");
            Invalidate();
            Call("SyncHiddenTextLayer");
            Wait(80);
            Check(!Hidden(), "贴图划选：切「只在文字层里可划选」后不铺隐形层");
            Check(canvas.Visibility == Visibility.Collapsed, "贴图划选：隐形层画布收起");

            PinSet("SetTextSelectionMode", "always");
            Invalidate();
            Call("SyncHiddenTextLayer");
            Wait(80);
            Check(Hidden(), "贴图划选：切回「始终」又铺回来");

            type.GetField("OcrFixture", Inst)!.SetValue(pin, null);

            // ============ ⑤ 抖动提醒 ============
            double origin = pin.Left;
            Call("ShakeForAttention");
            Check((bool)type.GetProperty("AttentionShaking", Inst)!.GetValue(pin)!,
                "抖动提醒：调一下就抖起来");
            Wait(120);
            Wait(400);
            Check(!(bool)type.GetProperty("AttentionShaking", Inst)!.GetValue(pin)!,
                "抖动提醒：300ms 之后自己停");
            Check(Math.Abs(pin.Left - origin) < 0.5, $"抖动提醒：停后回到原位（{pin.Left:0.#} vs {origin:0.#}）");

            // 锁定的贴图不抖
            type.GetMethod("SetLocked", Inst)!.Invoke(pin, new object[] { true });
            Call("ShakeForAttention");
            Check(!(bool)type.GetProperty("AttentionShaking", Inst)!.GetValue(pin)!,
                "抖动提醒：锁定的贴图不抖");
            type.GetMethod("SetLocked", Inst)!.Invoke(pin, new object[] { false });

            // ============ ⑥ 重复内容四动作 ============
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            Wait(120);

            string key = (string)CallStatic(identity, "ClipboardKey", 1234u)!;
            Check((bool)CallStatic(identity, "IsClipboardKey", key)!, "重复内容：剪贴板身份认得出");

            Window PinDup(string sourceKey)
            {
                var w = manager.GetMethod("PinClipboardImage")!.Invoke(null,
                    new object[] { image, 1.0, new Rect(0, 0, 1920, 1020), "clipboard", null, sourceKey }) as Window;
                if (w != null)
                {
                    created.Add((string)w.GetType().GetProperty("PinId", Inst)!.GetValue(w)!);
                    Pump(w);
                }

                return w!;
            }

            string Outcome(string sourceKey) =>
                CallStatic(manager, "HandleDuplicate", sourceKey)!.ToString()!;

            // 第一次贴：没有重复
            dup = PinDup(key);
            Check(dup != null, "重复内容：第一张贴出来");
            Wait(100);
            Check(Outcome(key) == "Handled" || Outcome(key) == "Proceed", "重复内容：有身份时能判定");

            // 照常再贴
            PinSet("SetDuplicateMode", "repeat_action");
            Invalidate();
            Check(Outcome(key) == "Proceed", "重复内容「照常再贴」= 放行");

            // 抖一下已有的那张
            PinSet("SetDuplicateMode", "shake_window");
            Invalidate();
            int beforeCount = (int)manager.GetProperty("Count", Stat)!.GetValue(null)!;
            Check(Outcome(key) == "Handled", "重复内容「抖一下」= 拦下、不新建");
            Check((int)manager.GetProperty("Count", Stat)!.GetValue(null)! == beforeCount,
                "重复内容「抖一下」：窗口数量不变");
            Check((bool)dup.GetType().GetProperty("AttentionShaking", Inst)!.GetValue(dup)!,
                "重复内容「抖一下」：已有的那张抖起来");
            Wait(420);

            // 不贴
            PinSet("SetDuplicateMode", "none");
            Invalidate();
            Check(Outcome(key) == "Handled", "重复内容「不贴」= 拦下");
            Check((int)manager.GetProperty("Count", Stat)!.GetValue(null)! == beforeCount,
                "重复内容「不贴」：窗口数量不变");

            // 贴回最近关闭
            PinSet("SetDuplicateMode", "restore_last_closed_window");
            Invalidate();
            Check(Outcome(key) == "Handled", "重复内容「贴回最近关闭」= 拦下");

            // 没有身份（截图 / 历史贴图）时永远放行
            Check(Outcome("") == "Proceed", "重复内容：没有身份的贴图永远放行");

            // ============ ⑦ 关掉自动缩小：大图按 100% 贴出 ============
            PinSet("SetDuplicateMode", "repeat_action");   // 免得被重复规则拦住
            PinSet("SetAutoResizeWindow", false);
            Invalidate();

            var wide = new WriteableBitmap(3000, 2000, 96, 96, PixelFormats.Pbgra32, null);
            wide.Freeze();

            Window MakeBig()
            {
                var w = manager.GetMethod("PinClipboardImage")!.Invoke(null,
                    new object[] { wide, 1.0, new Rect(0, 0, 1920, 1020), "clipboard", null, "" }) as Window;
                if (w != null)
                {
                    created.Add((string)w.GetType().GetProperty("PinId", Inst)!.GetValue(w)!);
                    Pump(w);
                }

                return w!;
            }

            big = MakeBig();
            Check(big != null && big.IsVisible, "自动缩小：大图也能贴出来");
            if (big != null)
            {
                double zoom = (double)Field(big, "_zoom")!;
                double baseWidth = (double)Field(big, "_baseWidth")!;
                Check(Math.Abs(zoom - 1.0) < 0.001, $"自动缩小关掉 = 100%（实际 {zoom:0.###}）");
                Check(Math.Abs(baseWidth - 3000) < 2,
                    $"自动缩小关掉：内容按原始尺寸（{baseWidth:0.#}）");
                big.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(big, null);
                big = null;
            }

            // 打开自动缩小：同一张大图会等比缩到工作区里
            PinSet("SetAutoResizeWindow", true);
            Invalidate();
            big = MakeBig();
            Check(big != null, "自动缩小：打开后也能贴出来");
            if (big != null)
            {
                double zoom = (double)Field(big, "_zoom")!;
                Check(zoom < 1.0, $"自动缩小打开：大图等比缩到工作区里（{zoom:0.###}）");
                Check(big.Width <= 1920 + 1, $"自动缩小打开：宽度不超过工作区（{big.Width:0.#}）");
            }
        }
        finally
        {
            pinType.GetField("AutoOcrEnabled", Stat)!.SetValue(null, savedAutoOcr);

            try { if (pin != null && pin.IsVisible) pin.GetType().GetMethod("RequestUserClose", Inst)!.Invoke(pin, null); }
            catch { /* 已经关了 */ }

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
