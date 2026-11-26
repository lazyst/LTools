using System;
using System.Collections;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DBitmap = System.Drawing.Bitmap;
using DRect = System.Drawing.Rectangle;
using System.Windows.Threading;
using LTools.Features;

/// <summary>
/// 找图 / 找字「多个目标」的冒烟：真窗口 + 真截屏 + 真 OCR。
/// </summary>
/// <remarks>
/// 纯逻辑（重叠去重、词边界、排序）在 <c>--selftest --filter findTargets</c> 里；
/// 这里只验「屏幕上真有三个一样的图标时，能分成三个目标，且点第 2 个落在第二个上」。
/// </remarks>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private static int _pass, _fail;
    private static Window _window = null!;
    private static readonly Border[] Icons = new Border[3];
    private static string _dir = "";

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += (_, _) =>
        {
            try { Run(); }
            catch (Exception ex) { Check("没有未处理异常", false, ex.ToString()); }
            finally { app.Shutdown(); }
        };
        app.Run();

        Console.WriteLine($"find-targets-smoke：{(_fail == 0 ? "通过" : $"{_fail} 条失败")}（{_pass} 条通过）");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ltools-find-targets-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);

        BuildWindow();
        try
        {
            FindImages();
            ClickNth();
            FindTexts();
        }
        finally
        {
            try { _window.Close(); } catch { /* 已关 */ }
            PumpFor(120);
            try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录 */ }
        }
    }

    // ============ 找图 ============

    private static void FindImages()
    {
        string template = CaptureTemplate();

        DRect window = WindowRect();
        var probe = new Probe
        {
            Kind = ProbeKind.image,
            Image = template,
            Region = ImageRegion.rect,
            Rect = new[] { window.X, window.Y, window.Width, window.Height },
            TimeoutMs = 0,
            IntervalMs = 200,
            MinScore = 0.9,
        };

        Type imageProbe = ProductType("LTools.Features.ImageProbe");
        object result = imageProbe.GetMethod("RunAll", Any)!
            .Invoke(null, new object?[] { probe, null, 200, 1 })!;
        var hits = Enumerate(result, "Hits");
        Check("三个相同图标 → 三个命中", hits.Count >= 3, $"实际 {hits.Count}");

        var ordered = hits
            .Select(h => (X: Int(h, "X"), Y: Int(h, "Y")))
            .OrderBy(p => p.Y).ThenBy(p => p.X)
            .ToList();
        if (ordered.Count < 3)
        {
            Check("排好序的三个位置", false, $"只有 {ordered.Count} 个");
            return;
        }

        Point c1 = Center(Icons[0]);
        Point c2 = Center(Icons[1]);
        Point c3 = Center(Icons[2]);
        Check("第一个是左边那个", Math.Abs(ordered[0].X - c1.X) <= 8, $"命中 {ordered[0].X} vs 图标 {c1.X}");
        Check("第二个是中间那个", Math.Abs(ordered[1].X - c2.X) <= 8, $"命中 {ordered[1].X} vs 图标 {c2.X}");
        Check("第三个是右边那个", Math.Abs(ordered[2].X - c3.X) <= 8, $"命中 {ordered[2].X} vs 图标 {c3.X}");
    }

    /// <summary>只框住第一个图标时点「第 2 个」必须报错，而不是退回去点第一个。</summary>
    private static void ClickNth()
    {
        string template = Path.Combine(_dir, "icon.png");
        DRect first = RectOf(Icons[0]);

        Type requestType = ProductType("LTools.Features.ImageClickRequest");
        object request = Activator.CreateInstance(requestType)!;
        void Set(string name, object? value) => requestType.GetProperty(name)!.SetValue(request, value);

        Set("Name", "图片点击");
        Set("Image", template);
        Set("Region", ImageRegion.rect);
        Set("Rect", new[] { first.X, first.Y, first.Width, first.Height });
        Set("Index", 2);
        Set("TimeoutMs", 0);
        Set("MinScore", 0.9);

        Type clickAction = ProductType("LTools.Features.ImageClickAction");
        try
        {
            clickAction.GetMethod("Run", Any)!.Invoke(null, new object?[] { request, null });
            Check("只有 1 个时点第 2 个要报错", false, "没有抛错（可能悄悄点了第一个）");
        }
        catch (TargetInvocationException ex)
        {
            string message = ex.InnerException?.Message ?? "";
            Check("只有 1 个时点第 2 个要报错", message.Contains("只找到"), message);
        }
    }

    // ============ 找字 ============

    private static void FindTexts()
    {
        DRect window = WindowRect();
        Type locator = ProductType("LTools.Features.TextLocator");

        object lookup = locator.GetMethod("LocateAll", Any)!.Invoke(null, new object?[]
        {
            "确定", ImageRegion.rect, new[] { window.X, window.Y, window.Width, window.Height },
            "v6tiny", 0, 300, false, 1, null,
        })!;

        var hits = Enumerate(lookup, "Hits");
        Check("两处「确定」分别算命中", hits.Count >= 2, $"实际 {hits.Count}");

        bool allText = hits.All(h => Str(h, "Text").Contains("确定"));
        Check("每一项都带着命中的文字", hits.Count > 0 && allText,
            string.Join(" / ", hits.Select(h => Str(h, "Text"))));

        var ordered = hits
            .Select(h => (X: Int(h, "X"), Y: Int(h, "Y")))
            .OrderBy(p => p.Y).ThenBy(p => p.X)
            .ToList();
        Check("两处按钮在同一行、左右分开", ordered.Count >= 2 && ordered[0].X < ordered[1].X,
            ordered.Count >= 2 ? $"{ordered[0].X} → {ordered[1].X}" : "命中不足两个");
    }

    // ============ 窗口与坐标 ============

    private static void BuildWindow()
    {
        var root = new StackPanel { Margin = new Thickness(24) };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < 3; i++)
        {
            var icon = new Border
            {
                Width = 40,
                Height = 40,
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x33, 0x66)),
                CornerRadius = new CornerRadius(4),
                Margin = i == 2 ? new Thickness(0) : new Thickness(0, 0, 24, 0),
                Child = new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            Icons[i] = icon;
            row.Children.Add(icon);
        }
        root.Children.Add(row);

        var words = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 30, 0, 0) };
        words.Children.Add(Word("确定"));
        words.Children.Add(Word("确定"));
        root.Children.Add(words);

        _window = new Window
        {
            Title = "find-targets-smoke",
            Width = 460,
            Height = 210,
            Left = 160,
            Top = 160,
            WindowStartupLocation = WindowStartupLocation.Manual,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Background = Brushes.White,
            Content = root,
        };
        _window.Show();
        _window.Activate();
        PumpFor(600);
    }

    private static TextBlock Word(string text) => new()
    {
        Text = text,
        FontSize = 30,
        FontFamily = new FontFamily("Microsoft YaHei"),
        Margin = new Thickness(0, 0, 60, 0),
    };

    /// <summary>把第一个图标截成模板图（与实际像素完全一致，不受抗锯齿影响）。</summary>
    private static string CaptureTemplate()
    {
        DRect r = RectOf(Icons[0]);
        using DBitmap shot = Capture(r.X, r.Y, r.Width, r.Height);
        string path = Path.Combine(_dir, "icon.png");
        shot.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>截一块屏幕（ScreenCapture 是 internal，冒烟里只能反射）。</summary>
    private static DBitmap Capture(int x, int y, int w, int h)
    {
        Type type = ProductType("LTools.Native.ScreenCapture");
        MethodInfo capture = type.GetMethod("Capture", Any, null,
            new[] { typeof(int), typeof(int), typeof(int), typeof(int) }, null)!;
        return (DBitmap)capture.Invoke(null, new object[] { x, y, w, h })!;
    }
    private static DRect RectOf(FrameworkElement element)
    {
        Point at = _window.PointToScreen(element.TransformToAncestor(_window).Transform(new Point(0, 0)));
        DpiScale dpi = VisualTreeHelper.GetDpi(_window);
        return new DRect(
            (int)Math.Round(at.X),
            (int)Math.Round(at.Y),
            Math.Max(1, (int)Math.Round(element.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(element.ActualHeight * dpi.DpiScaleY)));
    }

    private static DRect WindowRect()
    {
        Point at = _window.PointToScreen(new Point(0, 0));
        DpiScale dpi = VisualTreeHelper.GetDpi(_window);
        return new DRect(
            (int)Math.Round(at.X),
            (int)Math.Round(at.Y),
            Math.Max(1, (int)Math.Round(_window.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Round(_window.ActualHeight * dpi.DpiScaleY)));
    }

    private static Point Center(FrameworkElement element)
    {
        DRect r = RectOf(element);
        return new Point(r.X + r.Width / 2.0, r.Y + r.Height / 2.0);
    }

    // ============ 反射小工具 ============

    private static Type ProductType(string name) =>
        typeof(Probe).Assembly.GetType(name, true)!;

    private static System.Collections.Generic.List<object> Enumerate(object target, string property)
    {
        var items = (IEnumerable)target.GetType().GetProperty(property)!.GetValue(target)!;
        return items.Cast<object>().ToList();
    }

    private static int Int(object target, string property) =>
        (int)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static string Str(object target, string property) =>
        (string)target.GetType().GetProperty(property)!.GetValue(target)!;

    private static void Check(string what, bool ok, string detail)
    {
        if (ok) { _pass++; Console.WriteLine("ok   " + what); return; }
        _fail++;
        Console.WriteLine("FAIL " + what + " — " + detail);
    }

    private static void PumpFor(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
