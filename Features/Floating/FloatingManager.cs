using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LTools.Config;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features.Floating;

/// <summary>
/// 悬浮动作 / 悬浮分组的窗口管理器：创建 / 关闭 / 恢复 / 吸附 / 拖到一起成组。
/// </summary>
/// <remarks>
/// <para>
/// 所有方法必须在 UI 线程调用（WPF 窗口只能在创建它的线程上碰）。
/// 面板右键与设置页都是 UI 线程，直接调即可；配置侧的调用统一经 <see cref="OnUi"/> 兜底。
/// </para>
/// <para>
/// 悬浮项**全局常驻**：没有按前台进程显隐的机制（一期已确认），
/// 所以窗口一旦建出来就一直在桌面上，只有用户关它才会消失。
/// </para>
/// </remarks>
internal static class FloatingManager
{
    private const string LogSource = "Floating";

    private static readonly Dictionary<string, FloatWindow> Windows = new(StringComparer.Ordinal);

    /// <summary>启动时调用（悬浮项无状态，不恢复任何东西）。</summary>
    public static void Initialize()
    {
        // 悬浮项只在本次运行存在（与 Quicker 一致）：启动时不建窗口、也不读布局。
        AppLog.Info(LogSource, "悬浮项无状态：启动不恢复，等用户手动悬浮");
    }

    /// <summary>退出前收尾：关窗（没有要落盘的悬浮项，偏好另有保存路径）。</summary>
    public static void Shutdown()
    {
        foreach (var w in Windows.Values.ToList())
        {
            try { w.Close(); } catch { /* 已在关闭 */ }
        }
        Windows.Clear();
    }

    // —— 菜单入口 ——

    /// <summary>
    /// 「悬浮此动作」：已悬浮则取消（分组里的成员只摘掉自己）。
    /// </summary>
    public static void ToggleAction(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return;

        var action = ActionRegistry.FindById(actionId);
        if (action == null)
        {
            ToastService.Show("动作不存在，无法悬浮");
            return;
        }

        var existing = FloatingLibrary.FindByAction(actionId);
        if (existing != null)
        {
            if (!Windows.ContainsKey(existing.Id))
            {
                CreateWindow(existing);
                ToastService.Show($"已显示悬浮「{action.Name}」");
                return;
            }

            if (FloatingLibrary.IsGroup(existing))
            {
                FloatingLibrary.RemoveMember(existing.Id, actionId);
                RefreshItem(existing.Id);
            }
            else
            {
                Close(existing.Id);
            }
            ToastService.Show($"已取消悬浮「{action.Name}」");
            return;
        }

        var (x, y) = DefaultPosition();
        var item = FloatingLibrary.FloatAction(actionId, x, y);
        if (item == null)
        {
            ToastService.Show($"悬浮项已达上限（{FloatingLibrary.Prefs.MaxItems}），请先关闭一些");
            return;
        }

        CreateWindow(item);
        ToastService.Show($"已悬浮「{action.Name}」");
    }

    /// <summary>「悬浮此分组」：把分组里的动作一起放上桌面（已在别处悬浮的动作会跳过）。</summary>
    public static void FloatGroup(PanelGroupDto group)
    {
        if (group == null) return;
        if (group.Actions.Count == 0)
        {
            ToastService.Show("这个分组还没有动作");
            return;
        }

        var (x, y) = DefaultPosition();
        var item = FloatingLibrary.FloatGroup(group, x, y);
        if (item == null)
        {
            ToastService.Show($"没有可悬浮的动作（上限 {FloatingLibrary.Prefs.MaxItems} 或都已在桌面）");
            return;
        }

        CreateWindow(item);
        ToastService.Show(item.Members.Count > 1
            ? $"已悬浮分组「{group.Name}」（{item.Members.Count} 个动作）"
            : "已悬浮该分组的唯一动作");
    }

    /// <summary>能否「悬浮此分组」：至少有一个动作还没被悬浮。</summary>
    public static bool CanFloatGroup(PanelGroupDto group) =>
        group.Actions.Any(id => !FloatingLibrary.IsFloating(id));

    /// <summary>这个动作现在真的有一个窗口在桌面上（配置里有、但没建窗口 = 未显示）。</summary>
    public static bool IsOnDesktop(string actionId)
    {
        var item = FloatingLibrary.FindByAction(actionId);
        return item != null && Windows.ContainsKey(item.Id);
    }

    /// <summary>关闭一个悬浮项（整组）。</summary>
    public static void Close(string itemId)
    {
        if (Windows.Remove(itemId, out var window))
        {
            try { window.Close(); } catch { /* 已关 */ }
        }
        FloatingLibrary.RemoveItem(itemId);
    }

    /// <summary>关闭所有悬浮项（托盘 / 右键菜单）。</summary>
    public static void CloseAll()
    {
        OnUi(() =>
        {
            bool any = Windows.Count > 0 || FloatingLibrary.Items.Count > 0;
            foreach (var w in Windows.Values.ToList())
            {
                try { w.Close(); } catch { /* 已关 */ }
            }
            Windows.Clear();
            FloatingLibrary.Items.Clear();
            if (any) ToastService.Show("已关闭全部悬浮项");
        });
    }

    /// <summary>动作改名 / 改图标 / 删除后重建所有按钮。</summary>
    public static void RefreshAll()
    {
        OnUi(() =>
        {
            FloatingLibrary.CollectGarbage();
            foreach (var item in FloatingLibrary.Items.ToList())
                RefreshItem(item.Id);
        });
    }

    /// <summary>重建单个悬浮项的窗口内容（成员变化时）。</summary>
    public static void RefreshItem(string itemId)
    {
        OnUi(() =>
        {
            if (!Windows.TryGetValue(itemId, out var window))
            {
                // 窗口还没建（拆分出来的新项）就现建一个
                var pending = FloatingLibrary.Find(itemId);
                if (pending == null) return;
                CreateWindow(pending);
                return;
            }

            var current = FloatingLibrary.Find(itemId);
            if (current == null)
            {
                Windows.Remove(itemId);
                try { window.Close(); } catch { /* 已关 */ }
                return;
            }

            window.Rebuild();
            if (!window.IsVisible) window.Show();
            // 改尺寸 / 改方向 / 拆分后卡片可能超出屏幕，夹回来
            var (x, y) = ClampToScreen(window.Left, window.Top, window.Width, window.Height);
            window.Left = x;
            window.Top = y;
            window.Item.X = x;
            window.Item.Y = y;
        });
    }

    /// <summary>窗口自己关掉时（被系统关 / 退出）把引用摘掉，别让字典里留死窗口。</summary>
    internal static void ForgetWindow(FloatWindow window)
    {
        if (Windows.TryGetValue(window.ItemId, out var existing) && ReferenceEquals(existing, window))
            Windows.Remove(window.ItemId);
    }

    /// <summary>
    /// 把所有悬浮项顶回 topmost 带的最前面（不抢焦点）。
    /// </summary>
    /// <remarks>
    /// 悬浮窗和超级面板都是 <c>Topmost</c>，同一层里**谁最后 SetWindowPos 谁在前**：
    /// 面板一显示 / 一激活就会把悬浮按钮盖住，而 Quicker 的悬浮按钮永远显示在面板前面。
    /// 所以面板显示、激活之后（以及面板里再悬浮出新按钮时）调这里把悬浮项重新顶上去。
    /// 悬浮窗是 <c>WS_EX_NOACTIVATE</c>，顶上去也不会把焦点从面板抢走。
    /// </remarks>
    internal static void RaiseAllAbove()
    {
        foreach (var w in Windows.Values.ToList()) w.RaiseToTop();
    }

    // —— 右键菜单的「点外关闭」（由 <see cref="Hooks.MouseHook"/> 调）——
    //
    // 悬浮窗不是前台窗口，它的 ContextMenu popup 拿不到**有效**的鼠标捕获：
    // Windows 只让前台窗口捕获「窗口之外」的点击，所以 WPF 自己那套
    // 「点外部关菜单」在悬浮窗上永远不触发（实测点外部一条鼠标消息都收不到）。
    // 只能照 SuperPanel / MenuSystem 的做法，用全局鼠标钩子按坐标判定。

    /// <summary>有没有悬浮窗的右键菜单开着。</summary>
    internal static bool IsMenuOpen => Windows.Values.Any(w => w.IsMenuOpen);

    /// <summary>这个点是否落在某个开着的菜单里（物理屏幕像素）。</summary>
    internal static bool MenuContains(int screenX, int screenY) =>
        Windows.Values.Any(w => w.IsMenuOpen && w.MenuContains(screenX, screenY));

    /// <summary>关掉所有开着的悬浮菜单。</summary>
    internal static void CloseOpenMenu()
    {
        foreach (var w in Windows.Values.ToList()) w.CloseMenuNow();
    }

    /// <summary>
    /// 钩子用：这一下点击若落在菜单外就关掉菜单。返回是否真的关掉了。
    /// 判定放在这里（而不是钩子里），冒烟可以直接调它验行为。
    /// </summary>
    internal static bool CloseMenuIfOutside(int screenX, int screenY)
    {
        if (!IsMenuOpen || MenuContains(screenX, screenY)) return false;
        CloseOpenMenu();
        return true;
    }

    /// <summary>动手改窗口前回到 UI 线程（配置侧可能从后台线程调过来）。</summary>
    private static void OnUi(Action action)
    {
        var app = Application.Current;
        if (app == null) return;
        if (app.Dispatcher.CheckAccess()) action();
        else app.Dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// 拖动结束：记录位置、吸附屏幕边缘、判定是不是靠近了另一个悬浮项（成组）。
    /// 判定用「矩形间距 ≤ 吸附阈值」——照用户要求：**靠近（不必压上去）就合并成分组**。
    /// </summary>
    internal static void OnDragFinished(FloatWindow window)
    {
        var item = FloatingLibrary.Find(window.ItemId);
        if (item == null) return;

        window.SyncPosition();
        SnapToEdges(window);
        window.SyncPosition();

        var target = FindMergeTarget(window);
        if (target != null)
        {
            var merged = FloatingLibrary.Merge(window.ItemId, target.ItemId);
            if (merged != null)
            {
                Windows.Remove(window.ItemId);
                try { window.Close(); } catch { /* 已关 */ }
                SnapToEdges(target);
                target.SyncPosition();
                RefreshItem(target.ItemId);
                ToastService.Show($"已合并为悬浮分组（{merged.Members.Count} 个动作）");
                return;
            }
        }
    }

    // —— 窗口创建 / 恢复 ——

    private static FloatWindow CreateWindow(FloatingItemDto item)
    {
        if (Windows.TryGetValue(item.Id, out var existing)) return existing;

        var (width, height) = FloatWindow.MeasureItem(item);
        var (x, y) = ClampToScreen(item.X, item.Y, width, height);
        item.X = x;
        item.Y = y;

        var window = new FloatWindow(item);
        Windows[item.Id] = window;
        window.Show();
        return window;
    }

    // —— 位置 ——

    /// <summary>
    /// 新悬浮项的落点：**鼠标指针处**（照 Quicker 的 <c>FloatAction</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Quicker 的 <c>zEw7.FloatAction(action, ownerWindow)</c> 调
    /// <c>InternalAppHelper.GetTopLeftScreenPositionBasedOnMouseAndVisualOffset(ownerWindow, 40×40)</c>：
    /// 取 <c>GetCursorPos</c> 再减 40 DIP / 当前 DPI 缩放 → **按钮左上角在指针左上方 40 DIP**
    /// （指针因此落在按钮里偏右下，不是中心）。
    /// </para>
    /// <para>
    /// 我们没有它的布局引擎（Quicker 会把落在同一处的按钮自动吸附成链接），
    /// 原地叠两个会完全看不见，所以**这个点被占了就按格子往右、再往下一格**；
    /// 只有用户主动拖到一起才会合并成分组。
    /// </para>
    /// </remarks>
    internal static (double X, double Y) DefaultPosition()
    {
        var cell = FloatWindow.Footprint(FloatingLibrary.Prefs.DefaultSize);
        var (cx, cy) = CursorInDip();

        // 拿不到光标（极少数）时退回主屏右上角
        double baseX = double.IsNaN(cx) ? SystemParameters.WorkArea.Right - 24 - cell.Width : cx - 40;
        double baseY = double.IsNaN(cy) ? SystemParameters.WorkArea.Top + 24 : cy - 40;

        double stepX = cell.Width + 8;
        double stepY = cell.Height + 8;
        (double X, double Y) spot = (baseX, baseY);
        for (int i = 0; i < 12; i++)
        {
            spot = (baseX + i % 4 * stepX, baseY + i / 4 * stepY);
            if (!Occupied(spot.X, spot.Y, cell.Width, cell.Height)) break;
        }
        return spot;
    }

    /// <summary>光标位置换算成 WPF 的 DIP（GetCursorPos 给的是物理像素）。</summary>
    private static (double X, double Y) CursorInDip()
    {
        if (!Win32.GetCursorPos(out var pt)) return (double.NaN, double.NaN);

        double scale = 1.0;
        var monitor = Win32.MonitorFromPoint(
            new Win32.Point { X = pt.X, Y = pt.Y }, Win32.MonitorDefaultToNearest);
        if (monitor != IntPtr.Zero
            && Win32.GetDpiForMonitor(monitor, Win32.MdtEffectiveDpi, out uint dpiX, out _) == 0
            && dpiX > 0)
        {
            scale = dpiX / 96.0;
        }

        return (pt.X / scale, pt.Y / scale);
    }

    /// <summary>这个落点会不会压住已有的悬浮项（只看内存里的项，新建项还没加进去）。</summary>
    private static bool Occupied(double x, double y, double width, double height)
    {
        var candidate = new Rect(x, y, width, height);
        foreach (var item in FloatingLibrary.Items)
        {
            var (w, h) = FloatWindow.MeasureItem(item);
            if (candidate.IntersectsWith(new Rect(item.X, item.Y, w, h))) return true;
        }
        return false;
    }

    /// <summary>整块都在屏幕外时的兜底落点（主屏右上角）。</summary>
    private static (double X, double Y) FallbackPosition()
    {
        var wa = SystemParameters.WorkArea;
        var cell = FloatWindow.Footprint(FloatingLibrary.Prefs.DefaultSize);
        return (wa.Right - 24 - cell.Width, wa.Top + 24);
    }

    /// <summary>把位置夹回虚拟桌面；整块都在屏幕外（拔了显示器）时回到兜底落点。</summary>
    internal static (double X, double Y) ClampToScreen(double x, double y, double width, double height)
    {
        var fallback = FloatWindow.Footprint(FloatingLibrary.Prefs.DefaultSize);
        if (width <= 0) width = fallback.Width;
        if (height <= 0) height = fallback.Height;

        double vl = SystemParameters.VirtualScreenLeft;
        double vt = SystemParameters.VirtualScreenTop;
        double vr = vl + SystemParameters.VirtualScreenWidth;
        double vb = vt + SystemParameters.VirtualScreenHeight;

        if (double.IsNaN(x) || double.IsNaN(y) || x + width < vl || x > vr || y + height < vt || y > vb)
            return FallbackPosition();

        return (Math.Clamp(x, vl, Math.Max(vl, vr - width)),
                Math.Clamp(y, vt, Math.Max(vt, vb - height)));
    }

    /// <summary>吸附到屏幕工作区边缘（窗口在哪个显示器由位置决定，这里用虚拟桌面 + 主屏工作区）。</summary>
    private static void SnapToEdges(FloatWindow window)
    {
        if (!FloatingLibrary.Prefs.SnapToEdges) return;

        double d = FloatingLibrary.Prefs.SnapDistance;
        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth;
        double bottom = top + SystemParameters.VirtualScreenHeight;

        var wa = SystemParameters.WorkArea;
        if (window.Top >= wa.Top && window.Top <= wa.Bottom)
        {
            top = Math.Max(top, wa.Top);
            bottom = Math.Min(bottom, wa.Bottom);
        }

        if (Math.Abs(window.Left - left) < d) window.Left = left;
        else if (Math.Abs(right - (window.Left + window.Width)) < d) window.Left = right - window.Width;

        if (Math.Abs(window.Top - top) < d) window.Top = top;
        else if (Math.Abs(bottom - (window.Top + window.Height)) < d) window.Top = bottom - window.Height;
    }

    /// <summary>
    /// 拖到附近的另一个悬浮项：**矩形间距 ≤ 吸附阈值**或**重叠面积超过被拖项一半**都算。
    /// 「靠近就合并」是用户明确要的（Quicker 是贴住后一起移动，这里选更省事的分组）。
    /// </summary>
    private static FloatWindow? FindMergeTarget(FloatWindow dragged)
    {
        Rect source = dragged.Bounds;
        if (source.Width <= 0 || source.Height <= 0) return null;

        double sourceArea = source.Width * source.Height;
        double snap = Math.Max(1, FloatingLibrary.Prefs.SnapDistance);

        FloatWindow? best = null;
        double bestScore = double.MaxValue;

        foreach (var other in Windows.Values)
        {
            if (ReferenceEquals(other, dragged) || !other.IsVisible) continue;
            Rect target = other.Bounds;

            // 两矩形之间的最短距离（重叠时为 0）
            double dx = Math.Max(0, Math.Max(source.Left - target.Right, target.Left - source.Right));
            double dy = Math.Max(0, Math.Max(source.Top - target.Bottom, target.Top - source.Bottom));
            double gap = Math.Sqrt(dx * dx + dy * dy);

            Rect overlap = Rect.Intersect(source, target);
            double ratio = overlap.IsEmpty ? 0 : overlap.Width * overlap.Height / sourceArea;

            bool near = gap <= snap;
            bool covered = ratio > 0.5;
            if (!near && !covered) continue;

            // 重叠越多 / 间距越小越优先
            double score = near ? gap : -ratio * 1000;
            if (score < bestScore)
            {
                bestScore = score;
                best = other;
            }
        }
        return best;
    }
}
