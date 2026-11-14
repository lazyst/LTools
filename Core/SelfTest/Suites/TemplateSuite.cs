using LTools.Core.Actions;

namespace LTools.Core.SelfTest;

/// <summary>
/// 起步模板（编辑器空动作时那块「从模板开始」）：每条点下去都要**能直接跑**，
/// 所以逐条拼成动作过一遍静态校验，不允许出现 error。
/// </summary>
/// <remarks>
/// 只断言「没有 error」：模板里 <c>out</c> 写出的变量在第一次运行时才进变量表，
/// 编辑器会给「变量表里看不到它」的提醒，那是预期行为，不算模板坏。
/// </remarks>
internal static class TemplateSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("templates", "模板清单完整", Catalog));
        cases.Add(new("templates", "每条模板都能通过校验", EachValidates));
    }

    private static void Catalog()
    {
        IReadOnlyList<ActionTemplate> all = ActionTemplates.All;

        Check.True(all.Count >= 4, $"起步模板应至少 4 条（实际 {all.Count}）");
        Check.Equal(all.Count, all.Select(t => t.Name).Distinct(StringComparer.Ordinal).Count(),
            "模板名称不该重复");

        foreach (ActionTemplate template in all)
        {
            Check.False(template.Name.Trim().Length == 0, "模板缺少名称");
            Check.False(template.Hint.Trim().Length == 0, $"模板「{template.Name}」缺少说明");
            Check.True(template.Build().Count > 0, $"模板「{template.Name}」没有步骤");
        }
    }

    private static void EachValidates()
    {
        foreach (ActionTemplate template in ActionTemplates.All)
        {
            ActionProgram program = ToProgram(template);
            ActionValidationResult result = ActionValidator.Validate(program);

            Check.True(result.Ok,
                $"模板「{template.Name}」应能直接运行：" + Describe(result));
            Check.Equal(0, result.Errors, $"模板「{template.Name}」不该有错误：" + Describe(result));
        }
    }

    // ============ 工具 ============

    /// <summary>按编辑器插入模板时的做法拼一个动作：变量声明去重，步骤照原样追加。</summary>
    private static ActionProgram ToProgram(ActionTemplate template)
    {
        var program = new ActionProgram { Name = template.Name };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (VariableDef variable in template.Variables())
        {
            if (string.IsNullOrWhiteSpace(variable.Key) || !seen.Add(variable.Key)) continue;
            program.Variables.Add(variable.Clone());
        }

        foreach (Step step in template.Build()) program.Steps.Add(step.Clone());
        return program;
    }

    private static string Describe(ActionValidationResult result) =>
        result.Issues.Count == 0
            ? "（无问题）"
            : string.Join(" | ", result.Issues.Select(i => $"{i.Severity} {i.Path} {i.Message}"));
}
