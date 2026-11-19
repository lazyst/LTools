using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 录屏工具条「贴边自动收起」（P8-e，照上游 <c>screenrecordingtoolbarwindow.cpp</c> 的 AutoHide）：
/// 真面板 + 真光标 —— 贴左边 → 鼠标离开 200ms 收起成小条 → 悬停 / 点击唤出 → 再次收起，
/// 以及「拖着」「指针还在条上」「设置关着」「没在录」都不收起。
/// </summary>
internal static partial class Program
{
    private const BindingFlags AutoPrivate = BindingFlags.NonPublic | BindingFlags.Instance;

    private const long WsExNoactivate = 0x08000000;
    private const long WsExToolwindow = 0x00000080;
    private const uint WdaExcludeFromCapture = 0x00000011;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    private static void RecordAutoHideSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;
        var controllerType = asm.GetType("LTools.Features.CapturePro.RecordController")!;
        var barType = asm.GetType("LTools.Views.CapturePro.CaptureRecordWindow")!;

        const BindingFlags AnyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // 面板：没有 gate = 立刻就在录（auto-hide 只在这个状态下生效）
        var area = new Int32Rect(300, 240, 700, 460);
        var plan = planType.GetMethod("FromValues")!.Invoke(null,
            new object[] { "all", "mp4", 30, 0, false, null })!;

        var options = plan.GetType().GetProperty("Options")!.GetValue(plan)!;
        var autoHideSetting = options.GetType().GetProperty("AutoHideToolbar")!;
        autoHideSetting.SetValue(options, true);

        object controller = Activator.CreateInstance(controllerType)!;
        var bar = (Window)Activator.CreateInstance(barType, AnyCtor, null,
            new object[] { area, plan, controller, null }, null)!;

        var reconcile = barType.GetMethod("ReconcileAutoHide", AutoPrivate)!;
        var autoHidden = barType.GetProperty("AutoHidden", AutoPrivate)!;
        var sideProperty = barType.GetProperty("AutoHideSide", AutoPrivate)!;
        var tabProperty = barType.GetProperty("AutoHideTabWindow", AutoPrivate)!;
        var recordingField = barType.GetField("_recording", AutoPrivate)!;
        var draggingField = barType.GetField("_dragging", AutoPrivate)!;

        bool IsHidden() => (bool)autoHidden.GetValue(bar)!;
        string Side() => sideProperty.GetValue(bar)!.ToString()!;
        Window? Tab() => tabProperty.GetValue(bar) as Window;
        IntPtr Handle(Window window) => new WindowInteropHelper(window).Handle;

        void MoveAway() => SetCursorPos(10, 1060);
        void MoveTo(Window window)
        {
            var rect = WindowBounds(window);
            SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        }

        try
        {
            bar.Show();
            PumpFor(120);

            // 摆到屏幕左边（物理 0）：面板本来摆在区域下方居中，这里手动拖过去
            bar.Left = 0;
            bar.Top = 400;
            PumpFor(120);

            // ---- 鼠标离开 → 200ms 后收起 ----
            MoveAway();
            reconcile.Invoke(bar, null);
            Check(WaitUntil(IsHidden, 4000), "贴左边、鼠标移开后收起");
            Check(!bar.IsVisible, "收起后工具条自己隐起来");
            Check(Side() == "Left", $"贴的是左边（{Side()}）");

            var tab = Tab();
            Check(tab != null, "收起后有贴边小条");
            if (tab == null) return;

            Check(tab.IsVisible, "小条是可见的");
            var tabHandle = Handle(tab);
            Check(tabHandle != IntPtr.Zero, "小条有窗口句柄");

            long exStyle = GetWindowLongPtr(tabHandle, -20).ToInt64();
            Check((exStyle & WsExNoactivate) != 0, "小条不抢焦点（WS_EX_NOACTIVATE）");
            Check((exStyle & WsExToolwindow) != 0, "小条不进 Alt+Tab（WS_EX_TOOLWINDOW）");
            Check(GetWindowDisplayAffinity(tabHandle, out uint affinity) != 0 && affinity == WdaExcludeFromCapture,
                "小条自己不入镜（WDA_EXCLUDEFROMCAPTURE）");

            var tabRect = WindowBounds(tab);
            double scale = Math.Max(1, GetDpiForWindow(tabHandle) / 96.0);
            int expectedWidth = (int)Math.Round(20 * scale);
            Check(tabRect.Left == 0, $"小条贴住屏幕左边（x={tabRect.Left}）");
            Check(Math.Abs(tabRect.Right - tabRect.Left - expectedWidth) <= 2,
                $"小条宽 20 DIP（实际 {tabRect.Right - tabRect.Left}，期望 {expectedWidth}）");
            Check(tabRect.Bottom - tabRect.Top >= 22, $"小条有高度（{tabRect.Bottom - tabRect.Top}）");

            // 留一张 4 倍渲染图给人眼看形状 / 箭头方向
            try
            {
                tab.UpdateLayout();
                var bitmap = new RenderTargetBitmap(
                    Math.Max(1, (int)(tab.ActualWidth * 4)), Math.Max(1, (int)(tab.ActualHeight * 4)),
                    384, 384, PixelFormats.Pbgra32);
                bitmap.Render(tab);
                Save(bitmap, Path.Combine(_outDir, "record-autohide-tab.png"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("渲染收起条失败：" + ex.Message);
            }

            // ---- 悬停小条 100ms → 唤出 ----
            MoveTo(tab);
            Check(WaitUntil(() => !IsHidden(), 4000), "指针停在小条上就展开");
            Check(bar.IsVisible, "展开后工具条回来");
            Check(tab.IsVisible == false, "展开后小条收起");
            Check(Math.Abs(bar.Left) < 1, $"展开后位置不丢（Left={bar.Left:0.##}）");

            // ---- 再移开 → 又收起（说明是循环的，不是一次性） ----
            MoveAway();
            Check(WaitUntil(IsHidden, 4000), "第二次移开照样收起");

            // ---- 点小条立即展开（不等 100ms 悬停） ----
            tab = Tab();
            if (tab != null)
            {
                var rect = WindowBounds(tab);
                SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                PumpFor(30);
                mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
                mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
                Check(WaitUntil(() => !IsHidden(), 2000), "点小条立即展开");
            }

            // ---- 指针还停在工具条上：不收 ----
            MoveTo(bar);
            reconcile.Invoke(bar, null);
            PumpFor(700);
            Check(!IsHidden(), "鼠标还在工具条上时不收起");

            // ---- 拖着工具条时不收 ----
            MoveAway();
            draggingField.SetValue(bar, true);
            try
            {
                reconcile.Invoke(bar, null);
                PumpFor(700);
                Check(!IsHidden(), "正拖着工具条时不收起");
            }
            finally
            {
                draggingField.SetValue(bar, false);
            }

            Check(WaitUntil(IsHidden, 4000), "松手 + 鼠标移开之后才收起");

            // ---- 设置关掉：收起中的立刻展开，之后不再收起 ----
            autoHideSetting.SetValue(options, false);
            reconcile.Invoke(bar, null);
            Check(WaitUntil(() => !IsHidden(), 2000), "关掉设置后立刻展开");
            MoveAway();
            reconcile.Invoke(bar, null);
            PumpFor(700);
            Check(!IsHidden(), "设置关着就不会再收起");
            autoHideSetting.SetValue(options, true);

            // ---- 没在录（摆着等开始）：从来都不收 ----
            recordingField.SetValue(bar, false);
            try
            {
                reconcile.Invoke(bar, null);
                PumpFor(700);
                Check(!IsHidden(), "摆着等用户点录制时不收起");
            }
            finally
            {
                recordingField.SetValue(bar, true);
            }

            // 复原之后还能正常收起（证明上面几条不是把它弄坏了）
            reconcile.Invoke(bar, null);
            Check(WaitUntil(IsHidden, 4000), "恢复「在录 + 设置开着」之后照常收起");

            // ---- 关面板要把小条一起收掉 ----
            bar.Close();
            PumpFor(200);
            Check(Tab() == null, "关面板时小条一起关掉");
            Check(!bar.IsVisible, "工具条已关闭");
        }
        finally
        {
            try { if (bar.IsVisible) bar.Close(); } catch { /* 已经关了 */ }
            PumpFor(120);
            SetCursorPos(960, 540);
        }
    }

    private static WindowRect WindowBounds(Window window)
    {
        GetWindowRect(new WindowInteropHelper(window).Handle, out var rect);
        return rect;
    }
}
