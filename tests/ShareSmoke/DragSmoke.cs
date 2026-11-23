using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShareSmoke;

/// <summary>
/// 真鼠标的拖放冒烟。两段：
/// <list type="bullet">
/// <item><c>Run</c>：普通窗口当对照组 + 共享窗口，源窗口用 <c>DoDragDrop</c>（OLE）。</item>
/// <item><c>RunExplorer</c>：真的开资源管理器，把文件图标拖进共享窗口（提权时只有 WM_DROPFILES 能到）。</item>
/// </list>
/// </summary>
/// <remarks>
/// 直接调 <c>Window_PreviewDrop</c> 只能证明「处理函数会加文件」，证明不了拖放目标真的注册上了
/// —— 上一版就漏在这里。断言用 <c>AddHandler(..., handledEventsToo: true)</c>，否则产品自己的
/// 处理器先 <c>e.Handled = true</c>，这里就永远数到 0（踩过）。
/// </remarks>
internal static class DragSmoke
{
    public static void Run(Action<bool, string> check, string tempRoot)
    {
        string dragged = Path.Combine(tempRoot, "drag-smoke.txt");
        File.WriteAllText(dragged, "dragged");

        // 对照组：最普通的 Window + AllowDrop。
        var plain = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = 320,
            Top = 40,
            Width = 280,
            Height = 180,
            Background = Brushes.White,
            Content = new Grid(),
        };

        plain.AllowDrop = true;
        int plainOver = 0, plainDrop = 0;
        plain.AddHandler(UIElement.PreviewDragOverEvent, new DragEventHandler((_, _) => plainOver++), true);
        plain.AddHandler(UIElement.PreviewDropEvent, new DragEventHandler((_, _) => plainDrop++), true);
        plain.Show();
        Pump(plain);

        DragDropEffects effect = DragOnce(dragged, ScreenCenter(plain));
        Console.WriteLine($"    [drag] 普通窗口：DragOver {plainOver} / Drop {plainDrop} / 源返回 {effect}");
        check(plainDrop > 0, "真鼠标拖放：普通 WPF 窗口能收到（对照组，证明自动化可用）");

        plain.Close();

        // 被测：共享窗口（显式打开 OLE，测「拖放目标本身通不通」）。
        var window = ShareWindow();
        var outList = (ItemsControl)Field(window, "OutList");

        window.AllowDrop = true;
        window.Topmost = true;   // 别让冒烟自己的控制台窗口盖在上面
        window.Activate();
        Pump(window);

        int over = 0, drop = 0;
        window.AddHandler(UIElement.PreviewDragOverEvent, new DragEventHandler((_, _) => over++), true);
        window.AddHandler(UIElement.PreviewDropEvent, new DragEventHandler((_, _) => drop++), true);

        DragDropEffects shareEffect = DragOnce(dragged, ScreenCenter(window, outList));
        Console.WriteLine($"    [drag] 共享窗口：DragOver {over} / Drop {drop} / 源返回 {shareEffect}");

        check(drop > 0, "真鼠标拖放：共享窗口的 OLE 目标能收到 Drop");
        check(StoreItems().Count(i => NameOf(i) == "drag-smoke.txt") == 1, "真鼠标拖放：文件进了共享区");
    }

    /// <summary>
    /// 从**真的资源管理器**拖一个文件进来。提权时 OLE 被 UIPI 拦住，看 WM_DROPFILES 有没有兜住；
    /// 非提权时走 OLE。窗口的 <c>AllowDrop</c> 按进程权限还原成产品的真实状态（前面那一段
    /// 为了测 OLE 曾把它强行打开）。
    /// </summary>
    public static void RunExplorer(Action<bool, string> check, string tempRoot, bool elevated)
    {
        string folder = Path.Combine(tempRoot, "拖放测试-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        string folderName = Path.GetFileName(folder);
        File.WriteAllText(Path.Combine(folder, "拖我.txt"), "explorer-drag");

        var window = ShareWindow();
        var outList = (ItemsControl)Field(window, "OutList");

        window.AllowDrop = !elevated;

        // 数一下 WM_DROPFILES 到底有没有到（到 0 个文件 / 根本没到，是两种完全不同的原因）。
        int dropFiles = 0;
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)!.AddHook(
            (IntPtr _, int message, IntPtr _, IntPtr _, ref bool _) =>
            {
                if (message == 0x0233) dropFiles++;
                return IntPtr.Zero;
            });

        // 共享窗口挪到屏幕右侧，资源管理器留在它默认的位置：两块不重叠，也就不必挪资源管理器
        // （挪了 UIA 给的矩形要过一会儿才刷新，反而更不稳）。
        int screenW = GetSystemMetrics(0 /* SM_CXSCREEN */);
        SetWindowPos(new WindowInteropHelper(window).Handle, HwndTopmost,
            Math.Max(0, screenW - 980), 40, 940, 620, 0x0040 /* SWP_SHOWWINDOW */);
        window.Activate();
        Pump(window);

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        IntPtr hwnd = WaitWindow("CabinetWClass", folderName, 8000);
        check(hwnd != IntPtr.Zero, "找到资源管理器窗口（" + folderName + "）");
        if (hwnd == IntPtr.Zero) return;

        try
        {
            SetForegroundWindow(hwnd);
            Thread.Sleep(1500);

            GetWindowRect(hwnd, out RECT box);
            Console.WriteLine($"    [drag] 资源管理器窗口 {box.Left},{box.Top} {box.Right - box.Left}×{box.Bottom - box.Top}");

            System.Drawing.Rectangle? item = FindNode(hwnd, "拖我", verbose: true);
            check(item != null, "UIA 找到资源管理器里的文件图标");
            if (item == null) return;

            // 行的矩形是整行（含右侧空白），按在行中心可能落在空白处、拖不起来，按左边图标那一带。
            var from = new Point(item.Value.Left + 80, item.Value.Top + item.Value.Height / 2.0);
            Point to = ScreenCenter(window, outList);
            Console.WriteLine($"    [drag] 资源管理器：图标 {from.X:0},{from.Y:0} → 共享窗口 {to.X:0},{to.Y:0}");

            var driver = new Thread(() => Drive(from, to)) { IsBackground = true };
            driver.SetApartmentState(ApartmentState.STA);
            driver.Start();
            Wait(driver, window);

            Console.WriteLine($"    [drag] 共享窗口收到 WM_DROPFILES {dropFiles} 次");
            bool landed = StoreItems().Any(i => NameOf(i) == "拖我.txt");

            // 提权窗口收不到是 Windows 的规则（UIPI），不是回归——共享助手就是为这件事拆出去的。
            check(elevated ? !landed : landed,
                elevated
                    ? "提权窗口收不到 Explorer 的拖放（UIPI；共享窗口因此跑在 ShareHost.exe 里）"
                    : "从资源管理器真拖进来：文件进了共享区");
        }
        finally
        {
            SendMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>开一个源窗口，把 <paramref name="file"/> 拖到 <paramref name="to"/>（屏幕物理像素）。</summary>
    private static DragDropEffects DragOnce(string file, Point to)
    {
        var source = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = 40,
            Top = 40,
            Width = 140,
            Height = 140,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
        };

        var handle = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(220, 38, 38)),
            CornerRadius = new CornerRadius(8),
        };

        source.Content = handle;
        source.Show();
        Pump(source);

        DragDropEffects effect = DragDropEffects.None;
        int started = 0;

        handle.MouseLeftButtonDown += (_, _) =>
        {
            started++;
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { file });
            effect = DragDrop.DoDragDrop(handle, data, DragDropEffects.Copy);
        };

        Point from = ScreenCenter(source);
        var driver = new Thread(() => Drive(from, to)) { IsBackground = true };
        driver.SetApartmentState(ApartmentState.STA);
        driver.Start();

        Wait(driver, source);

        Console.WriteLine($"    [drag] 源窗口按下 {started} 次，光标终点 {CursorPos()}");
        source.Close();
        return effect;
    }

    private static void Drive(Point from, Point to)
    {
        SetCursorPos((int)from.X, (int)from.Y);
        Thread.Sleep(200);
        mouse_event(MouseLeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(250);

        for (int step = 1; step <= 24; step++)
        {
            double t = step / 24.0;
            SetCursorPos((int)(from.X + (to.X - from.X) * t), (int)(from.Y + (to.Y - from.Y) * t));
            Thread.Sleep(20);
        }

        Thread.Sleep(250);
        mouse_event(MouseLeftUp, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(400);
    }

    /// <summary>等拖放走完，期间**必须泵消息**（源窗口的按下、DoDragDrop、OLE / WM_DROPFILES 回调都在这条线程上）。</summary>
    private static void Wait(Thread driver, Window window)
    {
        var frame = new DispatcherFrame();
        var pump = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
            (_, _) =>
            {
                if (!driver.IsAlive) frame.Continue = false;
            }, window.Dispatcher);
        var deadline = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, window.Dispatcher);

        pump.Start();
        deadline.Start();
        Dispatcher.PushFrame(frame);
        pump.Stop();
        deadline.Stop();
        Pump(window);
    }

    private static Window ShareWindow() =>
        Application.Current.Windows.Cast<Window>().First(w => w.GetType().Name == "ShareWindow");

    /// <summary>
    /// 把文件从真资源管理器拖进**另一个进程**的窗口（共享助手）。返回时拖放流程已走完，
    /// 是否真的收下由调用方读清单判断。
    /// </summary>
    public static void DragFileInto(Action<bool, string> check, string tempRoot, IntPtr target, string fileName)
    {
        string folder = Path.Combine(tempRoot, "助手拖放-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        string folderName = Path.GetFileName(folder);
        File.WriteAllText(Path.Combine(folder, fileName), "helper-drag");

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        IntPtr hwnd = WaitWindow("CabinetWClass", folderName, 8000);
        check(hwnd != IntPtr.Zero, "找到资源管理器窗口（" + folderName + "）");
        if (hwnd == IntPtr.Zero) return;

        try
        {
            // 资源管理器默认窗口很宽，会盖住右边的助手窗口；先把它压到左半边再找图标。
            int screenW = GetSystemMetrics(0);
            SetWindowPos(hwnd, IntPtr.Zero /* HWND_TOP */, 0, 40,
                Math.Max(560, screenW / 2 - 40), 700, 0x0040 /* SWP_SHOWWINDOW */);
            SetForegroundWindow(hwnd);
            Thread.Sleep(1200);

            System.Drawing.Rectangle? item = FindNode(hwnd, Path.GetFileNameWithoutExtension(fileName), verbose: true);
            check(item != null, "UIA 找到资源管理器里的文件图标");
            if (item == null) return;

            GetWindowRect(target, out RECT box);
            var to = new Point(box.Left + 200, box.Top + 260);

            var under = new StringBuilder(256);
            IntPtr at = WindowFromPoint(new POINT { X = (int)to.X, Y = (int)to.Y });
            GetClassName(at, under, 256);

            var title = new StringBuilder(256);
            GetWindowText(at, title, 256);
            Console.WriteLine($"    [drag] 目标窗口 {box.Left},{box.Top} {box.Right - box.Left}×{box.Bottom - box.Top}" +
                              $"；落点下方 = {under} / {title}");            var from = new Point(item.Value.Left + 80, item.Value.Top + item.Value.Height / 2.0);
            Console.WriteLine($"    [drag] 图标 {from.X:0},{from.Y:0} → 助手窗口 {to.X:0},{to.Y:0}");

            var driver = new Thread(() => Drive(from, to)) { IsBackground = true };
            driver.SetApartmentState(ApartmentState.STA);
            driver.Start();

            // 目标窗口在别的进程里，这边没有要泵的消息：等鼠标线程自己跑完。
            check(driver.Join(TimeSpan.FromSeconds(15)), "拖动流程走完");
            Thread.Sleep(1500);
        }
        finally
        {
            SendMessage(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>等某个进程的主窗口出现（共享助手没有可预测的标题遍历，直接问进程）。</summary>
    public static IntPtr WaitProcessWindow(string processName, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var p in Process.GetProcessesByName(processName))
            {
                if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            }
            Thread.Sleep(150);
        }
        return IntPtr.Zero;
    }

    /// <summary>窗口所属进程有没有提权：<c>0</c> = 普通权限，<c>1</c> = 提权，<c>-1</c> = 问不到。</summary>
    public static int ElevationOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return -1;

        IntPtr process = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (int)pid);
        if (process == IntPtr.Zero) return -1;

        try
        {
            if (!OpenProcessToken(process, 0x0008 /* TOKEN_QUERY */, out IntPtr token)) return -1;
            try
            {
                return GetTokenInformation(token, 20 /* TokenElevation */, out int elevated, sizeof(int), out _)
                    ? elevated
                    : -1;
            }
            finally { CloseHandle(token); }
        }
        finally { CloseHandle(process); }
    }

    /// <summary>把窗口摆到屏幕右侧并置顶（资源管理器留在左边，拖的时候不会挡住落点）。</summary>
    public static void ParkRight(IntPtr hwnd)
    {
        int screenW = GetSystemMetrics(0);
        SetWindowPos(hwnd, HwndTopmost, Math.Max(0, screenW - 980), 40, 940, 620,
            0x0040 /* SWP_SHOWWINDOW */);
        SetForegroundWindow(hwnd);
    }

    private static Point ScreenCenter(Window window, FrameworkElement? target = null)
    {
        if (target != null)
        {
            Point local = new(target.ActualWidth / 2, Math.Min(80, Math.Max(10, target.ActualHeight / 2)));
            return target.PointToScreen(local);
        }

        GetWindowRect(new WindowInteropHelper(window).Handle, out RECT rect);
        return new Point((rect.Left + rect.Right) / 2.0, (rect.Top + rect.Bottom) / 2.0);
    }

    private static string CursorPos()
    {
        GetCursorPos(out POINT point);
        return $"{point.X},{point.Y}";
    }

    /// <summary>UIA 快照里找名字含 <paramref name="name"/> 的元素（产品的 ControlTargets，反射调）。</summary>
    private static System.Drawing.Rectangle? FindNode(IntPtr hwnd, string name, bool verbose = false)
    {
        Type targets = typeof(LTools.Core.Share.ShareOptions).Assembly
            .GetType("LTools.Features.ControlTargets", true)!;

        object tree = targets.GetMethod("Snapshot")!.Invoke(null, new object[] { hwnd, 12, 1000 })!;
        var nodes = (System.Collections.IEnumerable)tree.GetType().GetProperty("Nodes")!.GetValue(tree)!;

        System.Drawing.Rectangle? found = null;

        foreach (object node in nodes)
        {
            if (node.GetType().GetProperty("Name")?.GetValue(node) is not string nodeName) continue;
            if (!nodeName.Contains(name, StringComparison.Ordinal)) continue;

            var bounds = (System.Drawing.Rectangle)node.GetType().GetProperty("Bounds")!.GetValue(node)!;
            if (verbose) Console.WriteLine($"    [uia] {nodeName} @ {bounds}");

            if (found == null && bounds.Width > 4 && bounds.Height > 4) found = bounds;
        }

        return found;
    }

    /// <summary>找类名 / 标题匹配的顶层窗口（资源管理器会复用进程，不能按进程找）。</summary>
    private static IntPtr WaitWindow(string className, string titlePart, int timeoutMs)
    {
        IntPtr found = IntPtr.Zero;
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

        while (DateTime.UtcNow < deadline)
        {
            EnumWindows((hwnd, _) =>
            {
                if (!IsWindowVisible(hwnd)) return true;

                var cls = new StringBuilder(256);
                GetClassName(hwnd, cls, cls.Capacity);
                if (cls.ToString() != className) return true;

                var title = new StringBuilder(512);
                GetWindowText(hwnd, title, title.Capacity);
                if (!title.ToString().Contains(titlePart, StringComparison.Ordinal)) return true;

                found = hwnd;
                return false;
            }, IntPtr.Zero);

            if (found != IntPtr.Zero) return found;
            Thread.Sleep(200);
        }

        return found;
    }

    // ============ 反射 / 泵 ============

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)!
            .GetValue(target)!;

    private static Type StoreType => typeof(LTools.Core.Share.ShareOptions).Assembly
        .GetType("LTools.Core.Share.ShareStore", true)!;

    private static List<object> StoreItems() =>
        ((System.Collections.IEnumerable)StoreType.GetMethod("Items")!
            .Invoke(StoreType.GetProperty("Default")!.GetValue(null), null)!).Cast<object>().ToList();

    private static string NameOf(object item) =>
        (string)item.GetType().GetProperty("Name")!.GetValue(item)!;

    private static void Pump(Window window) =>
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    // ============ Win32 ============

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder buffer, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int value, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
