using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 截图行为设置（P9-a）：照上游 <c>screenshot/*</c> 的七项设置——
/// 默认值 / 归一 / 落盘 / 设置页回显与改动 / HiDPI 降采样 / PDF 纸张与真实文件 /
/// 「快速选择修改」与「起拖贴到指针」的真鼠标拖框 / Esc 退出确认。
/// </summary>
/// <remarks>
/// 与 <c>DrawingSection</c> 同一套做法：把 <c>ConfigStore.ConfigPath</c> 指到本段自己的临时目录，
/// 断言完恢复原路径并 <c>Invalidate()</c>，不给后面的段留脏配置。
/// </remarks>
internal static partial class Program
{
    private static void BehaviorSection()
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var behaviorType = asm.GetType("LTools.Features.CapturePro.ScreenshotBehavior")!;
        var hidpiType = asm.GetType("LTools.Features.CapturePro.HiDpiImageScaling")!;
        var pdfType = asm.GetType("LTools.Features.CapturePro.ScreenshotPdf")!;
        var screenShotType = asm.GetType("LTools.Features.CapturePro.ScreenShot")!;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;

        object Tool(string name) => Enum.Parse(toolType, name);

        object Get(string name) => behaviorType.GetProperty(name, Stat)!.GetValue(null)!;
        void Set(string method, params object?[] args) =>
            behaviorType.GetMethod(method, Stat)!.Invoke(null, args);

        int Interval() => (int)Get("ScrollingAutoScrollIntervalMs");
        bool Flag(string name) => (bool)Get(name);
        string Text(string name) => (string)Get(name);

        void Invalidate()
        {
            behaviorType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
            hidpiType.GetMethod("Invalidate", Stat)?.Invoke(null, null);
        }

        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;

        string configRoot = Path.Combine(_outDir, "behavior-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string configPath = Path.Combine(configRoot, "LTools.json");
        string settingsFile = Path.Combine(configRoot, "configs", "settings.json");

        configPathSet.Invoke(null, new object[] { configPath });
        Invalidate();

        Window? settings = null;
        var overlays = new List<Window>();
        try
        {
            // ============ ① 默认值（上游 configurationschema.cpp） ============
            Check(Interval() == 200, $"长截图自动滚动间隔默认 200ms（实际 {Interval()}）");
            Check(!Flag("CaptureCursor"), "截图带指针默认关");
            Check(!Flag("ConfirmBeforeExitViaShortcut"), "Esc 退出确认默认关");
            Check(Flag("QuickSelectionModification"), "非选择工具拖边框默认开");
            Check(Text("SelectionResizeMode") == "follow_mouse_movement",
                "拖动改尺寸默认按位移：" + Text("SelectionResizeMode"));
            Check(Text("HidpiImageScaling") == "disabled", "HiDPI 降采样默认关：" + Text("HidpiImageScaling"));
            Check(Text("PdfPageSize") == "a4_portrait", "PDF 纸张默认 A4 竖版：" + Text("PdfPageSize"));
            Check(!File.Exists(settingsFile), "只读默认值不会平白落一份 settings.json");

            // ============ ② 写入即落盘 + 归一化 ============
            Set("SetCaptureCursor", true);
            Check(File.Exists(settingsFile), "改一项才出现 settings.json");
            Check(File.ReadAllText(settingsFile).Contains("\"CaptureCursor\": true"),
                "settings.json 的 Screenshot 段写着 CaptureCursor = true");
            Invalidate();
            Check(Flag("CaptureCursor"), "清缓存重新读盘：截图带指针仍是开");

            Set("SetScrollingAutoScrollIntervalMs", 50);
            Invalidate();
            Check(Interval() == 128, $"间隔太小归一到 128（实际 {Interval()}）");
            Set("SetScrollingAutoScrollIntervalMs", 5000);
            Invalidate();
            Check(Interval() == 1000, $"间隔太大归一到 1000（实际 {Interval()}）");

            Set("SetSelectionResizeMode", "nope");
            Invalidate();
            Check(Text("SelectionResizeMode") == "follow_mouse_movement", "认不出的尺寸方式按「位移」");
            Set("SetHidpiImageScaling", "127");
            Invalidate();
            Check(Text("HidpiImageScaling") == "disabled", "不是 25 倍数的 HiDPI 档位回落关闭");
            Set("SetPdfPageSize", "b5");
            Invalidate();
            Check(Text("PdfPageSize") == "a4_portrait", "认不出的 PDF 纸张回落 A4 竖版");

            // 回到默认，后面的断言才有确定答案
            Set("SetCaptureCursor", false);
            Set("SetConfirmBeforeExitViaShortcut", false);
            Set("SetQuickSelectionModification", true);
            Set("SetScrollingAutoScrollIntervalMs", 200);
            Set("SetSelectionResizeMode", "follow_mouse_movement");
            Set("SetHidpiImageScaling", "disabled");
            Set("SetPdfPageSize", "a4_portrait");
            Invalidate();

            // ============ ③ 设置页：七个控件回显 + 改动即落盘 ============
            settings = new LTools.Views.ConfigHelperWindow(configPath);
            settings.Show();
            Pump(settings);

            settings.GetType().GetField("_initializing", Inst)!.SetValue(settings, false);
            settings.GetType().GetMethod("PopulateBehavior", Inst)!.Invoke(settings, null);

            CheckBox? Box(string name) => settings!.FindName(name) as CheckBox;
            ComboBox? Combo(string name) => settings!.FindName(name) as ComboBox;
            TextBox? BoxText(string name) => settings!.FindName(name) as TextBox;

            Check(Box("CaptureCursorBox")?.IsChecked == false, "「截图带指针」回显为不勾");
            Check(Box("CaptureConfirmExitBox")?.IsChecked == false, "「Esc 退出先确认」回显为不勾");
            Check(Box("CaptureQuickSelectionModBox")?.IsChecked == true, "「非选择工具拖边框」回显为勾上");
            Check(Combo("CaptureResizeModeCombo")?.SelectedIndex == 0, "尺寸方式回显「按鼠标位移」");
            Check(Combo("CaptureHidpiCombo")?.SelectedIndex == 0, "HiDPI 回显「关闭」");
            Check(Combo("CapturePdfPageSizeCombo")?.SelectedIndex == 0, "PDF 回显「A4 竖版」");
            Check(BoxText("CaptureScrollIntervalBox")?.Text == "200", "间隔框回显 200");

            Box("CaptureCursorBox")!.IsChecked = true;
            Pump(settings);
            Check(Flag("CaptureCursor"), "勾上「截图带指针」立刻写进设置");
            Check(File.ReadAllText(settingsFile).Contains("\"CaptureCursor\": true"),
                "同一个勾也落到 settings.json");
            Box("CaptureCursorBox")!.IsChecked = false;
            Pump(settings);
            Check(!Flag("CaptureCursor"), "取消勾选同样即时生效");

            Combo("CaptureResizeModeCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(Text("SelectionResizeMode") == "follow_mouse_position",
                "下拉切到「起拖贴到指针」并落盘：" + Text("SelectionResizeMode"));

            Combo("CaptureHidpiCombo")!.SelectedIndex = 3;   // 0 关闭 / 1 自动 / 2 125% / 3 150%
            Pump(settings);
            Check(Text("HidpiImageScaling") == "150", "HiDPI 下拉第 3 项 = 150%：" + Text("HidpiImageScaling"));

            Combo("CapturePdfPageSizeCombo")!.SelectedIndex = 1;
            Pump(settings);
            Check(Text("PdfPageSize") == "a4_landscape", "PDF 下拉切到横版：" + Text("PdfPageSize"));

            BoxText("CaptureScrollIntervalBox")!.Text = "300";
            settings.GetType().GetMethod("CommitScrollInterval", Inst)!.Invoke(settings, null);
            Pump(settings);
            Check(Interval() == 300, $"间隔框写 300 落盘（实际 {Interval()}）");

            BoxText("CaptureScrollIntervalBox")!.Text = "5";
            settings.GetType().GetMethod("CommitScrollInterval", Inst)!.Invoke(settings, null);
            Pump(settings);
            Check(Interval() == 128, $"间隔框写 5 → 归一成 128（实际 {Interval()}）");
            Check(BoxText("CaptureScrollIntervalBox")!.Text == "128", "写回时框里显示归一后的值");

            settings.GetType().GetMethod("PopulateBehavior", Inst)!.Invoke(settings, null);
            Check(Combo("CaptureHidpiCombo")!.SelectedIndex == 3, "重新回填：HiDPI 仍是 150%");
            Check(Combo("CaptureResizeModeCombo")!.SelectedIndex == 1, "重新回填：尺寸方式仍是贴到指针");
            Check(BoxText("CaptureScrollIntervalBox")!.Text == "128", "重新回填：间隔仍是 128");

            settings.Close();
            settings = null;

            // ============ ④ HiDPI 降采样：真按比例缩、至少 1 像素 ============
            Set("SetHidpiImageScaling", "200");
            Invalidate();
            var big = BuildShot(400, 200);
            var scaled = (BitmapSource)hidpiType.GetMethod("Apply", Stat)!
                .Invoke(null, new object?[] { big, new Int32Rect(0, 0, 400, 200) })!;
            Check(scaled.PixelWidth == 200 && scaled.PixelHeight == 100,
                $"200% 档把 400×200 缩成 200×100（实际 {scaled.PixelWidth}×{scaled.PixelHeight}）");
            Check(ReferenceEquals(
                    hidpiType.GetMethod("Apply", Stat)!.Invoke(null, new object?[] { big, null }),
                    big), "不知道落点时不缩（原样返回）");

            Set("SetHidpiImageScaling", "disabled");
            Invalidate();
            Check(ReferenceEquals(
                    hidpiType.GetMethod("Apply", Stat)!.Invoke(null, new object?[] { big, new Int32Rect(0, 0, 400, 200) }),
                    big), "关闭时原样返回");

            // ============ ⑤ PDF：真文件 + 页面尺寸 ============
            var image400 = BuildShot(400, 200);
            string pdfPath = Path.Combine(_outDir, "behavior-image-size.pdf");
            Set("SetPdfPageSize", "image_size");
            Invalidate();
            pdfType.GetMethod("SaveFile", Stat)!.Invoke(null, new object[] { pdfPath, image400, 100 });
            Check(File.Exists(pdfPath), "PDF 真的写出来了：" + pdfPath);
            string pdf = File.ReadAllText(pdfPath, System.Text.Encoding.Latin1);
            Check(pdf.StartsWith("%PDF-1.7", StringComparison.Ordinal), "文件头是 %PDF-1.7");
            Check(pdf.Contains("/MediaBox [0 0 300 150]"),
                "「图片尺寸」页面 = 像素 × 72/96（400×200 → 300×150）");
            Check(pdf.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "文件以 %%EOF 收尾");

            Set("SetPdfPageSize", "a4_portrait");
            Invalidate();
            string portraitPath = Path.Combine(_outDir, "behavior-a4.pdf");
            pdfType.GetMethod("SaveFile", Stat)!.Invoke(null, new object[] { portraitPath, image400, 90 });
            string a4 = File.ReadAllText(portraitPath, System.Text.Encoding.Latin1);
            Check(a4.Contains("/MediaBox [0 0 595.2755") && a4.Contains("841.8897"),
                "A4 竖版 MediaBox 是 210×297mm 换成点（595.28 × 841.89）");
            Check(a4.Contains("/DCTDecode"), "质量 < 100 的 PDF 内嵌 JPEG（DCTDecode）");

            // ============ ⑥ 拖边框：「快速选择修改」与「起拖贴到指针」的真鼠标 ============
            var shot = BuildShot(1200, 800);
            var monitor = new System.Drawing.Rectangle(0, 0, 1200, 800);
            var window = NewOverlay(shot, monitor);
            overlays.Add(window);
            window.Show();
            window.Activate();
            Pump(window);

            double scale = 1200 / Math.Max(1, window.Width);
            var selField = typeof(CaptureOverlayWindow).GetField("_sel", Private)!;
            var setTool = typeof(CaptureOverlayWindow).GetMethod("SetTool", Inst)!;
            var syncArea = typeof(CaptureOverlayWindow).GetMethod("SyncArea", Inst)!;
            void Tool2(string name) => setTool.Invoke(window, new[] { Tool(name) });
            Rect Sel() => (Rect?)selField.GetValue(window) ?? Rect.Empty;

            void ResetSelection()
            {
                SetSelection(window, new Rect(200, 150, 400, 300));
                syncArea.Invoke(window, null);
                Pump(window);
            }

            void Drag(Point from, Point to)
            {
                PointScreen(window, from);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(90);
                PointScreen(window, new Point((from.X + to.X) / 2, (from.Y + to.Y) / 2), 80);
                PointScreen(window, to, 120);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(280);
                Pump(window);
            }

            // 关着「快速选择修改」：画笔碰右边框只是画，不改框
            Set("SetQuickSelectionModification", false);
            Set("SetSelectionResizeMode", "follow_mouse_movement");
            Invalidate();
            Tool2("Pen");
            ResetSelection();
            Rect before = Sel();
            Drag(new Point(600, 300), new Point(700, 300));
            Rect after = Sel();
            Check(Math.Abs(after.Width - before.Width) < 1.5 && Math.Abs(after.Left - before.Left) < 1.5,
                $"消掉「快速选择修改」后画笔拖边框不改框：{before.Width:0.#} → {after.Width:0.#}");
            var marksNow = (System.Collections.IList)Member(window, "Document")!
                .GetType().GetProperty("Marks")!.GetValue(Member(window, "Document"))!;
            Check(marksNow.Count >= 1, "那一下确实画了一条标注（不是没反应）");

            // 开着：选择工具拖右边框按位移走
            Set("SetQuickSelectionModification", true);
            Invalidate();
            Tool2("Select");
            ResetSelection();
            before = Sel();
            Drag(new Point(600, 300), new Point(700, 300));
            after = Sel();
            // 「按位移」= 边框跟着鼠标走了多少就走多少（不是跳到指针上）。
            // 真鼠标 + 125% 缩放下落点有亚像素换算误差，这里只卡「大致 100 DIP」这个量级；
            // 与「贴到指针」的区分由下一段（起拖偏移 5 DIP）负责。
            Check(after.Width > before.Width + 60 && after.Width < before.Width + 140,
                $"按位移：右边框拖 +100 DIP，宽度 {before.Width:0.#} → {after.Width:0.#}");
            Check(Math.Abs(after.Left - before.Left) < 1.5,
                $"按位移只动右边：左边仍是 {before.Left:0.#}（实际 {after.Left:0.##}）");

            // 起拖贴到指针：从边框外侧 5 DIP 起拖，边框先跳到指针那一格再跟着走
            Set("SetSelectionResizeMode", "follow_mouse_position");
            Invalidate();
            ResetSelection();
            Drag(new Point(605, 300), new Point(705, 300));
            after = Sel();
            const double expected = 706;
            Check(Math.Abs(after.Right - expected) <= 2.5,
                $"贴到指针：右边界落在指针 {expected:0} 附近（实际 {after.Right:0.##}）");
            Check(after.Right > 703, "没有退回「按位移」的 700（那是差 6 DIP 的可分辨差异）");

            // ============ ⑦ Esc 退出确认：勾上先问、取消就留在截图里 ============
            Set("SetSelectionResizeMode", "follow_mouse_movement");
            Set("SetConfirmBeforeExitViaShortcut", true);
            Invalidate();
            ResetSelection();

            DispatcherTimer? cancelTimer = null;
            cancelTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal,
                (_, _) =>
                {
                    cancelTimer!.Stop();
                    var dialog = Application.Current.Windows.OfType<Window>()
                        .FirstOrDefault(w => w.GetType().Name == "ConfirmDialog");
                    Check(dialog != null, "勾上「Esc 退出先确认」后弹出了确认框");
                    if (dialog?.FindName("CancelBtn") is Button cancel)
                        cancel.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }, Dispatcher.CurrentDispatcher);
            cancelTimer.Start();

            RaiseEscape(window);
            Pump(window);
            cancelTimer.Stop();
            Check(window.IsVisible, "确认框里点「取消」= 留在截图里继续改");

            Set("SetConfirmBeforeExitViaShortcut", false);
            Invalidate();
            RaiseEscape(window);
            Pump(window);
            Check(!window.IsVisible, "关掉确认之后 Esc 直接退出截图");
            overlays.Remove(window);

            // ============ ⑧ 截图带指针：真实抓帧能走通（DIB / DrawIconEx 不抛） ============
            var bounds = (Int32Rect)screenShotType.GetMethod("Bounds", Stat)!.Invoke(null, null)!;
            var plain = (BitmapSource)screenShotType.GetMethod("Capture", Stat)!
                .Invoke(null, new object[] { bounds, false })!;
            var withCursor = (BitmapSource)screenShotType.GetMethod("Capture", Stat)!
                .Invoke(null, new object[] { bounds, true })!;
            Check(plain.PixelWidth == bounds.Width && plain.PixelHeight == bounds.Height,
                $"整屏抓帧尺寸 = 虚拟桌面 {bounds.Width}×{bounds.Height}");
            Check(withCursor.PixelWidth == plain.PixelWidth && withCursor.PixelHeight == plain.PixelHeight,
                "带上指针不改变画面尺寸");
        }
        finally
        {
            settings?.Close();
            foreach (var overlay in overlays)
            {
                try { overlay.Close(); } catch { }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
        }
    }

    /// <summary>往覆盖层投一次 Esc（走真实的 <c>PreviewKeyDown</c> 处理链）。</summary>
    private static void RaiseEscape(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        if (source == null)
        {
            Check(false, "窗口没有 PresentationSource，无法投 Esc");
            return;
        }

        var args = new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent,
        };
        window.RaiseEvent(args);
    }
}
