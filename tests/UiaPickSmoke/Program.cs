using System;
using System.Diagnostics;
using System.Drawing;
using DPoint = System.Drawing.Point;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using LTools.Features;

/// <summary>
/// 控件选择器冒烟：起一个子进程 WPF 窗口（带 AutomationId 的两个按钮），
/// 对它的真实 UIA 控件树跑 Snapshot / AtPoint / RollUp / CountSame。
/// </summary>
/// <remarks>
/// 不分两个进程会死锁：UIA 的 provider 在目标进程的 UI 线程上响应，父进程如果在
/// 自己的 UI 线程上阻塞等结果，那条线程就再也发不出响应。子进程跑自己的 <c>Application.Run</c>。
/// 汇总不比对具体像素，只断言「找得到 / 抬得上去 / 同名判断」。
/// </remarks>
internal static class Program
{
    private const string HostTitle = "LTools UiaPick Smoke";

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);

    private static int _pass, _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && string.Equals(args[0], "host", StringComparison.OrdinalIgnoreCase))
        {
            Host();
            return 0;
        }

        Run();

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    // ============ 父进程：断言 ============

    private static void Run()
    {
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("取不到自己");
        using var child = Process.Start(new ProcessStartInfo(exe, "host") { UseShellExecute = false })
                          ?? throw new InvalidOperationException("起不了子进程");

        try
        {
            IntPtr hwnd = WaitForWindow(child, TimeSpan.FromSeconds(20));
            Check("找到子进程窗口", hwnd != IntPtr.Zero);
            if (hwnd == IntPtr.Zero) return;

            Check("窗口标题是测试窗口", TitleOf(hwnd).Contains("UiaPick Smoke"));

            ControlTree tree = Snapshot(hwnd);
            Check("读控件树没报错", !tree.Unavailable, tree.Problem ?? "");
            Check("树里有控件", tree.Count > 3, $"实际 {tree.Count} 个");

            ControlTarget? ok = tree.Nodes.FirstOrDefault(n => n.AutomationId == "OkBtn");
            Check("按 AutomationId 找到「确定」", ok != null);
            if (ok == null) return;

            Check("名称是「确定」", ok.Name == "确定", ok.Name);
            Check("控件类型是 Button", ok.ControlType == "Button", ok.ControlType);
            Check("矩形有效", ok.Bounds.Width > 0 && ok.Bounds.Height > 0,
                $"{ok.Bounds.Width} × {ok.Bounds.Height}");
            Check("有窗口类名", WindowClassOf(hwnd).Length > 0);

            ControlTarget? cancel = tree.Nodes.FirstOrDefault(n => n.AutomationId == "CancelBtn");
            Check("找到「取消」", cancel != null);

            // 光标落在「确定」中心：可能先命中更深的内部元素，RollUp 必须抬回这个按钮
            var center = new DPoint(ok.Bounds.X + ok.Bounds.Width / 2,
                ok.Bounds.Y + ok.Bounds.Height / 2);
            int hit = AtPoint(tree, center);
            Check("光标下命中一个控件", hit >= 0, $"实际 {hit}");
            if (hit >= 0)
            {
                int target = RollUp(tree, hit);
                Check("往上抬之后是「确定」按钮", tree.Nodes[target].AutomationId == "OkBtn",
                    $"抬到 {tree.Nodes[target].ControlType} · {tree.Nodes[target].AutomationId}");
                Check("同名计数只有它自己", CountSame(tree, tree.Nodes[target]) == 1);
            }

            // 空白处（窗口右上角外侧）不该命中
            Check("窗口外的点不命中", AtPoint(tree, new DPoint(ok.Bounds.Right + 2000, ok.Bounds.Top)) < 0);

            Check("标题过滤能按窗口找到它",
                FilterTitle(tree, "确定").Any(n => n.AutomationId == "OkBtn"));
        }
        catch (Exception ex)
        {
            Check("未抛异常", false, ex.ToString());
        }
        finally
        {
            try { child.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
        }
    }

    // ============ 子进程：目标窗口 ============

    private static void Host()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = HostTitle,
            Width = 460,
            Height = 260,
            Left = 120,
            Top = 120,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(MakeButton("确定", "OkBtn"));
        panel.Children.Add(MakeButton("取消", "CancelBtn"));
        window.Content = panel;

        app.Run(window);
    }

    private static Button MakeButton(string text, string automationId)
    {
        var button = new Button
        {
            Content = text,
            Width = 120,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 12),
        };
        AutomationProperties.SetAutomationId(button, automationId);
        return button;
    }

    // ============ 反射调产品 internal 入口 ============

    private static readonly Type Targets =
        typeof(ControlTree).Assembly.GetType("LTools.Features.ControlTargets", throwOnError: true)!;

    private static ControlTree Snapshot(IntPtr hwnd) =>
        (ControlTree)Targets.GetMethod("Snapshot", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { hwnd, 12, 1000 })!;

    private static int AtPoint(ControlTree tree, DPoint point) =>
        (int)Targets.GetMethod("AtPoint", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { tree.Nodes, point })!;

    private static int RollUp(ControlTree tree, int index) =>
        (int)Targets.GetMethod("RollUp", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { tree.Nodes, index })!;

    private static int CountSame(ControlTree tree, ControlTarget node) =>
        (int)Targets.GetMethod("CountSame", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object[] { tree.Nodes, node })!;

    private static System.Collections.Generic.IReadOnlyList<ControlTarget> FilterTitle(
        ControlTree tree, string query) =>
        (System.Collections.Generic.IReadOnlyList<ControlTarget>)Targets
            .GetMethod("Filter", BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, new object?[] { tree.Nodes, query })!;

    // ============ 等窗口 / 读窗口 ============

    private static IntPtr WaitForWindow(Process process, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            process.Refresh();
            if (process.HasExited) return IntPtr.Zero;

            IntPtr hwnd = process.MainWindowHandle;
            if (hwnd != IntPtr.Zero && TitleOf(hwnd).Contains("UiaPick Smoke")) return hwnd;

            Thread.Sleep(120);
        }

        return IntPtr.Zero;
    }

    private static string TitleOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    private static string WindowClassOf(IntPtr hwnd)
    {
        var text = new StringBuilder(256);
        GetClassName(hwnd, text, text.Capacity);
        return text.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? "  [" + detail + "]" : ""));
        if (ok) _pass++; else _fail++;
    }
}
