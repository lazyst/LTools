using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图组：所有还开着的贴图窗口的登记处（分组 / 多选批量 / 「关闭全部贴图」/ 顶回最前 / 成组移动）。
/// </summary>
/// <remarks>
/// <para>
/// 贴图**不进配置**（与悬浮项同一口径）：关掉程序就没了，重启不恢复——
/// 它们是一次性的临时视图，不是用户资产。分组因此也只在内存里，组空了自动消失（默认组永远在）。
/// </para>
/// <para>
/// **切分组是隐藏不是关闭**：别的组只是 <c>Hide()</c>，切回来原位 <c>Show()</c>。
/// 所以凡是「把所有贴图一起做点什么」的地方（拖动参与者、全选、顶回最前）都必须按
/// <see cref="IsShown"/> 过滤，否则会把藏起来的贴图一起拖走 / 在看不见的分组上做批量。
/// </para>
/// <para>
/// 只在 UI 线程用：建窗口、<c>Show()</c>、关闭都必须在 WPF 的 Dispatcher 上。
/// 关窗由窗口自己 <see cref="Forget"/> 摘掉，所以用户的 ✕ / 双击 / 关闭全部三条路都对得上。
/// </para>
/// </remarks>
internal static class CapturePinManager
{
    private static readonly List<CapturePinWindow> Pins = new();

    /// <summary>分组名单（第一个永远是 <see cref="PinGroups.Default"/>）。</summary>
    private static readonly List<string> GroupList = new() { PinGroups.Default };

    /// <summary>多选中的贴图（批量操作的接收方）。</summary>
    private static readonly List<CapturePinWindow> Selection = new();

    /// <summary>拖动开始时的落点快照（成组移动按「起点 + 同一个位移」算，逐次累加会攒误差）。</summary>
    private static readonly List<(CapturePinWindow Pin, Rect Box)> MoveBase = new();

    private static CapturePinWindow? _moving;

    /// <summary>还开着的贴图数量（含被分组藏起来的）。</summary>
    public static int Count => Pins.Count;

    /// <summary>当前贴图（按出现的先后，含被分组藏起来的）。</summary>
    public static IReadOnlyList<CapturePinWindow> All => Pins;

    /// <summary>看得见的贴图（当前没有分组过滤 = 全部）。</summary>
    public static IReadOnlyList<CapturePinWindow> Visible => Pins.Where(IsShown).ToArray();

    /// <summary>分组名单（默认组在最前，其余按建的先后）。</summary>
    public static IReadOnlyList<string> Groups => GroupList;

    /// <summary>现在只看哪个分组（<c>null</c> = 显示全部）。</summary>
    public static string? ActiveGroup { get; private set; }

    /// <summary>多选中的贴图（批量关闭 / 另存 / 锁定 / 缩略图的接收方）。</summary>
    public static IReadOnlyList<CapturePinWindow> Selected => Selection;

    /// <summary>
    /// 成组移动：开着时拖任意一张，全组一起走（默认关，跟着进程，不落盘）。
    /// 只有一张时没有「组」可言，菜单里会置灰。
    /// </summary>
    public static bool GroupMove { get; set; }

    /// <summary>这张贴图现在该不该看得见（分组过滤）。</summary>
    public static bool IsShown(CapturePinWindow pin) =>
        ActiveGroup == null || PinGroups.Same(pin.Group, ActiveGroup);

    /// <summary>把一张图贴到屏幕上（正好盖住它截下来的那块区域）。</summary>
    public static CapturePinWindow Pin(BitmapSource image, Int32Rect area)
    {
        // 缩放比按这块区域所在显示器算：跨屏选区时不能用「覆盖层那块屏」的比例
        var pin = new CapturePinWindow(image, area, ScreenShot.ScaleOf(area));

        // 正只看某个分组时，新贴图落进那个组 —— 否则贴上来立刻被自己藏起来，看着像「贴图没反应」
        pin.SetGroup(ActiveGroup ?? PinGroups.Default);

        Pins.Add(pin);
        pin.Show();          // 无激活窗口：ShowActivated=False + WS_EX_NOACTIVATE
        pin.RaiseToTop();
        return pin;
    }

    /// <summary>
    /// 贴一块**实时**的图：跟住 <paramref name="hwnd"/>，每 200ms 刷新一次内容，
    /// 窗口尺寸 / 位置仍是 <paramref name="area"/> 那块（源窗口挪开的地方留透明）。
    /// </summary>
    /// <remarks>
    /// 只是 <see cref="Pin"/> 之后多开一次实时刷新，所以分组 / 多选 / 拖动 / 关闭全部
    /// 与普通贴图完全同一条路径（<see cref="CapturePinWindow.StopLive"/> 之后就是普通贴图）。
    /// </remarks>
    public static CapturePinWindow PinLive(BitmapSource image, Int32Rect area, IntPtr hwnd)
    {
        var pin = Pin(image, area);
        pin.StartLive(hwnd);
        return pin;
    }

    /// <summary>全关（右键菜单「关闭全部贴图」，含被分组藏起来的）。</summary>
    public static void CloseAll()
    {
        EndMove();
        ClearSelection();
        foreach (var pin in Pins.ToArray()) pin.Close();
        Pins.Clear();
        GroupList.RemoveAll(group => !PinGroups.Same(group, PinGroups.Default));
        ActiveGroup = null;
    }

    // ============ 分组 ============

    /// <summary>
    /// 新建分组；名字清洗后为空 = <c>null</c>（调用方提示）。已经存在（忽略大小写）就返回原来那个，
    /// 不会出现「外部」与「外部 」两个组。
    /// </summary>
    public static string? CreateGroup(string? raw)
    {
        string? name = PinGroups.Clean(raw);
        if (name == null) return null;

        var existing = PinGroups.Find(name, GroupList);
        if (existing != null) return existing;

        GroupList.Add(name);
        return name;
    }

    /// <summary>把一张贴图挪到某个分组（组不存在就先建）。挪出当前看着的组就当场藏起来。</summary>
    public static void MoveToGroup(CapturePinWindow pin, string group)
    {
        string? name = PinGroups.Find(group, GroupList) ?? CreateGroup(group);
        if (name == null) return;

        pin.SetGroup(name);
        if (!Pins.Contains(pin)) return;

        if (IsShown(pin))
        {
            if (!pin.IsVisible) { pin.Show(); pin.RaiseToTop(); }
        }
        else if (pin.IsVisible)
        {
            pin.Hide();
        }
    }

    /// <summary>
    /// 只看一个分组（<c>null</c> = 显示全部）：别的组 <c>Hide()</c>，切回来原位 <c>Show()</c>。
    /// 拖动与多选一并收掉——藏起来的窗口不该还留在「已经选中 / 正在拖」的状态里。
    /// </summary>
    public static void SetActiveGroup(string? group)
    {
        EndMove();
        ClearSelection();

        ActiveGroup = group == null ? null : PinGroups.Find(group, GroupList);

        foreach (var pin in Pins)
        {
            bool show = IsShown(pin);
            if (show && !pin.IsVisible)
            {
                pin.Show();
                pin.RaiseToTop();
            }
            else if (!show && pin.IsVisible)
            {
                pin.Hide();
            }
        }
    }

    // ============ 多选与批量 ============

    /// <summary>这张贴图在不在多选里。</summary>
    public static bool IsSelected(CapturePinWindow pin) => Selection.Contains(pin);

    /// <summary><c>Ctrl</c>+单击：选中 / 取消选中这一张（贴图不抢焦点，键盘收不到，所以只有鼠标这一条路）。</summary>
    public static void ToggleSelect(CapturePinWindow pin)
    {
        if (Selection.Remove(pin))
        {
            pin.SetSelected(false);
            return;
        }

        Selection.Add(pin);
        pin.SetSelected(true);
    }

    /// <summary>取消全部选择。</summary>
    public static void ClearSelection()
    {
        if (Selection.Count == 0) return;
        foreach (var pin in Selection) pin.SetSelected(false);
        Selection.Clear();
    }

    /// <summary>全选（只看当前分组里看得见的那些）。</summary>
    public static int SelectAll()
    {
        ClearSelection();
        foreach (var pin in Pins)
        {
            if (!IsShown(pin)) continue;
            Selection.Add(pin);
            pin.SetSelected(true);
        }
        return Selection.Count;
    }

    /// <summary>关闭选中的（返回关掉几张）。</summary>
    public static int CloseSelected()
    {
        var targets = Selection.ToArray();
        ClearSelection();
        foreach (var pin in targets) pin.Close();
        return targets.Length;
    }

    /// <summary>批量锁定 / 解锁。</summary>
    public static void SetSelectedLocked(bool locked)
    {
        foreach (var pin in Selection.ToArray()) pin.SetLocked(locked);
    }

    /// <summary>批量缩略图 / 还原。</summary>
    public static void SetSelectedThumbnail(bool on)
    {
        foreach (var pin in Selection.ToArray()) pin.SetThumbnail(on);
    }

    // ============ 拖动 / 成组移动 ============

    /// <summary>
    /// 拖动开始：记下这次的参与者与它们的落点。三种情况一条路径（<see cref="MoveBy"/> 只认快照）：
    /// 拖的那张在多选里 = 只动选中的那些；否则成组移动开着 = 看得见的全组；再否则只有自己。
    /// </summary>
    public static void BeginMove(CapturePinWindow source)
    {
        MoveBase.Clear();
        _moving = source;

        // 拖一张没选中的：用户显然在操作这一张，把选择收起来
        // （否则菜单里还写着「已选 N 张」，跟手上做的事对不上）
        if (Selection.Count > 0 && !Selection.Contains(source)) ClearSelection();

        CapturePinWindow[] participants = Selection.Count > 0 && Selection.Contains(source)
            ? Selection.Where(IsShown).ToArray()
            : GroupMove
                ? Pins.Where(IsShown).ToArray()
                : new[] { source };

        foreach (var pin in participants)
            MoveBase.Add((pin, new Rect(pin.Left, pin.Top, pin.Width, pin.Height)));
    }

    /// <summary>
    /// 拖到哪：位移先按整组夹一次（谁都不许被推出桌面），再按「起点 + 位移」写回。
    /// 松手前重复调用也只累一个位移，不会越拖越飘。
    /// </summary>
    public static void MoveBy(CapturePinWindow source, double dx, double dy)
    {
        if (!ReferenceEquals(_moving, source) || MoveBase.Count == 0) return;

        var boxes = new Rect[MoveBase.Count];
        for (int i = 0; i < boxes.Length; i++) boxes[i] = MoveBase[i].Box;

        Vector delta = PinMath.ConstrainGroupMove(new Vector(dx, dy), boxes, PinMath.VirtualDesktop,
            PinMath.KeepVisible);

        foreach (var (pin, box) in MoveBase)
        {
            pin.Left = box.Left + delta.X;
            pin.Top = box.Top + delta.Y;
        }
    }

    /// <summary>松手：丢掉快照（下一次拖动重新取起点）。</summary>
    public static void EndMove()
    {
        _moving = null;
        MoveBase.Clear();
    }

    /// <summary>窗口关闭时摘掉自己（重复调用无害）：多选 / 拖动快照 / 空分组都跟着收敛。</summary>
    internal static void Forget(CapturePinWindow pin)
    {
        Selection.Remove(pin);
        Pins.Remove(pin);
        MoveBase.RemoveAll(entry => ReferenceEquals(entry.Pin, pin));
        if (ReferenceEquals(_moving, pin)) EndMove();

        // 空了的分组自己消失（默认组永远在）
        for (int i = GroupList.Count - 1; i >= 1; i--)
            if (!Pins.Any(p => PinGroups.Same(p.Group, GroupList[i])))
                GroupList.RemoveAt(i);

        // 正看着的那个组被关空了：回到「显示全部」，剩下的贴图露出来
        if (ActiveGroup != null && PinGroups.Find(ActiveGroup, GroupList) == null)
        {
            ActiveGroup = null;
            foreach (var other in Pins)
                if (!other.IsVisible)
                {
                    other.Show();
                    other.RaiseToTop();
                }
        }
    }
}
