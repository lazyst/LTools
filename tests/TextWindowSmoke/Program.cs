using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Views;

namespace TextWindowSmoke;

/// <summary>
/// 文本窗口的窗口行为（真窗口，不注入键鼠）：
/// 默认不置顶、任务栏有按钮、图钉能翻转置顶、**最小化后屏幕上不留一条小标题栏**。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        int code = 0;
        app.Startup += (_, _) =>
        {
            try { code = Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            app.Shutdown(code);
        };
        app.Run();
        return code;
    }

    private static int Run()
    {
        var window = new TextViewWindow("冒烟 · 文本窗口", "第一行\n第二行", editable: false,
            monospace: false, wrap: true, activate: false);
        window.Show();
        Pump(250);

        Check("默认不置顶", !window.Topmost);
        Check("在任务栏里有按钮（ShowInTaskbar = true）", window.ShowInTaskbar);

        // 图钉：走真按钮的处理函数（点标题栏那颗图钉时就是它）。
        bool? fired = null;
        window.TopmostChanged += (_, on) => fired = on;

        InvokePin(window);
        Check("点图钉 → 置顶", window.Topmost);
        Check("事件带回 true", fired == true);
        Check("标题栏图钉画成「已钉」", PinState(window));

        InvokePin(window);
        Check("再点图钉 → 取消置顶", !window.Topmost);
        Check("事件带回 false", fired == false);
        Check("标题栏图钉画成「未钉」", !PinState(window));

        // 最小化：旧实现（无任务栏按钮）会在这里留一条小标题栏在屏幕左下角。
        var hwnd = new WindowInteropHelper(window).Handle;
        window.WindowState = WindowState.Minimized;
        Pump(500);

        Check("确实最小化了", IsIconic(hwnd));
        GetWindowRect(hwnd, out Rect rect);
        string where = $"窗口矩形 {rect.Left},{rect.Top} {rect.Right - rect.Left}×{rect.Bottom - rect.Top}";
        Check("最小化后屏幕上没有一条小标题栏", !SmallStripOnScreen(rect), where);

        window.WindowState = WindowState.Normal;
        Pump(350);
        Check("还原后仍在（没被最小化卡死）", window.IsVisible && !IsIconic(hwnd));

        window.Close();
        Pump(120);

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    /// <summary>
    /// 「屏幕左下角露出的一小块」= 最小化后仍留在屏幕上的小矩形（标题栏大小）。
    /// 有任务栏按钮的窗口最小化后会被挪出屏幕（-32000 那种），这里只认「小 + 靠左 + 在屏幕内」。
    /// 用物理像素 + 屏幕坐标判定，不拿 <see cref="SystemParameters.WorkArea"/>（它是 DIP，125% 缩放会差一截）。
    /// </summary>
    private static bool SmallStripOnScreen(Rect r)
    {
        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return false;
        if (w >= 400 || h >= 200) return false;
        if (r.Left < -100 || r.Left > 300) return false;   // 已经挪到屏幕外 / 不在左缘
        return r.Top > 0;
    }

    private static void InvokePin(TextViewWindow window)
    {
        MethodInfo? pin = typeof(TextViewWindow).GetMethod("Bar_PinClicked",
            BindingFlags.Instance | BindingFlags.NonPublic);
        pin!.Invoke(window, new object?[] { null, EventArgs.Empty });
        Pump(60);
    }

    private static bool PinState(TextViewWindow window)
    {
        object bar = typeof(TextViewWindow)
            .GetField("Bar", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(window)!;
        PropertyInfo? pinned = bar.GetType().GetProperty("IsPinned");
        return pinned != null && (bool)pinned.GetValue(bar)!;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"ok   {what}");
            return;
        }

        _fail++;
        Console.WriteLine($"FAIL {what}{(detail == null ? "" : " · " + detail)}");
    }
}
