using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;

namespace LTools.Features.CapturePro;

/// <summary>框选时可以吸附的一个矩形：窗口、子窗口或 UI Automation 控件（屏幕物理像素）。</summary>
internal readonly record struct SnapBox(Rectangle Bounds, string Label, IntPtr Owner = default)
{
    /// <summary>面积（用于在重叠的候选里挑最小的那个）。</summary>
    public long Area => (long)Bounds.Width * Bounds.Height;
}

/// <summary>一条支路走到哪儿停下。</summary>
internal enum SnapStop
{
    /// <summary>走到叶子，光标不再落在任何子控件里：这条支路读完了。</summary>
    complete,

    /// <summary>只用了已展开的层，还有一层没读（要放到后台再走一次）。</summary>
    pending,

    /// <summary>时间预算用完。下次还能接着走（已展开的层都留着）。</summary>
    budget,

    /// <summary>步数 / 节点上限。</summary>
    steps,

    /// <summary>provider 超时（已经重试过一次）。</summary>
    timeout,

    /// <summary>provider 报错 / 这个窗口不暴露控件。</summary>
    failed,
}

/// <summary>一个子节点：句柄（真实实现是 UIA 元素）+ 矩形 + 是不是结构性容器（Pane / Group）。</summary>
internal readonly record struct SnapChild(object Handle, Rectangle Bounds, bool Structural);

/// <summary>
/// 「这个窗口里往下有什么控件」的唯一取数入口：真实实现是 UI Automation，测试里换成假树。
/// </summary>
/// <remarks>
/// 空集合与失败**必须分开**：<see cref="Children"/> 返回 <c>null</c> 表示确实没有孩子（叶子），
/// 抛异常表示 provider 读失败（超时 / 无响应）。把失败当叶子会让整棵树静默变浅——
/// 旧实现「一次性 BFS 整棵树 + catch 当空」就是这样在某些窗口只吸得到整窗。
/// </remarks>
internal interface ISnapSource
{
    /// <summary>窗口根元素；<c>null</c> = 这个窗口不暴露 UIA。可以抛异常（超时 / 无响应）。</summary>
    object? Root(IntPtr hwnd);

    /// <summary>一层子节点；<c>null</c> = 叶子。provider 失败请抛异常。</summary>
    IReadOnlyList<SnapChild>? Children(object node);

    /// <summary>控件标签（「按钮 · 保存」）。只在真正走到的支路上读，别按整层读。</summary>
    string Label(object node);

    /// <summary>
    /// 可选：这个节点的**完整身份**（控件选择器要名称 / AutomationId / 类型 / 类名；截图吸附只要标签）。
    /// 同样只沿走到的支路读。返回 <c>null</c> = 这个消费方不需要。
    /// </summary>
    object? Describe(object node) => null;
}

/// <summary>支路上的一层：矩形 + 消费方自己的身份数据（吸附是 null，点选器是 <c>ControlTarget</c>）。</summary>
internal readonly record struct SnapLevel(SnapBox Box, object? Info);

/// <summary>一次下钻的结果：深 → 浅的矩形 + 停在哪里 + 计数（诊断用）。</summary>
internal sealed class SnapPath
{
    public static readonly SnapPath Empty = new(Array.Empty<SnapLevel>(), SnapStop.complete, 0, 0, 0, 0, 0);

    public SnapPath(IReadOnlyList<SnapLevel> levels, SnapStop stop, int nodes, int depth,
        int steps, long elapsedMs, int signature)
    {
        Levels = levels;

        var boxes = new SnapBox[levels.Count];
        for (int i = 0; i < levels.Count; i++) boxes[i] = levels[i].Box;
        Boxes = boxes;

        Stop = stop;
        Nodes = nodes;
        Depth = depth;
        Steps = steps;
        ElapsedMs = elapsedMs;
        Signature = signature;
    }

    public SnapPath(IReadOnlyList<SnapBox> boxes, SnapStop stop, int nodes, int depth,
        int steps, long elapsedMs, int signature)
        : this(ToLevels(boxes), stop, nodes, depth, steps, elapsedMs, signature)
    {
    }

    private static IReadOnlyList<SnapLevel> ToLevels(IReadOnlyList<SnapBox> boxes)
    {
        var levels = new SnapLevel[boxes.Count];
        for (int i = 0; i < boxes.Count; i++) levels[i] = new SnapLevel(boxes[i], null);
        return levels;
    }

    /// <summary>深 → 浅：矩形 + 身份（点选器用 <see cref="Levels"/>，吸附只看 <see cref="Boxes"/>）。</summary>
    public IReadOnlyList<SnapLevel> Levels { get; }

    /// <summary>深 → 浅（第一条是最深的控件，最后一条是窗口本身）。</summary>
    public IReadOnlyList<SnapBox> Boxes { get; }

    public SnapStop Stop { get; }

    /// <summary>这条支路读完了（走到叶子）。false = 还要在后台补一层。</summary>
    public bool Complete => Stop == SnapStop.complete;

    /// <summary>这个窗口一共记了多少个节点。</summary>
    public int Nodes { get; }

    /// <summary>这条支路有多少层（含窗口本身）。</summary>
    public int Depth { get; }

    public int Steps { get; }

    public long ElapsedMs { get; }

    /// <summary>矩形集合的指纹：UI 线程用它判断「吸附线要不要重建」。</summary>
    public int Signature { get; }

    /// <summary>日志 / 提示用的一行说明。</summary>
    public string Describe() =>
        $"stop={Stop} nodes={Nodes} depth={Depth} steps={Steps} {ElapsedMs}ms";
}

/// <summary>
/// 一个窗口的「懒下钻」控件树：只展开光标所在的那条支路，展开过的层缓存下来。
/// </summary>
/// <remarks>
/// <para>
/// 旧实现是「进窗口就 BFS 爬整棵树」，两个致命点：节点上限按**层序**截断，深层内容永远最后才轮到；
/// 每层还要给每个节点读 3 个属性，一棵 700 节点的树就是两千多次跨进程调用，慢应用上必然超时。
/// 这里照上游 snow-shot（<c>snow-ui-selector</c>）的做法改成：沿一条支路逐层下钻，每层只读一次，
/// 走到底就停——深度和窗口复杂度彻底解耦，Chromium 那种十几层容器的界面也能到真正的控件。
/// </para>
/// <para>
/// 线程：<see cref="Resolve"/> 可以在 UI 线程（<c>expand: false</c>，只用缓存、绝不发跨进程调用）
/// 和后台线程（<c>expand: true</c>，缺哪层补哪层）同时调用。**锁里只读写树结构，跨进程调用一律在锁外**——
/// 否则 UI 线程会等着一趟 UIA 走完，鼠标就卡住了。
/// </para>
/// <para>
/// 结构回溯（<see cref="StructuralAlternative"/>）：UIA 的兄弟顺序不是 z 序，Chromium 里常有一个
/// 和父级等大的 Pane 盖在内容前面。支路走空时，只有「和父级等大」的 Pane / Group 才回溯去找
/// 前面那个重叠的兄弟；真控件和矩形不等的容器保持原有优先级（否则会莫名其妙跳到别的元素上）。
/// </para>
/// </remarks>
internal sealed class SnapTree
{
    /// <summary>沿一条支路最多走多少步（上游 80）：只要光标一直在子控件里就继续往下，不按「层数」设限。</summary>
    private const int MaxSteps = 80;

    /// <summary>一个窗口最多记多少个节点（只在访问过的支路上增长，正常用不到）。</summary>
    private const int MaxNodes = 4000;

    /// <summary>一次下钻的墙上时间预算：够走十几层，又不至于把后台线程占太久。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(600);

    /// <summary>回溯最多抬几层（防止环形父链）。</summary>
    private const int MaxBacktrack = 32;

    private static readonly List<int> EmptyChildren = new();

    private sealed class Node
    {
        public Rectangle Bounds;
        public string Label = "";
        public object? Info;
        public object? Handle;
        public bool Structural;
        public int Parent = -1;

        /// <summary>在父节点子列表里的下标（结构回溯要找「它前面的兄弟」）。</summary>
        public int Order;

        /// <summary>null = 还没展开；空列表 = 叶子（确实没有孩子）。</summary>
        public List<int>? Children;

        /// <summary>非 complete = 这一层读失败过（失败也缓存：这个窗口别再反复问）。</summary>
        public SnapStop Failed;

        public bool TimeoutRetried;
    }

    private readonly IntPtr _hwnd;
    private readonly ISnapSource _source;
    private readonly List<Node> _nodes = new();
    private readonly object _gate = new();
    private int _timeouts;

    public SnapTree(IntPtr hwnd, Rectangle bounds, string label, ISnapSource source)
    {
        _hwnd = hwnd;
        _source = source;
        _nodes.Add(new Node
        {
            Bounds = bounds,
            Label = label,
            Structural = false,
        });
    }

    /// <summary>这个窗口一共读失败过几次（超时只算重试之后仍失败的那次）。</summary>
    public int Timeouts => Volatile.Read(ref _timeouts);

    /// <summary>
    /// 光标下那条支路（屏幕物理像素）。<paramref name="expand"/> = 允许发跨进程调用补没读过的层；
    /// UI 线程一律传 <c>false</c>（只用缓存，微秒级），需要新层时交给后台再走一趟。
    /// </summary>
    /// <param name="point">光标（屏幕物理像素）。</param>
    /// <param name="clip">裁到这个矩形（一般是光标所在显示器）。</param>
    /// <param name="expand">是否允许读还没展开的层。</param>
    public SnapPath Resolve(Point point, Rectangle clip, bool expand)
    {
        var watch = Stopwatch.StartNew();
        var chain = new List<int>(16);
        SnapStop stop = SnapStop.complete;
        int steps = 0;
        int current;

        lock (_gate)
        {
            if (!_nodes[0].Bounds.Contains(point)) return SnapPath.Empty;
            current = 0;
            chain.Add(0);
        }

        while (true)
        {
            if (++steps > MaxSteps) { stop = SnapStop.steps; break; }

            SnapStop state = ChildrenState(current);
            if (state == SnapStop.pending)
            {
                if (!expand) { stop = SnapStop.pending; break; }
                state = Expand(current, watch);   // 锁外读一层
            }

            if (state != SnapStop.complete) { stop = state; break; }

            int next;
            lock (_gate)
            {
                List<int> children = _nodes[current].Children!;
                next = -1;

                // 同层多个子控件都包含这一点时取**最后一个**（UIA 的兄弟顺序当作默认优先级，
                // 与上游 hit_before 一致）；更深的那个由下一轮继续下钻。
                for (int i = children.Count - 1; i >= 0; i--)
                {
                    int index = children[i];
                    if (_nodes[index].Bounds.Contains(point)) { next = index; break; }
                }

                if (next < 0) next = StructuralAlternative(current, point);
                if (next >= 0) chain.Add(next);
            }

            if (next < 0) break;   // 真的到底了：光标不再落在任何子控件里
            current = next;
        }

        // 标签 / 身份只读这条支路上的十来个节点，而且只在后台那一趟读：按整层读会在
        // 「几百个子控件的 Pane」上白付几百次跨进程调用（旧实现的主要耗时之一）。
        if (expand)
        {
            for (int i = chain.Count - 1; i > 0; i--) FillLabel(chain[i]);

            // 身份连窗口本身一起读：消费方（点选器）按「最后一条 = 窗口」来处理这条路径
            for (int i = chain.Count - 1; i >= 0; i--) FillInfo(chain[i]);
        }

        var levels = new List<SnapLevel>(8);
        lock (_gate)
        {
            BuildLevels(chain, clip, levels);

            var boxes = new SnapBox[levels.Count];
            for (int i = 0; i < levels.Count; i++) boxes[i] = levels[i].Box;

            return new SnapPath(levels, stop, _nodes.Count, chain.Count, steps,
                watch.ElapsedMilliseconds, Signature(boxes));
        }
    }

    // ============ 展开（跨进程部分在锁外） ============

    /// <summary>这一层的状态：<c>complete</c> = 已有孩子可以往下走；<c>pending</c> = 还没读。</summary>
    private SnapStop ChildrenState(int index)
    {
        lock (_gate)
        {
            Node node = _nodes[index];
            if (node.Children != null) return SnapStop.complete;
            if (node.Failed != SnapStop.complete) return node.Failed;
            if (_nodes.Count > MaxNodes) return SnapStop.steps;
            return SnapStop.pending;
        }
    }

    private SnapStop Expand(int index, Stopwatch watch)
    {
        if (watch.Elapsed > Budget) return SnapStop.budget;

        object? parent;
        bool needRoot;
        lock (_gate)
        {
            parent = _nodes[index].Handle;
            needRoot = index == 0 && parent == null;
        }

        if (needRoot)
        {
            SnapStop root = TryProvider(index, () => _source.Root(_hwnd), out object? element);
            if (root != SnapStop.complete) return root;

            if (element == null)
            {
                // 这个窗口不暴露 UIA（提权 / 自绘 / 老程序）：记成失败，别再反复问。
                lock (_gate) _nodes[index].Failed = SnapStop.failed;
                return SnapStop.failed;
            }

            parent = element;
            lock (_gate) _nodes[index].Handle = element;
        }

        if (watch.Elapsed > Budget) return SnapStop.budget;

        SnapStop state = TryProvider(index, () => _source.Children(parent!), out object? kids);
        if (state != SnapStop.complete) return state;

        var children = (IReadOnlyList<SnapChild>?)kids;
        lock (_gate)
        {
            _nodes[index].Children = children == null || children.Count == 0
                ? EmptyChildren
                : AddChildren(index, _nodes[0].Bounds, children);
        }

        return SnapStop.complete;
    }

    /// <summary>
    /// 调一次 provider，超时重试一次（上游的做法：前台不重试、细化重试）。
    /// 其它失败直接缓存；重试的标记留在节点上，同一次会话不会再无休止地重试。
    /// </summary>
    private SnapStop TryProvider<T>(int index, Func<T?> call, out T? value) where T : class
    {
        while (true)
        {
            bool retried;
            lock (_gate) retried = _nodes[index].TimeoutRetried;

            try
            {
                value = call();
                return SnapStop.complete;
            }
            catch (Exception ex) when (!retried && IsTimeout(ex))
            {
                lock (_gate) _nodes[index].TimeoutRetried = true;
            }
            catch (Exception ex)
            {
                bool timeout = IsTimeout(ex);
                if (timeout) Interlocked.Increment(ref _timeouts);

                lock (_gate) _nodes[index].Failed = timeout ? SnapStop.timeout : SnapStop.failed;
                value = default;
                return timeout ? SnapStop.timeout : SnapStop.failed;
            }
        }
    }

    private List<int> AddChildren(int parent, Rectangle clip, IReadOnlyList<SnapChild> children)
    {
        var list = new List<int>(children.Count);

        for (int i = 0; i < children.Count; i++)
        {
            SnapChild child = children[i];

            // 只裁到**窗口**（照上游 `intersect_rect(bounds, nodes[0].bounds)`）：
            // 裁到父级会把「比父级大 / 与父级错位」的真实控件切小，下一层的命中判断就跟着丢点，
            // 表现是「明明里面有按钮，却只吸到外面那个 Pane」。完全跑到窗口外的子节点仍然丢掉。
            Rectangle bounds = Rectangle.Intersect(child.Bounds, clip);
            if (bounds.Width < 2 || bounds.Height < 2) continue;

            int index = _nodes.Count;
            _nodes.Add(new Node
            {
                Bounds = bounds,
                Handle = child.Handle,
                Structural = child.Structural,
                Parent = parent,
                Order = list.Count,
            });
            list.Add(index);

            if (_nodes.Count > MaxNodes) break;
        }

        return list;
    }

    /// <summary>
    /// 支路走空时的结构回溯（照上游 <c>structural_alternative</c>）：只有当当前节点是
    /// **和父级等大的 Pane / Group** 时，才往上去找「排在自己前面、同样包含光标」的兄弟。
    /// Chromium 常用一个透明的结构 Pane 盖住内容，不回溯就会停在那个空 Pane 上。
    /// </summary>
    private int StructuralAlternative(int current, Point point)
    {
        int walker = current;

        for (int guard = 0; guard < MaxBacktrack; guard++)
        {
            Node node = _nodes[walker];
            if (node.Parent < 0 || !node.Structural) return -1;

            Node parent = _nodes[node.Parent];
            if (parent.Bounds != node.Bounds) return -1;   // 只回溯和父级等大的结构节点

            List<int>? siblings = parent.Children;
            if (siblings == null) return -1;

            for (int i = node.Order - 1; i >= 0; i--)
            {
                int index = siblings[i];
                if (_nodes[index].Bounds.Contains(point)) return index;
            }

            walker = node.Parent;
        }

        return -1;
    }

    // ============ 输出 ============

    private void FillLabel(int index)
    {
        object? handle;
        lock (_gate)
        {
            Node node = _nodes[index];
            if (node.Label.Length > 0 || node.Handle == null) return;
            handle = node.Handle;
        }

        string label;
        try { label = _source.Label(handle); }
        catch { label = "控件"; }   // 标签读不到不该毁掉整条支路

        lock (_gate) _nodes[index].Label = label;
    }

    private void FillInfo(int index)
    {
        object? handle;
        lock (_gate)
        {
            Node node = _nodes[index];
            if (node.Info != null || node.Handle == null) return;
            handle = node.Handle;
        }

        object? info;
        try { info = _source.Describe(handle); }
        catch { return; }   // 身份读不到不该毁掉整条支路（吸附也用不着它）

        if (info == null) return;
        lock (_gate) _nodes[index].Info = info;
    }

    private void BuildLevels(List<int> chain, Rectangle clip, List<SnapLevel> levels)
    {
        var seen = new HashSet<(int X, int Y, int W, int H)>(8);

        for (int i = chain.Count - 1; i >= 0; i--)
        {
            Node node = _nodes[chain[i]];
            Rectangle bounds = Rectangle.Intersect(node.Bounds, clip);
            if (bounds.Width < 2 || bounds.Height < 2) continue;

            // 父 Pane 与子 Button 常常完全重合：只留更深的那一条（滚轮不白转一圈）。
            if (!seen.Add((bounds.X, bounds.Y, bounds.Width, bounds.Height))) continue;

            var box = new SnapBox(bounds, node.Label.Length > 0 ? node.Label : "控件", _hwnd);
            levels.Add(new SnapLevel(box, node.Info));
        }
    }

    private static int Signature(IReadOnlyList<SnapBox> boxes)
    {
        var hash = new HashCode();
        foreach (SnapBox box in boxes)
        {
            hash.Add(box.Bounds);
            hash.Add(box.Label);   // 标签是后台补的：它到了也要让调用方重建一次
        }
        return hash.ToHashCode();
    }

    /// <summary>UIA 超时 / 无响应：只有这一类值得重试一次（其它失败重试也是白等）。</summary>
    private static bool IsTimeout(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is TimeoutException) return true;

            uint hr = unchecked((uint)e.HResult);
            if (hr == 0x80131505u) return true;   // UIA_E_TIMEOUT
            if (hr == 0x800705B4u) return true;   // ERROR_TIMEOUT
        }

        return false;
    }
}
