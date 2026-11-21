using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

/// <summary>
/// 右键菜单「预测锥」冒烟：真实鼠标斜穿相邻菜单项、走向子菜单的下半部分时，
/// 子菜单必须保持打开、邻项不能顶掉它；明确移动到别的父项才换。
/// </summary>
internal static class Program
{
    private const BindingFlags Any =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MouseEventMove = 0x0001;

    private static int _pass, _fail;
    private static MethodInfo _submenu = null!;
    private static Assembly _lt = null!;
    private static Type _cone = null!;
    private static Type _menuFx = null!;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        _lt = Assembly.LoadFrom(System.IO.Path.Combine(AppContext.BaseDirectory, "LTools.dll"));
        _cone = _lt.GetType("LTools.Views.Controls.MenuCone", true)!;
        var menuFx = _lt.GetType("LTools.Views.Controls.MenuFx", true)!;
        _menuFx = menuFx;
        _submenu = menuFx.GetMethods(Any).First(m => m.Name == "Submenu" && m.GetParameters().Length == 2
                                                    && m.GetParameters()[1].ParameterType == typeof(object));

        GetCursorPos(out var saved);
        try { Run(); }
        catch (Exception ex) { Check("未抛异常", false, ex.ToString()); }
        finally { SetCursorPos(saved.X, saved.Y); }

        Console.WriteLine();
        Console.WriteLine($"结果：pass={_pass} fail={_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static void Run()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint };
        MenuItem a = Submenu(menu, "运行");
        AddLeaf(a, "运行 · 一项");
        AddLeaf(a, "运行 · 二项");
        AddLeaf(a, "运行 · 三项");
        AddLeaf(a, "运行 · 四项");
        AddLeaf(a, "运行 · 五项");
        AddLeaf(a, "运行 · 六项");
        MenuItem b = Submenu(menu, "按钮大小");
        AddLeaf(b, "按钮大小 · 一项");
        MenuItem c = Submenu(menu, "不透明度");
        AddLeaf(c, "不透明度 · 一项");
        AddLeaf(menu, "删除");

        SetCursorPos(360, 220);
        Pump(80);
        menu.IsOpen = true;
        Pump(250);
        Check("菜单打开了", menu.IsOpen);

        var aRect = Rect(a);
        var bRect = Rect(b);
        var cRect = Rect(c);
        Console.WriteLine($"菜单项 A={Fmt(aRect)} B={Fmt(bRect)} C={Fmt(cRect)}");

        // ① 悬停 A → 立即开
        MoveTo(Center(aRect));
        Pump(250);
        Check("悬停父项立即展开子菜单", a.IsSubmenuOpen, $"A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen}");
        Check("没有打开邻项的子菜单", !b.IsSubmenuOpen && !c.IsSubmenuOpen);

        var child = PopupChild(a);
        Check("拿到子菜单窗体", child != null);
        if (child == null) return;
        var childRect = Rect(child);
        Console.WriteLine($"A 子菜单 {Fmt(childRect)}");

        // ② 斜向走到子菜单**下半部分**：中途会划过 B / C 的右侧区域
        var target = new Point(childRect.Left + childRect.Width / 2, childRect.Bottom - 14);
        var start = Center(aRect);
        // 分 12 步慢慢走（每步 30ms，总 ~360ms —— 超过 WPF 的 MenuShowDelay，正是原来会换项的场景）
        for (int i = 1; i <= 12; i++)
        {
            MoveTo(new Point(start.X + (target.X - start.X) * i / 12.0,
                             start.Y + (target.Y - start.Y) * i / 12.0));
            Pump(30);
            if (i == 6)
                Console.WriteLine($"中途（第 6 步）：A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen} C.sub={c.IsSubmenuOpen}");
        }
        Pump(120);
        Check("斜穿邻项后子菜单仍在", a.IsSubmenuOpen, $"A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen} C.sub={c.IsSubmenuOpen}");
        Check("斜穿没有换到邻项", !b.IsSubmenuOpen && !c.IsSubmenuOpen);
        Check("指针已落在子菜单里", child.IsMouseOver, $"child.IsMouseOver={child.IsMouseOver}");

        // ③ 在子菜单里安静待一会儿（原来 WPF 的 400ms 关闭计时器会把它收掉）
        Pump(600);
        Check("在子菜单里停留后仍然打开", a.IsSubmenuOpen, $"A.sub={a.IsSubmenuOpen}");

        // ④ 明确移到邻项（离开锥形通道，往菜单左侧走）：应该换过去
        MoveTo(new Point(bRect.Left + 12, bRect.Top + bRect.Height / 2));
        Pump(450);
        Check("明确移到邻项才换子菜单", !a.IsSubmenuOpen && b.IsSubmenuOpen,
            $"A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen}");

        // ⑤ 回到 A：明确移到另一个父项，必须立刻换回来（不能因为上一条锥卡住）
        MoveTo(Center(aRect));
        Pump(400);
        Check("回到 A 重新展开", a.IsSubmenuOpen,
            $"A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen} cone={ConeState()}");

        // ⑥ 这次「斜穿时停在邻项右侧」——那一点在锥形通道里：
        //    短停子菜单要保住；停过宽限说明用户真想选 B，让位
        var skim = new Point(bRect.Right - 20, bRect.Top + bRect.Height / 2);
        MoveTo(skim);
        Pump(140);
        Check("停在锥内（邻项上）子菜单暂时保持", a.IsSubmenuOpen && !b.IsSubmenuOpen,
            $"停 140ms：A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen} at={skim.X:0},{skim.Y:0}");
        Check("被划过的邻项不上色（ConePassOver 生效）", b.IsHighlighted && PassOver(b) && BackOf(b) == Colors.Transparent,
            $"B.IsHighlighted={b.IsHighlighted} passOver={PassOver(b)} bg={BackOf(b)}");
        Pump(520);
        Check("停住超过宽限后让位给邻项", b.IsSubmenuOpen,
            $"停 660ms：A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen}");

        // ⑦ 从菜单里横向走出去：锥顶住过 WPF 的收起计时器，所以离开时必须自己把子菜单收掉，
        //    不能留下它挂在那儿（未选中任何东西，指针早已不在菜单上）
        MoveTo(Center(aRect));
        Pump(350);
        Check("⑦ 前置：A 的子菜单又开了", a.IsSubmenuOpen, $"A.sub={a.IsSubmenuOpen}");
        MoveTo(new Point(aRect.Left - 140, aRect.Top + aRect.Height / 2));
        Pump(450);
        Check("横向离开菜单后子菜单收起", !a.IsSubmenuOpen && !b.IsSubmenuOpen && !c.IsSubmenuOpen,
            $"A.sub={a.IsSubmenuOpen} B.sub={b.IsSubmenuOpen} C.sub={c.IsSubmenuOpen}");

        menu.IsOpen = false;
    }

    private static MenuItem Submenu(ItemsControl menu, string header) =>
        (MenuItem)_submenu.Invoke(null, new object?[] { menu, header })!;

    private static MenuItem AddLeaf(ItemsControl menu, string header)
    {
        var mi = new MenuItem { Header = header };
        menu.Items.Add(mi);
        return mi;
    }

    private static UIElement? PopupChild(MenuItem mi) =>
        mi.Template?.FindName("PART_Popup", mi) is Popup { Child: UIElement e } ? e : null;

    /// <summary>元素在屏幕上的物理像素矩形（PointToScreen 实测给的就是物理像素）。</summary>
    private static Rect Rect(FrameworkElement e)
    {
        try
        {
            var p = e.PointToScreen(new Point(0, 0));
            return new Rect(p, new Size(e.ActualWidth * Scale(e), e.ActualHeight * Scale(e)));
        }
        catch
        {
            return System.Windows.Rect.Empty;   // popup 已经拆掉 / 还没连上 PresentationSource
        }
    }

    private static double Scale(FrameworkElement e) => VisualTreeHelper.GetDpi(e).DpiScaleX;

    private static Rect Rect(UIElement e)
    {
        var fe = (FrameworkElement)e;
        return Rect(fe);
    }

    private static Point Center(Rect r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    private static void MoveTo(Point physical)
    {
        SetCursorPos((int)Math.Round(physical.X), (int)Math.Round(physical.Y));
        mouse_event(MouseEventMove, 1, 0, 0, IntPtr.Zero);
        mouse_event(MouseEventMove, -1, 0, 0, IntPtr.Zero);
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static string ConeState()
    {
        try
        {
            object? owner = _cone.GetField("_owner", Any)?.GetValue(null);
            object? apex = _cone.GetField("_apex", Any)?.GetValue(null);
            bool ready = (bool)(_cone.GetField("_ready", Any)?.GetValue(null) ?? false);
            string header = owner is MenuItem mi ? mi.Header?.ToString() ?? "?" : "-";
            return $"{header} ready={ready} apex={(Point?)apex}";
        }
        catch (Exception ex) { return "ERR " + ex.Message; }
    }

    /// <summary>MenuFx.ConePassOver 附加属性：锥划过这一项时置 true（主题据此压掉高亮）。</summary>
    private static bool PassOver(MenuItem mi)
    {
        if (_menuFx.GetField("ConePassOverProperty", Any)?.GetValue(null) is not DependencyProperty prop)
            return false;
        return mi.GetValue(prop) is true;
    }

    /// <summary>菜单项模板里那块底板（<c>bd</c>）当前背景色。</summary>
    private static Color BackOf(MenuItem mi) =>
        mi.Template?.FindName("bd", mi) is Border { Background: SolidColorBrush b } ? b.Color : Colors.Transparent;

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (detail != null ? "  [" + detail + "]" : ""));
        if (ok) _pass++; else _fail++;
    }

    private static string Fmt(Rect r) => $"{r.Left:0},{r.Top:0} {r.Width:0}×{r.Height:0}";
}
