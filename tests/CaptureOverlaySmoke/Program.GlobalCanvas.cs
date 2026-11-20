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
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 全屏画布（P8-a，照上游 <c>GlobalCanvasController</c>）的端到端冒烟：
/// 真窗口盖住显示器 → 真鼠标画一笔 → 选择 / 撤销 → 缩放 / 平移 → 激光拖尾 →
/// 点击穿透（真的点到下面的窗口 / 工具条仍可点 / 0.32 不透明度）→ 再按一次切回 →
/// 文字标注 → Esc 退出。
/// </summary>
/// <remarks>
/// 全部走产品代码（真 <see cref="GlobalCanvasWindow"/>、真鼠标键盘事件、真 WM_NCHITTEST），
/// 不伪造手势；只有「文件保存弹框」这类不可控入口不碰。
/// </remarks>
internal static partial class Program
{
    private const int WmNchittest = 0x0084;
    private const int HtTransparent = -1;

    private const uint MiddleDown = 0x0020;
    private const uint MiddleUp = 0x0040;
    private const uint WheelFlag = 0x0800;

    private const int GwlExstyle = -20;
    private const long WsExTopmost = 0x00000008;

    private const byte VkControl = 0x11;
    private const byte VkZ = 0x5A;
    private const byte VkY = 0x59;
    private const byte VkP = 0x50;
    private const byte VkEnter = 0x0D;

    private const uint SwpNomove = 0x0002;
    private const uint SwpNosize = 0x0001;
    private const uint SwpNoactivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageW(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    private static void GlobalCanvasSection()
    {
        var asm = typeof(GlobalCanvasWindow).Assembly;
        var toolType = asm.GetType("LTools.Features.CapturePro.CaptureTool")!;
        var catalogType = asm.GetType("LTools.Features.CapturePro.ToolCatalog")!;
        var sessionType = asm.GetType("LTools.Features.CapturePro.GlobalCanvasSession")!;
        var laserType = asm.GetType("LTools.Features.CapturePro.GlobalCanvasLaser")!;

        object Call(object target, string name, params object[] args) =>
            target.GetType().GetMethod(name, Private)!.Invoke(target, args);

        // 走真实入口：面板 / 快捷键 / 悬浮都调 GlobalCanvasSession.Activate()，
        // 那样「再按一次 = 切点击穿透」「关窗 = 会话归零」才是产品行为。
        var activate = sessionType.GetMethod("Activate",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        activate.Invoke(null, null);
        PumpFor(280);

        var window = (GlobalCanvasWindow)sessionType
            .GetField("_window", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(null)!;
        Pump(window);
        PumpFor(180);
        Check(window.IsVisible, "会话入口真的把画布打开了");

        // 注入的真键盘要求窗口在前台（前面的段关掉自己的窗口后，前台可能已经留给别的程序，
        // 这时 Activate() 会被前台锁拦掉；ForceForeground 与产品 WindowAction 同一套兜底）
        ForceForeground(window);
        PumpFor(140);
        Check(window.IsActive, "全屏画布拿到前台 / 键盘焦点（真键盘注入靠它）");

        // 输入法会把注入的字母吃掉（Key.ImeProcessed，中文模式下 P / V 都到不了窗口）；
        // 这一段只按字母键、文字直接写进 TextEntry，所以关掉输入法。
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(window, false);

        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var surface = (FrameworkElement)Field(window, "Surface");
            var toolbar = (FrameworkElement)Field(window, "Toolbar");
            var canvas = Field(window, "Canvas");
            var layer = (FrameworkElement)Field(window, "LaserLayer");
            var textEntry = (TextBox)Field(window, "TextEntry");
            var doc = Field(window, "_doc");

            object CanvasProp(string name) => canvas.GetType()
                .GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.GetValue(canvas)!;

            double scale = window.Width > 0 ? window.PointToScreen(new Point(1, 0)).X
                - window.PointToScreen(new Point(0, 0)).X : 1;
            if (scale <= 0) scale = 1;

            // ============ 1) 窗口盖住光标所在显示器，物理像素对齐 ============
            var monitor = (System.Drawing.Rectangle)asm.GetType("LTools.Views.ScreenPicker")!
                .GetMethod("MonitorUnderCursor", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, null)!;

            var rect = WindowBounds(window);
            Check(Math.Abs(rect.Left - monitor.Left) <= 1 && Math.Abs(rect.Top - monitor.Top) <= 1 &&
                  Math.Abs(rect.Right - monitor.Right) <= 1 && Math.Abs(rect.Bottom - monitor.Bottom) <= 1,
                $"画布正好盖住光标所在显示器：{rect.Left},{rect.Top} {rect.Right - rect.Left}×{rect.Bottom - rect.Top} " +
                $"（显示器 {monitor.Left},{monitor.Top} {monitor.Width}×{monitor.Height}）");

            long ex = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
            Check((ex & WsExTopmost) != 0, "画布置顶（WS_EX_TOPMOST）");
            Check(window.Topmost, "WPF 侧也是 Topmost");

            // ============ 2) 画布是「透明表面」：没有底图、没有选区压暗 ============
            Check(!(bool)CanvasProp("ShowBase"), "不画底图（ShowBase = false）");
            Check(!(bool)CanvasProp("ShowSelectionChrome"), "不画选区压暗 / 边框（ShowSelectionChrome = false）");
            Check(CanvasProp("Document") != null, "画布接了文档");
            Check(!((UIElement)canvas).IsHitTestVisible, "画布自己不接鼠标（手势走 Surface）");

            int markCount = ((ICollection)doc.GetType().GetProperty("Marks")!.GetValue(doc)!).Count;
            Check(markCount == 0, $"刚打开是空画布（{markCount} 条标注）");

            var overrideRect = (Rect?)CanvasProp("CanvasOverride");
            Check(overrideRect is { } o && o.Width > 10 && o.Height > 10,
                $"画布范围交给视图矩阵（{overrideRect}）");

            // ============ 3) 工具条：右下各留 15、只有不依赖底图的工具 ============
            Check(toolbar.Visibility == Visibility.Visible, "工具条可见");
            double tbLeft = Canvas.GetLeft(toolbar);
            double tbTop = Canvas.GetTop(toolbar);
            double rightGap = surface.ActualWidth - (tbLeft + toolbar.ActualWidth);
            double bottomGap = surface.ActualHeight - (tbTop + toolbar.ActualHeight);
            Check(Math.Abs(rightGap - 15) < 3 && Math.Abs(bottomGap - 15) < 3,
                $"工具条贴右下各留 15（右 {rightGap:0.#} / 下 {bottomGap:0.#}）");
            Check(Math.Abs(toolbar.Opacity - 1.0) < 0.01, "编辑态工具条不透明");

            var tools = (Array)catalogType.GetField("GlobalCanvasTools",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
            var toolNames = tools.Cast<object>().Select(t => t.ToString()).ToList();
            foreach (string needed in new[] { "Select", "Shape", "Arrow", "Pen", "Text", "Serial", "Watermark", "Eraser" })
                Check(toolNames.Contains(needed), $"全屏画布工具表含「{needed}」");
            foreach (string banned in new[] { "Magnifier", "Mosaic", "Filter", "BrushFilter", "AutoFilter", "RectEraser", "BrushEraser" })
                Check(!toolNames.Contains(banned), $"全屏画布不提供依赖底图的「{banned}」");

            var supports = catalogType.GetMethod("SupportsGlobalCanvas",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
            Check((bool)supports.Invoke(null, new[] { Enum.Parse(toolType, "Magnifier") })! == false,
                "SupportsGlobalCanvas(放大镜) = false");
            Check((bool)supports.Invoke(null, new[] { Enum.Parse(toolType, "Pen") })! == true,
                "SupportsGlobalCanvas(画笔) = true");

            // 工具条按「格子」摆（同一格可以塞多个工具），所以格子数 ≤ 工具数；
            // 这里核实：每格里都是全屏画布认识的工具，且常用的那几个真的在。
            var cells = (IList)Field(toolbar, "_cells");
            var cellTools = new List<string>();
            foreach (var cell in cells)
            {
                var set = cell!.GetType()
                    .GetProperty("Tools", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(cell) as IEnumerable;
                if (set != null) cellTools.AddRange(set.Cast<object>().Select(t => t.ToString()));
            }
            Check(cells.Count > 6, $"工具条建出了格子（{cells.Count} 格）");
            Check(cellTools.All(toolNames.Contains), "每格里都是全屏画布认识的工具");
            foreach (string needed in new[] { "Select", "Pen", "Text", "Serial", "Watermark" })
                Check(cellTools.Contains(needed), $"工具条上能点到「{needed}」");

            // ============ 4) 真鼠标画一笔（画笔） ============
            Call(window, "SetTool", Enum.Parse(toolType, "Pen"));
            PumpFor(120);

            var start = window.PointToScreen(new Point(300, 300));
            var end = window.PointToScreen(new Point(700, 300));
            SetCursorPos((int)Math.Round(start.X), (int)Math.Round(start.Y));
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(60);
            for (int i = 1; i <= 8; i++)
            {
                SetCursorPos((int)Math.Round(start.X + (end.X - start.X) * i / 8.0),
                    (int)Math.Round(start.Y + (end.Y - start.Y) * i / 8.0));
                PumpFor(45);
            }
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(200);

            var marks = (ICollection)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
            Check(marks.Count == 1, $"真鼠标拖一下落一条标注（{marks.Count}）");
            if (marks.Count == 1)
            {
                object mark = ((IList)marks)[0]!;
                Check(mark.GetType().Name == "Stroke", $"落的是手绘笔画（{mark.GetType().Name}）");
                var points = (ICollection)mark.GetType().GetProperty("Points")!.GetValue(mark)!;
                Check(points.Count >= 4, $"笔画有多个采样点（{points.Count}）");
            }

            // ============ 5) 工具字母键（照上游默认：P = 画笔 / V = 选择） ============
            TapKey(VkP);
            PumpFor(140);
            Check(Field(window, "_tool").ToString() == "Pen", $"按 P 切到画笔（{Field(window, "_tool")}）");

            TapKey(VkV);
            PumpFor(140);
            Check(Field(window, "_tool").ToString() == "Select", $"按 V 切到选择（{Field(window, "_tool")}）");

            // ============ 6) 点选 / Ctrl+Z / Ctrl+Y ============
            var hitPoint = window.PointToScreen(new Point(500, 300));
            SetCursorPos((int)Math.Round(hitPoint.X), (int)Math.Round(hitPoint.Y));
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(60);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(180);
            Check((int)Field(window, "_selectedIndex") == 0,
                $"点一下命中那条笔画（选中 {Field(window, "_selectedIndex")}）");
            Check((bool)CanvasProp("ShowHandles"), "单选时显示八个把手");

            Chord(VkControl, VkZ);
            PumpFor(180);
            Check(((ICollection)doc.GetType().GetProperty("Marks")!.GetValue(doc)!).Count == 0, "Ctrl+Z 撤销掉这笔");
            Chord(VkControl, VkY);
            PumpFor(180);
            Check(((ICollection)doc.GetType().GetProperty("Marks")!.GetValue(doc)!).Count == 1, "Ctrl+Y 重做回来");

            // ============ 7) Ctrl+滚轮缩放：夹 0.1–8 ============
            var center = window.PointToScreen(new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
            SetCursorPos((int)Math.Round(center.X), (int)Math.Round(center.Y));
            PumpFor(90);

            keybd_event(VkControl, 0, 0, UIntPtr.Zero);
            for (int i = 0; i < 12; i++)
            {
                mouse_event(WheelFlag, 0, 0, 120, UIntPtr.Zero);
                PumpFor(16);
            }
            PumpFor(120);
            double zoomIn = (double)Field(window, "_zoom");
            Check(zoomIn > 1.5, $"Ctrl+滚轮放大（{zoomIn:0.##}）");
            Check(zoomIn <= 8.0 + 1e-9, $"放大不超过 8（{zoomIn:0.##}）");

            for (int i = 0; i < 120; i++)
            {
                mouse_event(WheelFlag, 0, 0, 120, UIntPtr.Zero);
                PumpFor(4);
            }
            PumpFor(140);
            Check(Math.Abs((double)Field(window, "_zoom") - 8.0) < 1e-6, $"一直放大夹在上限 8（{Field(window, "_zoom")}）");

            for (int i = 0; i < 260; i++)
            {
                mouse_event(WheelFlag, 0, 0, unchecked((uint)-120), UIntPtr.Zero);
                PumpFor(4);
            }
            PumpFor(140);
            Check(Math.Abs((double)Field(window, "_zoom") - 0.1) < 1e-6, $"一直缩小夹在下限 0.1（{Field(window, "_zoom")}）");
            keybd_event(VkControl, 0, 2, UIntPtr.Zero);
            PumpFor(80);

            // 回到 1:1（真实缩放只验夹取，逐像素的锚点对账走 selftest）
            SetField(window.GetType(), window, "_zoom", 1.0);
            Call(window, "UpdateView");
            PumpFor(120);

            // ============ 8) 中键拖画面 ============
            var before = (Matrix)CanvasProp("View");
            SetCursorPos((int)Math.Round(center.X), (int)Math.Round(center.Y));
            PumpFor(90);
            mouse_event(MiddleDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(60);
            SetCursorPos((int)Math.Round(center.X) + 90, (int)Math.Round(center.Y));
            PumpFor(70);
            SetCursorPos((int)Math.Round(center.X) + 180, (int)Math.Round(center.Y));
            PumpFor(70);
            mouse_event(MiddleUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(160);
            var afterPan = (Matrix)CanvasProp("View");
            Check(Math.Abs(afterPan.M11 - before.M11) < 1e-9, "中键平移不改缩放");
            Check(Math.Abs(afterPan.OffsetX - before.OffsetX) > 10, $"中键平移真的挪了画面（Δx {afterPan.OffsetX - before.OffsetX:0.#}）");

            // 回正，后面的点击按屏幕坐标算
            SetField(window.GetType(), window, "_center",
                new Point(surface.ActualWidth / 2, surface.ActualHeight / 2));
            Call(window, "UpdateView");
            PumpFor(120);

            // ============ 9) 激光笔拖尾（真指针，500ms 红细线） ============
            var laserToggle = ToolRow(window)!.Children.OfType<ToggleButton>()
                .First(b => (b.ToolTip?.ToString() ?? "").Contains("激光笔"));
            laserToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(160);
            Check((bool)Field(window, "_laserOn"), "工具条的激光笔按钮真的开了");
            Check(laserToggle.IsChecked == true, "按钮自己显示为按下");
            Check(laserType.GetField("Ink",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(null) is Color ink &&
                  ink.R == 255 && ink.G == 0 && ink.B == 0, "拖尾是纯红");

            var laserA = window.PointToScreen(new Point(260, 520));
            var laserB = window.PointToScreen(new Point(900, 620));
            for (int i = 0; i <= 10; i++)
            {
                SetCursorPos((int)Math.Round(laserA.X + (laserB.X - laserA.X) * i / 10.0),
                    (int)Math.Round(laserA.Y + (laserB.Y - laserA.Y) * i / 10.0));
                PumpFor(40);
            }
            PumpFor(160);

            var trail = layer.GetType().GetProperty("Trail")!.GetValue(layer);
            Check(trail != null, "激光层拿到了拖尾模型");
            if (trail != null)
            {
                var segments = (ICollection)trail.GetType()
                    .GetMethod("Segments", new[] { typeof(long) })!
                    .Invoke(trail, new object[] { Environment.TickCount64 })!;
                Check(segments.Count > 0, $"指针走过之后有可见线段（{segments.Count}）");
                int points = (int)trail.GetType().GetProperty("Count")!.GetValue(trail)!;
                Check(points >= 2, $"采样点数够画线（{points}）");
            }

            laserToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(160);
            Check(!(bool)Field(window, "_laserOn"), "再点一下关掉激光笔");
            Check(layer.GetType().GetProperty("Trail")!.GetValue(layer) == null, "关掉后激光层清空");

            // ============ 10) 点击穿透 ============
            var throughToggle = ToolRow(window)!.Children.OfType<ToggleButton>()
                .First(b => (b.ToolTip?.ToString() ?? "").Contains("点击穿透"));

            // 光标先停到画布中央（不在工具条上）
            SetCursorPos((int)Math.Round(center.X), (int)Math.Round(center.Y));
            PumpFor(120);

            throughToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            PumpFor(240);
            Check((bool)Field(window, "_clickThrough"), "工具条的「点击穿透」按钮真的切了");
            Check(throughToggle.IsChecked == true, "按钮自己显示为按下");

            long throughEx = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
            Check((throughEx & 0x08000000) != 0, "穿透期间补上 WS_EX_NOACTIVATE（点画布不抢焦点）");

            Check(Math.Abs(toolbar.Opacity - 0.32) < 0.01,
                $"穿透且指针不在工具条上时工具条压到 0.32（{toolbar.Opacity:0.##}）");

            // WM_NCHITTEST：画布中央 → HTTRANSPARENT（点到下面的程序）
            var hitCenter = HitTest(hwnd, (int)Math.Round(center.X), (int)Math.Round(center.Y));
            Check(hitCenter == HtTransparent, $"画布中央的命中测试返回 HTTRANSPARENT（{hitCenter}）");

            // 工具条上 → 照常吃鼠标
            var tbScreen = toolbar.PointToScreen(new Point(toolbar.ActualWidth / 2, toolbar.ActualHeight / 2));
            int hitToolbar = HitTest(hwnd, (int)Math.Round(tbScreen.X), (int)Math.Round(tbScreen.Y));
            Check(hitToolbar != HtTransparent, $"工具条上仍可点（命中 {hitToolbar}）");

            // 真的点下去：画布下面那个窗口收到点击
            int probeClicks = 0;
            var probe = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Background = Brushes.DarkSlateBlue,
                Width = 240,
                Height = 160,
                WindowStartupLocation = WindowStartupLocation.Manual,
            };
            probe.Left = 180;
            probe.Top = 180;
            probe.MouseLeftButtonDown += (_, _) => probeClicks++;
            probe.Show();
            PumpFor(200);

            // 画布在它上面（都是 Topmost，重新提一次保证 z 序）
            SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNomove | SwpNosize | SwpNoactivate);
            PumpFor(200);

            // PointToScreen 要的是「窗口内坐标」，别再叠一次 Left/Top
            var probeScreen = probe.PointToScreen(new Point(probe.Width / 2, probe.Height / 2));
            SetCursorPos((int)Math.Round(probeScreen.X), (int)Math.Round(probeScreen.Y));
            PumpFor(120);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(80);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(220);
            Check(probeClicks > 0, $"穿透时点击真的落到下面的窗口（{probeClicks} 次）");
            Check(!window.IsActive, "穿透时不抢前台（画布不是活动窗口）");

            probe.Close();
            PumpFor(160);

            // 指针回到工具条上 → 不透明度恢复 1.0
            SetCursorPos((int)Math.Round(tbScreen.X), (int)Math.Round(tbScreen.Y));
            PumpFor(220);
            Check(Math.Abs(toolbar.Opacity - 1.0) < 0.01,
                $"指针停到工具条上恢复 1.0（{toolbar.Opacity:0.##}）");

            // 再按一次「打开全屏画布」= 切回非穿透（走 GlobalCanvasSession，和热键同一条路）
            sessionType.GetMethod("Activate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(null, null);
            PumpFor(240);
            Check(!(bool)Field(window, "_clickThrough"), "再触发一次会话入口 = 切回非穿透");
            long backEx = GetWindowLongPtr(hwnd, GwlExstyle).ToInt64();
            Check((backEx & 0x08000000) == 0, "切回后去掉 WS_EX_NOACTIVATE");
            Check(HitTest(hwnd, (int)Math.Round(center.X), (int)Math.Round(center.Y)) != HtTransparent,
                "切回后画布照常吃鼠标");

            // ============ 11) 文字工具：就地输入 → Enter 落地 ============
            Call(window, "SetTool", Enum.Parse(toolType, "Text"));
            PumpFor(140);

            var textPoint = window.PointToScreen(new Point(420, 560));
            SetCursorPos((int)Math.Round(textPoint.X), (int)Math.Round(textPoint.Y));
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(60);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(220);

            Check(textEntry.Visibility == Visibility.Visible, "单击弹出就地输入框");
            textEntry.Text = "全屏画布冒烟";
            TapKey(VkEnter);
            PumpFor(220);

            Check(textEntry.Visibility != Visibility.Visible, "Enter 后收起输入框");
            var afterText = (ICollection)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
            Check(afterText.Count == 2, $"文字落地成一条标注（{afterText.Count}）");
            if (afterText.Count == 2)
            {
                object last = ((IList)afterText)[1]!;
                Check(last.GetType().Name == "Text", $"落的是文字（{last.GetType().Name}）");
            }

            // ============ 12) 序号工具：点一下 +1 ============
            Call(window, "SetTool", Enum.Parse(toolType, "Serial"));
            PumpFor(120);
            var serialPoint = window.PointToScreen(new Point(840, 420));
            SetCursorPos((int)Math.Round(serialPoint.X), (int)Math.Round(serialPoint.Y));
            PumpFor(110);
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(60);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(200);
            var afterSerial = (ICollection)doc.GetType().GetProperty("Marks")!.GetValue(doc)!;
            Check(afterSerial.Count == 3, $"序号点一下落一条（{afterSerial.Count}）");
            if (afterSerial.Count == 3)
                Check(((IList)afterSerial)[2]!.GetType().Name == "Serial",
                    $"落的是序号（{((IList)afterSerial)[2]!.GetType().Name}）");

            // 参数行出现 / 收起时工具条仍锚在右下角（别长出屏幕下边缘被裁掉）
            double gapRight = surface.ActualWidth - (Canvas.GetLeft(toolbar) + toolbar.ActualWidth);
            double gapBottom = surface.ActualHeight - (Canvas.GetTop(toolbar) + toolbar.ActualHeight);
            Check(Math.Abs(gapRight - 15) < 3 && Math.Abs(gapBottom - 15) < 3,
                $"切到带参数行的工具后工具条仍贴右下（右 {gapRight:0.#} / 下 {gapBottom:0.#}，高 {toolbar.ActualHeight:0.#}）");

            // 留下一张给人看的图：画布上那几笔 / 文字 / 序号 + 右下工具条
            var root = (FrameworkElement)Field(window, "Root");
            var shot = new RenderTargetBitmap(
                (int)Math.Round(surface.ActualWidth * scale), (int)Math.Round(surface.ActualHeight * scale),
                96 * scale, 96 * scale, PixelFormats.Pbgra32);
            shot.Render(root);
            Save(shot, Path.Combine(_outDir, "globalcanvas.png"));

            // ============ 12b) 取色：选中即取色 → 色卡跟指针 → 单击复制 + 切回上一个工具 ============
            Call(window, "SetTool", Enum.Parse(toolType, "Pen"));
            PumpFor(120);
            Call(window, "SetTool", Enum.Parse(toolType, "Picker"));
            PumpFor(420);
            Check((bool)Field(window, "_pickerSampling"), "选中「取色」就进入取色（先抓了一次屏幕）");

            var chip = (FrameworkElement)Field(window, "PickerChip");
            var chipText = (TextBlock)Field(window, "PickerText");
            var swatch = (Border)Field(window, "PickerSwatch");
            var sample = window.PointToScreen(new Point(640, 300));
            SetCursorPos((int)Math.Round(sample.X), (int)Math.Round(sample.Y));
            PumpFor(240);

            Check(chip.Visibility == Visibility.Visible, "取色时指针旁有色卡");
            string shown = chipText.Text;
            Check(System.Text.RegularExpressions.Regex.IsMatch(shown, @"^#[0-9A-F]{6} · rgb\(\d+, \d+, \d+\)$"),
                $"色卡写着十六进制与分量（{shown}）");
            Check(swatch.Background is SolidColorBrush sb && $"#{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}" == shown.Substring(0, 7),
                "色块就是取到的那一色");

            Clipboard.Clear();
            mouse_event(LeftDown, 0, 0, 0, UIntPtr.Zero);
            PumpFor(80);
            mouse_event(LeftUp, 0, 0, 0, UIntPtr.Zero);
            PumpFor(260);

            Check(!(bool)Field(window, "_pickerSampling"), "单击之后退出取色");
            Check(WaitUntil(() =>
            {
                try { return Clipboard.ContainsText() && Clipboard.GetText() == shown.Substring(0, 7); }
                catch { return false; }
            }, 2000), $"取色把 {shown.Substring(0, 7)} 复制进剪贴板");
            Check(Field(window, "_tool").ToString() == "Pen", $"取完色切回上一个绘图工具（{Field(window, "_tool")}）");

            // ============ 13) Esc 退出（真键），会话回到关闭 ============
            SetCursorPos((int)Math.Round(center.X), (int)Math.Round(center.Y));
            window.Activate();
            surface.Focus();
            PumpFor(180);
            TapKey(VkEsc);
            PumpFor(320);

            Check(!window.IsVisible || !window.IsLoaded, "Esc 关掉全屏画布");
            Check((bool)sessionType.GetProperty("IsOpen",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(null)! == false, "会话回到关闭（再按热键会重新打开）");

            // ============ 14) 内置功能目录里有这条入口 ============
            var catalog = asm.GetType("LTools.Features.Builtin.FeatureCatalog")!;
            var defs = (IEnumerable)catalog.GetField("Defs",
                BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            bool found = false;
            foreach (var def in defs)
            {
                var id = def!.GetType().GetProperty("Id")!.GetValue(def)?.ToString();
                if (id == "ltools.globalCanvas") found = true;
            }
            Check(found, "内置功能目录里有「全屏画布」（ltools.globalCanvas）");
            Check((string)sessionType.GetField("HotkeyRef",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                    .GetValue(null)! == "ltools.globalCanvas", "热键引用与功能 Id 一致");
        }
        finally
        {
            try { if (window.IsVisible) window.Close(); } catch { }
        }
    }

    /// <summary>真鼠标事件的一次按键（按下 + 抬起）。</summary>
    private static void TapKey(ushort vk)
    {
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        PumpFor(60);
        keybd_event((byte)vk, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        PumpFor(60);
    }

    /// <summary>按下 Ctrl（按住）→ 敲一个键 → 松开 Ctrl。</summary>
    private static void Chord(byte modifier, ushort vk)
    {
        keybd_event(modifier, 0, 0, UIntPtr.Zero);
        PumpFor(40);
        keybd_event((byte)vk, 0, 0, UIntPtr.Zero);
        PumpFor(50);
        keybd_event((byte)vk, 0, 2, UIntPtr.Zero);
        PumpFor(40);
        keybd_event(modifier, 0, 2, UIntPtr.Zero);
        PumpFor(60);
    }

    /// <summary>给窗口发一次 WM_NCHITTEST（屏幕物理像素），返回命中码。</summary>
    private static int HitTest(IntPtr hwnd, int screenX, int screenY)
    {
        long packed = ((long)(screenY & 0xFFFF) << 16) | (uint)(screenX & 0xFFFF);
        return SendMessageW(hwnd, WmNchittest, IntPtr.Zero, new IntPtr(packed)).ToInt32();
    }
}
