using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 录屏面板的快捷键（P8-d，照上游 <c>screenrecordingshortcutcontroller.cpp</c>）：
/// 快照发布 / 指针门槛 / 命中后真的点按钮 / Esc 抬起才关 / R·Shift+R 翻录屏区域历史。
/// </summary>
internal static partial class Program
{
    private const BindingFlags KeyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private const ushort VkEsc = 0x1B;
    private const ushort VkR = 0x52;
    private const ushort VkV = 0x56;
    private const ushort VkF5 = 0x74;
    private const byte VkShift = 0xA0;

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private static void RecordKeysSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;
        var controllerType = asm.GetType("LTools.Features.CapturePro.RecordController")!;
        var barType = asm.GetType("LTools.Views.CapturePro.CaptureRecordWindow")!;
        var gateType = asm.GetType("LTools.Features.CapturePro.RecordStartGate")!;
        var routeType = asm.GetType("LTools.Features.CapturePro.RecordShortcutRoute")!;
        var historyType = asm.GetType("LTools.Features.CapturePro.RecordAreaHistory")!;
        var uiType = asm.GetType("LTools.Core.Ui")!;

        // 钩子把动作排回 UI 线程走 Core.Ui.Post（正式运行时 App.OnStartup 会初始化它）。
        // 冒烟没有 OnStartup，这里临时接上，跑完再还原成「未初始化」。
        var initializeUi = uiType.GetMethod("Initialize", KeyStatic)!;
        initializeUi.Invoke(null, new object[] { Application.Current.Dispatcher });

        const BindingFlags AnyCtor = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        // 区域历史的落盘文件先备份：跑完原样写回，别把用户真机上的历史搅乱
        string historyPath = (string)historyType.GetProperty("FilePath", KeyStatic)!.GetValue(null)!;
        byte[] savedHistory = Array.Empty<byte>();
        bool hadHistory = false;
        try
        {
            if (File.Exists(historyPath))
            {
                savedHistory = File.ReadAllBytes(historyPath);
                hadHistory = true;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("读区域历史失败（不备份）：" + ex.Message);
        }

        var handle = routeType.GetMethod("Handle", KeyStatic)!;
        var published = routeType.GetProperty("IsPublished", KeyStatic)!;
        var snapshotField = routeType.GetField("_current", KeyStatic)!;
        var publish = barType.GetMethod("PublishShortcuts", Private)!;
        var append = historyType.GetMethod("Append", KeyStatic)!;
        var entriesProperty = historyType.GetProperty("Entries", KeyStatic)!;
        var invalidate = historyType.GetMethod("Invalidate", KeyStatic)!;

        bool Hit(ushort vk, bool down, bool up) => (bool)handle.Invoke(null, new object[] { vk, down, up })!;
        bool IsPublished() => (bool)published.GetValue(null)!;

        object Snapshot() => snapshotField.GetValue(null);
        bool IsHot()
        {
            var snapshot = Snapshot();
            if (snapshot == null) return false;
            return (bool)snapshot.GetType().GetProperty("Hot")!.GetValue(snapshot)!;
        }

        Int32Rect AreaOf(Window bar) => (Int32Rect)Field(bar, "_area");

        string Describe(Int32Rect area) => $"{area.X},{area.Y} {area.Width}×{area.Height}";

        void MoveToBar(Window bar)
        {
            var hwnd = new WindowInteropHelper(bar).Handle;
            GetWindowRect(hwnd, out var rect);
            SetCursorPos((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        }

        // 历史里先摆两块在屏幕上的区域，再让面板停在第三块上
        var a = new Int32Rect(100, 100, 320, 240);
        var b = new Int32Rect(300, 200, 360, 260);
        var c = new Int32Rect(200, 160, 400, 300);
        Check((bool)append.Invoke(null, new object[] { a })!, "历史里记一块区域");
        Check((bool)append.Invoke(null, new object[] { b })!, "历史里再记一块区域");

        // 面板：摆着等开始的那种（gate != null，所以是 Idle）
        var plan = planType.GetMethod("FromValues")!.Invoke(null, new object[] { "all", "mp4", 30, 0, false, null })!;
        object controller = Activator.CreateInstance(controllerType)!;
        object gate = Activator.CreateInstance(gateType, AnyCtor | BindingFlags.NonPublic, null, null, null)!;
        var bar = (Window)Activator.CreateInstance(barType, AnyCtor, null,
            new object[] { c, plan, controller, gate }, null)!;

        try
        {
            bar.Show();
            Pump(bar);

            Check(WaitUntil(IsPublished, 2000), "面板一打开就把快捷键快照发布给钩子");

            var styleBar = (FrameworkElement)Field(bar, "StyleBar");
            var exportToggle = (ToggleButton)Field(bar, "_exportToggle");
            var tools = ((IEnumerable)Field(bar, "_toolButtons")).Cast<RadioButton>().ToList();

            // 翻过区域之后面板会挪位置（PlaceBar），所以每次按 R 之前都要把指针重新摆到它身上
            void RefreshHot()
            {
                MoveToBar(bar);
                PumpFor(40);
                publish.Invoke(bar, null);
            }

            bool ToolChecked(string tool) => tools.Any(t => t.Tag?.ToString() == tool && t.IsChecked == true);

            // ---- 指针停在面板上：V / 1 真的把工具点上 ----
            MoveToBar(bar);
            PumpFor(40);
            publish.Invoke(bar, null);
            Check(IsHot(), "指针在面板上时快照标成「热」");

            Check(Hit(VkV, true, false), "指针在面板上：V 被我们接住");
            Pump(bar);
            Check(ToolChecked("Select"), "V 真的把「选择」工具点上");
            Check(styleBar.Visibility != Visibility.Visible, "选中工具后导出设置收起（跟点按钮一致）");
            Check(Hit(VkV, false, true), "V 的抬起也一并吞掉（事件平衡）");

            // ---- 不是我们定义过的键：照常放行 ----
            Check(!Hit(VkF5, true, false), "F5 不在快捷键表里，不吞");
            Check(!Hit(VkF5, false, true), "F5 的抬起也不吞");

            // ---- 指针离开面板：一个键都不碰 ----
            SetCursorPos(2, 2);
            PumpFor(40);
            publish.Invoke(bar, null);
            Check(!IsHot(), "指针离开面板后快照标成「冷」");
            Check(!Hit(VkV, true, false), "指针不在面板上：V 放行（不会抢别处的键）");
            Check(!Hit(VkEsc, true, false), "指针不在面板上：Esc 也放行");
            Check(!(bool)gateType.GetProperty("Cancelled")!.GetValue(gate)!, "没被接住的 Esc 当然没关面板");

            // ---- 摆回导出设置那一行，R / Shift+R 翻区域历史 ----
            exportToggle.IsChecked = true;
            Pump(bar);
            RefreshHot();
            Check(IsHot(), "搬回面板上又是热的");

            var start = new Int32Rect(200, 160, 400, 300);
            Check(AreaOf(bar) == start, $"面板停在第三块区域（{Describe(AreaOf(bar))}）");

            Check(Hit(VkR, true, false), "R 被接住");
            Pump(bar);
            Check(AreaOf(bar) == b, $"R 翻到最新的历史区域（{Describe(AreaOf(bar))}）");
            Check(Hit(VkR, false, true), "R 的抬起一并吞掉");

            RefreshHot();
            Check(Hit(VkR, true, false), "再按一次 R");
            Pump(bar);
            Check(AreaOf(bar) == a, $"R 继续往回翻（{Describe(AreaOf(bar))}）");
            Hit(VkR, false, true);

            // Shift+R 要真的按住 Shift（钩子里的修饰键是问 GetAsyncKeyState 的）
            RefreshHot();
            keybd_event(VkShift, 0, 0, UIntPtr.Zero);
            try
            {
                Check(Hit(VkR, true, false), "Shift+R 被接住");
                Pump(bar);
                Check(AreaOf(bar) == b, $"Shift+R 往回翻到下一块（{Describe(AreaOf(bar))}）");
                Hit(VkR, false, true);
            }
            finally
            {
                keybd_event(VkShift, 0, 2, UIntPtr.Zero);   // KEYEVENTF_KEYUP
            }

            // 已经在最新那块：再按 Shift+R 没有更晚的（上游同样的边界）
            RefreshHot();
            var atLatest = AreaOf(bar);
            keybd_event(VkShift, 0, 0, UIntPtr.Zero);
            try
            {
                Hit(VkR, true, false);
                Pump(bar);
                Hit(VkR, false, true);
            }
            finally
            {
                keybd_event(VkShift, 0, 2, UIntPtr.Zero);
            }
            Check(AreaOf(bar) == atLatest, "翻到头之后区域不再变");

            // ---- Esc：按下吞掉、抬起才真的关 ----
            RefreshHot();
            Check(Hit(VkEsc, true, false), "Esc 按下被接住");
            Pump(bar);
            Check(!(bool)gateType.GetProperty("Cancelled")!.GetValue(gate)!, "Esc 按下时还没关面板（抬起才触发）");
            Check(Hit(VkEsc, false, true), "Esc 抬起被接住");
            Pump(bar);
            Check((bool)gateType.GetProperty("Cancelled")!.GetValue(gate)!, "Esc 抬起才真的关面板");
        }
        finally
        {
            try { bar.Close(); } catch { /* 已经关了 */ }
            PumpFor(80);
        }

        Check(!IsPublished(), "面板关掉之后钩子不再接管任何键");
        Check(!Hit(VkV, true, false), "面板关掉之后 V 放行");

        Check(((IEnumerable)entriesProperty.GetValue(null)!).Cast<Int32Rect>().Any(r => r == a),
            "刚录的区域进了历史");

        // 历史文件写回原样
        try
        {
            if (hadHistory) File.WriteAllBytes(historyPath, savedHistory);
            else if (File.Exists(historyPath)) File.Delete(historyPath);

            invalidate.Invoke(null, null);
        }
        catch (Exception ex)
        {
            Console.WriteLine("恢复区域历史失败：" + ex.Message);
        }

        // 还原成「未初始化」（让正式路径之外的代码保持原来的行为）
        try { initializeUi.Invoke(null, new object[] { null }); } catch { /* 无所谓 */ }

        SetCursorPos(960, 540);
    }
}
