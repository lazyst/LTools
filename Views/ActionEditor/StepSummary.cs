using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Views;

/// <summary>
/// 步骤的一行摘要（编辑器列表 / 工具箱提示共用）。
/// 只依赖 <see cref="UnitSpec"/> 与步骤数据，新动作单元不需要改这里就能显示出像样的摘要。
/// </summary>
internal static class StepSummary
{
    private static readonly HashSet<string> UnaryOps = new(StringComparer.OrdinalIgnoreCase)
    {
        "isEmpty", "isNotEmpty", "isTrue", "isFalse",
        "found", "notFound", "active", "notActive",
    };

    public static string Title(Step step) => Title(step.Unit);

    public static string Title(string unitKey) =>
        StepRegistry.Default.TryGet(unitKey, out var unit) ? unit.Spec.Name : unitKey;

    /// <summary>参数摘要（不含动作单元名）。</summary>
    public static string Describe(Step step)
    {
        if (!StepRegistry.Default.TryGet(step.Unit, out var unit)) return "";

        if (step.Unit is "flow.if" or "flow.while") return ConditionText(step);
        if (step.Unit == "sub.run") return SubActionText(step);
        if (step.Unit == MacroAction.UnitKey) return MacroText(step);
        if (step.Unit == "input.sendKeys") return KeyText(step);

        var parts = new List<string>();
        foreach (var input in unit.Spec.Inputs)
        {
            if (!IsVisible(input, step, unit.Spec)) continue;
            if (!step.In.TryGetValue(input.Key, out var arg)) continue;
            if (IsEmpty(arg)) continue;
            if (input.Default != null && arg.Kind == StepArgKind.literal
                && string.Equals(arg.Literal.AsText(), input.Default.AsText(), StringComparison.Ordinal))
                continue;

            string value = input.Editor == InputEditor.enumList
                ? OptionText(input, arg)
                : ArgText(arg);
            parts.Add(input.Editor == InputEditor.enumList ? value : $"{input.Label} {value}");
            if (parts.Count >= 2) break;
        }

        return string.Join(" · ", parts);
    }

    /// <summary>输出变量 chip（没有输出变量时为空）。</summary>
    public static string OutputText(Step step)
    {
        if (step.Out.Count == 0) return "";
        return step.Out.Count == 1
            ? step.Out.Values.First()
            : string.Join("，", step.Out.Values);
    }

    public static string ArgText(StepArg? arg)
    {
        if (arg == null) return "";

        return arg.Kind switch
        {
            StepArgKind.variable => "${" + arg.VariablePath + "}",
            StepArgKind.inline => Title(arg.InlineStep?.Unit ?? ""),
            _ => Quote(arg.Literal.AsText()),
        };
    }

    /// <summary>把字面量文本压成一行短句（换行变 ⏎，过长截断）。</summary>
    public static string Quote(string? text)
    {
        string s = (text ?? "").Replace("\r", "").Replace("\n", "⏎").Trim();
        if (s.Length > 42) s = s[..41] + "…";
        return s;
    }

    /// <summary>
    /// 枚举参数的摘要文本：字面量翻成中文名，变量 / 内联取值原样显示
    /// （它们运行期才知道值，翻不了也不该翻）。
    /// </summary>
    private static string OptionText(InputSpec input, StepArg arg) =>
        arg.Kind == StepArgKind.literal
            ? OptionLabels.For(input, arg.Literal.AsText())
            : ArgText(arg);

    /// <summary>
    /// 「模拟按键」的摘要：<c>短按 Ctrl+S</c> / <c>按下 Shift</c> / <c>抬起 Shift</c>。
    /// 三个参数（按键 / 动作 / 修饰键）拼成一句话，比「按键 S · 动作 短按」好读。
    /// </summary>
    private static string KeyText(Step step)
    {
        if (!step.In.TryGetValue("key", out var keyArg)) return "";
        if (keyArg.Kind != StepArgKind.literal) return ArgText(keyArg);

        string key = keyArg.Literal.AsText().Trim();
        if (key.Length == 0) return "（未选择按键）";

        string action = step.In.TryGetValue("action", out var actionArg)
                        && actionArg.Kind == StepArgKind.literal
            ? actionArg.Literal.AsText().Trim()
            : "tap";
        string verb = action switch
        {
            "down" => "按下",
            "up" => "抬起",
            _ => "短按",
        };

        var parts = new List<string>();
        if (step.In.TryGetValue("mods", out var modsArg) && modsArg.Kind == StepArgKind.literal)
        {
            foreach (string mod in modsArg.Literal.AsText()
                         .Split('+', StringSplitOptions.RemoveEmptyEntries))
            {
                string label = KeyCatalog.LabelOf(mod.Trim());
                if (label.Length > 0) parts.Add(label);
            }
        }
        parts.Add(KeyCatalog.LabelOf(key));

        return $"{verb} {string.Join("+", parts)}";
    }

    /// <summary>
    /// 「回放宏」步骤的摘要：宏名字 + 时长（比裸宏 Id 有用）。宏文件没了就说清楚，
    /// 而不是显示一个看不懂的 Id。
    /// </summary>
    private static string MacroText(Step step)
    {
        if (!step.In.TryGetValue("macroId", out var arg)) return "";
        if (arg.Kind != StepArgKind.literal) return ArgText(arg);

        string id = arg.Literal.AsText().Trim();
        if (id.Length == 0) return "（未选择宏）";

        var meta = MacroStore.MetaOf(id);
        if (meta == null) return "宏文件缺失";
        return $"{meta.Name} · {meta.DurationText}";
    }

    /// <summary>
    /// 「调用子动作」步骤的摘要：子动作名字 + 前两个输入（比裸 id 有用）。
    /// 子动作被删了就直说，而不是显示一个看不懂的 id。
    /// </summary>
    private static string SubActionText(Step step)
    {
        if (!step.In.TryGetValue("subAction", out var arg)) return "";
        if (arg.Kind != StepArgKind.literal) return ArgText(arg);

        string id = arg.Literal.AsText().Trim();
        if (id.Length == 0) return "（未选择子动作）";

        var def = SubActionStore.Load(id);
        if (def == null) return $"⚠ 找不到：{id}";

        string name = string.IsNullOrWhiteSpace(def.Name) ? id : def.Name;
        var parts = new List<string>();
        foreach (var variable in def.Variables)
        {
            if (variable.Direction != VariableDirection.input) continue;
            string key = (variable.Key ?? "").Trim();
            if (key.Length == 0 || !step.In.TryGetValue(key, out var value)) continue;
            if (IsEmpty(value)) continue;

            parts.Add($"{key} {ArgText(value)}");
            if (parts.Count >= 2) break;
        }

        return parts.Count == 0 ? name : $"{name}（{string.Join("，", parts)}）";
    }

    private static bool IsEmpty(StepArg arg) =>
        arg.Kind == StepArgKind.literal
        && arg.Literal.Kind == ValueKind.text
        && arg.Literal.AsText().Length == 0;

    private static string ConditionText(Step step)
    {
        string left = step.In.TryGetValue("left", out var l) ? ArgText(l) : "";
        string op = step.In.TryGetValue("op", out var o) ? o.Literal.AsText() : "equals";
        string right = step.In.TryGetValue("right", out var r) ? ArgText(r) : "";
        string label = OptionLabels.Of(op);

        string text = UnaryOps.Contains(op) || right.Length == 0
            ? $"{left} {label}"
            : $"{left} {label} {right}";

        bool negate = step.In.TryGetValue("negate", out var n)
            && n.Kind == StepArgKind.literal
            && n.Literal.AsBool();
        return negate ? "非（" + text + "）" : text;
    }

    // —— VisibleWhen ——

    /// <summary>
    /// 按 <see cref="InputSpec.VisibleWhen"/> 判断参数行是否显示（规则见 <see cref="VisibleWhen"/>）。
    /// </summary>
    public static bool IsVisible(InputSpec input, Step step, UnitSpec spec) =>
        VisibleWhen.IsVisible(input, step.In, spec);

    /// <summary>
    /// 解析 <see cref="InputSpec.VisibleWhen"/> 的第一条 <c>key == 'value'</c>。
    /// 参数对话框用它把「配套枚举」切到让这一行出现的值（框选完自动切到「固定区域」）。
    /// </summary>
    public static bool TryGetVisibleRule(InputSpec input, out string key, out string value) =>
        VisibleWhen.TryGetRule(input, out key, out value);

    /// <summary>
    /// 找到「哪个枚举选项会显示这个参数」：参数走 <see cref="InputSpec.OptionParams"/> 时没有
    /// <see cref="InputSpec.VisibleWhen"/>，框选后要靠它把配套枚举切到该选项（如 <c>rect</c>）。
    /// </summary>
    public static bool TryGetOptionFor(InputSpec input, UnitSpec spec, out string key, out string value) =>
        VisibleWhen.TryGetOptionFor(input, spec, out key, out value);
}
