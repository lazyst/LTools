using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Core.SelfTest;

/// <summary>
/// 控件选择器里能纯逻辑化的部分：光标命中、往上抬父级、定位键选择、同名判断、搜索。
/// </summary>
/// <remarks>
/// 不碰真的 UIA、不开窗口：真控件树靠人工在目标程序上点一遍（编辑器的「▾ 来源 → 选控件…」）。
/// </remarks>
internal static class UiaPickSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("uiaPick", "光标命中：取最深、面积最小的那个控件", AtPoint));
        cases.Add(new("uiaPick", "光标命中：离屏元素不算、空白处返回 -1", AtPointMisses));
        cases.Add(new("uiaPick", "叶子往上抬到可交互父级；抬不上去保持原样", RollUp));
        cases.Add(new("uiaPick", "有没有定位条件：名称或 AutomationId 至少要一个", CanTarget));
        cases.Add(new("uiaPick", "同名判断按「名称 + id + 类型」AND，不是只比名称", CountSame));
        cases.Add(new("uiaPick", "搜索：名称 / id / 类型 / 类名，包含即命中", Filter));
        cases.Add(new("uiaPick", "列表与信息条的文案", Text));
        cases.Add(new("uiaPick", "控件树快照的截断标记", TreeFlags));
        cases.Add(new("uiaPick", "悬停那条支路里挑目标：跳过窗口、往上找可交互、退回最深", PickOnPath));
        cases.Add(new("uiaPick", "控件操作的三行定位条件都挂了「选控件…」", UnitWiring));
    }

    // ============ 命中 ============

    private static void AtPoint()
    {
        // 窗口 0..200 / 中间 20..180 / 按钮 40..120（同一层里最小的就是按钮）
        var nodes = new List<ControlTarget>
        {
            Node("Window", "", "", 0, -1, 0, 0, 200, 200),
            Node("Pane", "", "", 1, 0, 20, 20, 160, 160),
            Node("Button", "保存", "SaveBtn", 2, 1, 40, 40, 80, 40),
            Node("Text", "取消", "", 3, 2, 130, 40, 40, 30),
        };

        Check.Equal(3, ControlTargets.AtPoint(nodes, new Point(150, 50)), "更深的 Text 胜出");
        Check.Equal(2, ControlTargets.AtPoint(nodes, new Point(60, 50)), "只落在按钮里 → 按钮");
        Check.Equal(0, ControlTargets.AtPoint(nodes, new Point(5, 195)), "只有窗口包含它 → 窗口");

        // 两个控件同层、都包含该点：取面积小的那个
        var same = new List<ControlTarget>
        {
            Node("Pane", "", "", 1, -1, 0, 0, 100, 100),
            Node("Pane", "", "", 1, -1, 10, 10, 30, 30),
        };
        Check.Equal(1, ControlTargets.AtPoint(same, new Point(20, 20)), "同层取面积小的");
    }

    private static void AtPointMisses()
    {
        var nodes = new List<ControlTarget>
        {
            Node("Button", "保存", "", 1, -1, 0, 0, 50, 20),
            Node("Button", "离屏", "", 1, -1, 0, 0, 50, 20, offscreen: true),
        };

        Check.Equal(0, ControlTargets.AtPoint(nodes, new Point(10, 10)), "离屏元素跳过，剩第一个");
        Check.Equal(-1, ControlTargets.AtPoint(nodes, new Point(500, 500)), "空白处 -1");
        Check.Equal(-1, ControlTargets.AtPoint(new List<ControlTarget>(), new Point(1, 1)), "空树 -1");
    }

    private static void RollUp()
    {
        // Text 在 Button 里 → Button；Button 自己 → 自己
        var nodes = new List<ControlTarget>
        {
            Node("Window", "", "", 0, -1, 0, 0, 300, 300),
            Node("Pane", "", "", 1, 0, 10, 10, 280, 280),
            Node("Button", "保存", "SaveBtn", 2, 1, 20, 20, 100, 40),
            Node("Text", "保存", "", 3, 2, 30, 28, 80, 24),
            Node("Pane", "无父级可交互", "", 2, 1, 150, 20, 100, 40),
        };

        Check.Equal(2, ControlTargets.RollUp(nodes, 3), "Text → 最近的 Button");
        Check.Equal(2, ControlTargets.RollUp(nodes, 2), "Button 自己不动");
        Check.Equal(4, ControlTargets.RollUp(nodes, 4), "一路没有可交互父级 → 保持原控件");
        Check.Equal(0, ControlTargets.RollUp(nodes, 0), "窗口自己也不动（调用方再按 Depth 过滤）");
    }

    private static void CanTarget()
    {
        Check.True(ControlTargets.CanTarget(Node("Button", "保存", "", 1, 0)), "只有名称也行");
        Check.True(ControlTargets.CanTarget(Node("Button", "", "SaveBtn", 1, 0)), "只有 id 也行");
        Check.False(ControlTargets.CanTarget(Node("Pane", "", "", 1, 0)), "都没有就不行");
        Check.False(ControlTargets.CanTarget(Node("Pane", "  ", "  ", 1, 0)), "只有空白也不行");
    }

    private static void CountSame()
    {
        var target = Node("Button", "确定", "OkBtn", 2, 0);
        var nodes = new List<ControlTarget>
        {
            target,
            Node("Button", "确定", "OkBtn", 2, 0),       // 完全一样：算同名
            Node("Button", "确定", "OtherBtn", 2, 0),    // 名字一样、id 不同：不算
            Node("Button", "取消", "OkBtn", 2, 0),       // id 一样、名字不同：不算
            Node("Text", "确定", "OkBtn", 2, 0),         // 类型不同：不算
        };

        Check.Equal(2, ControlTargets.CountSame(nodes, target), "只算名称 + id + 类型全同的");
        Check.Equal(1, ControlTargets.CountSame(nodes, Node("Pane", "", "", 1, 0)), "没定位条件时恒为 1");
    }

    private static void Filter()
    {
        var nodes = new List<ControlTarget>
        {
            Node("Button", "保存", "SaveBtn", 1, 0, cls: "Button"),
            Node("Edit", "文件名", "FileNameBox", 2, 0, cls: "Edit"),
            Node("Text", "提示", "", 2, 0, cls: "TextBlock"),
        };

        Check.Equal(3, ControlTargets.Filter(nodes, "").Count, "空搜索 = 全部");
        Check.Equal(1, ControlTargets.Filter(nodes, "save").Count, "id 不区分大小写");
        Check.Equal(1, ControlTargets.Filter(nodes, "文件名").Count, "按名称");
        Check.Equal(1, ControlTargets.Filter(nodes, "edit").Count, "按类型 / 类名");
        Check.Equal(0, ControlTargets.Filter(nodes, "不存在").Count, "没命中");
        Check.True(ReferenceEquals(nodes, ControlTargets.Filter(nodes, "")), "空搜索不复制一份");
    }

    private static void Text()
    {
        var named = Node("Button", "保存", "SaveBtn", 1, 0, w: 80, h: 32, cls: "Button");
        Check.Equal("保存", ControlTargets.TitleOf(named), "有名字用名字");
        Check.Equal("(无名称)", ControlTargets.TitleOf(Node("Pane", "", "", 1, 0)), "没名字标出来");
        Check.Equal("id: SaveBtn · class: Button · 80 × 32", ControlTargets.DetailOf(named), "副标题");
        Check.Equal("", ControlTargets.DetailOf(Node("Pane", "", "", 1, 0, w: 0, h: 0)), "都没有就是空串");

        Check.True(ControlTargets.IsInteractive("Button"), "Button 可交互");
        Check.True(ControlTargets.IsInteractive("edit"), "类型比较不分大小写");
        Check.False(ControlTargets.IsInteractive("Text"), "Text 不可交互");
    }

    private static void TreeFlags()
    {
        var tree = new ControlTree(new List<ControlTarget>(), 12, 300, truncated: true, depthCut: true, null);
        Check.True(tree.Truncated && tree.DepthCut, "两个截断标记都带上");
        Check.False(tree.Unavailable, "没有 Problem 就是读到了");

        var broken = new ControlTree(new List<ControlTarget>(), 12, 300, false, false, "不暴露 UIA");
        Check.True(broken.Unavailable, "Problem 非空 = 打不开");
        Check.Equal(0, broken.Count, "打不开时没有节点");
    }

    /// <summary>
    /// 悬停只走了光标那条支路（深 → 浅，最后一条是窗口本身）：这里断言「该选哪一层」。
    /// 真控件树靠 tests/UiaPickSmoke 的跨进程冒烟验。
    /// </summary>
    private static void PickOnPath()
    {
        // [Text, Button, Window]：文字在按钮里 → 选按钮
        var inButton = new List<ControlTarget>
        {
            Node("Text", "确定", "", 0, -1, 30, 28, 80, 24),
            Node("Button", "确定", "OkBtn", 0, -1, 20, 20, 100, 40),
            Node("Window", "对话框", "", 0, -1, 0, 0, 400, 300),
        };
        Check.Equal(1, ControlTargets.PickOnPath(inButton), "文字在按钮里 → 选按钮");

        // [Button, Window]
        Check.Equal(0, ControlTargets.PickOnPath(inButton.GetRange(1, 2)), "最深那个就是按钮 → 选它");

        // [Pane, Window]：没有可交互的，退回最深（点面板中心也点得到）
        var paneOnly = new List<ControlTarget>
        {
            Node("Pane", "侧栏", "", 0, -1, 0, 0, 120, 300),
            Node("Window", "对话框", "", 0, -1, 0, 0, 400, 300),
        };
        Check.Equal(0, ControlTargets.PickOnPath(paneOnly), "一路都不是可交互 → 退回最深");

        // [Window]：整窗不当目标
        Check.Equal(-1, ControlTargets.PickOnPath(new List<ControlTarget> { paneOnly[1] }), "只有窗口本身 → -1");
        Check.Equal(-1, ControlTargets.PickOnPath(new List<ControlTarget>()), "空路径 → -1");

        // [Text, Pane, Window]：更浅的那个按钮才是目标（支路里从深往浅找）
        var shallowButton = new List<ControlTarget>
        {
            Node("Text", "确定", "", 0, -1, 30, 28, 80, 24),
            Node("Pane", "工具条", "", 0, -1, 20, 20, 100, 40),
            Node("Button", "确定", "OkBtn", 0, -1, 10, 10, 200, 60),
            Node("Window", "对话框", "", 0, -1, 0, 0, 400, 300),
        };
        Check.Equal(2, ControlTargets.PickOnPath(shallowButton), "跳过不可交互的 Pane，选更浅的按钮");

        // 零尺寸的节点跳过（UIA 偶尔给 0 宽高的占位元素）
        var zero = new List<ControlTarget>
        {
            Node("Button", "幽灵", "GhostBtn", 0, -1, 0, 0, 0, 0),
            Node("Button", "确定", "OkBtn", 0, -1, 10, 10, 100, 40),
            Node("Window", "对话框", "", 0, -1, 0, 0, 400, 300),
        };
        Check.Equal(1, ControlTargets.PickOnPath(zero), "零尺寸的按钮不算目标");
    }

    private static void UnitWiring()
    {
        var spec = new LTools.Features.Units.Ui.UiaUnit().Spec;
        foreach (string key in new[] { "name", "automationId", "controlType" })
        {
            InputSpec? input = spec.Inputs.FirstOrDefault(i => i.Key == key);
            Check.True(input != null, "有参数 " + key);
            if (input == null) continue;
            Check.True(input.PickerSet.HasFlag(PickerKind.control), key + " 挂了「选控件…」");
        }
    }

    // ============ 夹具 ============

    private static ControlTarget Node(string type, string name, string id, int depth, int parent,
        int x = 0, int y = 0, int w = 10, int h = 10, bool offscreen = false, string cls = "") =>
        new(new Rectangle(x, y, w, h), name, id, type, cls, "0", depth, parent, true, offscreen);
}
