using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CaptureOverlaySmoke;

/// <summary>
/// 「截图会不会把 LTools 自己的窗口拍进去」——2026-11-26 起默认不收自己的窗口，
/// 面板 / 贴图 / 悬浮都该原样留在冻结画面里。
/// </summary>
/// <remarks>
/// 断言的是「抓帧本身看不看得见自己进程的窗口」，不经过覆盖层：
/// <c>ScreenShot.Capture</c> 走 <c>Graphics.CopyFromScreen</c>（BitBlt），
/// 分层窗口（贴图 / 悬浮 / 提示卡片）与普通窗口（面板 / 设置）两种都要验。
/// </remarks>
internal static partial class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private static void OwnWindowSection()
    {
        var asm = typeof(LTools.Views.CapturePro.CaptureOverlayWindow).Assembly;
        var screenShot = asm.GetType("LTools.Features.CapturePro.ScreenShot")!;
        MethodInfo capture = screenShot.GetMethod("Capture",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;
        MethodInfo boundsOf = screenShot.GetMethod("Bounds",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

        Window? plain = null, layered = null;
        try
        {
            // 普通窗口：和超级面板 / 设置一样（WindowStyle=None + Topmost，不透明）
            plain = MakeSolidWindow(Color.FromRgb(0x00, 0xC8, 0x40), 380, 240, layered: false);
            // 分层窗口：贴图 / 悬浮按钮 / 提示卡片都是这一类（AllowsTransparency=True）
            layered = MakeSolidWindow(Color.FromRgb(0xC8, 0x00, 0xC8), 660, 240, layered: true);
            PumpFor(800);

            CheckCapture(capture, plain, "普通窗口（面板 / 设置这一类）", 0x00, 0xC8, 0x40);
            CheckCapture(capture, layered, "分层窗口（贴图 / 悬浮 / 提示卡片）", 0xC8, 0x00, 0xC8);

            var bounds = (Int32Rect)boundsOf.Invoke(null, null)!;
            var full = (BitmapSource)capture.Invoke(null, new object[] { bounds, false })!;
            SavePng(full, System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ownwin-full.png"));
        }
        finally
        {
            try { plain?.Close(); } catch { /* 已经关了 */ }
            try { layered?.Close(); } catch { /* 已经关了 */ }
            PumpFor(150);
        }
    }

    private static void CheckCapture(MethodInfo capture, Window window, string what,
        byte r, byte g, byte b)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        Point topLeft = window.PointToScreen(new Point(0, 0));
        Point bottomRight = window.PointToScreen(new Point(window.ActualWidth, window.ActualHeight));

        var area = new Int32Rect(
            (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            Math.Max(1, (int)Math.Round(bottomRight.X - topLeft.X)),
            Math.Max(1, (int)Math.Round(bottomRight.Y - topLeft.Y)));

        var probe = new POINT
        {
            X = area.X + area.Width / 2,
            Y = area.Y + area.Height / 2,
        };
        IntPtr hit = GetAncestor(WindowFromPoint(probe), 2 /* GA_ROOT */);
        var cls = new System.Text.StringBuilder(256);
        GetClassName(hit, cls, 256);
        GetWindowDisplayAffinity(hwnd, out uint affinity);

        Console.WriteLine($"      窗口 {hwnd} rect={area.X},{area.Y} {area.Width}x{area.Height}" +
            $" 可见={window.IsVisible} ex=0x{GetWindowLongPtr(hwnd, -20).ToInt64():X8} 抓取亲和={affinity}" +
            $" 命中={hit} 类名='{cls}'");

        // ① 窗口真的在屏幕上、而且在最上面（否则「没拍进去」是别的原因）
        Check(hit == hwnd, $"{what}在屏幕最上面（命中 {hit}，窗口 {hwnd}）");

        // ② 抓帧里那块像素就是它的颜色
        var shot = (BitmapSource)capture.Invoke(null, new object[] { area, false })!;
        Color got = PixelAt(shot, area.Width / 2, area.Height / 2);
        Check(got.R == r && got.G == g && got.B == b,
            $"{what}被拍进画面（期望 {r},{g},{b}，实得 {got.R},{got.G},{got.B}）");
    }

    private static Window MakeSolidWindow(Color color, double left, double top, bool layered)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            ShowActivated = false,
            AllowsTransparency = layered,
            Background = new SolidColorBrush(color),
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = left,
            Top = top,
            Width = 220,
            Height = 160,
        };
        w.Show();
        return w;
    }

    private static void SavePng(BitmapSource image, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = System.IO.File.Create(path);
        encoder.Save(stream);
    }
}
