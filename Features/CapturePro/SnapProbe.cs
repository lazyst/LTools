using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 覆盖层的「控件探测」会话：按窗口缓存 <see cref="SnapTree"/>，并在连续没进展时停止追问。
/// </summary>
/// <remarks>
/// <para>
/// 调用分两路：UI 线程用 <c>expand: false</c>（只用已展开的层，微秒级），缺层时后台跑一次
/// <c>expand: true</c>。同一个窗口的展开结果跨光标移动、跨多次进入都保留 —— 上游每次都发一次
/// 168 ms 的查询，我们这里只有「换窗口 / 换支路」才碰跨进程调用。
/// </para>
/// <para>
/// 连续两趟「节点数没涨、又没走完」就把这个窗口标成 <c>Stuck</c>：慢应用 / 提权窗口会一直
/// 超时，别让每次鼠标移动都白排一次后台查询。真正读不到的原因由 <see cref="SnapPath.Stop"/> 给出，
/// 提示条按它区分「读超时」与「这个程序不暴露控件」。
/// </para>
/// </remarks>
internal sealed class SnapProbe
{
    /// <summary>最多缓存多少个窗口的展开结果（浏览器 + 编辑器来回扫也不会无限涨）。</summary>
    private const int MaxWindows = 40;

    /// <summary>连续几趟没进展就放弃这个窗口。</summary>
    private const int MaxStrikes = 2;

    private readonly ConcurrentDictionary<IntPtr, Entry> _trees = new();
    private readonly ConcurrentDictionary<IntPtr, SnapPath> _pins = new();
    private readonly ISnapSource? _source;

    public SnapProbe()
    {
    }

    /// <summary>
    /// 换一份取数实现：控件选择器用同一套懒下钻，只是节点还要多带身份
    /// （<see cref="ISnapSource.Describe"/>）。缓存 / 放弃判定 / 诊断全共用，别另写一套遍历。
    /// </summary>
    internal SnapProbe(ISnapSource source) => _source = source;

    /// <summary>真实取数（UI Automation）；换过实现时用换过的那份。</summary>
    private ISnapSource SourceOf() => _source ?? RealSource.Value;

    private sealed class Entry
    {
        public SnapTree Tree = null!;
        public int Strikes;
        public int Nodes = 1;
        public bool Stuck;
        public bool Logged;
    }

    /// <summary>
    /// 把某个窗口的命中路径钉死（不再读 UIA）。
    /// </summary>
    /// <remarks>
    /// 两个用途：① 自动化冒烟里真实 UIA 不可控（光标下永远是桌面窗口），用它注入一条固定路径；
    /// ② 排查「这个窗口读出来的到底是什么」时把结果抄出来复现。<c>null</c> = 解除。
    /// </remarks>
    internal void Pin(IntPtr hwnd, IReadOnlyList<SnapBox>? boxes)
    {
        if (boxes == null)
        {
            _pins.TryRemove(hwnd, out _);
            return;
        }

        _pins[hwnd] = new SnapPath(boxes, SnapStop.complete, boxes.Count, boxes.Count, boxes.Count, 0,
            PinSignature(boxes));
    }

    private static int PinSignature(IReadOnlyList<SnapBox> boxes)
    {
        var hash = new HashCode();
        foreach (SnapBox box in boxes) hash.Add(box.Bounds);
        return hash.ToHashCode();
    }

    /// <summary>
    /// 光标下那条支路。<paramref name="expand"/> = true 时缺哪层补哪层（后台线程），
    /// false 时只用缓存（UI 线程）。放弃过的窗口即使传 true 也只读缓存。
    /// </summary>
    public SnapPath Resolve(IntPtr hwnd, Point point, Rectangle monitor, bool expand)
    {
        if (_pins.TryGetValue(hwnd, out SnapPath? pinned)) return pinned;

        Entry entry = Get(hwnd);
        SnapPath path = entry.Tree.Resolve(point, monitor, expand && !entry.Stuck);

        if (!expand) return path;

        if (path.Nodes > entry.Nodes)
        {
            entry.Strikes = 0;
            entry.Nodes = path.Nodes;
        }
        else if (!path.Complete && path.Stop is SnapStop.timeout or SnapStop.failed
                     or SnapStop.steps or SnapStop.budget)
        {
            if (++entry.Strikes >= MaxStrikes)
            {
                entry.Stuck = true;
                AppLog.Debug("snap", $"放弃 {DescribeWindow(hwnd)}：{path.Describe()}");
            }
        }

        if (!entry.Logged && path.Complete)
        {
            entry.Logged = true;
            AppLog.Debug("snap", $"{DescribeWindow(hwnd)}：{path.Describe()}");
        }

        return path;
    }

    /// <summary>这个窗口还愿不愿意再读一层（被标记放弃的窗口只用缓存）。</summary>
    public bool CanExpand(IntPtr hwnd) => !_pins.ContainsKey(hwnd) &&
        (!_trees.TryGetValue(hwnd, out Entry? entry) || !entry.Stuck);

    private Entry Get(IntPtr hwnd)
    {
        if (_trees.TryGetValue(hwnd, out Entry? entry)) return entry;

        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
        string label = WindowFinder.GetText(hwnd);
        if (label.Trim().Length == 0) label = WindowFinder.GetClass(hwnd);

        entry = new Entry
        {
            Tree = new SnapTree(hwnd, new Rectangle(x, y, Math.Max(0, w), Math.Max(0, h)),
                Shorten(label.Trim()), SourceOf()),
        };

        if (_trees.Count >= MaxWindows) _trees.Clear();
        _trees[hwnd] = entry;
        return entry;
    }

    private static string DescribeWindow(IntPtr hwnd)
    {
        string title = WindowFinder.GetText(hwnd).Trim();
        return title.Length > 0 ? $"0x{hwnd:X}「{Shorten(title)}」" : $"0x{hwnd:X} {WindowFinder.GetClass(hwnd)}";
    }

    private static string Shorten(string s) => s.Length <= 28 ? s : s[..28] + "…";

    private static readonly Lazy<ISnapSource> RealSource = new(() => new UiaSnapSource());
}
