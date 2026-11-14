using System.Text.RegularExpressions;
using LTools.Core.Actions;

namespace LTools.Core.SelfTest;

/// <summary>
/// 动作单元的声明自检：遍历 <see cref="StepRegistry.Default"/> 的每一个单元，
/// 把「新加一个单元时会忘的事」变成断言（名称 / 图标 / 分类 / 枚举中文名 / 子步骤 / 动态参数）。
/// </summary>
/// <remarks>
/// 这一组本身就是「覆盖清单」：凡是注册进 <see cref="StepRegistry.Default"/> 的单元都会被遍历到，
/// 新增单元不需要另外登记测试。分类顺序只在 <see cref="UnitCategories"/> 里排，
/// 所以这里断言「分类必须在表里」（<c>Rank == Order.Length</c> 表示不在表里）。
/// </remarks>
internal static class DeclarationSuite
{
    /// <summary>动作单元键：小写开头的小驼峰点分（<c>screen.findImage</c>）。</summary>
    private static readonly Regex UnitKeyPattern = new(
        @"^[a-z][a-zA-Z0-9]*(\.[a-z][a-zA-Z0-9]*)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>参数键 / 变量名：小写开头的小驼峰（<c>timeoutMs</c> / <c>shotX</c>），不许点 / 空格 / 连字符。</summary>
    private static readonly Regex KeyPattern = new(
        @"^[a-z][a-zA-Z0-9]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>允许带子步骤的单元（新增一个之前先想清楚：编辑器 / 校验 / 步骤文本都要跟着改）。</summary>
    private static readonly string[] BodyUnits = { "flow.repeat", "flow.forEach", "flow.while" };

    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("declaration", "单元总数与键唯一", Count));
        cases.Add(new("declaration", "名称 / 图标 / 说明 / 分类", Basics));
        cases.Add(new("declaration", "子步骤声明只给流程单元", BodyDeclarations));
        cases.Add(new("declaration", "动态参数只给调用子动作", DynamicInputs));
        cases.Add(new("declaration", "输入参数声明完整", Inputs));
        cases.Add(new("declaration", "选项参数引用真实存在", OptionParams));
        cases.Add(new("declaration", "可见条件引用真实参数", VisibleRules));
        cases.Add(new("declaration", "输出参数声明完整", Outputs));
        cases.Add(new("declaration", "枚举选项都有中文名", OptionLabelCoverage));
    }

    private static void Count()
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicated = 0;

        foreach (IStepUnit unit in StepRegistry.Default.Units)
            if (!keys.Add(unit.Spec.Key)) duplicated++;

        Check.Equal(0, duplicated, "动作单元键不该重复（注册表会拦，这里兜一层）");
        Check.True(keys.Count >= 70, $"已注册动作单元应 ≥ 70 个（实际 {keys.Count}）");
        Check.Note($"动作单元 {keys.Count} 个");
    }

    private static void Basics()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            UnitSpec spec = unit.Spec;
            string where = "单元「" + spec.Key + "」";

            Check.True(UnitKeyPattern.IsMatch(spec.Key), $"{where}：键应是「段.段」的小驼峰（实际「{spec.Key}」）");
            Check.False(spec.Name.Trim().Length == 0, $"{where}：名称不能为空");
            Check.False(spec.Name.Contains('\n'), $"{where}：名称不该换行");
            Check.False(spec.Icon.Trim().Length == 0, $"{where}：图标不能为空");
            Check.False(spec.Description.Trim().Length == 0, $"{where}：说明不能为空（知识包与左栏提示都用它）");
            Check.False(spec.Category.Trim().Length == 0, $"{where}：分类不能为空");
            Check.True(UnitCategories.Rank(spec.Category) < 10,
                $"{where}：分类「{spec.Category}」不在 UnitCategories.Order 里（左栏会排到最后）");

            // 注册时已经调过 Validate()；这里再调一次，断言它对当前声明是幂等的（不靠注册路径兜底）。
            try
            {
                spec.Validate();
                Check.True(true, $"{where}：声明自校验通过");
            }
            catch (ActionException ex)
            {
                Check.Fail($"{where}：声明自校验失败", ex.Message);
            }
        }
    }

    private static void BodyDeclarations()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            UnitSpec spec = unit.Spec;
            string where = "单元「" + spec.Key + "」";

            if (spec.Body == BodyKind.none)
            {
                Check.False(BodyUnits.Contains(spec.Key, StringComparer.OrdinalIgnoreCase),
                    $"{where}：循环单元必须声明 Body = body");
                continue;
            }

            if (spec.Body == BodyKind.thenElse)
            {
                Check.Equal("flow.if", spec.Key, $"{where}：只有「如果」用 then / else");
                Check.Equal(UnitKind.action, spec.Kind, $"{where}：带分支的单元不该是取值单元");
                continue;
            }

            Check.True(BodyUnits.Contains(spec.Key, StringComparer.OrdinalIgnoreCase),
                $"{where}：带循环体的单元不在允许清单里（新加循环单元时同步这里与 ActionValidator）");
            Check.Equal(UnitKind.action, spec.Kind, $"{where}：带循环体的单元不该是取值单元");
        }
    }

    private static void DynamicInputs()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            if (!unit.Spec.DynamicInputs) continue;
            Check.Equal("sub.run", unit.Spec.Key,
                $"单元「{unit.Spec.Key}」声明了 DynamicInputs，但只有「调用子动作」按变量名收参数");
        }
    }

    private static void Inputs()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            UnitSpec spec = unit.Spec;
            foreach (InputSpec input in spec.Inputs)
            {
                string where = $"单元「{spec.Key}」的参数「{input.Key}」";

                Check.False(input.Label.Trim().Length == 0, $"{where}：缺少显示名（Label）");
                Check.True(KeyPattern.IsMatch(input.Key), $"{where}：键应是首字母小写的字母数字（实际「{input.Key}」）");

                if (input.Editor is not (InputEditor.enumList or InputEditor.flags)) continue;

                Check.True(input.Options is { Count: > 0 }, $"{where}：{input.Editor} 必须声明 Options");
                if (input.Options is not { Count: > 0 }) continue;

                Check.Equal(0, input.Options.Count(o => string.IsNullOrWhiteSpace(o)),
                    $"{where}：Options 里不该有空值");
                Check.Equal(input.Options.Count, input.Options.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    $"{where}：Options 有重复值");

                if (input.Default is { Kind: ValueKind.text } defaultValue)
                    Check.True(input.Options.Contains(defaultValue.AsText(), StringComparer.OrdinalIgnoreCase),
                        $"{where}：默认值「{defaultValue.AsText()}」不在 Options 里");

                if (input.Labels == null) continue;
                foreach (string key in input.Labels.Keys)
                    Check.True(input.Options.Contains(key, StringComparer.OrdinalIgnoreCase),
                        $"{where}：私有中文名「{key}」不是 Options 里的值");
            }
        }
    }

    private static void OptionParams()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            UnitSpec spec = unit.Spec;
            var keys = new HashSet<string>(spec.Inputs.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

            foreach (InputSpec input in spec.Inputs)
            {
                if (input.OptionParams is not { Count: > 0 }) continue;

                Check.Equal(InputEditor.enumList, input.Editor,
                    $"单元「{spec.Key}」的参数「{input.Key}」：OptionParams 只能挂在枚举参数上");
                Check.True(input.Options != null && input.OptionParams.Keys.All(o =>
                        input.Options.Contains(o, StringComparer.OrdinalIgnoreCase)),
                    $"单元「{spec.Key}」的参数「{input.Key}」：OptionParams 的选项都必须出现在 Options 里");

                foreach (var pair in input.OptionParams)
                foreach (string target in pair.Value)
                    Check.True(keys.Contains(target),
                        $"单元「{spec.Key}」的参数「{input.Key}」：OptionParams 引用了不存在的参数「{target}」");
            }
        }
    }

    private static void VisibleRules()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            UnitSpec spec = unit.Spec;
            var keys = new HashSet<string>(spec.Inputs.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);

            foreach (InputSpec input in spec.Inputs)
            {
                if (string.IsNullOrWhiteSpace(input.VisibleWhen)) continue;

                Check.True(VisibleWhen.TryGetRule(input, out string key, out string value),
                    $"单元「{spec.Key}」的参数「{input.Key}」：可见条件「{input.VisibleWhen}」解析不了");
                if (key.Length == 0) continue;

                Check.True(keys.Contains(key),
                    $"单元「{spec.Key}」的参数「{input.Key}」：可见条件引用了不存在的参数「{key}」");

                // 值是带引号的枚举字面量时，必须在那个参数的 Options 里；布尔 / 裸值不查。
                InputSpec? driver = spec.Inputs.FirstOrDefault(i =>
                    string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase));
                if (driver?.Options is not { Count: > 0 }) continue;
                if (input.VisibleWhen!.IndexOf('\'') < 0) continue;

                Check.True(driver.Options.Contains(value, StringComparer.OrdinalIgnoreCase),
                    $"单元「{spec.Key}」的参数「{input.Key}」：可见条件里的「{value}」不是「{key}」的选项");
            }
        }
    }

    private static void Outputs()
    {
        foreach (IStepUnit unit in StepRegistry.Default.Units)
        {
            UnitSpec spec = unit.Spec;
            foreach (OutputSpec output in spec.Outputs)
            {
                string where = $"单元「{spec.Key}」的输出「{output.Key}」";

                Check.True(KeyPattern.IsMatch(output.Key), $"{where}：键应是首字母小写的字母数字");
                Check.False(output.Label.Trim().Length == 0, $"{where}：缺少显示名（Label）");

                if (string.IsNullOrEmpty(output.DefaultVariable)) continue;
                Check.True(KeyPattern.IsMatch(output.DefaultVariable),
                    $"{where}：建议变量名「{output.DefaultVariable}」应是合法标识符（不能带空格 / 点 / 连字符）");
            }
        }
    }

    private static void OptionLabelCoverage()
    {
        var missing = new List<string>();

        foreach (IStepUnit unit in StepRegistry.Default.Units)
        foreach (InputSpec input in unit.Spec.Inputs)
        {
            if (input.Options is not { Count: > 0 }) continue;
            foreach (string option in input.Options)
                if (string.Equals(OptionLabels.For(input, option), option, StringComparison.Ordinal))
                    missing.Add($"{unit.Spec.Key}.{input.Key}={option}");
        }

        if (missing.Count == 0)
        {
            Check.True(true, "所有枚举值都有中文名");
            return;
        }

        Check.Note($"没有中文名的枚举值 {missing.Count} 个：" +
            string.Join(", ", missing.Take(12)) + (missing.Count > 12 ? " …" : ""));
    }
}
