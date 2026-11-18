using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Interop;
using System.Windows.Threading;
using LTools.Native;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图的前后顺序（front-to-back，与窗口句柄 / 可见性 / 分组过滤无关）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>storage::PinnedWindowStackingOrder</c>：每个分组一份逻辑顺序，新贴的插到最前，
/// 用户点 / 拖哪一张就把它提到最前；关闭（或暂时藏起来）的成员**保留它原来的槽位**，
/// 再贴回屏幕时按记录的相对位置回去，而不是一律跳到最上面。
/// </para>
/// <para>
/// 纯数据结构，不碰窗口也不读盘：<c>--selftest</c> 直接断言；落盘与协调在
/// <see cref="PinStore"/> 与 <see cref="PinStacking"/> 里。
/// </para>
/// </remarks>
internal sealed class PinStackOrder
{
    /// <summary>分组名清洗：空 / 空白 = 默认组。</summary>
    public static string GroupOf(string? group) =>
        string.IsNullOrWhiteSpace(group) ? PinGroups.Default : group;

    private readonly Dictionary<string, List<string>> _groups = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string> _members = new(StringComparer.Ordinal);

    private long _revision;

    /// <summary>改了几次（只增加；载入历史不算改动）。</summary>
    public long Revision => _revision;

    /// <summary>某个分组的前后序（最前 → 最后）。</summary>
    public IReadOnlyList<string> Order(string group) =>
        _groups.TryGetValue(GroupOf(group), out var list) ? list.ToArray() : Array.Empty<string>();

    /// <summary>有成员的分组（空组不落盘）。</summary>
    public IReadOnlyDictionary<string, List<string>> Orders()
    {
        var copy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in _groups)
            if (pair.Value.Count > 0) copy[pair.Key] = new List<string>(pair.Value);
        return copy;
    }

    public bool Contains(string id) => id.Length > 0 && _members.ContainsKey(id);

    public string? GroupOfMember(string id) =>
        _members.TryGetValue(id, out var group) ? group : null;

    /// <summary>
    /// 加一个成员：不存在就插到分组最前；已在别的组就搬过去；同一组里什么都不做
    /// （照上游 <c>add</c>：加进来不等于提到最前，提升另有 <see cref="Promote"/>）。
    /// </summary>
    public bool Add(string id, string group)
    {
        if (string.IsNullOrEmpty(id)) return false;

        string target = GroupOf(group);
        if (_members.TryGetValue(id, out var current))
            return current == target ? false : Move(new[] { id }, target);

        Group(target).Insert(0, id);
        _members[id] = target;
        _revision++;
        return true;
    }

    /// <summary>删掉一个成员（记录被永久删除时）。</summary>
    public bool Remove(string id)
    {
        if (!_members.TryGetValue(id, out var group)) return false;

        _groups[group].Remove(id);
        _members.Remove(id);
        _revision++;
        return true;
    }

    /// <summary>提到分组最前（用户点 / 拖了它）。已经最前时返回 false，不白写盘。</summary>
    public bool Promote(string id)
    {
        if (!_members.TryGetValue(id, out var group)) return false;

        var list = _groups[group];
        int index = list.IndexOf(id);
        if (index <= 0) return false;

        list.RemoveAt(index);
        list.Insert(0, id);
        _revision++;
        return true;
    }

    /// <summary>
    /// 把一批成员搬到 <paramref name="group"/> 的最前。
    /// 顺序取**源列表自己的顺序**（不是调用方集合的枚举顺序）；目标组里本来就有的留在原位。
    /// </summary>
    public bool Move(IEnumerable<string> ids, string group)
    {
        string target = GroupOf(group);

        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in ids)
            if (!string.IsNullOrEmpty(id)) wanted.Add(id);

        var moved = new List<string>();
        foreach (string name in _groups.Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray())
        {
            if (name == target) continue;

            var list = _groups[name];
            for (int i = 0; i < list.Count;)
            {
                string id = list[i];
                if (!wanted.Contains(id)) { i++; continue; }

                list.RemoveAt(i);
                _members[id] = target;
                moved.Add(id);
            }
        }

        if (moved.Count == 0) return false;

        Group(target).InsertRange(0, moved);
        _revision++;
        return true;
    }

    /// <summary>
    /// 用一次真实观测到的前后序**只重排观测到的成员**：隐藏 / 关闭 / 还没出场的成员保留原位，
    /// 观测里多出来的（别的组 / 已删除）直接忽略，少于两个成员不算一次观测。
    /// </summary>
    public bool Observe(string group, IReadOnlyList<string> observed)
    {
        string target = GroupOf(group);
        if (!_groups.TryGetValue(target, out var members)) return false;

        var filtered = new List<string>();
        var selected = new HashSet<string>(StringComparer.Ordinal);
        foreach (string id in observed)
        {
            if (string.IsNullOrEmpty(id) || !_members.TryGetValue(id, out var owner) || owner != target)
                continue;
            if (selected.Add(id)) filtered.Add(id);
        }

        if (filtered.Count < 2) return false;

        bool changed = false;
        int index = 0;
        for (int i = 0; i < members.Count; i++)
        {
            if (!selected.Contains(members[i])) continue;

            string id = filtered[index++];
            if (members[i] == id) continue;

            members[i] = id;
            changed = true;
        }

        if (changed) _revision++;
        return changed;
    }

    /// <summary>
    /// 从磁盘状态还原：<paramref name="saved"/> 里的顺序优先，没记过的记录按
    /// 「新 → 旧」排在最前（老配置没有堆积信息时的默认观感与现在一致）。
    /// 记录里已经没有的 Id 不复活。
    /// </summary>
    public static PinStackOrder From(IEnumerable<(string Id, string Group)> records,
        IReadOnlyDictionary<string, List<string>>? saved)
    {
        var order = new PinStackOrder();

        var groupOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var list = new List<(string Id, string Group)>();
        foreach (var (id, group) in records)
        {
            if (string.IsNullOrEmpty(id) || groupOf.ContainsKey(id)) continue;

            string target = GroupOf(group);
            groupOf[id] = target;
            list.Add((id, target));
        }

        // 先按存档顺序填（同一组里只认本组成员，跨组 / 已删除的 Id 丢弃）
        if (saved != null)
        {
            var ordered = saved.Keys.OrderBy(key => key, StringComparer.Ordinal);
            foreach (string key in ordered)
            {
                string group = GroupOf(key);
                var members = order.Group(group);
                foreach (string id in saved[key])
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (!groupOf.TryGetValue(id, out var owner) || owner != group) continue;
                    if (order._members.ContainsKey(id)) continue;

                    members.Add(id);
                    order._members[id] = group;
                }
            }
        }

        // 没记过的按「新 → 旧」补到组末尾（老配置没有堆积信息时的默认观感）
        foreach (var (id, group) in list)
        {
            if (order._members.ContainsKey(id)) continue;

            order.Group(group).Add(id);
            order._members[id] = group;
        }

        order._revision = 0;
        return order;
    }

    private List<string> Group(string group)
    {
        if (_groups.TryGetValue(group, out var list)) return list;

        list = new List<string>();
        _groups[group] = list;
        return list;
    }
}

/// <summary>
/// 贴图堆叠协调器：让逻辑前后序（<see cref="PinStackOrder"/>）与真实 z 序对上。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>presentation::PinnedWindowStackingCoordinator</c> 的四条主线：
/// </para>
/// <list type="number">
/// <item>**出场**：新贴图插到组最前；贴回屏幕 / 切分组露出来的按记录槽位落位（不盖住更新的）。</item>
/// <item>**提升**：用户点 / 拖 / 右键哪一张，就把它在记录里提到最前（只改顺序，不改几何）。</item>
/// <item>**观测**：原生 z 序变了（点击提升、别的入口顶窗口）就延迟一拍读回真实顺序存盘。</item>
/// <item>**批量**：整批贴回 / 整批关闭期间不观测（中间态没有意义），结束时统一落一次。</item>
/// </list>
/// <para>
/// 上游的「恢复批次纪元 / 取消分组」在本地不需要：贴图只有当前进程内的这一份、贴回也是同步的
/// （没有异步解码窗口期），所以批次只用一层挂起计数就够。层级只分「置顶 / 普通」两档，
/// 跨档不做相对落位（与上游 <c>place</c> 的 <c>level</c> 判断同口径）。
/// </para>
/// <para>只在 UI 线程用（<c>SetWindowPos</c> 与 WPF 窗口句柄都要求）。</para>
/// </remarks>
internal static class PinStacking
{
    private static readonly Dictionary<string, CapturePinWindow> Windows = new(StringComparer.Ordinal);

    private static bool _applying;

    private static int _suspend;

    private static bool _queued;

    /// <summary>正在按记录摆位（此时原生 z 序变化不是用户意图，不观测）。</summary>
    internal static bool Applying => _applying;

    /// <summary>登记在册的贴图数量（冒烟断言用）。</summary>
    internal static int TrackedCount => Windows.Count;

    /// <summary>「关闭全部贴图」之后清账（测试夹具也用它复位）。</summary>
    internal static void Reset()
    {
        Windows.Clear();
        _applying = false;
        _suspend = 0;
        _queued = false;
    }

    /// <summary>把一张贴图登记进来（Id 为空 = 不记历史，也就不参与堆积）。</summary>
    internal static void Track(CapturePinWindow pin)
    {
        string id = pin.PinId;
        if (id.Length == 0) return;

        Windows[id] = pin;
        PinStore.AddStacking(id, pin.Group);
    }

    /// <summary>
    /// 出场 / 重新露面之后按记录落位。
    /// </summary>
    /// <param name="preserveFocus">
    /// <c>true</c> = 保持它在记录里的槽位（贴回屏幕 / 切分组）；
    /// <c>false</c> = 顺带提到最前（新贴的、用户点「贴回」的那一张）。
    /// </param>
    internal static void Presented(CapturePinWindow pin, bool preserveFocus)
    {
        if (pin.PinId.Length == 0) return;

        Track(pin);
        if (!preserveFocus) PinStore.PromoteStacking(pin.PinId);
        Place(pin);
    }

    /// <summary>用户点了 / 拖了 / 右键了它：在记录里提到最前，再按新顺序摆一次。</summary>
    internal static void Promote(CapturePinWindow pin)
    {
        if (pin.PinId.Length == 0) return;

        if (PinStore.PromoteStacking(pin.PinId)) Place(pin);
    }

    /// <summary>关闭一张：先把当前真实顺序记下来，再摘登记（记录里的槽位保留）。</summary>
    internal static void Retiring(CapturePinWindow pin)
    {
        if (pin.PinId.Length == 0) return;

        Checkpoint();
        Windows.Remove(pin.PinId);
    }

    /// <summary>原生 z 序变了（<c>WM_WINDOWPOSCHANGED</c>）：延迟一拍读回真实顺序。</summary>
    internal static void NativeOrderChanged(CapturePinWindow pin)
    {
        if (_applying || _suspend > 0) return;
        if (pin.PinId.Length == 0 || !Windows.ContainsKey(pin.PinId)) return;

        Schedule();
    }

    /// <summary>把某个分组（<c>null</c> = 所有分组）按记录顺序摆一遍。</summary>
    public static void Apply(string? group = null)
    {
        if (_applying) return;

        var orders = PinStore.StackingOrders();
        foreach (var pair in orders)
        {
            if (group != null && !PinGroups.Same(pair.Key, group)) continue;
            ApplyGroup(pair.Key, pair.Value);
        }
    }

    /// <summary>
    /// 按记录落一张：找到它在分组顺序里的位置，插到「更靠前的那张」下面（没有更靠前的就
    /// 插到「更靠后的那张」上面），与上游 <c>platform.place(window, above, below)</c> 同口径。
    /// </summary>
    private static void Place(CapturePinWindow pin)
    {
        if (_applying || !pin.IsVisible) return;

        string group = PinStackOrder.GroupOf(pin.Group);
        var order = PinStore.StackingOrder(group);
        int index = -1;
        for (int i = 0; i < order.Count; i++)
            if (order[i] == pin.PinId) { index = i; break; }

        if (index < 0) return;

        IntPtr hwnd = HandleOf(pin);
        if (hwnd == IntPtr.Zero) return;

        CapturePinWindow? above = null;
        for (int i = index - 1; i >= 0 && above == null; i--) above = Lookup(order[i], group, pin);
        if (above != null) PlaceBelow(pin, hwnd, above);
        else
        {
            CapturePinWindow? below = null;
            for (int i = index + 1; i < order.Count && below == null; i++)
                below = Lookup(order[i], group, pin);
            if (below != null) PlaceAbove(pin, hwnd, below);
        }
    }

    /// <summary>按记录顺序整组摆一遍（从最前那张开始，逐张插到前一张下面）。</summary>
    private static void ApplyGroup(string group, IReadOnlyList<string> order)
    {
        var peers = new List<CapturePinWindow>();
        foreach (string id in order)
        {
            var pin = Lookup(id, group, null);
            if (pin != null) peers.Add(pin);
        }

        if (peers.Count < 2) return;

        _applying = true;
        try
        {
            for (int i = 1; i < peers.Count; i++)
            {
                IntPtr hwnd = HandleOf(peers[i]);
                IntPtr after = HandleOf(peers[i - 1]);
                if (hwnd == IntPtr.Zero || after == IntPtr.Zero) continue;

                Win32.SetWindowPos(hwnd, after, 0, 0, 0, 0,
                    Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate | Win32.SwpNoownerzorder);
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private static CapturePinWindow? Lookup(string id, string group, CapturePinWindow? self)
    {
        if (!Windows.TryGetValue(id, out var pin)) return null;
        if (ReferenceEquals(pin, self)) return null;
        if (!pin.IsVisible) return null;
        if (!PinGroups.Same(pin.Group, group)) return null;

        // 跨层级（置顶 / 普通）不做相对落位：窗口会换 band，排不进同一串
        if (self != null && pin.Topmost != self.Topmost) return null;

        return pin;
    }

    private static void PlaceBelow(CapturePinWindow pin, IntPtr hwnd, CapturePinWindow above)
    {
        IntPtr after = HandleOf(above);
        if (after == IntPtr.Zero) return;

        _applying = true;
        try
        {
            Win32.SetWindowPos(hwnd, after, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate | Win32.SwpNoownerzorder);
        }
        finally
        {
            _applying = false;
        }
    }

    private static void PlaceAbove(CapturePinWindow pin, IntPtr hwnd, CapturePinWindow below)
    {
        IntPtr belowHwnd = HandleOf(below);
        if (belowHwnd == IntPtr.Zero) return;

        // 直接插到 below 的上一个位置 = 正好在它上面
        IntPtr after = Win32.GetWindow(belowHwnd, Win32.GwHwndprev);
        if (after == hwnd) return;
        if (after == IntPtr.Zero) after = pin.Topmost ? Win32.HwndTopmost : IntPtr.Zero;

        _applying = true;
        try
        {
            Win32.SetWindowPos(hwnd, after, 0, 0, 0, 0,
                Win32.SwpNomove | Win32.SwpNosize | Win32.SwpNoactivate | Win32.SwpNoownerzorder);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>把一整批操作期间的观测挂起（贴回 / 关闭一组时用），结束后自己结算。</summary>
    internal static IDisposable Batch() => new BatchScope();

    private sealed class BatchScope : IDisposable
    {
        public BatchScope() => _suspend++;

        public void Dispose()
        {
            if (_suspend > 0) _suspend--;
        }
    }

    private static void Schedule()
    {
        if (_queued) return;

        _queued = true;
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false;
            Checkpoint();
        }));
    }

    /// <summary>读回当前真实前后序并落盘（一次扫描全部可见贴图）。</summary>
    private static void Checkpoint()
    {
        if (_applying || _suspend > 0) return;

        var groups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pin in Windows.Values)
            if (pin.IsVisible) groups.Add(PinStackOrder.GroupOf(pin.Group));

        foreach (string group in groups) ObserveGroup(group);
    }

    private static void ObserveGroup(string group)
    {
        var byHandle = new Dictionary<IntPtr, CapturePinWindow>();
        foreach (var pin in Windows.Values)
        {
            if (!pin.IsVisible || !PinGroups.Same(pin.Group, group)) continue;

            IntPtr hwnd = HandleOf(pin);
            if (hwnd != IntPtr.Zero) byHandle[hwnd] = pin;
        }

        if (byHandle.Count < 2) return;

        var ordered = new List<string>(byHandle.Count);
        Win32.EnumWindows((hwnd, _) =>
        {
            // EnumWindows 就是 z 序（最前 → 最后），只留下我们在跟的贴图
            if (byHandle.TryGetValue(hwnd, out var pin)) ordered.Add(pin.PinId);
            return true;
        }, IntPtr.Zero);

        if (ordered.Count >= 2) PinStore.UpdateStackingOrder(group, ordered);
    }

    private static IntPtr HandleOf(CapturePinWindow pin)
    {
        try
        {
            return new WindowInteropHelper(pin).Handle;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}
