using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Features;
using LTools.Views.Controls;

namespace PanelDragSmoke;

/// <summary>
/// 面板拖放的冒烟：真开一扇 <c>SuperPanelWindow</c>，真鼠标把文件拖到磁贴网格上，
/// 拖动期间高频采样**虚线占位还挂不挂着**。
/// </summary>
/// <remarks>
/// <para>
/// 回归的是 2026-11-25 报的那个 bug：占位是 <c>IsHitTestVisible=false</c> 的，插到光标那一格时
/// 命中会穿到块容器上；旧实现靠 <c>InputHitTest</c> 找网格 → 判不出来 → 每个 <c>DragOver</c>
/// 都先把占位清掉再加回来，看起来就是「一直闪」。
/// </para>
/// <para>
/// 所以断言的是「拖停在网格上、光标来回抖的时候，占位始终在可视树里」（采样绝大多数挂着），
/// 而不是「拖完能建动作」——后者只证明能落，证明不了过程稳。
/// </para>
/// </remarks>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    private static readonly Assembly Lt = typeof(PanelConfig).Assembly;

    private static int _pass, _fail;

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        string root = Path.Combine(Path.GetTempPath(), "ltools-paneldrag-" + Guid.NewGuid().ToString("N")[..8]);
        string configPath = Path.Combine(root, "LTools.json");
        Window? panel = null;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        try
        {
            // ---- ① 临时数据根：6 个动作塞进一个分组（和真面板长得一样）----
            Directory.CreateDirectory(root);
            Type store = Lt.GetType("LTools.Features.ConfigStore", true)!;
            Type panels = Lt.GetType("LTools.Features.Panel.PanelLibrary", true)!;

            Call(store, "Initialize", configPath);

            var ids = new List<string>();
            for (int i = 1; i <= 6; i++)
            {
                var action = new ActionDto { Name = "冒烟动作 " + i, Icon = "app" };
                Call(store, "AddAction", action);
                ids.Add(action.Id);
            }
            Call(store, "Save");

            var config = (PanelConfig)Get(panels, "Current")!;
            config.Preferences.Width = 760;
            config.Preferences.Height = 560;
            config.Preferences.ShowGroupTitles = true;

            if (config.Global.Scenes.Count == 0) config.Global.Scenes.Add(new PanelSceneDto { Id = "s1", Name = "全局" });
            var group = config.Global.Scenes[0].Groups.FirstOrDefault();
            if (group == null)
            {
                group = new PanelGroupDto { Id = "g1", Name = "常用" };
                config.Global.Scenes[0].Groups.Add(group);
            }
            group.Actions.Clear();
            group.Actions.AddRange(ids);
            Call(panels, "Save");

            // ---- ② 开面板 ----
            Type panelType = Lt.GetType("LTools.Views.SuperPanelWindow", true)!;
            panel = (Window)Activator.CreateInstance(panelType, Any, null, new object?[] { null, 0, 0 }, null)!;
            panel.WindowStartupLocation = WindowStartupLocation.Manual;
            panel.Show();
            panel.Left = 520;
            panel.Top = 120;
            panel.Width = 760;
            panel.Height = 560;
            Pump(panel);

            Check("面板出来了", panel.IsVisible && panel.ActualWidth > 400);

            TileGridPanel? grid = FindGrid(panel);
            Check("磁贴网格里有 6 个磁贴", grid != null && Tiles(grid).Count == 6,
                grid == null ? "没找到网格" : Tiles(grid).Count.ToString());
            if (grid == null) return Finish();

            // ---- ③ 真鼠标：源窗口 → 网格中间，停住并来回抖，期间采样占位 ----
            string file = Path.Combine(root, "拖我.txt");
            File.WriteAllText(file, "panel-drag");

            Button tile = Tiles(grid)[Math.Min(2, Tiles(grid).Count - 1)];
            Point to = tile.PointToScreen(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2));

            FieldInfo holeField = panelType.GetField("_dropHole", Any)!;
            int samples = 0, attached = 0;
            bool sampling = false;

            var sampler = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(5) };
            sampler.Tick += (_, _) =>
            {
                if (!sampling) return;
                samples++;
                if (holeField.GetValue(panel) is FrameworkElement { Parent: not null }) attached++;
            };
            sampler.Start();

            int before = group.Actions.Count;
            DragDropEffects effect = DragOnce(file, to,
                onHold: () => sampling = true,
                onDone: () => sampling = false,
                panel);
            sampler.Stop();

            double ratio = samples == 0 ? 0 : (double)attached / samples;
            Check("拖动期间采到样本", samples >= 20, $"样本 {samples}");
            Check("拖停在网格上时占位一直挂着（不再一明一灭）", ratio >= 0.95,
                $"挂着 {attached}/{samples} = {ratio:P0}");
            Check("松手后文件落成动作", group.Actions.Count == before + 1,
                $"{before} → {group.Actions.Count}，源返回 {effect}");

            // ---- ④ 拖到面板上再拖走（不松手在面板上）：占位要收掉，动作不加 ----
            int afterDrop = group.Actions.Count;
            Point away = new(40, 300);
            DragOnce(file, to, onHold: null, onDone: null, panel, cancelTo: away);
            Pump(panel, 400);

            Check("拖走（不在面板上松手）不建动作", group.Actions.Count == afterDrop,
                $"{afterDrop} → {group.Actions.Count}");
            Check("拖走后占位收掉", holeField.GetValue(panel) == null,
                holeField.GetValue(panel) == null ? "" : "占位还在");

            // ---- ⑤ 钉住 = 工具盘（2026-11-26）：点动作 / 右键编辑都不收，只有主动关闭才关 ----
            // 让控制器认识这扇窗（正式链路里由 SuperPanel.Show 建窗）。
            Type controller = Lt.GetType("LTools.Features.SuperPanel", true)!;
            controller.GetField("_window", Any)!.SetValue(null, panel);

            panelType.GetMethod("SetPinned", Any)!.Invoke(panel, new object[] { true });
            Pump(panel);
            Check("钉住：进入工具盘态", (bool)Get(controller, "IsPinned")!);

            panelType.GetMethod("RequestEscape", Any)!.Invoke(panel, null);
            Pump(panel);
            Check("钉住：Esc 不收面板", panel.IsVisible && (bool)Get(controller, "IsOpen")!);

            Call(controller, "CloseAfterUse");   // 执行动作 / 草稿那一下
            Pump(panel);
            Check("钉住：用完即收不收面板", panel.IsVisible && (bool)Get(controller, "IsOpen")!);

            var aside = (IDisposable)Call(controller, "StepAside")!;   // 右键「编辑动作…」
            Pump(panel);
            Check("钉住：开编辑器前面板让开（隐藏但没关）",
                !panel.IsVisible && (bool)Get(controller, "IsOpen")!);
            aside.Dispose();
            Pump(panel);
            Check("钉住：编辑器关掉后自己回来", panel.IsVisible && (bool)Get(controller, "IsOpen")!);

            panelType.GetMethod("SetPinned", Any)!.Invoke(panel, new object[] { false });
            Pump(panel);
            var aside2 = (IDisposable)Call(controller, "StepAside")!;
            Pump(panel);
            Check("未钉住：开编辑器前面板照旧关掉",
                !panel.IsVisible && !(bool)Get(controller, "IsOpen")!);
            aside2.Dispose();
        }
        catch (Exception ex)
        {
            Check("冒烟未抛异常", false, ex.ToString());
        }
        finally
        {
            try { panel?.Close(); } catch { /* 已经在关 */ }
            Pump(null, 200);
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { /* 临时目录 */ }
        }

        return Finish();
    }

    // ============ 拖放驱动（真鼠标，照 tests/ShareSmoke/DragSmoke 的 Drive） ============

    private static DragDropEffects DragOnce(string file, Point to, Action? onHold, Action? onDone,
                                            Window host, Point? cancelTo = null)
    {
        var source = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Left = 40,
            Top = 120,
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
        handle.MouseLeftButtonDown += (_, _) =>
        {
            var data = new DataObject();
            data.SetData(DataFormats.FileDrop, new[] { file });
            effect = DragDrop.DoDragDrop(handle, data, DragDropEffects.Copy);
        };

        Point from = new(source.Left + source.Width / 2, source.Top + source.Height / 2);
        var driver = new Thread(() => Drive(from, to, onHold, onDone, cancelTo)) { IsBackground = true };
        driver.SetApartmentState(ApartmentState.STA);
        driver.Start();

        Wait(driver, host);
        source.Close();
        Pump(host);
        return effect;
    }

    private static void Drive(Point from, Point to, Action? onHold, Action? onDone, Point? cancelTo)
    {
        SetCursorPos((int)from.X, (int)from.Y);
        Thread.Sleep(200);
        mouse_event(LeftDown, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(250);

        for (int step = 1; step <= 24; step++)
        {
            double t = step / 24.0;
            SetCursorPos((int)(from.X + (to.X - from.X) * t), (int)(from.Y + (to.Y - from.Y) * t));
            Thread.Sleep(20);
        }

        Thread.Sleep(250);
        onHold?.Invoke();

        // 停在网格上左右抖：每次移动都会重算落点，占位只要被清掉一次就会看出来。
        for (int i = 0; i < 40; i++)
        {
            SetCursorPos((int)to.X + (i % 2 == 0 ? -5 : 5), (int)to.Y + (i % 4 < 2 ? -3 : 3));
            Thread.Sleep(12);
        }

        onDone?.Invoke();
        Thread.Sleep(120);

        if (cancelTo is { } away)
        {
            for (int step = 1; step <= 12; step++)
            {
                double t = step / 12.0;
                SetCursorPos((int)(to.X + (away.X - to.X) * t), (int)(to.Y + (away.Y - to.Y) * t));
                Thread.Sleep(20);
            }

            Thread.Sleep(200);
        }
        else
        {
            SetCursorPos((int)to.X, (int)to.Y);
            Thread.Sleep(100);
        }

        mouse_event(LeftUp, 0, 0, 0, IntPtr.Zero);
        Thread.Sleep(300);
    }

    private static void Wait(Thread driver, Window? host)
    {
        var frame = new DispatcherFrame();
        var pump = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background,
            (_, _) => { if (!driver.IsAlive) frame.Continue = false; }, host?.Dispatcher);
        var deadline = new DispatcherTimer(TimeSpan.FromSeconds(20), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, host?.Dispatcher);

        pump.Start();
        deadline.Start();
        Dispatcher.PushFrame(frame);
        pump.Stop();
        deadline.Stop();
    }

    // ============ 视觉树 ============

    private static TileGridPanel? FindGrid(DependencyObject root)
    {
        if (root is TileGridPanel grid && Tiles(grid).Count > 0) return grid;
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            if (FindGrid(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }

    /// <summary>真磁贴（排除「＋」加号格：它的 <c>ActionId</c> 是 null）。</summary>
    private static List<Button> Tiles(TileGridPanel grid) =>
        grid.Children.OfType<Button>()
            .Where(b => b.Tag?.GetType().GetProperty("ActionId")?.GetValue(b.Tag) is string && b.IsVisible)
            .ToList();

    private static void Pump(Window? window, int ms = 150)
    {
        Dispatcher dispatcher = window?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        var frame = new DispatcherFrame();
        var stop = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, dispatcher);
        stop.Start();
        Dispatcher.PushFrame(frame);
        stop.Stop();
    }

    // ============ 反射小工具 ============

    private static object? Call(Type type, string name, params object?[] args) =>
        Find(type, name, args.Length).Invoke(null, args);

    private static object? Get(Type type, string name) => type.GetProperty(name, Any)!.GetValue(null);

    private static MethodInfo Find(Type type, string name, int count) =>
        type.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count)
        ?? throw new MissingMethodException(type.Name + "." + name + "/" + count);

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok) _pass++;
        else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(string.IsNullOrEmpty(detail) ? "" : $"  [{detail}]")}");
    }

    // ============ Win32 ============



    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
}
