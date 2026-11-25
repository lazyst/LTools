using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Native;

namespace LTools.Views.Controls;

/// <summary>
/// 右键菜单的「预测锥」（Fitts 定律下的 safe triangle）：子菜单打开后，从指针离开父项的位置
/// （锥顶）到子菜单靠近菜单那条边（锥底）围出一个三角形通道。
/// </summary>
/// <remarks>
/// <para>
/// 指针**斜着**从父项走向子菜单时，中途一定会划过相邻的菜单项。没有这个通道时：
/// ① <see cref="MenuFx"/> 的「悬停立即展开」会当场把邻项的子菜单顶出来（用户根本没想选它）；
/// ② 邻项被点亮、父项的子菜单被 WPF 的 <c>MenuShowDelay</c>（默认 400ms）收起，
/// 指针走到一半子菜单就没了。
/// </para>
/// <para>
/// 通道里只做三件事：不换子菜单、压掉被划过那一项的高亮（<see cref="MenuFx.ConePassOver"/>）、
/// 顶住 WPF 的收起计时器（它会把 <c>IsSubmenuOpen</c> 改回 false，这里再打开）。
/// 指针到了子菜单里不会拆锥——它转成「回程待命」，从子菜单沿原路返回一级菜单时用的正是同一条
/// 通道（2026-11-26 修：以前一进子菜单就拆，回来的路上没人保护，慢了就被 <see cref="MenuFx.HoverCloseMs"/>
/// 收掉子菜单）；指针回到父项、离开通道、或者进到下一级子菜单时，锥才让位。
/// 在通道里**停住**超过 <see cref="DwellMs"/> 说明用户其实想选指针下那一项，也让它接管。
/// </para>
/// <para>
/// 坐标一律屏幕**物理像素**（<c>PointToScreen</c> 与 <c>GetCursorPos</c> 同一套，与截图 / 鼠标注入一致）。
/// 几何部分是纯函数（<see cref="InTriangle"/> / <see cref="NearEdge"/>），<c>--selftest</c> 直接断言；
/// 真机行为（斜穿不换项 / 停住让位）靠仓库外的 <c>menu-cone-smoke</c>。
/// </para>
/// </remarks>
internal static class MenuCone
{
    /// <summary>在通道里停住多久就当成「想选指针下这一项」（毫秒）。可写字段：冒烟会临时调。</summary>
    internal static int DwellMs = 450;

    /// <summary>轮询间隔（毫秒）：30ms 足够跟手，又不至于占满 Dispatcher。</summary>
    private const int WatchMs = 30;

    /// <summary>硬上限：万一几何算错 / 窗口状态异常，锥也不能一直压着菜单（毫秒）。</summary>
    private const int MaxHoldMs = 2500;

    /// <summary>锥底向两侧各外扩多少（物理像素）：手抖出边一点点仍然算在通道里。</summary>
    internal const double Spread = 16;

    private static MenuItem? _owner;
    private static Rect _ownerRect;
    private static Rect _childRect;
    private static Point _baseA;
    private static Point _baseB;
    private static Point _apex;
    private static Point _last;
    private static bool _ready;
    private static int _stillMs;
    private static int _heldMs;
    private static MenuItem? _skimmed;
    private static DispatcherTimer? _watch;

    /// <summary>锥是否正在生效。</summary>
    public static bool Active => _owner != null;

    /// <summary>
    /// 子菜单刚打开：布锥。
    /// </summary>
    /// <remarks>
    /// 锥顶取**这一刻**的指针位置（悬停 / 点开父项时它一定在父项里），**不要**在
    /// <c>MouseLeave</c> 里再更新：WPF 的 <c>MouseLeave</c> 会晚一拍（实测指针已经走到目标项上才发），
    /// 锥顶会落到「要去的下一个项」上，锥就再也放不开了。
    /// </remarks>
    public static void Arm(MenuItem owner)
    {
        if (ReferenceEquals(_owner, owner)) return;

        // 祖先的 SubmenuOpened 会在 WPF 重开它那层 popup 时再来一次：多级菜单里指针已经在
        // 更深的子菜单（三级）里，这时**不能让浅层的锥盖掉深层的**——否则 `Tick` 会按一级的
        // 几何把「指针在三级」判成「离开通道」，`HandOff` 把整支收掉（2026-11-26 用户报
        // 「从二级往回走，整片菜单关了」，真机上就是 `top` 的子菜单被收、二级 + 三级一起消失）。
        if (IsAncestorOf(owner, _owner)) return;

        Disarm();

        _owner = owner;
        _apex = Pointer();
        _last = _apex;
        _ready = false;
        _stillMs = 0;
        _heldMs = 0;

        _watch ??= NewWatch();
        _watch.Start();

        // 子菜单偶尔在第一拍就已经量好尺寸：先算一次，别让「指针已经斜穿出去」赶在 30ms 轮询之前
        EnsureGeometry();
    }

    /// <summary>父项自己收起来了、或者菜单整体关了：忘掉这条锥。</summary>
    public static void Forget(MenuItem owner)
    {
        if (ReferenceEquals(_owner, owner)) Disarm();
    }

    /// <summary>锥正在「压着」这个父项（它的子菜单要留着，别让 <see cref="MenuFx"/> 的收起宽限收掉）。</summary>
    public static bool HoldsOwner(MenuItem owner)
    {
        if (!ReferenceEquals(_owner, owner)) return false;
        if (!Inside(out bool inOwner, out bool inChild)) return false;
        return !inOwner && !inChild;
    }

    /// <summary>
    /// 这个父项现在不该因为悬停而展开：指针正在往**另一个**子菜单走的通道里，
    /// 而且它是同层的邻项（子菜单里的项永远不挡，那是下一层）。
    /// </summary>
    public static bool BlocksOpen(MenuItem item)
    {
        var owner = _owner;
        if (owner == null || ReferenceEquals(owner, item)) return false;
        if (!SameLevel(owner, item)) return false;
        if (!Inside(out bool inOwner, out bool inChild)) return false;
        return !inOwner && !inChild;
    }

    /// <summary>屏幕物理像素矩形：<c>PointToScreen</c> 给物理像素，尺寸要按 DPI 换算。</summary>
    internal static Rect ScreenRect(FrameworkElement element)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            var origin = element.PointToScreen(new Point(0, 0));
            double w = Math.Max(0, element.RenderSize.Width) * dpi.DpiScaleX;
            double h = Math.Max(0, element.RenderSize.Height) * dpi.DpiScaleY;
            return new Rect(origin, new Size(w, h));
        }
        catch
        {
            // 还没连上 PresentationSource（刚构造 / 已销毁）：当作空矩形，调用方按「不生效」处理
            return Rect.Empty;
        }
    }

    /// <summary>子菜单的 popup 内容（独立窗口，父项的 <c>IsMouseOver</c> 覆盖不到）。</summary>
    internal static UIElement? PopupChild(MenuItem owner) =>
        owner.Template?.FindName("PART_Popup", owner) is System.Windows.Controls.Primitives.Popup { Child: UIElement child }
            ? child
            : null;

    /// <summary>点是否落在三角形里（含边界；两种绕法都认，退化三角形不算）。</summary>
    internal static bool InTriangle(Point apex, Point a, Point b, Point p)
    {
        static double Cross(Point o, Point u, Point v) =>
            (u.X - o.X) * (v.Y - o.Y) - (u.Y - o.Y) * (v.X - o.X);

        // 三点共线 / 重合：没有「通道」可言，一律不算
        if (Math.Abs(Cross(apex, a, b)) < 1e-6) return false;

        double d1 = Cross(apex, a, p);
        double d2 = Cross(a, b, p);
        double d3 = Cross(b, apex, p);

        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    /// <summary>
    /// 子菜单与父项相邻的那条边（锥底）：看子菜单整体在父项的哪一侧
    /// （左右相差大就取竖边、上下相差大就取横边），两端各向外扩 <see cref="Spread"/>。
    /// </summary>
    /// <remarks>
    /// 不用「离锥顶最近的边」：锥顶永远贴着父项，按距离算会被子菜单的上下角带偏，
    /// 而菜单的相邻关系是确定的（子菜单在右边就一定是左边那条竖边）。
    /// </remarks>
    internal static (Point A, Point B) NearEdge(Rect submenu, Rect owner)
    {
        if (submenu.IsEmpty) return (new Point(0, 0), new Point(0, 0));

        double ox = owner.IsEmpty ? submenu.Left - 1 : owner.Left + owner.Width / 2;
        double oy = owner.IsEmpty ? submenu.Top - 1 : owner.Top + owner.Height / 2;
        double dx = submenu.Left + submenu.Width / 2 - ox;
        double dy = submenu.Top + submenu.Height / 2 - oy;

        if (Math.Abs(dx) >= Math.Abs(dy))
        {
            return dx >= 0
                ? (new Point(submenu.Left, submenu.Top - Spread), new Point(submenu.Left, submenu.Bottom + Spread))
                : (new Point(submenu.Right, submenu.Top - Spread), new Point(submenu.Right, submenu.Bottom + Spread));
        }

        return dy >= 0
            ? (new Point(submenu.Left - Spread, submenu.Top), new Point(submenu.Right + Spread, submenu.Top))
            : (new Point(submenu.Left - Spread, submenu.Bottom), new Point(submenu.Right + Spread, submenu.Bottom));
    }

    /// <summary>指针（屏幕物理像素）。</summary>
    private static Point Pointer() =>
        Win32.GetCursorPos(out var pt) ? new Point(pt.X, pt.Y) : _last;

    /// <summary>锥是否覆盖指针；顺带给出「在父项上」「已经在子菜单里」两个短路条件。</summary>
    private static bool Inside(out bool inOwner, out bool inChild)
    {
        inOwner = false;
        inChild = false;

        if (_owner == null) return false;

        EnsureGeometry();
        if (!_ready) return false;

        var p = Pointer();
        inOwner = _ownerRect.Contains(p);
        inChild = _childRect.Contains(p);
        if (inOwner || inChild) return true;

        return InTriangle(_apex, _baseA, _baseB, p);
    }

    private static void EnsureGeometry()
    {
        if (_ready) return;

        var owner = _owner;
        if (owner == null) return;
        if (PopupChild(owner) is not FrameworkElement child) return;
        if (child.RenderSize.Width <= 0 || child.RenderSize.Height <= 0) return;

        var ownerRect = ScreenRect(owner);
        var childRect = ScreenRect(child);
        if (ownerRect.IsEmpty || childRect.IsEmpty) return;

        _ownerRect = ownerRect;
        _childRect = childRect;
        (_baseA, _baseB) = NearEdge(childRect, ownerRect);
        _ready = true;
    }

    private static void Tick()
    {
        var owner = _owner;
        if (owner == null)
        {
            Stop();
            return;
        }

        _heldMs += WatchMs;

        // 整个菜单关了（Esc / 点了某一项 / 点了外面）：锥跟着结束
        var root = RootMenu(owner);
        if (root != null && !root.IsOpen)
        {
            Disarm();
            return;
        }

        var p = Pointer();
        if (p != _last)
        {
            _stillMs = 0;
            _last = p;
        }
        else
        {
            _stillMs += WatchMs;
        }

        // 子菜单还没量出尺寸（刚打开那一两拍）：先等几何，别急着判断
        EnsureGeometry();
        if (!_ready)
        {
            if (_heldMs >= MaxHoldMs) Disarm();
            return;
        }

        bool inOwner = _ownerRect.Contains(p);
        bool inChild = _childRect.Contains(p);

        // 已经在子菜单里：锥进入「回程待命」，**不要拆掉**。
        // 从子菜单沿原路回到一级菜单用的正是这条通道：拆了就没有谁保护它——MenuFx 的
        // HoverCloseMs 宽限一到就把子菜单收掉，用户看到的就是「从二级往回走，菜单自己关了」。
        // 指针进了下一级子菜单时，那一级的 Arm 会 Disarm 这一条。
        if (inChild)
        {
            _heldMs = 0;
            SetSkimmed(null);
            return;
        }

        // 在父项上：锥保持待命（不计超时——用户可能慢慢挑，随时会斜穿出去）
        if (inOwner)
        {
            _heldMs = 0;
            SetSkimmed(null);
            return;
        }

        // 兜底：几何再准也不能一直压着菜单
        if (_heldMs >= MaxHoldMs)
        {
            HandOff(owner, null, p);
            return;
        }

        // 离开通道：把选择权交还给指针下的那一项（立刻展开，不等系统的 400ms）
        if (!InTriangle(_apex, _baseA, _baseB, p))
        {
            HandOff(owner, ItemAt(p, owner), p);
            return;
        }

        // 在通道里：顶住 WPF 的收起（它 400ms 后会把 IsSubmenuOpen 改回 false）
        if (!owner.IsSubmenuOpen) owner.IsSubmenuOpen = true;

        var hit = ItemAt(p, owner);
        SetSkimmed(hit);

        // WPF 自己的 400ms 悬停展开也压掉：通道里不许冒出第二个子菜单
        if (hit != null && hit.IsSubmenuOpen) hit.IsSubmenuOpen = false;

        // 停住了：用户其实想选指针下这一项，让位
        if (_stillMs >= DwellMs && hit != null) HandOff(owner, hit, p);
    }

    /// <summary>
    /// 锥结束、把选择权交给指针下的那一项：先收掉这条锥的父项子菜单（指针已经进到里面时保留），
    /// 再展开「下面那一项」的子菜单。指针停在菜单外（<paramref name="under"/> 为 null）时只收不展。
    /// </summary>
    /// <remarks>
    /// 必须显式收父项：锥顶住过 WPF 的收起计时器，那个计时器不会再自己起来，
    /// 不收的话指针离开菜单后子菜单会一直挂在那里。
    /// </remarks>
    private static void HandOff(MenuItem owner, MenuItem? under, Point p)
    {
        bool keepOwner = _ready && (_ownerRect.Contains(p) || _childRect.Contains(p));
        Disarm();
        if (!keepOwner)
        {
            // 先把焦点留在菜单里，再收子菜单：程序化关掉弹出窗口时 WPF 会把键盘焦点还给
            // owner 窗口（面板），面板的 Activated → FocusSearch 又把它抢到搜索框，
            // 根菜单的 IsKeyboardFocusWithin 变 false → WPF 把整份菜单关掉（2026-11-26 用户报）。
            try { owner.Focus(); } catch { /* 菜单正在关 */ }
            owner.IsSubmenuOpen = false;
        }
        if (under != null && !ReferenceEquals(under, owner) && under.HasItems)
            under.IsSubmenuOpen = true;
    }

    private static void Disarm()
    {
        SetSkimmed(null);
        _owner = null;
        _ready = false;
        _stillMs = 0;
        _heldMs = 0;
        Stop();
    }

    private static void Stop()
    {
        if (_watch?.IsEnabled == true) _watch.Stop();
    }

    private static void SetSkimmed(MenuItem? item)
    {
        if (ReferenceEquals(_skimmed, item)) return;
        if (_skimmed != null) MenuFx.SetConePassOver(_skimmed, false);
        _skimmed = item;
        if (_skimmed != null)
        {
            try { MenuFx.SetConePassOver(_skimmed, true); } catch { /* 正在关闭 */ }
        }
    }

    /// <summary>指针下同层的那一项（不算父项自己；分隔符 / 空位返回 null）。</summary>
    private static MenuItem? ItemAt(Point point, MenuItem owner)
    {
        var parent = ItemsControl.ItemsControlFromItemContainer(owner);
        if (parent == null) return null;

        for (int i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not MenuItem item) continue;
            if (ReferenceEquals(item, owner) || !item.IsVisible) continue;
            if (item.RenderSize.Width <= 0 || item.RenderSize.Height <= 0) continue;
            if (ScreenRect(item).Contains(point)) return item;
        }
        return null;
    }

    private static bool SameLevel(MenuItem a, MenuItem b) =>
        ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(a),
            ItemsControl.ItemsControlFromItemContainer(b));

    /// <summary><paramref name="candidate"/> 是不是 <paramref name="item"/> 的祖先（含它自己）。</summary>
    private static bool IsAncestorOf(MenuItem candidate, MenuItem? item)
    {
        for (MenuItem? cur = item; cur != null; cur = ParentOf(cur))
            if (ReferenceEquals(cur, candidate)) return true;
        return false;
    }

    private static MenuItem? ParentOf(MenuItem item) =>
        ItemsControl.ItemsControlFromItemContainer(item) as MenuItem;

    /// <summary>这条锥属于哪个 <see cref="ContextMenu"/>（往上找，子菜单里的项也能找到）。</summary>
    private static ContextMenu? RootMenu(MenuItem owner)
    {
        ItemsControl? current = ItemsControl.ItemsControlFromItemContainer(owner);
        while (current != null)
        {
            if (current is ContextMenu menu) return menu;
            current = ItemsControl.ItemsControlFromItemContainer(current);
        }
        return null;
    }

    private static DispatcherTimer NewWatch()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(WatchMs),
        };
        timer.Tick += (_, _) => Tick();
        return timer;
    }
}
