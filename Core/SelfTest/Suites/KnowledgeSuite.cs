using LTools.Core.Actions;
using LTools.Core.Ai;

namespace LTools.Core.SelfTest;

/// <summary>
/// 动作单元索引（<c>ltools_units_list</c>）的自检：类型标签、按类型反查，以及与单元声明的一致性。
/// </summary>
/// <remarks>
/// 索引是 agent 找动作单元的唯一入口（「谁产数字」「谁能吃文本」都靠它），
/// 所以断言一律拿 <see cref="StepRegistry.Default"/> 当基准现算，不给索引另留一份清单——
/// 新增单元时这里自动覆盖，写错则会红。
/// </remarks>
internal static class KnowledgeSuite
{
    /// <summary>给 agent 看的类型名（与 <c>units_get</c> 的 <c>type</c> 字段同一套）。</summary>
    private static readonly string[] TypeNames = { "any", "bool", "list", "number", "object", "text" };

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("knowledge", "索引：每个单元都有类型标签", TypeTags));
        cases.Add(new("knowledge", "索引：类型与声明一致", TypesMatchDeclarations));
        cases.Add(new("knowledge", "索引：按类型反查", FilterByType));
        cases.Add(new("knowledge", "索引：过滤词与反查是 AND", FilterCombines));
        cases.Add(new("knowledge", "索引：无命中返回空表", NoMatch));
    }

    private static void TypeTags()
    {
        List<Dictionary<string, object?>> rows = Rows(Units(null, null, null));
        Check.True(rows.Count >= 70, $"索引应列出全部动作单元（实际 {rows.Count}）");

        foreach (Dictionary<string, object?> row in rows)
        {
            string where = "单元「" + row["key"] + "」";
            Check.True(row.ContainsKey("in") && row.ContainsKey("out"), $"{where}：缺少 in / out 类型");
            Check.False(row.ContainsKey("inputs") || row.ContainsKey("outputs"),
                $"{where}：in / out 已经是类型与数量，不该再返回裸计数");
            Check.False(row.ContainsKey("description") == false, $"{where}：缺少说明");

            foreach (string name in Names(row, "in").Concat(Names(row, "out")))
                Check.True(TypeNames.Contains(name), $"{where}：类型名「{name}」不在 {string.Join(" / ", TypeNames)} 里");

            Check.Equal(Names(row, "in").Distinct().Count(), Names(row, "in").Count,
                $"{where}：in 里有重复类型（去重后兼作参数个数）");
        }
    }

    private static void TypesMatchDeclarations()
    {
        var expected = new Dictionary<string, (string In, string Out)>(StringComparer.OrdinalIgnoreCase);
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            expected[unit.Spec.Key] =
                (string.Join(",", Types(unit.Spec.Inputs.Select(i => i.Type))),
                 string.Join(",", Types(unit.Spec.Outputs.Select(o => o.Type))));
        }

        foreach (Dictionary<string, object?> row in Rows(Units(null, null, null)))
        {
            string key = (string)row["key"]!;
            if (!expected.TryGetValue(key, out var want))
            {
                Check.True(false, $"索引里的「{key}」不在注册表里");
                continue;
            }

            Check.Equal(want.In, string.Join(",", Names(row, "in")), $"单元「{key}」的 in 与声明不一致");
            Check.Equal(want.Out, string.Join(",", Names(row, "out")), $"单元「{key}」的 out 与声明不一致");
        }
    }

    private static void FilterByType()
    {
        Dictionary<string, object?> all = Units(null, null, null);
        var allKeys = Keys(all);

        foreach (string type in TypeNames)
        {
            var produced = Rows(Units(null, type, null));
            var expectedProduced = allKeys
                .Where(key => RowOf(all, key) is { } row && Names(row, "out").Contains(type))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Check.Equal(string.Join(",", expectedProduced),
                string.Join(",", produced.Select(r => (string)r["key"]!)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)),
                $"produces:\"{type}\" 与「输出里有 {type}」不一致");

            var consumed = Rows(Units(null, null, type));
            Check.True(consumed.All(r => Names(r, "in").Contains(type)),
                $"consumes:\"{type}\" 返回了不消费 {type} 的单元");

            // any 只代表「声明没限定类型」，不该混进具体类型的命中里。
            if (type != "any")
                Check.True(consumed.All(r => !Names(r, "in").Contains("any") || Names(r, "in").Contains(type)),
                    $"consumes:\"{type}\" 混进了只写 any 的单元");
        }

        Check.True(Rows(Units(null, "list", null)).Count > 0, "应该有产列表的单元（产列表 / 消费列表都查得到）");
        Check.True(Rows(Units(null, null, "number")).Count > 0, "应该有吃数字参数的单元");
    }

    private static void FilterCombines()
    {
        Dictionary<string, object?> combined = Units("data", "list", null);
        var keys = Keys(combined);

        Check.True(keys.Count > 0, "「data」里应该有产列表的单元");
        Check.True(keys.All(k => k.Contains("data", StringComparison.OrdinalIgnoreCase)),
            "关键词过滤失效：命中了不含「data」的单元");

        var producedList = Keys(Units(null, "list", null));
        Check.True(keys.All(producedList.Contains), "关键词与类型过滤不是 AND：出现了不产列表的单元");

        Dictionary<string, object?> multi = Units(null, null, "number,list");
        Check.True(Keys(multi).All(k => Keys(Units(null, null, "number")).Contains(k)
                                       || Keys(Units(null, null, "list")).Contains(k)),
            "多值反查应取并集（命中任一）");
    }

    private static void NoMatch()
    {
        Dictionary<string, object?> none = Units(null, "nosuchtype", null);
        Check.Equal(0, (int)none["count"]!, "未知类型应返回 0 个");
        Check.Equal(0, Rows(none).Count, "未知类型不该返回行");

        Check.Equal(0, (int)Units("绝不存在的单元名", null, null)["count"]!, "查不到的关键词应返回 0 个");
    }

    // ===== 小工具 =====

    private static Dictionary<string, object?> Units(string? query, string? produces, string? consumes) =>
        AiToolHandlers.UnitsList(query, produces, consumes);

    private static List<Dictionary<string, object?>> Rows(Dictionary<string, object?> payload) =>
        ((IEnumerable<Dictionary<string, object?>>)payload["units"]!).ToList();

    private static Dictionary<string, object?>? RowOf(Dictionary<string, object?> payload, string key) =>
        Rows(payload).FirstOrDefault(r => string.Equals((string?)r["key"], key, StringComparison.OrdinalIgnoreCase));

    private static List<string> Keys(Dictionary<string, object?> payload) =>
        Rows(payload).Select(r => (string)r["key"]!).ToList();

    private static IReadOnlyList<string> Names(Dictionary<string, object?> row, string field) =>
        row.TryGetValue(field, out object? value) && value is IEnumerable<string> names
            ? names.ToList()
            : Array.Empty<string>();

    private static IEnumerable<string> Types(IEnumerable<ValueKind?> kinds) =>
        kinds.Select(KnowledgePack.MachineName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal);
}
