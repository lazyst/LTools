using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 框选吸附的「懒下钻」控件树：只验纯逻辑，喂一棵假树，不碰真的 UI Automation。
/// </summary>
/// <remarks>
/// 旧实现（一次性 BFS 整棵树）的问题就出在这些纯逻辑上：深度 8 截断、按节点上限截断、
/// 把 provider 失败当成叶子。所以这里断言的是「只走命中的那条支路」「超时重试一次」这些事，
/// 真机效果（在 Chromium / Electron 上能不能吸到内部控件）仍要人拖一次看。
/// </remarks>
internal static class SnapTreeSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("snapTree", "深窗口：超过旧的 8 层上限也能一路走到叶子", DeepBranch));
        cases.Add(new("snapTree", "只走命中的那条支路：不读兄弟", OnlyHitBranch));
        cases.Add(new("snapTree", "结构回溯：等大的结构 Pane 盖住内容时回到前面的兄弟", StructuralBacktrack));
        cases.Add(new("snapTree", "超时重试一次：重试成功不当失败", TimeoutRetriedOnce));
        cases.Add(new("snapTree", "一直超时就放弃：失败也缓存，不再重复问", TimeoutGivesUp));
        cases.Add(new("snapTree", "打不开 UIA 的窗口：failed，不是空树", Unavailable));
        cases.Add(new("snapTree", "只用缓存（expand:false）时不发任何跨进程调用", PendingWithoutExpand));
        cases.Add(new("snapTree", "展开过的层不再重复读（同支路移动零调用）", ExpandedLevelsCached));
        cases.Add(new("snapTree", "父子同矩形只留更深的那一条（滚轮不白转）", DuplicateRects));
        cases.Add(new("snapTree", "矩形裁到显示器；光标不在窗口里时给空路径", ClipAndOutside));
    }

    // ============ 用例 ============

    /// <summary>Chromium 那种「十几层容器」：旧实现限深 8，会停在中途的 Pane 上。</summary>
    private static void DeepBranch()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 1000, 800));

        FakeNode parent = root;
        for (int i = 1; i <= 14; i++)
            parent = fake.Add(parent, "控件" + i, new Rectangle(i * 4, i * 4, 1000 - i * 8, 800 - i * 8));

        SnapPath path = Walk(fake, new Point(500, 400));

        Check.True(path.Complete, $"走到叶子（实际 {path.Stop}）");
        Check.Equal(15, path.Depth, "根 + 14 层");
        Check.Equal(15, path.Boxes.Count, "每一层一个矩形");
        Check.Equal("控件14", path.Boxes[0].Label, "最深的那个排第一");
        Check.Equal("窗口", path.Boxes[^1].Label, "窗口本身排最后");
        Check.Equal(15, fake.ChildCalls, "每层只读一次");
    }

    private static void OnlyHitBranch()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 1000, 800));
        FakeNode left = fake.Add(root, "左栏", new Rectangle(0, 0, 400, 800));
        fake.Add(left, "左按钮", new Rectangle(10, 10, 100, 30));
        FakeNode right = fake.Add(root, "右栏", new Rectangle(400, 0, 600, 800));
        fake.Add(right, "右按钮", new Rectangle(500, 10, 100, 30));

        SnapPath path = Walk(fake, new Point(50, 20));

        Check.Equal("左按钮", path.Boxes[0].Label, "命中左边那个按钮");
        Check.False(fake.Requested.Contains(right), "没读过右栏");
        Check.Equal(3, fake.ChildCalls, "只读了根、左栏和左按钮");
        Check.True(path.Complete, "支路走完");
    }

    /// <summary>Chromium 常见形状：一个和父级等大的透明 Pane 排在内容后面盖住它。</summary>
    private static void StructuralBacktrack()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 1000, 800));
        FakeNode content = fake.Add(root, "文档", new Rectangle(0, 0, 1000, 800));
        fake.Add(content, "保存按钮", new Rectangle(10, 10, 100, 30));
        FakeNode overlay = fake.Add(root, "覆盖层", new Rectangle(0, 0, 1000, 800), structural: true);

        SnapPath path = Walk(fake, new Point(50, 20));

        Check.Equal("保存按钮", path.Boxes[0].Label, "回溯到前面的内容兄弟");
        Check.False(path.Boxes.Any(b => b.Label == "覆盖层"), "等大的空壳不进路径");
        Check.Equal(1, fake.Requested.Count(n => ReferenceEquals(n, overlay)), "空壳只读一次就放弃");
        Check.True(path.Complete, "支路走完");
    }

    private static void TimeoutRetriedOnce()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 600, 400));
        fake.Add(root, "按钮", new Rectangle(10, 10, 100, 30));
        root.Throws = 1;   // 第一次读孩子超时

        SnapPath path = Walk(fake, new Point(20, 20), out SnapTree tree);

        Check.True(path.Complete, $"重试之后走完（实际 {path.Stop}）");
        Check.Equal(0, tree.Timeouts, "重试成功不算失败");
        Check.Equal("按钮", path.Boxes[0].Label, "拿到子控件");
        Check.Equal(3, fake.ChildCalls, "根读了两次（超时 + 重试）、按钮一次");
    }

    private static void TimeoutGivesUp()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 600, 400));
        root.Throws = 99;

        SnapPath first = Walk(fake, new Point(20, 20), out SnapTree tree);

        Check.Equal(SnapStop.timeout, first.Stop, "报超时");
        Check.False(first.Complete, "没走完");
        Check.Equal(1, first.Boxes.Count, "只剩窗口本身");
        Check.Equal(1, tree.Timeouts, "记一次超时");
        Check.Equal(2, fake.ChildCalls, "超时 + 重试各一次");

        SnapPath second = tree.Resolve(new Point(30, 30), new Rectangle(0, 0, 1920, 1080), expand: true);

        Check.Equal(SnapStop.timeout, second.Stop, "失败缓存住了");
        Check.Equal(2, fake.ChildCalls, "第二次不再问 provider");
    }

    private static void Unavailable()
    {
        var fake = new FakeSource { RootValue = null };

        SnapPath path = Walk(fake, new Point(20, 20));

        Check.Equal(SnapStop.failed, path.Stop, "打不开 UIA = failed");
        Check.Equal(1, path.Boxes.Count, "退回窗口本身");
        Check.False(path.Complete, "不算读完");
    }

    private static void PendingWithoutExpand()
    {
        var fake = new FakeSource();
        fake.SetRoot("窗口", new Rectangle(0, 0, 600, 400));

        SnapPath path = Walk(fake, new Point(20, 20), expand: false);

        Check.Equal(SnapStop.pending, path.Stop, "还没读，等后台补");
        Check.Equal(0, fake.ChildCalls, "UI 线程一次跨进程调用都不发");
        Check.Equal(1, path.Boxes.Count, "先给窗口本身");
    }

    private static void ExpandedLevelsCached()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 1000, 800));
        FakeNode left = fake.Add(root, "左栏", new Rectangle(0, 0, 400, 800));
        fake.Add(left, "左按钮", new Rectangle(10, 10, 100, 30));
        FakeNode right = fake.Add(root, "右栏", new Rectangle(400, 0, 600, 800));
        fake.Add(right, "右按钮", new Rectangle(500, 10, 100, 30));

        Walk(fake, new Point(50, 20), out SnapTree tree);
        int afterFirst = fake.ChildCalls;

        // 同一支路里移动：全在缓存里
        tree.Resolve(new Point(60, 25), new Rectangle(0, 0, 1920, 1080), expand: true);
        Check.Equal(afterFirst, fake.ChildCalls, "同支路移动零调用");

        // 换到还没展开的兄弟：先报 pending，再补一层
        SnapPath cold = tree.Resolve(new Point(520, 25), new Rectangle(0, 0, 1920, 1080), expand: false);
        Check.Equal(SnapStop.pending, cold.Stop, "新支路还没读");
        Check.Equal(afterFirst, fake.ChildCalls, "只读缓存时还是零调用");

        SnapPath warm = tree.Resolve(new Point(520, 25), new Rectangle(0, 0, 1920, 1080), expand: true);
        Check.True(warm.Complete, "后台补完");
        Check.Equal("右按钮", warm.Boxes[0].Label, "读到右边那个按钮");
        Check.Equal(afterFirst + 2, fake.ChildCalls, "只补了右栏和右按钮");
    }

    private static void DuplicateRects()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 400, 300));
        FakeNode pane = fake.Add(root, "面板", new Rectangle(0, 0, 400, 300), structural: true);
        fake.Add(pane, "按钮", new Rectangle(0, 0, 400, 300));

        SnapPath path = Walk(fake, new Point(10, 10));

        Check.Equal(3, path.Depth, "路径还是三层");
        Check.Equal(1, path.Boxes.Count, "三条同矩形只留一条");
        Check.Equal("按钮", path.Boxes[0].Label, "留最深的那条");
    }

    private static void ClipAndOutside()
    {
        var fake = new FakeSource();
        FakeNode root = fake.SetRoot("窗口", new Rectangle(0, 0, 1000, 800));
        fake.Add(root, "大图", new Rectangle(900, 700, 300, 300));

        // 子矩形先裁到窗口（100×100），再裁到显示器（60×100）
        SnapPath inside = Walk(fake, new Point(910, 710), new Rectangle(0, 0, 960, 800));
        Check.Equal(2, inside.Boxes.Count, "窗口 + 大图");
        Check.Equal(new Rectangle(900, 700, 60, 100), inside.Boxes[0].Bounds, "裁到显示器");

        SnapPath outside = Walk(fake, new Point(2000, 2000));
        Check.Equal(SnapStop.complete, outside.Stop, "窗口外的点");
        Check.Equal(0, outside.Boxes.Count, "什么都不给");
    }

    // ============ 夹具 ============

    private static SnapPath Walk(FakeSource fake, Point point, Rectangle? clip = null, bool expand = true)
        => Walk(fake, point, out _, clip, expand);

    private static SnapPath Walk(FakeSource fake, Point point, out SnapTree tree,
        Rectangle? clip = null, bool expand = true)
    {
        tree = fake.NewTree();
        return tree.Resolve(point, clip ?? new Rectangle(0, 0, 1920, 1080), expand);
    }

    private sealed class FakeNode
    {
        public string Name = "";
        public Rectangle Rect;
        public bool Structural;
        public bool HasKids;
        public List<FakeNode> Kids = new();

        /// <summary>还要抛几次（模拟 UIA 超时 / 无响应）。</summary>
        public int Throws;
    }

    /// <summary>一棵假树：读孩子的次数、读过哪些节点都能断言。</summary>
    private sealed class FakeSource : ISnapSource
    {
        private FakeNode? _root;

        public FakeNode? RootValue
        {
            get => _root;
            set => _root = value;
        }

        public int ChildCalls { get; private set; }

        public List<object> Requested { get; } = new();

        public FakeNode SetRoot(string name, Rectangle rect)
        {
            _root = new FakeNode { Name = name, Rect = rect, HasKids = false };
            return _root;
        }

        public FakeNode Add(FakeNode parent, string name, Rectangle rect, bool structural = false)
        {
            var child = new FakeNode { Name = name, Rect = rect, Structural = structural };
            parent.Kids.Add(child);
            parent.HasKids = true;
            return child;
        }

        public SnapTree NewTree() => new(IntPtr.Zero, _root?.Rect ?? new Rectangle(0, 0, 1000, 800), "窗口", this);

        public object? Root(IntPtr hwnd) => _root;

        public IReadOnlyList<SnapChild>? Children(object node)
        {
            ChildCalls++;
            Requested.Add(node);

            if (node is not FakeNode n) return null;
            if (n.Throws > 0)
            {
                n.Throws--;
                throw new TimeoutException("uia children");
            }

            if (!n.HasKids) return null;   // 叶子

            var list = new List<SnapChild>(n.Kids.Count);
            foreach (FakeNode kid in n.Kids) list.Add(new SnapChild(kid, kid.Rect, kid.Structural));
            return list;
        }

        public string Label(object node) => node is FakeNode n ? n.Name : "控件";
    }
}
