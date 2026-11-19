using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 剪贴板外观 / 落位元数据（P7-e，照 Snow Shot 的 <c>screenshotclipboardappearance</c> /
/// <c>screenshotclipboardplacement</c>）：真复制一次截图 → 从剪贴板读回两份自定义格式 →
/// 「贴剪贴板」按**原窗口矩形 + 原外观**贴回（文件列表那条路也认文件签名）。
/// </summary>
/// <remarks>
/// 全程用真实剪贴板与真实贴图窗口：复制走 <c>CaptureSave.CopyImage</c>（与覆盖层 / 贴图右键
/// 同一入口），贴回走 <c>PinClipboard.Pin</c>（与内置功能同一入口）。
/// 段尾把剪贴板与「截图输出」设置原样还原。
/// </remarks>
internal static partial class Program
{
    private static void ClipboardMetaSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var clipboard = asm.GetType("LTools.Native.NativeClipboard")!;
        var saveType = asm.GetType("LTools.Features.CapturePro.CaptureSave")!;
        var settingsType = asm.GetType("LTools.Features.CapturePro.ExportSettings")!;
        var fileType = asm.GetType("LTools.Config.ExportFile")!;
        var contextType = asm.GetType("LTools.Features.CapturePro.ExportNameContext")!;
        var appearanceType = asm.GetType("LTools.Features.CapturePro.PinAppearance")!;
        var captureType = asm.GetType("LTools.Features.CapturePro.ClipboardCapture")!;
        var metaType = asm.GetType("LTools.Features.CapturePro.ClipboardMeta")!;
        var clipboardMeta = asm.GetType("LTools.Features.CapturePro.CaptureClipboard")!;
        var pinClipboard = asm.GetType("LTools.Features.CapturePro.PinClipboard")!;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var screensType = asm.GetType("LTools.Native.Screens")!;

        // 文件那条路用 Ui.Post 回主线程；冒烟是独立 WPF 进程，得先把 Ui 的 dispatcher 接上
        // （产品里由 App.OnStartup 调，这里等价于同一件事）
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize")!
            .Invoke(null, new object[] { System.Windows.Threading.Dispatcher.CurrentDispatcher });

        var clipCapture = clipboard.GetMethod("Capture")!;
        var clipRestore = clipboard.GetMethod("Restore")!;
        var tryFileList = clipboard.GetMethod("TryGetFileList",
            new[] { typeof(List<string>).MakeByRefType(), typeof(bool).MakeByRefType() })!;
        var tryCustom = clipboard.GetMethod("TryGetCustom")!;
        var copyImage = saveType.GetMethod("CopyImage",
            new[]
            {
                typeof(BitmapSource), typeof(string), contextType,
                typeof(Nullable<>).MakeGenericType(captureType),
                typeof(Nullable<>).MakeGenericType(typeof(Int32Rect)),
            })!;
        var readMeta = clipboardMeta.GetMethod("Read")!;
        var appearanceFor = metaType.GetMethod("AppearanceFor")!;
        var placementFor = metaType.GetMethod("PlacementFor")!;
        var pin = pinClipboard.GetMethod("Pin")!;
        var closeAll = manager.GetMethod("CloseAll")!;
        var clearStore = store.GetMethod("Clear")!;

        string dir = Path.Combine(_outDir, "clipboard-meta");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var backup = Activator.CreateInstance(fileType)!;
        settingsType.GetMethod("Capture")!.Invoke(null, new[] { backup });
        var snapshot = clipCapture.Invoke(null, null);

        void Apply(bool copyFile)
        {
            var file = Activator.CreateInstance(fileType)!;
            void Set(string name, object value) => fileType.GetProperty(name)!.SetValue(file, value);
            Set("Format", "png");
            Set("Quality", 100);
            Set("Compression", "medium");
            Set("SaveDirectory", dir);
            Set("ManualFileNameFormat", "M_{HH}-{mm}-{ss}-{fff}");
            Set("AutoFileNameFormat", "A_{HH}-{mm}-{ss}-{fff}");
            Set("SaveAsDialog", "builtin");
            Set("ShowSaveResult", false);
            Set("CopyImageFileToClipboard", copyFile);
            fileType.GetProperty("AutoSaveAfterExecutionActions")!.SetValue(file, new List<string>());
            settingsType.GetMethod("Apply")!.Invoke(null, new[] { file });
        }

        var names = contextType.GetMethod("Of", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { "LTSmoke.exe", "剪贴板 元数据" })!;

        object Capture(Int32Rect window, int radius)
        {
            var look = Activator.CreateInstance(appearanceType)!;
            appearanceType.GetProperty("RadiusPx")!.SetValue(look, radius);
            appearanceType.GetProperty("EffectPx")!.SetValue(look, 0);
            appearanceType.GetProperty("Decoration")!.SetValue(look, "");
            return captureType.GetConstructors()[0].Invoke(new[] { window, look, (object)true });
        }

        Window OpenWindow()
        {
            var windows = Application.Current.Windows.OfType<Window>()
                .Where(w => w.GetType().Name == "CapturePinWindow" && w.IsVisible).ToList();
            return windows.Count == 1 ? windows[0] : windows.LastOrDefault();
        }

        // 文件那条路在后台线程解码，贴出来是异步的：一边抽消息一边等
        Window WaitWindow(int timeoutMs = 4000)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                var frame = new System.Windows.Threading.DispatcherFrame();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.Background,
                    new Action(() => frame.Continue = false));
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                if (OpenWindow() is Window found) return found;
                System.Threading.Thread.Sleep(30);
            }

            return OpenWindow();
        }

        object Prop(object target, string name) =>
            target.GetType().GetProperty(name, Private)?.GetValue(target)
            ?? target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(target);

        Int32Rect Physical(object window) =>
            (Int32Rect)window.GetType().GetMethod("PhysicalArea", Private)!.Invoke(window, null)!;

        (int X, int Y) Near(Int32Rect a, Int32Rect b) => (Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        try
        {
            var image = BuildShot(160, 96);

            // 贴到主屏工作区左上角附近（窗口矩形就是「贴图窗口」那一块）
            var screens = ((IEnumerable)screensType.GetMethod("All")!.Invoke(null, null)!)
                .Cast<object>().ToList();
            var primary = screens.FirstOrDefault(s => (bool)Prop(s, "Primary")!) ?? screens[0];
            int workLeft = (int)Prop(primary, "WorkLeft")!;
            int workTop = (int)Prop(primary, "WorkTop")!;
            var windowRect = new Int32Rect(workLeft + 80, workTop + 60, image.PixelWidth, image.PixelHeight);

            // ---------- 1) 纯图片复制：两份元数据都写进去 ----------
            Apply(copyFile: false);
            bool copied = (bool)copyImage.Invoke(null,
                new object?[] { image, "copy_screenshot", names, Capture(windowRect, 12), null })!;
            Check(copied, "复制截图成功");

            var rawArgs = new object[] { "LToolsScreenshotAppearance", 64 * 1024, null };
            bool hasAppearance = (bool)tryCustom.Invoke(null, rawArgs)!;
            Check(hasAppearance, "剪贴板里带外观元数据（LToolsScreenshotAppearance）");

            rawArgs = new object[] { "LToolsScreenshotPlacement", 16 * 1024, null };
            bool hasPlacement = (bool)tryCustom.Invoke(null, rawArgs)!;
            Check(hasPlacement, "剪贴板里带落位元数据（LToolsScreenshotPlacement）");

            var meta = readMeta.Invoke(null, null)!;
            var appearance = appearanceFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight, null });
            var placement = placementFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight, null });

            Check(appearance != null, "按图像素尺寸解得出外观");
            Check(placement != null, "按图像素尺寸解得出落位");

            Check((int)Prop(appearance, "RadiusPx")! == 12, "外观圆角是 12（原样读回）");
            Check((bool)Prop(appearance, "Checkerboard")!, "外观记着棋盘格");

            Check((Int32Rect)Prop(placement, "WindowRect")! == windowRect,
                $"落位窗口矩形原样读回（{Prop(placement, "WindowRect")}）");
            Check(appearanceFor.Invoke(meta, new object[] { image.PixelWidth + 1, image.PixelHeight, null }) == null,
                "图像素尺寸对不上时外观作废");
            Check(placementFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight + 1, null }) == null,
                "图像素尺寸对不上时落位作废");

            // ---------- 2) 贴剪贴板：按原位置 + 原外观贴回 ----------
            closeAll.Invoke(null, null);
            pin.Invoke(null, null);
            var pinned = OpenWindow();
            Check(pinned != null, "贴剪贴板贴出了一张贴图");
            if (pinned != null)
            {
                Pump(pinned);

                var area = Physical(pinned);
                var (dx, dy) = Near(area, windowRect);
                Check(dx <= 2 && dy <= 2,
                    $"贴回原窗口位置（期望 {windowRect.X},{windowRect.Y} 实际 {area.X},{area.Y}）");
                Check(Math.Abs(area.Width - windowRect.Width) <= 2
                    && Math.Abs(area.Height - windowRect.Height) <= 2,
                    $"贴回原尺寸（{area.Width}×{area.Height}）");

                Check(Prop(pinned, "CardKind")?.ToString() == "Image", "恢复的是图片贴图");
                var checker = pinned.FindName("Checker") as FrameworkElement;
                var backdrop = pinned.FindName("Backdrop") as FrameworkElement;
                Check(checker is { Visibility: Visibility.Visible }, "有外观时铺透明棋盘格");
                Check(backdrop is { Visibility: Visibility.Collapsed }, "铺棋盘格时不叠不透明底");

                Save(Render(pinned), Path.Combine(dir, "pin-restored.png"));

                closeAll.Invoke(null, null);
                Check(OpenWindow() == null, "贴图关掉了");
            }

            // ---------- 3) 带文件的复制：文件签名认得出来 ----------
            closeAll.Invoke(null, null);
            Apply(copyFile: true);
            int before = Directory.GetFiles(dir).Length;
            copied = (bool)copyImage.Invoke(null,
                new object?[] { image, "copy_screenshot", names, Capture(windowRect, 12), null })!;
            Check(copied, "带文件复制成功");

            string[] files = Directory.GetFiles(dir).OrderBy(f => f).ToArray();
            Check(files.Length == before + 1, $"复制时写出一份文件（{files.Length - before}）");

            var pathArgs = new object[] { null, false };
            bool gotFiles = (bool)tryFileList.Invoke(null, pathArgs)!;
            var paths = (gotFiles ? pathArgs[0] as IEnumerable : null)?.Cast<string>().ToList() ?? new List<string>();
            Check(paths.Count == 1, $"剪贴板里同时有那份文件（{paths.Count}）");

            string file = paths.FirstOrDefault() ?? files.LastOrDefault() ?? "";
            Check(File.Exists(file), "文件真的在磁盘上：" + Path.GetFileName(file));

            meta = readMeta.Invoke(null, null)!;
            Check(appearanceFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight, file }) != null,
                "同尺寸 + 同文件 → 外观可用");
            Check(placementFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight, file }) != null,
                "同尺寸 + 同文件 → 落位可用");

            // 文件被改过：签名失配，不再按原位置 / 原外观贴回
            File.AppendAllText(file, "x");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(3));
            Check(appearanceFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight, file }) == null,
                "文件被改过 → 外观作废");
            Check(placementFor.Invoke(meta, new object[] { image.PixelWidth, image.PixelHeight, file }) == null,
                "文件被改过 → 落位作废");

            // ---------- 4) 带文件时贴剪贴板：文件那条路也按原位置贴回 ----------
            Apply(copyFile: true);
            copyImage.Invoke(null, new object?[] { image, "copy_screenshot", names, Capture(windowRect, 12), null });
            meta = readMeta.Invoke(null, null)!;

            closeAll.Invoke(null, null);
            pin.Invoke(null, null);
            var fromFile = WaitWindow();
            Check(fromFile != null, "带文件时也能贴出贴图");
            if (fromFile != null)
            {
                Pump(fromFile);
                var area = Physical(fromFile);
                var (dx, dy) = Near(area, windowRect);
                Check(dx <= 2 && dy <= 2,
                    $"文件那条路也贴回原位置（实际 {area.X},{area.Y}）");
                closeAll.Invoke(null, null);
            }

            // ---------- 5) 没有元数据时退回居中（不假装按原位置） ----------
            closeAll.Invoke(null, null);
            clipboard.GetMethod("SetImage")!.Invoke(null, new object[] { image });
            pin.Invoke(null, null);
            var plain = OpenWindow();
            Check(plain != null, "没有元数据的图片照样贴");
            if (plain != null)
            {
                Pump(plain);
                var area = Physical(plain);
                var (dx, dy) = Near(area, windowRect);
                Check(dx > 2 || dy > 2, $"没有元数据时不按原位置摆（实际 {area.X},{area.Y}）");
                closeAll.Invoke(null, null);
            }
        }
        finally
        {
            closeAll.Invoke(null, null);
            clearStore.Invoke(null, null);
            if (snapshot != null) clipRestore.Invoke(null, new object[] { snapshot });
            settingsType.GetMethod("Apply")!.Invoke(null, new[] { backup });
        }
    }
}
