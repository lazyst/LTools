using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 指针动作与「按下武装、抬起才执行」（P7-b，照 Snow Shot 的 <c>MouseReleaseActionController</c>
/// 与 <c>screenshot/*</c> / <c>pin_to_screen/*</c> 的四个指针动作设置）。
/// </summary>
/// <remarks>
/// 全用真鼠标（<c>SetCursorPos</c> + <c>mouse_event</c>）打在自己进程的窗口上：
/// 贴图不抢焦点、覆盖层要真的收得到抬起，这一层伪造事件验不出来。
/// 设置写进临时配置目录，跑完还原，别动开发机上的 <c>configs/settings.json</c>。
/// </remarks>
internal static partial class Program
{
    private const uint MouseRightDown = 0x0008;
    private const uint MouseRightUp = 0x0010;
    private const uint MouseMiddleDown = 0x0020;
    private const uint MouseMiddleUp = 0x0040;

    private static void ReleaseActionSection()
    {
        Console.WriteLine("---- 指针动作 / 鼠标释放动作（P7-b）----");

        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var settingsType = asm.GetType("LTools.Features.CapturePro.PointerSettings")!;
        var storeType = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var settingsStoreType = asm.GetType("LTools.Config.SettingsStore")!;

        // 设置写进临时配置：别动本机 configs/settings.json
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string configRoot = Path.Combine(_outDir, "release-config");
        if (Directory.Exists(configRoot)) Directory.Delete(configRoot, true);
        Directory.CreateDirectory(Path.Combine(configRoot, "configs"));
        string anchor = Path.Combine(configRoot, "LTools.json");
        configPathSet.Invoke(null, new object[] { anchor });

        // 复制结果 / Toast 都投递到 UI Dispatcher
        asm.GetType("LTools.Core.Ui")!.GetMethod("Initialize", Stat)!
            .Invoke(null, new object[] { System.Windows.Threading.Dispatcher.CurrentDispatcher });

        void Set(string method, object value) =>
            settingsType.GetMethod(method, Stat)!.Invoke(null, new[] { value });
        void Invalidate() => settingsType.GetMethod("Invalidate", Stat)!.Invoke(null, null);
        string Current(string property) => settingsType.GetProperty(property, Stat)!.GetValue(null)!.ToString()!;

        /// 磁盘上的真实值（写盘路径也要验：设置页存进去的是英文键）
        string Saved(string section, string property)
        {
            object file = settingsStoreType.GetMethod("Read", Stat)!.Invoke(null, new object[] { anchor })!;
            object part = file.GetType().GetProperty(section)!.GetValue(file)!;
            return part?.GetType().GetProperty(property)!.GetValue(part)?.ToString() ?? "";
        }

        static void PointAt(Window window)
        {
            Point p = window.PointToScreen(new Point(Math.Max(1, window.Width / 2),
                Math.Max(1, window.Height / 2)));
            SetCursorPos((int)Math.Round(p.X), (int)Math.Round(p.Y));
            Thread.Sleep(120);
        }

        static void Tap(uint down, uint up, int hold = 40)
        {
            mouse_event(down, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(hold);
            mouse_event(up, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
        }

        static bool GatePending(Window window)
        {
            object gate = Field(window, "_release")!;
            return (bool)gate.GetType().GetProperty("Pending")!.GetValue(gate)!;
        }

        static string GateButton(Window window)
        {
            object gate = Field(window, "_release")!;
            return gate.GetType().GetProperty("Button")!.GetValue(gate)?.ToString() ?? "";
        }

        static string PinIdOf(Window pin) =>
            typeof(CapturePinWindow).GetProperty("PinId", Private)?.GetValue(pin) as string ?? "";

        static bool Thumb(Window pin) => (bool)pin.GetType().GetProperty("Thumbnail")!.GetValue(pin)!;
        static double ZoomOf(Window pin) => (double)pin.GetType().GetProperty("Zoom")!.GetValue(pin)!;
        static string FinishOf(CaptureOverlayWindow overlay) =>
            typeof(CaptureOverlayWindow).GetProperty("Finish", Private)!.GetValue(overlay)!.ToString()!;
        static bool Flag(CaptureOverlayWindow overlay, string name) =>
            (bool)typeof(CaptureOverlayWindow).GetProperty(name)!.GetValue(overlay)!;

        List<object> PinRecords() =>
            ((IEnumerable)storeType.GetMethod("Load", Stat)!.Invoke(null, null)!).Cast<object>().ToList();

        static string IdOf(object record) => (string)record.GetType().GetProperty("Id")!.GetValue(record)!;

        var image = BuildShot(320, 200);
        var created = new List<string>();

        CapturePinWindow NewPin(int x, int y)
        {
            var pin = (CapturePinWindow)manager.GetMethod("Pin", Stat)!.Invoke(null,
                new object[]
                {
                    image, new Int32Rect(x, y, image.PixelWidth, image.PixelHeight), "screenshot", null,
                })!;
            Pump(pin);
            SetTopmost(pin, true);
            Thread.Sleep(120);
            Pump(pin);
            created.Add(PinIdOf(pin));
            return pin;
        }

        CaptureOverlayWindow NewCover()
        {
            var overlay = NewOverlay(BuildShot(600, 400), new System.Drawing.Rectangle(0, 0, 600, 400));
            overlay.Show();
            Pump(overlay);
            return overlay;
        }

        try
        {
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            Invalidate();

            // ============ 0) 默认值（照上游 configurationschema.cpp） ============
            Check(Current("PinDoubleClick") == "ThumbnailMode", "默认：贴图双击 = 缩略图模式");
            Check(Current("PinMiddleClick") == "ResetZoom", "默认：贴图中键 = 重置缩放");
            Check(Current("OverlayDoubleClick") == "Copy", "默认：框选后双击 = 复制");
            Check(Current("OverlayMiddleClick") == "Pin", "默认：框选后中键 = 贴到屏幕");
            Check((bool)settingsType.GetProperty("ConfirmBeforeDestroyingWindow", Stat)!.GetValue(null)!,
                "默认：删除贴图前先确认");

            // ============ 1) 贴图双击 = 缩略图模式（默认） ============
            var pin1 = NewPin(240, 180);
            Check(pin1.IsVisible && Math.Abs(ZoomOf(pin1) - 1) < 0.001, "贴图出来了，100%");
            Check(!Thumb(pin1), "默认不是缩略图");

            PointAt(pin1);
            Tap(LeftDown, LeftUp);
            Pump(pin1);
            Tap(LeftDown, LeftUp);
            Pump(pin1);
            Check(Thumb(pin1), "双击（默认缩略图模式）→ 缩成小方块");

            PointAt(pin1);            // 方块变小了，位置要重新算
            Tap(LeftDown, LeftUp);
            Pump(pin1);
            Tap(LeftDown, LeftUp);
            Pump(pin1);
            Check(!Thumb(pin1), "再双击一次 → 回到整张图");
            Check(pin1.IsVisible, "缩略图切换不关窗");

            pin1.Close();
            Pump(pin1);

            // ============ 2) 双击「关闭」：第二下按下不动，抬起之后一拍才关 ============
            Set("SetPinDoubleClick", "close");
            Invalidate();
            Check(Saved("PinToScreen", "DoubleClickAction") == "close", "设置写盘：PinToScreen.DoubleClickAction = close");

            var pin2 = NewPin(240, 180);
            PointAt(pin2);

            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);      // 第一下（单击 = 开始拖动）
            Thread.Sleep(40);
            Pump(pin2);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Pump(pin2);

            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);      // 第二下（双击 = 关闭）：只武装
            Thread.Sleep(60);
            Pump(pin2);
            Check(pin2.IsVisible, "双击「关闭」：第二下按下时窗口还在（等抬起）");
            Check(GatePending(pin2), "按下只武装、还没执行");
            Check(GateButton(pin2) == "Left", "武装的是左键");

            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Pump(pin2);
            Check(!pin2.IsVisible, "按钮抬起之后一拍才关掉");

            // ============ 3) 中键 = 重置缩放 ============
            Set("SetPinMiddleClick", "reset_zoom");
            var pin3 = NewPin(240, 180);
            pin3.SetZoom(2);
            Pump(pin3);
            Check(Math.Abs(ZoomOf(pin3) - 2) < 0.01, $"先放大到 200%（{ZoomOf(pin3):0.##}）");

            PointAt(pin3);
            Tap(MouseMiddleDown, MouseMiddleUp);
            Pump(pin3);
            Check(Math.Abs(ZoomOf(pin3) - 1) < 0.01, $"中键（默认重置缩放）→ 回到 100%（{ZoomOf(pin3):0.##}）");
            Check(pin3.IsVisible, "重置缩放不关窗");

            // 中键「关闭」也只武装
            Set("SetPinMiddleClick", "close");
            PointAt(pin3);
            mouse_event(MouseMiddleDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            Pump(pin3);
            Check(pin3.IsVisible && GatePending(pin3), "中键「关闭」按下只武装");
            mouse_event(MouseMiddleUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Pump(pin3);
            Check(!pin3.IsVisible, "中键抬起之后才关掉");

            // ============ 4) 双击「删除」：连贴图历史一起删 ============
            Set("SetConfirmBeforeDestroyingWindow", false);
            Set("SetPinDoubleClick", "destroy");
            Check(Saved("PinToScreen", "ConfirmBeforeDestroyingWindow") == "False",
                "设置写盘：ConfirmBeforeDestroyingWindow = false");

            var pin4 = NewPin(240, 180);
            string id4 = PinIdOf(pin4);
            Check(PinRecords().Any(r => IdOf(r) == id4), "删除前历史里有这条记录");

            PointAt(pin4);
            Tap(LeftDown, LeftUp);
            Pump(pin4);
            Tap(LeftDown, LeftUp);
            Pump(pin4);

            Check(!pin4.IsVisible, "双击「删除」→ 窗口关掉");
            Check(PinRecords().All(r => IdOf(r) != id4), "删除贴图连历史记录一起删掉（贴不回来）");
            created.Remove(id4);

            // ============ 5) 武装之后捕获被抢走 → 作废，抬起不关 ============
            Set("SetPinDoubleClick", "close");
            var pin5 = NewPin(240, 180);
            PointAt(pin5);

            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            Pump(pin5);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Pump(pin5);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(60);
            Pump(pin5);
            Check(GatePending(pin5), "第二次按下已武装");

            pin5.ReleaseMouseCapture();                       // 捕获被别的窗口抢走
            Thread.Sleep(60);
            Pump(pin5);
            Check(!GatePending(pin5), "捕获被抢走 → 这一次作废");

            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Pump(pin5);
            Check(pin5.IsVisible, "作废之后抬起不关窗（不会「点一下就没」）");

            pin5.Close();
            Pump(pin5);

            // ============ 6) 双击「不处理」= 什么都不做 ============
            Set("SetPinDoubleClick", "none");
            var pin6 = NewPin(240, 180);
            PointAt(pin6);
            Tap(LeftDown, LeftUp);
            Pump(pin6);
            Tap(LeftDown, LeftUp);
            Pump(pin6);
            Check(pin6.IsVisible && !Thumb(pin6), "双击「不处理」→ 窗口原样不动");
            pin6.Close();
            Pump(pin6);

            // 覆盖层要自己独占屏幕：先把贴图全收掉
            manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);
            Thread.Sleep(150);

            // ============ 7) 覆盖层右键：按下不关，抬起之后一拍才关 ============
            var cover1 = NewCover();
            Check(cover1.IsVisible, "覆盖层打开");

            PointAt(cover1);
            mouse_event(MouseRightDown, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(80);
            Pump(cover1);
            Check(cover1.IsVisible, "右键按下时覆盖层还开着（等抬起）");
            Check(GatePending(cover1), "右键按下只武装");
            Check(GateButton(cover1) == "Right", "武装的是右键");

            mouse_event(MouseRightUp, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(100);
            Pump(cover1);
            Check(!cover1.IsVisible, "右键抬起之后一拍才退出这次截图");
            Check(cover1.ResultImage == null, "右键退出不产出成品");
            Check(FinishOf(cover1) == "Copy", "取消不记完成动作");

            // ============ 8) 覆盖层中键 = 贴到屏幕（有选区） ============
            Set("SetOverlayMiddleClick", "pin");
            Invalidate();
            Check(Saved("Screenshot", "MiddleButtonAction") == "pin",
                "设置写盘：Screenshot.MiddleButtonAction = pin");
            Check(Current("OverlayMiddleClick") == "Pin", "缓存也跟着更新（不必重开窗口）");

            var cover2 = NewCover();
            SetSelection(cover2, new Rect(cover2.Width / 2 - 100, cover2.Height / 2 - 60, 200, 120));
            Pump(cover2);

            PointAt(cover2);
            Tap(MouseMiddleDown, MouseMiddleUp);
            Pump(cover2);
            Check(Flag(cover2, "PinRequested"), "中键（默认贴到屏幕）→ 请求贴图");
            Check(FinishOf(cover2) == "Pin", "完成动作记为贴图");
            Check(!cover2.IsVisible, "中键完成后覆盖层关掉");

            // ============ 9) 覆盖层中键：没有选区时什么都不做 ============
            var cover3 = NewCover();
            PointAt(cover3);
            Tap(MouseMiddleDown, MouseMiddleUp);
            Pump(cover3);
            Check(cover3.IsVisible, "没有选区时中键什么都不做（中键不是框选的起手式）");
            cover3.Close();
            Pump(cover3);

            // ============ 10) 覆盖层双击 = 复制（默认） ============
            Set("SetOverlayDoubleClick", "copy");
            Set("SetOverlayMiddleClick", "none");
            Invalidate();

            var cover4 = NewCover();
            SetSelection(cover4, new Rect(cover4.Width / 2 - 100, cover4.Height / 2 - 60, 200, 120));
            Pump(cover4);

            PointAt(cover4);
            Tap(LeftDown, LeftUp);
            Pump(cover4);
            Tap(LeftDown, LeftUp);
            Pump(cover4);

            Check(FinishOf(cover4) == "Copy", "双击（默认复制）→ 完成动作记为复制");
            Check(!cover4.IsVisible, "双击完成后覆盖层关掉");
            Check(cover4.ResultImage != null, "双击复制产出了成品");

            bool hasImage = false;
            try { hasImage = Clipboard.ContainsImage(); } catch { /* 被别的程序占着 */ }
            Check(hasImage, "双击复制后剪贴板里真的有图片");

            // ============ 11) 覆盖层双击 = 识别并复制：只挂标记，不真开识别窗 ============
            Set("SetOverlayDoubleClick", "ocr_copy");
            Invalidate();

            var cover5 = NewCover();
            SetSelection(cover5, new Rect(cover5.Width / 2 - 100, cover5.Height / 2 - 60, 200, 120));
            Pump(cover5);

            PointAt(cover5);
            Tap(LeftDown, LeftUp);
            Pump(cover5);
            Tap(LeftDown, LeftUp);
            Pump(cover5);

            Check(Flag(cover5, "RecognizeCopyRequested"), "双击「识别并复制」→ 置上识别并复制的标记");
            Check(Flag(cover5, "RecognizeRequested"), "同一次也走识别入口");
            Check(FinishOf(cover5) == "RecognizeCopy", "完成动作记为识别并复制");
            Check(!cover5.IsVisible, "标记落下后覆盖层关掉");

            // ============ 12) 覆盖层双击 = 不处理 ============
            Set("SetOverlayDoubleClick", "none");
            Invalidate();

            var cover6 = NewCover();
            SetSelection(cover6, new Rect(cover6.Width / 2 - 100, cover6.Height / 2 - 60, 200, 120));
            Pump(cover6);

            PointAt(cover6);
            Tap(LeftDown, LeftUp);
            Pump(cover6);
            Tap(LeftDown, LeftUp);
            Pump(cover6);
            Check(cover6.IsVisible && cover6.ResultImage == null, "双击「不处理」→ 留在覆盖层继续改");
            cover6.Close();
            Pump(cover6);
        }
        finally
        {
            try { manager.GetMethod("CloseAll", Stat)!.Invoke(null, null); } catch { /* 收摊 */ }

            // 删掉冒烟造的贴图历史（贴图记录在本机数据根的 pins/ 下）
            foreach (string id in created)
            {
                try { storeType.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id }); }
                catch { /* 已经不在了 */ }
            }

            configPathSet.Invoke(null, new object[] { oldConfigPath });
            Invalidate();
        }
    }
}
