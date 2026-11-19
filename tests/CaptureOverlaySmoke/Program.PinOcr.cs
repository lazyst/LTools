using System;
using System.Collections;
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
/// 贴图「显示文字识别结果」（P4-j，照上游 <c>show_text_recognition_results</c>）：
/// 右键菜单入口 → 识别结果铺回原图 → 拖动划选 / 复制 → 退出回到贴图 → 指针动作秒开缓存 →
/// 超出 3840×2160 的图不给识别。
/// </summary>
/// <remarks>
/// 识别本身不跑（一两秒 + 依赖 OCR 模型）：夹具 <c>OcrFixture</c> 直接灌一份结果，
/// 与真识别走同一条 <c>ApplyOcrResult</c> 路径。坐标 / 拖动 / 滚轮都是真鼠标。
/// </remarks>
internal static partial class Program
{
    private static void PinOcrSection(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var resultType = asm.GetType("LTools.Features.CapturePro.RecognizeResult")!;
        var lineType = asm.GetType("LTools.Features.OcrLineBox")!;
        var actionType = asm.GetType("LTools.Features.CapturePro.PinPointerAction")!;

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

        // 前一节可能还留着贴图：先全关
        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

        var pin = manager.GetMethod("Pin")!.Invoke(null,
                new object[]
                {
                    image, new Int32Rect(300, 200, image.PixelWidth, image.PixelHeight), "screenshot", null,
                })
            as Window;
        Check(pin != null && pin.IsVisible, "贴图文字：先贴一张图");
        if (pin == null) return;

        var type = pin.GetType();
        object Call(string name, params object[] args) => type.GetMethod(name, Inst)!.Invoke(pin, args);

        bool Mode() => (bool)type.GetProperty("OcrMode", Inst)!.GetValue(pin)!;
        string Phase() => type.GetProperty("OcrPhase", Inst)!.GetValue(pin)!.ToString()!;
        int LineCount() => (int)type.GetProperty("OcrLineCount", Inst)!.GetValue(pin)!;
        string FullText() => (string)type.GetProperty("OcrText", Inst)!.GetValue(pin)!;

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

        MenuItem Item(ContextMenu menu, string header)
        {
            foreach (var entry in menu.Items)
                if (entry is MenuItem mi && (mi.Header as string)?.StartsWith(header, StringComparison.Ordinal) == true)
                    return mi;
            return null;
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

        var canvas = Field(pin, "OcrCanvas")!;
        var canvasType = canvas.GetType();
        var bar = (FrameworkElement)Field(pin, "OcrBar")!;
        var barText = (TextBlock)Field(pin, "OcrBarText")!;
        var copySelBtn = (Button)Field(pin, "OcrCopySelectionBtn")!;
        var copyAllBtn = (Button)Field(pin, "OcrCopyAllBtn")!;
        var exitBtn = (Button)Field(pin, "OcrExitBtn")!;

        double scale = (double)Field(pin, "_scale")!;
        if (scale <= 0) scale = 1;

        try
        {
            Wait(150);
            Layout();

            // ============ 菜单：还没识别时「显示文字」可点、没勾 ============
            var menu = (ContextMenu)Call("BuildMenu")!;
            var ocrItem = Item(menu, "显示文字");
            Check(ocrItem != null, "贴图文字：右键菜单里有「显示文字」");
            Check(ocrItem is { IsEnabled: true }, "贴图文字：还没识别时菜单项可点");
            Check(ocrItem is { IsChecked: false }, "贴图文字：还没识别时菜单项没勾");
            Check(Item(menu, "复制所选文字") == null && Item(menu, "复制全部文字") == null,
                "贴图文字：没进文字层时不列复制项");
            menu.IsOpen = false;

            // ============ 进入：夹具识别结果铺回原图 ============
            object fixture = Fixture("第一行文字\n第二行文字\n第三行文字",
                Line("第一行文字", 24, 40, 200, 28),
                Line("第二行文字", 24, 90, 220, 28),
                Line("第三行文字", 24, 140, 180, 28));
            type.GetField("OcrFixture", Inst)!.SetValue(pin, fixture);

            Call("ToggleOcrMode");
            Wait(150);
            Layout();

            Check(Mode(), "贴图文字：点「显示文字」进入文字层");
            Check(Phase() == "Ready", $"贴图文字：识别结果已就绪（实际 {Phase()}）");
            Check(LineCount() == 3, $"贴图文字：铺了 3 行（实际 {LineCount()}）");
            Check(FullText() == "第一行文字\n第二行文字\n第三行文字", "贴图文字：全文按行拼起来");
            Check(bar.Visibility == Visibility.Visible, "贴图文字：图下方的状态条出来了");
            Check(barText.Text == "3 行 · 拖动选择文字", $"贴图文字：状态条报行数（实际「{barText.Text}」）");
            Check(copySelBtn.IsEnabled == false, "贴图文字：没选区时「复制所选」不可点");
            Check(copyAllBtn.IsEnabled, "贴图文字：有结果时「复制全文」可点");

            var canvasElement = (FrameworkElement)canvas;
            Check(canvasElement.Visibility == Visibility.Visible, "贴图文字：文字层画布显示出来了");
            Check(canvasElement.IsHitTestVisible, "贴图文字：文字层能接鼠标（要划选）");
            Check((int)canvasType.GetProperty("ImageWidth", Inst)!.GetValue(canvas)! == image.PixelWidth,
                "贴图文字：画布宽度 = 源图像素宽");

            double expectedZoom = 1.0 / scale;
            double zoom = (double)canvasType.GetProperty("Zoom", Inst)!.GetValue(canvas)!;
            Check(Math.Abs(zoom - expectedZoom) < 0.001,
                $"贴图文字：画布缩放 = 1 ÷ 屏幕缩放（{zoom:0.####} vs {expectedZoom:0.####}）");
            Check(Math.Abs(canvasElement.ActualWidth - pin.Width) <= 1.5,
                $"贴图文字：文字层宽度与贴图内容一致（{canvasElement.ActualWidth:0.#} vs {pin.Width:0.#}）");

            // ============ 菜单：铺着结果时勾上，多出两个复制项 ============
            menu = (ContextMenu)Call("BuildMenu")!;
            ocrItem = Item(menu, "显示文字");
            Check(ocrItem is { IsChecked: true }, "贴图文字：文字层开着时菜单项勾上");
            var copySelection = Item(menu, "复制所选文字");
            var copyAll = Item(menu, "复制全部文字");
            Check(copySelection != null, "贴图文字：菜单里有「复制所选文字」");
            Check(copyAll is { IsEnabled: true }, "贴图文字：菜单里「复制全部文字」可点");
            Check(copySelection is { IsEnabled: false }, "贴图文字：没选区时菜单「复制所选」不可点");
            menu.IsOpen = false;

            // ============ 滚轮：文字层里不改缩放 ============
            int ox = (int)Math.Round(pin.Left * scale);
            int oy = (int)Math.Round(pin.Top * scale);
            SetCursorPos(ox + image.PixelWidth / 2, oy + image.PixelHeight / 2);
            Wait(80);
            double beforeZoom = (double)Field(pin, "_zoom")!;
            mouse_event(0x0800 /* 滚轮 */, 0, 0, 120, UIntPtr.Zero);
            Wait(150);
            Check(Math.Abs((double)Field(pin, "_zoom")! - beforeZoom) < 0.0001,
                $"贴图文字：文字层里滚轮不动缩放（{(double)Field(pin, "_zoom")!:0.##}）");

            // ============ 拖动：划选文字，不移动窗口 ============
            double beforeLeft = pin.Left, beforeTop = pin.Top;
            int sx = ox + 60, sy = oy + 54;
            int ex = ox + 180, ey = oy + 104;      // 从第 1 行拖到第 2 行
            SetCursorPos(sx, sy);
            Wait(60);
            mouse_event(0x0002 /* 左键按下 */, 0, 0, 0, UIntPtr.Zero);
            Wait(40);
            SetCursorPos(ex, ey);
            Wait(80);
            mouse_event(0x0004 /* 左键抬起 */, 0, 0, 0, UIntPtr.Zero);
            Wait(150);

            Check(Math.Abs(pin.Left - beforeLeft) < 0.5 && Math.Abs(pin.Top - beforeTop) < 0.5,
                $"贴图文字：文字层里拖动不搬窗口（{pin.Left:0.#},{pin.Top:0.#}）");
            var model = Field(pin, "_ocrModel");
            Check(model != null, "贴图文字：文字层有可编辑模型");
            if (model == null) return;
            bool hasSelection = (bool)model.GetType().GetProperty("HasSelection", Inst)!.GetValue(model)!;
            Check(hasSelection, "贴图文字：真鼠标拖过之后有选区");
            string selection = (string)model.GetType().GetMethod("SelectionText", Inst)!
                .Invoke(model, null)!;
            Check(selection.Length > 0, $"贴图文字：选中了「{selection}」");
            Check(barText.Text.Contains("已选"), $"贴图文字：状态条报已选字数（「{barText.Text}」）");

            var shot = Render(pin);
            Save(shot, System.IO.Path.Combine(_outDir, "pin-ocr.png"));

            // 上游进入文字层会隐藏画布内容、识别内容页用自己的容器底色：这里必须是白纸页，
            // 而不是原图（原图那块左上角是深色底）
            var pagePixel = new byte[4];
            shot.CopyPixels(new Int32Rect(3, 3, 1, 1), pagePixel, 4, 0);
            Check(pagePixel[0] > 235 && pagePixel[1] > 235 && pagePixel[2] > 235,
                $"贴图文字：文字层是白纸页、不画原图（{pagePixel[2]},{pagePixel[1]},{pagePixel[0]}）");

            // ============ 复制所选 / 复制全文 ============
            try { Clipboard.Clear(); } catch { /* 被别的程序占着 */ }
            Call("CopyOcrSelection");
            Wait(120);
            Check(Clip() == selection, $"贴图文字：复制所选拿到「{selection}」");

            var selectAll = model.GetType().GetMethod("SelectAll", Inst)!;
            selectAll.Invoke(model, null);
            Wait(80);
            Check(barText.Text.Contains("已选"), "贴图文字：全选之后状态条也报已选");

            try { Clipboard.Clear(); } catch { /* 被别的程序占着 */ }
            Call("CopyOcrAll");
            Wait(120);
            Check(Clip() == FullText(), "贴图文字：复制全文拿到整段文字");

            // ============ 状态条上的「退出文字层」 ============
            exitBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wait(120);
            Check(!Mode(), "贴图文字：点「退出文字层」回到可拖动的贴图");
            // 退出文字层后若「识别结果可划选」开着（P9-d2 默认「始终」），画布会切成隐形文字层
            // （只画选区、原图照旧），所以这里不是「画布收起」，而是「不再铺白纸文字层」。
            bool hiddenAfterExit = (bool)canvasType.GetProperty("SelectionOnly", Inst)!.GetValue(canvas)!;
            Check(canvasElement.Visibility == Visibility.Collapsed || hiddenAfterExit,
                "贴图文字：退出后不再铺白纸文字层（收起或转成隐形划选层）");
            Check(bar.Visibility == Visibility.Collapsed, "贴图文字：退出后状态条收起");

            // ============ 指针动作（双击 / 中键设置）：走缓存秒开 ============
            object textRecognition = Enum.Parse(actionType, "TextRecognition");
            type.GetMethod("ExecutePointerAction", Inst)!.Invoke(pin, new[] { textRecognition });
            Wait(120);
            Layout();
            Check(Mode(), "贴图文字：双击动作「显示文字识别结果」能进文字层");
            Check(Phase() == "Ready" && LineCount() == 3,
                $"贴图文字：再进时直接用缓存（{Phase()} / {LineCount()} 行）");

            // ============ 状态条上的按钮要用真鼠标点（贴图不抢焦点，这是主出口） ============
            var barPoint = exitBtn.TranslatePoint(
                new Point(exitBtn.ActualWidth / 2, exitBtn.ActualHeight / 2), pin);
            SetCursorPos((int)Math.Round((pin.Left + barPoint.X) * scale),
                (int)Math.Round((pin.Top + barPoint.Y) * scale));
            Wait(80);
            mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
            Wait(40);
            mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
            Wait(150);
            Check(!Mode(), "贴图文字：真鼠标点状态条「退出文字层」能退出");

            object textRecognition2 = Enum.Parse(actionType, "TextRecognition");
            type.GetMethod("ExecutePointerAction", Inst)!.Invoke(pin, new[] { textRecognition2 });
            Wait(80);
            Call("ToggleOcrMode");
            Check(!Mode(), "贴图文字：再点一次菜单也能退出");
        }
        finally
        {
            type.GetField("OcrFixture", Inst)!.SetValue(pin, null);
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
        }

        // ============ 超过 3840×2160 的图不给识别 ============
        try
        {
            var big = new WriteableBitmap(3841, 2160, 96, 96, PixelFormats.Pbgra32, null);
            big.Freeze();

            var bigPin = manager.GetMethod("Pin")!.Invoke(null,
                    new object[] { big, new Int32Rect(0, 0, big.PixelWidth, big.PixelHeight), "screenshot", null })
                as Window;
            Check(bigPin != null && bigPin.IsVisible, "贴图文字：超大图也能贴出来");
            if (bigPin != null)
            {
                var bigType = bigPin.GetType();
                Wait(120);
                string bigPhase = bigType.GetProperty("OcrPhase", Inst)!.GetValue(bigPin)!.ToString()!;
                Check(bigPhase == "TooLarge",
                    $"贴图文字：3841×2160 超出识别上限（实际 {bigPhase}）");

                var bigMenu = (ContextMenu)bigType.GetMethod("BuildMenu", Inst)!.Invoke(bigPin, null)!;
                var bigItem = Item(bigMenu, "图片尺寸过大");
                Check(bigItem != null, "贴图文字：超限时菜单项写「图片尺寸过大」");
                Check(bigItem is { IsEnabled: false }, "贴图文字：超限时菜单项不可点");
                bigMenu.IsOpen = false;

                bigType.GetMethod("ToggleOcrMode", Inst)!.Invoke(bigPin, null);
                Wait(80);
                Check(!(bool)bigType.GetProperty("OcrMode", Inst)!.GetValue(bigPin)!,
                    "贴图文字：超限时点了也不进文字层");
            }
        }
        finally
        {
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            SetCursorPos(40, 40);
        }
    }
}
