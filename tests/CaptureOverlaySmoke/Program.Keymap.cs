using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 覆盖层内快捷键（P9-f）：四张表的默认值、真窗口里的录入 / 清空 / 恢复默认、落盘，
/// 以及贴图窗口「指针停上去 → 全局钩子派发」那条路。
/// </summary>
/// <remarks>
/// 配置写进临时目录（跑完还原），别动开发机的 <c>configs/settings.json</c>。
/// 明细规则（每条默认值、解析 / 归一化 / 匹配的边界）在 <c>--selftest</c> 的
/// 「截图快捷键」用例里，这里只验「真窗口 + 真按键 + 真落盘」。
/// </remarks>
internal static partial class Program
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.Public | BindingFlags.NonPublic;

    private static void KeymapSection()
    {
        Console.WriteLine("---- 覆盖层内快捷键（P9-f）----");

        var asm = typeof(CaptureOverlayWindow).Assembly;

        // 钩子那条路把动作排回「UI 线程」（Ui.Post）。冒烟本来就在 UI 线程上跑，
        // 初始化之后 Ui.Post 会就地执行，断言能立刻看到效果。
        asm.GetType("LTools.Core.Ui")!
            .GetMethod("Initialize", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, new object[] { System.Windows.Threading.Dispatcher.CurrentDispatcher });

        var tableType = asm.GetType("LTools.Features.CapturePro.CaptureShortcutTable")!;
        var scopeType = asm.GetType("LTools.Features.CapturePro.CaptureShortcutScope")!;
        var configStoreType = asm.GetType("LTools.Features.ConfigStore")!;

        // ---- 1) 表本身：四组条目数与几条关键默认值 ----
        object Scope(string name) => Enum.Parse(scopeType, name);
        var screenshot = Scope("Screenshot");
        var drawing = Scope("Drawing");
        var pin = Scope("PinToScreen");
        var recording = Scope("Recording");

        int CountOf(object scope) => ((IEnumerable)tableType.GetMethod("Of", Any)!
            .Invoke(null, new[] { scope })!).Cast<object>().Count();

        int shots = CountOf(screenshot), draws = CountOf(drawing);
        int pins = CountOf(pin), recs = CountOf(recording);
        Check(shots == 36, $"覆盖层 36 项（实际 {shots}；upload_to_cloud 不注册）");
        Check(draws == 26, $"绘图 26 项（实际 {draws}）");
        Check(pins == 24, $"贴图 24 项（实际 {pins}；上游 39 项，没有对应能力的 15 项不注册）");
        Check(recs == 6, $"录屏 6 项（实际 {recs}）");

        string Describe(object scope, string id) =>
            (string)tableType.GetMethod("Describe", Any)!.Invoke(null, new[] { scope, id })!;

        Check(Describe(screenshot, "copy_to_clipboard") == "Ctrl+C / Enter", "复制 = Ctrl+C / Enter");
        Check(Describe(screenshot, "move_cursor_up") == "W / Up", "光标上移 = W / Up");
        Check(Describe(screenshot, "keep_selection_width_and_height_consistent") == "Shift",
            "锁正方形 = 裸 Shift");
        Check(Describe(screenshot, "toggle_guides") == "Alt", "参考线 = 裸 Alt");
        Check(Describe(recording, "export") == "Ctrl+E", "录屏导出 = Ctrl+E");
        Check(Describe(drawing, "reset_canvas") == "Ctrl+Shift+Z", "清空画布 = Ctrl+Shift+Z");

        // ---- 2) 配置写进临时目录 ----
        var configPathProp = configStoreType.GetProperty("ConfigPath")!;
        var configPathSet = configPathProp.GetSetMethod(true)!;
        string oldConfigPath = (string)configPathProp.GetValue(null)!;
        string root = Path.Combine(_outDir, "keymap-config");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(Path.Combine(root, "configs"));
        string anchor = Path.Combine(root, "LTools.json");
        configPathSet.Invoke(null, new object[] { anchor });
        tableType.GetMethod("Invalidate", Any)!.Invoke(null, null);

        Window window = null;
        try
        {
            // ---- 3) 真窗口：四页签 + 行数 ----
            window = new CaptureKeymapWindow();
            window.Show();
            window.Activate();
            Pump(window);

            var tabs = (TabControl)window.FindName("Tabs")!;
            Check(tabs.Items.Count == 4, $"设置窗四个页签（实际 {tabs.Items.Count}）");

            StackPanel Host(string name) => (StackPanel)window.FindName(name)!;

            Check(Host("ScreenshotHost").Children.Count == shots, "覆盖层页每项一行");
            Check(Host("DrawingHost").Children.Count == draws, "绘图页每项一行");
            Check(Host("PinHost").Children.Count == pins, "贴图页每项一行");
            Check(Host("RecordingHost").Children.Count == recs, "录屏页每项一行");

            // 第一行 = move_tool（默认 M / Ctrl+E）：两个格子都显示出来
            var firstRow = (Grid)Host("ScreenshotHost").Children[0];
            var chips = (StackPanel)firstRow.Children[1];
            var firstChip = (TextBox)chips.Children[0];
            var secondChip = (TextBox)chips.Children[1];
            Check(firstChip.Text == "M" && secondChip.Text == "Ctrl+E",
                $"move_tool 回显 M / Ctrl+E（实际 {firstChip.Text} / {secondChip.Text}）");

            // ---- 4) 真录入：点第二个格子，按 F9 ----
            var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent,
            };
            secondChip.RaiseEvent(down);
            Pump(window);

            secondChip.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(secondChip)!, 0, Key.F9)
            {
                RoutedEvent = Keyboard.PreviewKeyDownEvent,
            });
            Pump(window);

            var chords = ((IEnumerable)tableType.GetMethod("Chords", Any)!
                    .Invoke(null, new[] { screenshot, "move_tool" })!)
                .Cast<string>().ToList();
            Check(chords.Count == 2 && chords[0] == "M" && chords[1] == "F9",
                $"录到 F9 后是 M / F9（实际 {string.Join(" / ", chords)}）");

            string settingsPath = Path.Combine(root, "configs", "settings.json");
            string json = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
            Check(json.Contains("\"move_tool\"") && json.Contains("F9"), "改动落进 settings.json");

            // ---- 5) 清空 / 恢复默认 ----
            var clear = (Button)chips.Children[2];
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            chords = ((IEnumerable)tableType.GetMethod("Chords", Any)!
                    .Invoke(null, new[] { screenshot, "move_tool" })!)
                .Cast<string>().ToList();
            Check(chords.Count == 0, "清空后没有快捷键（不是回落默认）");

            var reset = (Button)chips.Children[3];
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(window);
            chords = ((IEnumerable)tableType.GetMethod("Chords", Any)!
                    .Invoke(null, new[] { screenshot, "move_tool" })!)
                .Cast<string>().ToList();
            Check(chords.Count == 2 && chords[0] == "M" && chords[1] == "Ctrl+E", "恢复默认回到 M / Ctrl+E");

            int custom = (int)tableType.GetMethod("CustomCount", Any)!
                .Invoke(null, new[] { screenshot })!;
            Check(custom == 0, $"恢复默认后不算改过（实际 {custom}）");

            window.Close();
            window = null;

            // ---- 6) 贴图窗口：真窗口 + 光标停上去 + 全局钩子那条路真跑一遍 ----
            PinShortcutSection(asm);
        }
        finally
        {
            window?.Close();
            configPathSet.Invoke(null, new object[] { oldConfigPath });
            tableType.GetMethod("Invalidate", Any)!.Invoke(null, null);
        }
    }

    /// <summary>
    /// 贴图窗口的快捷键（<c>pin_to_screen_shortcuts/*</c>）：表 + 真窗口 + 光标停上去之后
    /// 由全局钩子那条路真派发一次。
    /// </summary>
    private static void PinShortcutSection(Assembly asm)
    {
        var pinType = asm.GetType("LTools.Features.CapturePro.PinShortcuts")!;
        var actionType = asm.GetType("LTools.Features.CapturePro.PinShortcutAction")!;
        var routeType = asm.GetType("LTools.Features.CapturePro.PinShortcutRoute")!;
        var tableType = asm.GetType("LTools.Features.CapturePro.CaptureShortcutTable")!;
        var pinScope = Enum.Parse(asm.GetType("LTools.Features.CapturePro.CaptureShortcutScope")!, "PinToScreen");

        string Describe(string id) =>
            (string)tableType.GetMethod("Describe", Any)!.Invoke(null, new[] { pinScope, id })!;

        var entries = ((IEnumerable)pinType.GetField("All", Any)!.GetValue(null)!).Cast<object>().ToList();
        Check(entries.Count == 24, $"贴图表 24 项（实际 {entries.Count}）");

        object Match(ushort vk, bool ctrl, bool shift, bool alt, bool win) =>
            pinType.GetMethod("Match", Any)!.Invoke(null, new object[] { vk, ctrl, shift, alt, win })!;

        Check(Match(0x43, true, false, false, false).ToString() == "Copy", "Ctrl+C = 复制");
        Check(Match(0x43, true, true, false, false).ToString() == "CopyOriginal", "Ctrl+Shift+C = 复制原文");
        Check(Match(0x42, false, false, false, false).ToString() == "ShowBorder", "B = 显示边框");
        Check(Match(0x1B, false, false, false, false).ToString() == "Close", "Esc = 关闭");
        Check(Match(0x41, false, false, false, false).ToString() == "MoveLeft", "A = 左移");
        Check(Match(0x51, true, false, false, false).ToString() == "None", "Ctrl+Q 没有动作");

        string Chords(string action) =>
            (string)pinType.GetMethod("ChordsOf", Any)!.Invoke(null, new[] { Enum.Parse(actionType, action) })!;
        Check(Chords("Copy") == "Ctrl+C", "ChordsOf 读到 Ctrl+C");
        Check(Chords("MoveUp") == "W / Up", "上移 = W / Up");

        // 上游有、LTools 贴图还没有的能力不进表（否则就是「按了没反应」的假条目）
        Check(Describe("rotate_clockwise").Length == 0, "旋转没有注册");
        Check(Describe("print").Length == 0, "打印没有注册");
        Check(Describe("destroy_window").Length == 0, "销毁没有注册");

        Check((int)routeType.GetProperty("Count", Any)!.GetValue(null)! == 0, "还没贴图时路由是空的");

        var bitmap = new RenderTargetBitmap(120, 80, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(Brushes.SteelBlue, null, new Rect(0, 0, 120, 80));
        bitmap.Render(visual);

        var pinWindow = (CapturePinWindow)Activator.CreateInstance(typeof(CapturePinWindow),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { bitmap, new Int32Rect(240, 240, 120, 80), 1.0, 1.0 }, null)!;

        try
        {
            pinWindow.Show();
            Pump(pinWindow);
            Check((int)routeType.GetProperty("Count", Any)!.GetValue(null)! >= 1, "贴图开好就登记进路由");

            bool Flag(string name) => (bool)pinWindow.GetType().GetField(name, Any)!.GetValue(pinWindow)!;

            // 光标挪到贴图正中：钩子只认「点过这张贴图、指针还停在它上面」
            var center = pinWindow.PointToScreen(new Point(pinWindow.ActualWidth / 2, pinWindow.ActualHeight / 2));
            var handle = routeType.GetMethod("Handle", Any)!;
            bool Handle(ushort vk, bool down) =>
                (bool)handle.Invoke(null, new object[] { vk, down, !down })!;

            SetCursorPos((int)center.X, (int)center.Y);
            Pump(pinWindow);

            // 还没点过：不接管（否则鼠标停在贴图上打字会被吞键）
            Check(!Handle(0x42, down: true), "没点过贴图时不接管按键");

            // 真点一下：贴图不抢焦点，靠这一下把键盘交给它
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            Pump(pinWindow);
            Check((bool)routeType.GetMethod("IsEngaged", Any)!.Invoke(null, new object[] { Member(pinWindow, "_hwnd") })!,
                "点一下之后键盘交给这张贴图");

            bool borderBefore = Flag("_showBorder");
            bool handled = Handle(0x42, down: true);          // B = 显示边框
            Pump(pinWindow);
            Check(handled, "点过贴图后 B 被接管");
            Check(Flag("_showBorder") != borderBefore, "B 真的切了显示边框");

            // 指针不在贴图上：一个键都不碰
            SetCursorPos(0, 0);
            Check(!Handle(0x42, down: true), "指针不在贴图上时 B 不接管");
            SetCursorPos((int)center.X, (int)center.Y);

            // Esc = 关掉这张贴图，并把自己从路由里摘掉
            Check(Handle(0x1B, down: true), "Esc 被接管");
            Pump(pinWindow);
            Check(!pinWindow.IsVisible, "Esc 关掉了贴图");
            Check((int)routeType.GetProperty("Count", Any)!.GetValue(null)! == 0, "关掉之后路由空了");
        }
        finally
        {
            if (pinWindow.IsVisible) pinWindow.Close();
            routeType.GetMethod("Clear", Any)!.Invoke(null, null);
        }
    }
}
