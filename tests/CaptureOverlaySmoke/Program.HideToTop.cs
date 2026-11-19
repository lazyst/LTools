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
/// 贴图「收到屏幕顶部」（P5-i，照上游 <c>screenshotpinnedhidetotopcontroller.cpp</c>）：
/// 真窗口进入动画 → 只剩 30×6 手柄 → 鼠标靠近展开 / 离开收起 → 手柄双击 / 中键 / 右键 →
/// 隐藏全部保留模式 → 关掉再贴回按记录收起。
/// </summary>
internal static partial class Program
{
    private static void HideToTopSection(BitmapSource image)
    {
        const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var asm = typeof(CaptureOverlayWindow).Assembly;
        var manager = asm.GetType("LTools.Features.CapturePro.CapturePinManager")!;
        var store = asm.GetType("LTools.Features.CapturePro.PinStore")!;
        var hide = asm.GetType("LTools.Features.CapturePro.PinHideToTop")!;
        var screens = asm.GetType("LTools.Native.Screens")!;
        var pointer = asm.GetType("LTools.Features.CapturePro.PointerActions")!;

        // 前一节可能还留着贴图：先全关
        manager.GetMethod("CloseAll", Stat)!.Invoke(null, null);

        var pin = manager.GetMethod("Pin")!.Invoke(null,
            new object[] { image, new Int32Rect(300, 200, image.PixelWidth, image.PixelHeight), "screenshot", null })
            as Window;
        Check(pin != null && pin.IsVisible, "收顶部：先贴一张图");
        if (pin == null) return;

        var type = pin.GetType();
        string id = (string)type.GetProperty("PinId", Inst)!.GetValue(pin)!;

        object Call(string name, params object[] args) =>
            type.GetMethod(name, Inst)!.Invoke(pin, args);

        string State() => Field(pin, "_hideState")!.ToString()!;
        bool Active() => (bool)type.GetProperty("HideToTopActive", Inst)!.GetValue(pin)!;
        bool Suppressed() => (bool)type.GetProperty("HideToTopSuppressed", Inst)!.GetValue(pin)!;
        int Accent() => (int)type.GetProperty("HideToTopAccent", Inst)!.GetValue(pin)!;
        Int32Rect? HandleRect() => (Int32Rect?)type.GetProperty("HideToTopHandleRect", Inst)!.GetValue(pin);
        Window? Handle() => Field(pin, "_hideHandle") as Window;

        // 这块窗口所在的屏（物理像素 + 缩放）
        object all = screens.GetMethod("All", Stat)!.Invoke(null, null)!;
        object screen = hide.GetMethod("MonitorAt", Stat)!.Invoke(null,
            new object[] { all, (int)Math.Round(pin.Left + pin.Width / 2), (int)Math.Round(pin.Top + pin.Height / 2) })!;
        var work = (Int32Rect)hide.GetMethod("WorkAreaOf", Stat)!.Invoke(null, new[] { screen })!;
        double scale = (double)screen.GetType().GetProperty("Scale", Inst)!.GetValue(screen)!;
        if (work.Width <= 0 || scale <= 0)
        {
            Check(false, "收顶部：拿不到工作区 / 缩放，跳过");
            return;
        }

        // 等真实时间过去，同时把消息泵转起来（动画 / 55ms 指针轮询都是 DispatcherTimer）
        void Wait(int ms)
        {
            long until = Environment.TickCount64 + ms;
            while (Environment.TickCount64 < until)
            {
                pin.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(12);
            }
        }

        void AwayFromHandle()
        {
            // 手柄之外、窗口之外：往工作区右下角外面一点（窗口只有 320×240 那么大）
            SetCursorPos(work.X + work.Width - 8, work.Y + work.Height - 8);
            Wait(150);
        }

        try
        {
            // ============ 菜单入口 ============
            var menu = (ContextMenu)Call("BuildMenu")!;
            bool hasItem = false;
            foreach (var item in menu.Items)
                if (item is MenuItem mi && (mi.Header as string)?.StartsWith("收到屏幕顶部", StringComparison.Ordinal) == true)
                hasItem = true;
            Check(hasItem, "收顶部：右键菜单里有「收到屏幕顶部」");
            menu.IsOpen = false;

            // ============ 进入：动画期间窗口还在滑、手柄不出现 ============
            Call("ToggleHideToTop");
            Wait(120);

            Check(State() == "Entering", $"收顶部：进入动画中（实际 {State()}）");
            Check(Math.Abs(pin.Opacity) < 0.95, $"收顶部：进入途中在淡出（{pin.Opacity:0.##}）");
            var handle = Handle();
            Check(handle == null || !handle.IsVisible, "收顶部：进入途中手柄还不出现");

            Wait(400);
            Check(State() == "Hidden", $"收顶部：动画结束只剩手柄（实际 {State()}）");
            Check(!pin.IsVisible, "收顶部：贴图窗口藏起来了");
            Check(Math.Abs(pin.Opacity) < 0.01, $"收顶部：藏起来时不透明度 0（{pin.Opacity:0.##}）");

            handle = Handle();
            Check(handle != null && handle.IsVisible, "收顶部：顶边手柄出来了");
            if (handle == null) return;

            var rect = HandleRect();
            Check(rect is { Width: > 0, Height: > 0 }, "收顶部：手柄矩形有值");
            if (rect is not { } hr) return;

            Check(hr.Y == work.Y, $"收顶部：手柄贴住工作区顶边（{hr.Y} vs {work.Y}）");
            Check(hr.X >= work.X && hr.X + hr.Width <= work.X + work.Width,
                $"收顶部：手柄在工作区里（{hr.X}+{hr.Width} ⊂ {work.X}+{work.Width}）");

            // 手柄窗口 = 命中区（比可见手柄大一圈），窗内那块彩色条 = 30×6 DIP
            var hit = (Int32Rect)hide.GetMethod("HitGeometry", Stat)!
                .Invoke(null, new object[] { hr, work, scale })!;
            Check(Math.Abs(handle.Left * scale - hit.X) <= 1.5,
                $"收顶部：手柄窗口左边对齐（{handle.Left * scale:0.#} vs {hit.X}）");
            Check(Math.Abs(handle.Top * scale - hr.Y) <= 1.5,
                $"收顶部：手柄窗口顶边对齐（{handle.Top * scale:0.#} vs {hr.Y}）");
            Check(Math.Abs(handle.Width - (hr.Width + 2 * 2 * scale) / scale) <= 1.5,
                $"收顶部：窗口是命中区（{handle.Width * scale:0.#} 物理 vs 手柄 {hr.Width}）");
            Check(Math.Abs(handle.Height - (hr.Height + 2 * scale) / scale) <= 1.5,
                $"收顶部：命中区下方多出 2 DIP（{handle.Height * scale:0.#} 物理）");

            var body = (Border)Field(handle, "_body")!;
            Check(Math.Abs(body.Width * scale - hr.Width) <= 1.5 &&
                  Math.Abs(body.Height * scale - hr.Height) <= 1.5,
                $"收顶部：可见手柄 {body.Width:0.#}×{body.Height:0.#} DIP（{hr.Width}×{hr.Height} 物理）");

            double dpi = Math.Max(1, scale);
            Check(Math.Abs(body.BorderThickness.Left - 2 / dpi) < 0.01 &&
                  Math.Abs(body.BorderThickness.Right - 2 / dpi) < 0.01 &&
                  Math.Abs(body.BorderThickness.Bottom - 2 / dpi) < 0.01 &&
                  Math.Abs(body.BorderThickness.Top) < 0.01,
                $"收顶部：描边左 / 右 / 下各 2 物理像素、顶边不画（{body.BorderThickness}）");

            var accentColor = ((SolidColorBrush)body.Background).Color;
            var expected = (Color)hide.GetMethod("AccentColor", Stat)!.Invoke(null, new object[] { Accent() })!;
            Check(accentColor == expected, $"收顶部：手柄用分配到的 accent（{accentColor} vs {expected}）");

            Save(Render(handle), Path.Combine(_outDir, "pin-hide-handle.png"));

            // ============ 记录：关掉 / 贴回都靠它 ============
            var record = ((System.Collections.IEnumerable)store.GetMethod("Load", Stat)!.Invoke(null, null)!)
                .Cast<object>()
                .FirstOrDefault(r => (string)r.GetType().GetProperty("Id")!.GetValue(r)! == id);
            Check(record != null, "收顶部：贴图历史里有这张");
            Check(record != null && (bool)record.GetType().GetProperty("HideToTop")!.GetValue(record)!,
                "收顶部：历史里记下「收在顶部」");
            Check(record != null &&
                  (int)record.GetType().GetProperty("HideToTopX")!.GetValue(record)! == hr.X,
                "收顶部：历史里记下手柄左边（物理像素）");
            Check(record != null &&
                  (int)record.GetType().GetProperty("HideToTopAccent")!.GetValue(record)! == Accent(),
                "收顶部：历史里记下 accent");

            // ============ 悬停展开 / 离开收起 ============
            int cx = hr.X + hr.Width / 2, cy = hr.Y + hr.Height / 2;
            SetCursorPos(cx, cy);
            Wait(250);

            Check(State() == "Revealed", $"收顶部：鼠标靠近手柄就展开（{State()}）");
            Check(pin.IsVisible, "收顶部：展开的贴图看得见");
            Check(Math.Abs(pin.Left * scale - hr.X) <= 1.5 &&
                  Math.Abs(pin.Top * scale - (hr.Y + hr.Height)) <= 1.5,
                $"收顶部：展开在手柄正下方（{pin.Left * scale:0.#},{pin.Top * scale:0.#}）");
            Check(Math.Abs(pin.Opacity - 1) < 0.02, $"收顶部：展开时恢复用户不透明度（{pin.Opacity:0.##}）");

            AwayFromHandle();
            Wait(250);
            Check(State() == "Hidden" && !pin.IsVisible, $"收顶部：离开之后收起（{State()}）");

            // ============ 手柄右键：先退出收顶部，再在光标处弹菜单 ============
            SetCursorPos(cx, cy);
            Wait(200);
            Check(State() == "Revealed", "收顶部：右键之前又展开了");

            mouse_event(0x0008 /* 右键按下 */, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(0x0010 /* 右键抬起 */, 0, 0, 0, UIntPtr.Zero);
            Wait(250);

            Check(!Active(), $"收顶部：手柄右键退出收顶部（{State()}）");
            Check(pin.IsVisible, "收顶部：退出之后整张图回到屏幕上");
            var opened = Field(pin, "_menu") as ContextMenu;
            Check(opened is { IsOpen: true }, "收顶部：手柄右键弹出贴图菜单");
            if (opened != null) opened.IsOpen = false;

            // ============ 手柄中键：按设置执行（默认重置缩放） ============
            Call("SetZoom", 1.5);
            Wait(80);
            Check(Math.Abs((double)Field(pin, "_zoom")! - 1.5) < 0.01, "收顶部：先把缩放调到 150%");

            Call("ToggleHideToTop");
            Wait(450);
            Check(State() == "Hidden", "收顶部：再次收起");

            // 重新收起之后手柄可能落到了别处（缩放改了窗口宽度 → 首选位置跟着变）
            var rect2 = HandleRect();
            Check(rect2 is { Width: > 0 }, "收顶部：再次收起时手柄还在");
            if (rect2 is not { } hr2) return;

            int cx2 = hr2.X + hr2.Width / 2, cy2 = hr2.Y + hr2.Height / 2;
            SetCursorPos(cx2, cy2);
            Wait(250);
            Check(State() == "Revealed", $"收顶部：中键之前先展开（{State()}）");

            mouse_event(0x0020 /* 中键按下 */, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(40);
            mouse_event(0x0040 /* 中键抬起 */, 0, 0, 0, UIntPtr.Zero);
            Wait(250);

            Check(Math.Abs((double)Field(pin, "_zoom")! - 1) < 0.01,
                $"收顶部：手柄中键 = 重置缩放（{Field(pin, "_zoom")}）");
            Check(!Active(), $"收顶部：中键动作之后退出收顶部（{State()}）");

            // ============ 隐藏全部：保留模式，放回来手柄还在 ============
            Call("ToggleHideToTop");
            Wait(450);
            Check(State() == "Hidden", "收顶部：进到隐藏态");
            var handle2 = Handle();
            Check(handle2 != null && handle2.IsVisible, "收顶部：手柄在");

            int hidden = (int)manager.GetMethod("HideAll", Stat)!.Invoke(null, null)!;
            Wait(120);
            Check(hidden >= 1, $"收顶部：隐藏全部收起了 {hidden} 张");
            Check(Suppressed(), "收顶部：隐藏全部 = 按住模式（不是退出）");
            Check(handle2 != null && !handle2.IsVisible, "收顶部：手柄一起收起来，不留在屏幕上");

            int shown = (int)manager.GetMethod("ShowAll", Stat)!.Invoke(null, null)!;
            Wait(150);
            Check(shown >= 1, $"收顶部：放回来 {shown} 张");
            Check(Active() && !Suppressed(), "收顶部：放回来还是收顶部模式");
            Check(handle2 != null && handle2.IsVisible, "收顶部：手柄回到屏幕上");
            Check(State() == "Hidden" && !pin.IsVisible, "收顶部：放回来时窗口仍然藏着");

            // ============ 关掉再贴回：按记录直接收起（不播动画） ============
            int accentBefore = Accent();
            int leftBefore = hr.X;
            Call("RequestUserClose");
            Wait(200);
            Check(!pin.IsVisible, "收顶部：关掉了");

            // 素材是后台写的，等一会儿再贴回
            string asset = (string)store.GetMethod("AssetPath", Stat)!.Invoke(null, new object[] { id })!;
            for (int i = 0; i < 40 && !File.Exists(asset); i++) Thread.Sleep(50);

            var restored = manager.GetMethod("RestoreLastClosed", Stat)!.Invoke(null, null) as Window;
            Check(restored != null, "收顶部：贴回最近关闭的那张");
            if (restored != null)
            {
                Wait(250);
                var rtype = restored.GetType();
                Check((bool)rtype.GetProperty("HideToTopActive", Inst)!.GetValue(restored)!,
                    "收顶部：贴回时直接是收顶部模式");
                Check(!restored.IsVisible, "收顶部：贴回的贴图不露出来（只剩手柄）");
                Check((int)rtype.GetProperty("HideToTopAccent", Inst)!.GetValue(restored)! == accentBefore,
                    "收顶部：贴回沿用原来的 accent");
                var rrect = (Int32Rect?)rtype.GetProperty("HideToTopHandleRect", Inst)!.GetValue(restored);
                Check(rrect is { } rr && rr.X == leftBefore,
                    $"收顶部：贴回手柄回到原左边（{rrect?.X} vs {leftBefore}）");
                var rhandle = rtype.GetField("_hideHandle", Inst)!.GetValue(restored) as Window;
                Check(rhandle != null && rhandle.IsVisible, "收顶部：贴回的手柄出来了");

                // 展开一次，确认位置就在手柄下方
                SetCursorPos(leftBefore + rr.Width / 2, rr.Y + rr.Height / 2);
                Wait(250);
                Check((bool)rtype.GetProperty("HideToTopActive", Inst)!.GetValue(restored)! &&
                      restored.IsVisible, "收顶部：贴回的那张也能悬停展开");
                AwayFromHandle();

                rtype.GetMethod("RequestUserClose", Inst)!.Invoke(restored, null);
                Wait(150);
            }
        }
        finally
        {
            try { if (pin.IsVisible || Active()) { type.GetMethod("RequestUserClose", Inst)!.Invoke(pin, null); } }
            catch { /* 已经关了 */ }

            store.GetMethod("Remove", Stat)!.Invoke(null, new object[] { id });
        }
    }
}
