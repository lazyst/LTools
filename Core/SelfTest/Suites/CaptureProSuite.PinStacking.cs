using System.Collections.Generic;
using System.Linq;
using LTools.Features.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>
/// 贴图的前后序（照上游 <c>storage::PinnedWindowStackingOrder</c>）：加 / 提 / 删 / 换组 / 观测 / 还原。
/// </summary>
/// <remarks>
/// 纯逻辑，不碰窗口也不读盘；真窗口的 z 序、点击提升、批量贴回保序由仓库外
/// <c>capture-overlay-smoke</c> 的 <c>PinStackSection</c> 端到端验。
/// </remarks>
internal static partial class CaptureProSuite
{
    private static void PinStackingCases()
    {
        static string Join(IEnumerable<string> ids) => string.Join(",", ids);

        // ============ 分组名清洗 ============
        Check.Equal(PinGroups.Default, PinStackOrder.GroupOf(null), "空分组 = 默认组");
        Check.Equal(PinGroups.Default, PinStackOrder.GroupOf("   "), "全空白 = 默认组");
        Check.Equal("工作", PinStackOrder.GroupOf("工作"), "有名字就原样");

        // ============ 加成员：新来的排最前，同组重复加什么都不做 ============
        var order = new PinStackOrder();
        Check.True(order.Add("a", PinGroups.Default), "第一张加进默认组");
        Check.Equal(1L, order.Revision, "加成员算一次改动");
        order.Add("b", PinGroups.Default);
        order.Add("c", PinGroups.Default);
        Check.Equal("c,b,a", Join(order.Order(PinGroups.Default)), "后来的一直排最前");

        Check.False(order.Add("a", PinGroups.Default), "同组重复加不算改动");
        Check.Equal(3L, order.Revision, "重复加不动 revision");
        Check.True(order.Contains("a"), "加过的成员查得到");
        Check.False(order.Contains(""), "空 Id 永远不登记");
        Check.Equal(PinGroups.Default, order.GroupOfMember("a"), "成员记得自己在哪一组");

        // ============ 换组：搬到目标组最前 ============
        Check.True(order.Add("a", "工作"), "加到别的组 = 搬家");
        Check.Equal("c,b", Join(order.Order(PinGroups.Default)), "原组少一个");
        Check.Equal("a", Join(order.Order("工作")), "新组排最前");
        Check.Equal("工作", order.GroupOfMember("a"), "成员的组跟着改");

        // ============ 提升：点 / 拖哪张，它就排到组最前 ============
        Check.True(order.Promote("b"), "把中间的提到最前");
        Check.Equal("b,c", Join(order.Order(PinGroups.Default)), "提升后的顺序");
        Check.False(order.Promote("b"), "已经最前就不用再动");
        Check.False(order.Promote("不在册"), "认不出的 Id 提不动");

        // ============ 删成员 ============
        Check.True(order.Remove("c"), "删掉一个成员");
        Check.Equal("b", Join(order.Order(PinGroups.Default)), "删完只剩一个");
        Check.False(order.Remove("c"), "重复删不算改动");
        Check.False(order.Contains("c"), "删过的不在册");

        // ============ 空组不落盘 ============
        var orders = order.Orders();
        Check.Equal(2, orders.Count, "两个组都还有成员");
        Check.True(orders.ContainsKey("工作"), "工作组在");
        var emptyGroup = new PinStackOrder();
        emptyGroup.Add("x", "临时");
        emptyGroup.Remove("x");
        Check.Equal(0, emptyGroup.Orders().Count, "成员被删光的组不写进盘点");

        // ============ 批量换组：按源列表顺序搬，目标组已有的留原位 ============
        var move = new PinStackOrder();
        move.Add("a", PinGroups.Default);
        move.Add("b", PinGroups.Default);
        move.Add("c", PinGroups.Default);          // 默认 = c,b,a
        move.Add("x", "其它");                      // 其它 = x
        Check.True(move.Move(new[] { "a", "c" }, "其它"), "两个成员一起换组");
        Check.Equal("c,a,x", Join(move.Order("其它")),
            "按源列表顺序（先 c 后 a）排到目标组最前，目标组原有的留在后面");
        Check.Equal("b", Join(move.Order(PinGroups.Default)), "原组只剩没搬的");
        Check.False(move.Move(new[] { "不在册" }, "其它"), "搬不认识的成员 = 没改动");
        Check.False(move.Move(new[] { "x" }, "其它"), "本来就在目标组 = 没改动");

        // ============ 观测：只重排看见的那几个，藏起来的保留槽位 ============
        var seen = new PinStackOrder();
        seen.Add("a", PinGroups.Default);
        seen.Add("b", PinGroups.Default);
        seen.Add("c", PinGroups.Default);
        seen.Add("d", PinGroups.Default);          // 默认 = d,c,b,a
        seen.Add("p", "工作");

        Check.True(seen.Observe(PinGroups.Default, new[] { "a", "b" }), "只观测到两张也能重排");
        Check.Equal("d,c,a,b", Join(seen.Order(PinGroups.Default)),
            "a / b 按观测顺序填回它们原来的两个槽位，d / c 不动");
        Check.False(seen.Observe(PinGroups.Default, new[] { "a", "b" }), "观测结果没变就不算改动");
        Check.False(seen.Observe(PinGroups.Default, new[] { "d" }), "少于两张不算一次观测");
        Check.False(seen.Observe(PinGroups.Default, new[] { "d", "不在册" }), "认不出的 Id 不复活");
        Check.False(seen.Observe(PinGroups.Default, new[] { "d", "p" }),
            "别的组的成员混进观测也不算");
        Check.False(seen.Observe("没有这个组", new[] { "a", "b" }), "不存在的组直接返回");
        Check.Equal("p", Join(seen.Order("工作")), "另一组不受影响");

        // ============ 从磁盘还原 ============
        var records = new List<(string Id, string Group)>
        {
            ("a", PinGroups.Default),
            ("b", PinGroups.Default),
            ("c", "工作"),
            ("d", PinGroups.Default),
        };

        var saved = new Dictionary<string, List<string>>
        {
            [PinGroups.Default] = new() { "b" },
            ["工作"] = new() { "c" },
            ["其它"] = new() { "a" },              // 分组对不上：丢掉
            ["旧组"] = new() { "zzz" },            // 记录里没有：丢掉
        };

        var restored = PinStackOrder.From(records, saved);
        Check.Equal("b,a,d", Join(restored.Order(PinGroups.Default)),
            "存档顺序在前，没记过的按「新 → 旧」补在后面");
        Check.Equal("c", Join(restored.Order("工作")), "另一组的存档照原样");
        Check.Equal(0, restored.Order("其它").Count, "分组对不上的 Id 不会凭空多出一个组");
        Check.False(restored.Contains("zzz"), "记录里没有的 Id 不复活");
        Check.Equal(0L, restored.Revision, "载入历史不算改动");

        restored.Add("e", PinGroups.Default);
        Check.Equal("e,b,a,d", Join(restored.Order(PinGroups.Default)), "载入之后新贴的照样排最前");

        // 没有存档（老配置）：全按记录顺序（已经是新的在前）
        var legacy = PinStackOrder.From(records, null);
        Check.Equal("a,b,d", Join(legacy.Order(PinGroups.Default)), "没有存档就是记录顺序");
        Check.Equal("c", Join(legacy.Order("工作")), "没有存档也不会漏组");

        // 存档把同一个 Id 写在两个组：只认它自己那一组
        var dup = PinStackOrder.From(records, new Dictionary<string, List<string>>
        {
            [PinGroups.Default] = new() { "b", "a" },
            ["工作"] = new() { "a" },
        });
        Check.Equal("b,a,d", Join(dup.Order(PinGroups.Default)), "第一个出现的组收下它");
        Check.Equal("c", Join(dup.Order("工作")), "另一个组不会把它抢过去");
    }
}
