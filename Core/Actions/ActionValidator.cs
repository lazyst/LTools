using System.Text.RegularExpressions;
using LTools.Features;

namespace LTools.Core.Actions;

/// <summary>问题级别：<see cref="error"/> 会让动作跑不起来，<see cref="warning"/> 只是提醒。</summary>
internal enum IssueSeverity
{
    error,
    warning,
}

/// <summary>
/// 一个静态校验问题。位置用引擎现成的步骤路径与人话标签（<c>第 3 行 › 满足时 › 第 1 行</c>），
/// 与试运行轨迹 / 失败提示同一套规则，不再另造「第几步」。
/// </summary>
internal sealed record ActionIssue(
    string Path,
    string Label,
    string Unit,
    string Key,
    string Message,
    IssueSeverity Severity);

/// <summary>一次静态校验的结果（照 Quicker 的 check 回执：可运行性 + 精确定位 + revision）。</summary>
internal sealed class ActionValidationResult
{
    public string? Revision { get; init; }

    public IReadOnlyList<ActionIssue> Issues { get; init; } = Array.Empty<ActionIssue>();

    /// <summary>没有 error 级问题就认为可以运行（warning 不拦）。</summary>
    public bool Ok => Issues.All(i => i.Severity != IssueSeverity.error);

    public int Errors => Issues.Count(i => i.Severity == IssueSeverity.error);

    public int Warnings => Issues.Count(i => i.Severity == IssueSeverity.warning);
}

/// <summary>
/// 动作静态校验器：AI 写出来的草稿「敢不敢用」的答案，也是编辑器里「校验」按钮的唯一实现。
/// </summary>
/// <remarks>
/// <para>
/// 规则来自 <see cref="UnitSpec"/> 的机器可读元数据（不是另写一份 YAML）：未知单元 / 未知参数、
/// 必填缺失（只对 <see cref="VisibleWhen"/> 判定为可见的参数，与引擎 / 参数对话框同一条规则）、
/// 字面量类型、枚举值、变量引用、条件运算符 / 正则、子动作与宏引用、内联取值单元、预算。
/// </para>
/// <para>
/// 校验器<b>不执行</b>动作，也不做会阻塞的事（宏 / 子动作只读文件）。
/// </para>
/// </remarks>
internal static class ActionValidator
{
    /// <summary>步骤数超过这个值就提醒（运行时上限是 10 万，这里只是「这个动作太大了」）。</summary>
    private const int LargeStepCount = 500;

    /// <summary>嵌套深度提醒阈值（子动作递归上限见 <see cref="ActionEngine.MaxSubActionDepth"/>）。</summary>
    private const int DeepNesting = 8;

    /// <summary>
    /// 校验一个动作体。
    /// </summary>
    /// <param name="revision">内容修订号（草稿 check 用；编辑器传 null）。</param>
    /// <param name="resourceRoot">
    /// 图片资源根：草稿是 <c>staging/&lt;id&gt;/files</c>，正式动作是 <c>actions/&lt;id&gt;/files</c>；
    /// null = 按数据根解析。动作里的 <c>files/x.png</c> 与裸文件名都先在这里找。
    /// </param>
    /// <param name="draftCheck">
    /// 草稿验收（check / promote）：图片**必须**在资源根里（否则采纳后会指向不存在的文件）；
    /// 编辑器校验传 false，找不到只报 warning（可能引用共享模板或用户素材）。
    /// </param>
    public static ActionValidationResult Validate(ActionProgram? program, string? revision = null,
        string? resourceRoot = null, bool draftCheck = false) =>
        ValidateCore(program, revision, resourceRoot, draftCheck, allowReturn: false, resolveSubAction: null);

    /// <summary>
    /// 校验一个子动作定义（写盘前 / AI 写入前）：动作那一套规则，加上子动作自己的规则。
    /// </summary>
    /// <remarks>
    /// 差别只有三处：<c>flow.return</c> 合法；输出变量必须有人写；调用成环要把「正在写的这一份」算进去
    /// （它还没落盘，<see cref="SubActionStore"/> 里查不到，自调用会漏过去）。
    /// </remarks>
    public static ActionValidationResult ValidateSubAction(SubActionDef? def, string? revision = null)
    {
        var issues = new List<ActionIssue>();
        if (def == null)
        {
            issues.Add(new ActionIssue("", "", "", "", "子动作定义是空的", IssueSeverity.error));
            return new ActionValidationResult { Revision = revision, Issues = issues };
        }

        string id = (def.Id ?? "").Trim();
        if (id.Length == 0)
            issues.Add(new ActionIssue("", "", "", "id", "子动作缺少 id（文件名就是 id）", IssueSeverity.error));

        // 子动作与动作同构：包成 ActionProgram 就能复用整套步骤 / 变量校验。
        var program = new ActionProgram
        {
            Id = id,
            Name = def.Name,
            Icon = def.Icon,
            Description = def.Description,
        };
        foreach (var variable in def.Variables) program.Variables.Add(variable.Clone());
        foreach (var step in def.Steps) program.Steps.Add(step.Clone());

        SubActionDef? Resolve(string target) =>
            string.Equals(target, id, StringComparison.OrdinalIgnoreCase) ? def : SubActionStore.Load(target);

        var result = ValidateCore(program, revision, resourceRoot: null, draftCheck: false,
            allowReturn: true, resolveSubAction: Resolve);
        issues.AddRange(result.Issues);

        // 输出变量没人写：调用方拿到的永远是默认值（多半是忘了赋值，或把方向写成了「输出」却当本地用）。
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var steps = new List<(Step Step, string Path, string Label, int Depth)>();
        ProgramWalker.CollectSteps(def.Steps, "/steps", "", 1, steps);
        foreach (var (step, _, _, _) in steps)
            foreach (var write in VariableWrites.Of(step))
                written.Add(RootName(write.Name));

        foreach (var variable in def.Variables)
        {
            if (variable.Direction != VariableDirection.output) continue;
            string key = (variable.Key ?? "").Trim();
            if (key.Length == 0 || written.Contains(key)) continue;

            issues.Add(new ActionIssue("/variables", "变量表", "", key,
                $"输出「{key}」在步骤里没有被赋值，调用方拿到的会是默认值", IssueSeverity.warning));
        }

        return new ActionValidationResult { Revision = revision, Issues = issues };
    }

    private static ActionValidationResult ValidateCore(ActionProgram? program, string? revision,
        string? resourceRoot, bool draftCheck, bool allowReturn,
        Func<string, SubActionDef?>? resolveSubAction)
    {
        var issues = new List<ActionIssue>();
        if (program == null)
        {
            issues.Add(new ActionIssue("", "", "", "", "动作体是空的（program.json 没有解析出内容）", IssueSeverity.error));
            return new ActionValidationResult { Revision = revision, Issues = issues };
        }

        if (string.IsNullOrWhiteSpace(program.Name))
            issues.Add(new ActionIssue("", "", "", "name", "没有名称（列表里会显示 id，仍能运行）", IssueSeverity.warning));

        ValidateScope(program, resourceRoot, draftCheck, allowReturn, resolveSubAction, issues);

        if (program.Steps.Count == 0)
            issues.Add(new ActionIssue("/steps", "", "", "steps",
                "动作没有任何步骤，运行起来什么都不会做", IssueSeverity.warning));

        return new ActionValidationResult { Revision = revision, Issues = issues };
    }

    // ============ 作用域 ============

    private static void ValidateScope(ActionProgram program, string? resourceRoot,
        bool draftCheck, bool allowReturn, Func<string, SubActionDef?>? resolveSubAction,
        List<ActionIssue> issues)
    {
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<VariableDef> variables = program.Variables;

        foreach (var variable in variables)
        {
            string key = (variable.Key ?? "").Trim();
            if (key.Length == 0)
            {
                issues.Add(new ActionIssue("/variables", "变量表", "", "", "有变量声明缺 key", IssueSeverity.error));
                continue;
            }

            if (!declared.Add(key))
                issues.Add(new ActionIssue("/variables", "变量表", "", key,
                    $"变量「{key}」重复声明", IssueSeverity.error));
        }

        // 运行时会创建 out 写到的变量与 flow.set 写到的变量，引用它们不算未定义。
        var produced = new HashSet<string>(declared, StringComparer.OrdinalIgnoreCase);

        const string rootPath = "/steps";
        const string rootLabel = "";

        var allSteps = new List<(Step Step, string Path, string Label, int Depth)>();
        ProgramWalker.CollectSteps(program.Steps, rootPath, rootLabel, 1, allSteps);

        foreach (var (step, path, label, _) in allSteps)
        {
            // 输出映射 + 设置变量的目标名（判定见 VariableWrites）。
            foreach (var write in VariableWrites.Of(step))
            {
                string name = RootName(write.Name);
                if (name.Length == 0)
                {
                    if (write.IsOutput)
                        issues.Add(new ActionIssue($"{path}/out", label, step.Unit, "",
                            "有 out 映射写到了空变量名", IssueSeverity.error));
                    continue;
                }

                produced.Add(name);
                if (write.IsOutput && !declared.Contains(name))
                {
                    issues.Add(new ActionIssue($"{path}/out", label, step.Unit, name,
                        $"写出了变量「{name}」，但它不在变量表里（运行时能创建，只是变量表里看不到它）",
                        IssueSeverity.warning));
                }
            }
        }

        foreach (var (step, path, label, depth) in allSteps)
            ValidateStep(program, step, path, label, depth, produced, resourceRoot, draftCheck, allowReturn,
                resolveSubAction, issues);

        if (allSteps.Count > LargeStepCount)
            issues.Add(new ActionIssue(rootPath, rootLabel, "", "steps",
                $"步骤很多（{allSteps.Count} 步），静态校验和运行都会比较慢", IssueSeverity.warning));
    }

    // ============ 单步 ============

    private static void ValidateStep(ActionProgram program, Step step, string path, string label, int depth,
        HashSet<string> produced, string? resourceRoot, bool draftCheck, bool allowReturn,
        Func<string, SubActionDef?>? resolveSubAction, List<ActionIssue> issues)
    {
        var registry = StepRegistry.Default;
        if (!registry.TryGet(step.Unit, out var unit))
        {
            issues.Add(new ActionIssue(path, label, step.Unit, "",
                string.IsNullOrWhiteSpace(step.Unit) ? "步骤缺少单元键" : $"未知的功能单元：{step.Unit}",
                IssueSeverity.error));
            return;
        }

        var spec = unit.Spec;
        if (depth > DeepNesting)
            issues.Add(new ActionIssue(path, label, step.Unit, "",
                $"嵌套层数较深（第 {depth} 层），排查起来会很费劲", IssueSeverity.warning));

        if (step.Disabled)
            return;

        // 「返回」只结束子动作；放在动作里运行时直接报错，静态就能拦。
        if (!allowReturn && step.Unit.Equals("flow.return", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ActionIssue(path, label, step.Unit, "",
                "「返回」只能用在子动作里（结束当前子动作）；动作要提前结束请用「停止」",
                IssueSeverity.error));
        }

        // —— 未知参数 ——
        foreach (string key in step.In.Keys)
        {
            if (spec.Inputs.Any(i => string.Equals(i.Key, key, StringComparison.OrdinalIgnoreCase))) continue;
            if (spec.DynamicInputs) continue;

            issues.Add(new ActionIssue($"{path}/in/{key}", label, step.Unit, key,
                $"「{spec.Name}」没有参数「{key}」（检查拼写）", IssueSeverity.error));
        }

        // —— 必填 / 类型 / 枚举 ——
        foreach (var input in spec.Inputs)
        {
            bool visible = VisibleWhen.IsVisible(input, step.In, spec);
            if (!visible) continue;

            if (!step.In.TryGetValue(input.Key, out var arg))
            {
                if (input.Required && input.Default == null)
                {
                    issues.Add(new ActionIssue($"{path}/in/{input.Key}", label, step.Unit, input.Key,
                        $"缺少必填参数「{input.Label}」", IssueSeverity.error));
                }
                continue;
            }

            ValidateArg(program, step, path, label, input, arg, produced, resourceRoot, draftCheck, allowReturn,
                resolveSubAction, issues);
        }

        // —— 条件 ——
        ValidateCondition(step, path, label, produced, issues);

        // —— 子动作 / 宏 ——
        ValidateReference(step, path, label, resolveSubAction, issues);

        // —— 预算：无界循环 ——
        if (step.Unit.Equals("flow.while", StringComparison.OrdinalIgnoreCase))
            issues.Add(new ActionIssue(path, label, step.Unit, "",
                "条件循环没有次数上限（运行时最多 2 万轮，但可能长时间占用键鼠）", IssueSeverity.warning));
    }

    private static void ValidateArg(ActionProgram program, Step step, string path, string label, InputSpec input,
        StepArg arg, HashSet<string> produced, string? resourceRoot, bool draftCheck, bool allowReturn,
        Func<string, SubActionDef?>? resolveSubAction, List<ActionIssue> issues)
    {
        string issuePath = $"{path}/in/{input.Key}";

        // 变量引用（{"var": ...}）
        if (arg.Kind == StepArgKind.variable)
            CheckVariable(arg.VariablePath, issuePath, label, input, produced, issues, isOutput: false);

        // 内联取值单元
        if (arg.Kind == StepArgKind.inline)
        {
            if (arg.InlineStep == null)
            {
                issues.Add(new ActionIssue(issuePath, label, step.Unit, input.Key,
                    "内联取值步骤为空", IssueSeverity.error));
                return;
            }

            if (!StepRegistry.Default.TryGet(arg.InlineStep.Unit, out var inlineUnit))
            {
                issues.Add(new ActionIssue(issuePath, label, arg.InlineStep.Unit, input.Key,
                    $"内联取值引用了未知单元：{arg.InlineStep.Unit}", IssueSeverity.error));
            }
            else if (inlineUnit.Spec.Kind != UnitKind.pure)
            {
                issues.Add(new ActionIssue(issuePath, label, arg.InlineStep.Unit, input.Key,
                    $"「{inlineUnit.Spec.Name}」不是取值单元，不能内联在值的位置", IssueSeverity.error));
            }
            else
            {
                ValidateStep(program, arg.InlineStep, issuePath, label, 1, produced, resourceRoot, draftCheck,
                    allowReturn, resolveSubAction, issues);
            }
        }

        if (arg.Kind != StepArgKind.literal) return;

        Value value = arg.Literal;
        string text = value.AsText();

        // 枚举值（保留旧值的只警告）。多值编辑器（flags 存 a+b）没法拿整串比，
        // 按键由 KeyStroke 在运行时给明确报错，两者都不在这里查。
        if (input.Editor == InputEditor.enumList
            && input.Options is { Count: > 0 } && text.Trim().Length > 0 && !text.Contains("${"))
        {
            if (!input.Options.Any(o => string.Equals(o, text.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ActionIssue(issuePath, label, step.Unit, input.Key,
                    $"值「{text.Trim()}」不在可选范围里（{string.Join(" / ", input.Options.Select(o => OptionLabels.For(input, o)))}）",
                    IssueSeverity.warning));
            }
        }

        // 类型（文本里带 ${…} 时交给运行时，不做静态判断）
        if (input.Type is { } kind && !text.Contains("${"))
        {
            try
            {
                if (kind == ValueKind.list && value.Kind == ValueKind.text)
                {
                    if (!ListText.TryParseDefault(ValueKind.list, text, out _, out string error))
                        throw new ActionException(error);
                }
                else if (kind == ValueKind.number && value.Kind == ValueKind.text && text.Trim().Length == 0)
                {
                    // 空文本给数字参数：运行时会按空值处理，不在这里报错
                }
                else
                {
                    value.Coerce(kind);
                }
            }
            catch (ActionException ex)
            {
                issues.Add(new ActionIssue(issuePath, label, step.Unit, input.Key,
                    $"「{input.Label}」的值类型不对：{ex.Message}", IssueSeverity.error));
            }
        }

        // 文本里的 ${变量}
        if (!input.Raw)
            CheckTemplate(text, issuePath, label, input, produced, issues);

        // 图片（动作目录 files/、草稿 files/ 或既有共享资源）
        if (string.Equals(input.Key, "image", StringComparison.OrdinalIgnoreCase) && text.Trim().Length > 0 &&
            !text.Contains("${"))
            ValidateImage(text.Trim(), issuePath, label, step.Unit, resourceRoot, draftCheck, issues);
    }

    private static void CheckVariable(string path, string issuePath, string label, InputSpec input,
        HashSet<string> produced, List<ActionIssue> issues, bool isOutput)
    {
        string name = RootName(path);
        if (name.Length == 0) return;
        if (produced.Contains(name)) return;

        issues.Add(new ActionIssue(issuePath, label, "", input.Key,
            isOutput
                ? $"写出到未声明的变量「{name}」"
                : $"引用了未声明的变量「{name}」（运行到这里会失败，先添加变量或让前面的步骤写出它）",
            IssueSeverity.warning));
    }

    private static void CheckTemplate(string text, string issuePath, string label, InputSpec input,
        HashSet<string> produced, List<ActionIssue> issues)
    {
        if (text.IndexOf('$') < 0) return;

        foreach (string name in TemplateVariables(text))
            if (!produced.Contains(name))
            {
                issues.Add(new ActionIssue(issuePath, label, "", input.Key,
                    $"模板里引用了未声明的变量「{name}」", IssueSeverity.warning));
            }

        // 少写右花括号的模板在运行时会直接抛错，静态就能看出来。
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '$' || i + 1 >= text.Length) continue;
            if (text[i + 1] == '$') { i++; continue; }
            if (text[i + 1] != '{') continue;
            int end = text.IndexOf('}', i + 2);
            if (end < 0)
            {
                issues.Add(new ActionIssue(issuePath, label, "", input.Key,
                    "模板缺少右花括号：${ 没有闭合", IssueSeverity.error));
                return;
            }

            string inner = text[(i + 2)..end].Trim();
            if (inner.Length == 0)
            {
                issues.Add(new ActionIssue(issuePath, label, "", input.Key,
                    "模板里的变量名为空：${}", IssueSeverity.error));
            }
            i = end;
        }
    }

    /// <summary>取出文本里所有 <c>${路径}</c> 的根变量名（忽略 <c>$$</c> 转义）。</summary>
    private static IEnumerable<string> TemplateVariables(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '$' || i + 1 >= text.Length) continue;
            if (text[i + 1] == '$') { i++; continue; }
            if (text[i + 1] != '{') continue;

            int end = text.IndexOf('}', i + 2);
            if (end < 0) yield break;

            string name = RootName(text[(i + 2)..end]);
            if (name.Length > 0) yield return name;
            i = end;
        }
    }

    private static void ValidateCondition(Step step, string path, string label,
        HashSet<string> produced, List<ActionIssue> issues)
    {
        if (!step.Unit.Equals("flow.if", StringComparison.OrdinalIgnoreCase) &&
            !step.Unit.Equals("flow.while", StringComparison.OrdinalIgnoreCase))
            return;

        string op = ProgramWalker.LiteralText(step, "op")?.Trim() ?? "";
        if (op.Length == 0)
        {
            issues.Add(new ActionIssue($"{path}/in/op", label, step.Unit, "op",
                "条件缺少比较方式", IssueSeverity.error));
        }
        else if (!FlowUnits.Compare.Ops.Contains(op, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new ActionIssue($"{path}/in/op", label, step.Unit, "op",
                $"未知比较方式：{op}（可用：{string.Join(" / ", FlowUnits.Compare.Ops)}）", IssueSeverity.error));
        }
        else if (op.Equals("regex", StringComparison.OrdinalIgnoreCase))
        {
            string pattern = ProgramWalker.LiteralText(step, "right") ?? "";
            if (pattern.Length > 0 && !pattern.Contains("${"))
            {
                try { _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                catch (ArgumentException ex)
                {
                    issues.Add(new ActionIssue($"{path}/in/right", label, step.Unit, "right",
                        $"正则表达式无效：{ex.Message}", IssueSeverity.error));
                }
            }
        }

        // 左值写成了变量名本身（"found" 而不是 ${found}）：运行时会把它当一段文本，
        // 条件于是恒定成立 / 恒定不成立——用户报过「怎么判断都进 then」。
        string left = ProgramWalker.LiteralText(step, "left")?.Trim() ?? "";
        if (left.Length > 0 && !left.Contains("${") && produced.Contains(RootName(left))
            && string.Equals(left, RootName(left), StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ActionIssue($"{path}/in/left", label, step.Unit, "left",
                $"左值写成了文本「{left}」，变量表里有同名变量——要判断这个变量请写成 ${{{left}}}，" +
                "或把值的来源切到「变量」（现在只会拿这段文本去比，结果不会随变量变化）",
                IssueSeverity.warning));
        }
    }

    private static void ValidateReference(Step step, string path, string label,
        Func<string, SubActionDef?>? resolveSubAction, List<ActionIssue> issues)
    {
        if (step.Unit.Equals("sub.run", StringComparison.OrdinalIgnoreCase))
        {
            string? id = ProgramWalker.LiteralText(step, "subAction")?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                if (step.In.ContainsKey("subAction"))
                    issues.Add(new ActionIssue($"{path}/in/subAction", label, step.Unit, "subAction",
                        "子动作 id 不是字面量，静态校验不了（运行时才能确定）", IssueSeverity.warning));
                return;
            }

            var def = ResolveSubAction(id, resolveSubAction);
            if (def == null)
            {
                issues.Add(new ActionIssue($"{path}/in/subAction", label, step.Unit, "subAction",
                    $"找不到子动作：{id}", IssueSeverity.error));
                return;
            }

            ValidateSubActionCall(def, step, path, label, resolveSubAction, issues);
        }

        if (step.Unit.Equals("input.macro", StringComparison.OrdinalIgnoreCase))
        {
            string? id = ProgramWalker.LiteralText(step, "macroId")?.Trim();
            if (string.IsNullOrEmpty(id))
            {
                if (step.In.ContainsKey("macroId"))
                    issues.Add(new ActionIssue($"{path}/in/macroId", label, step.Unit, "macroId",
                        "宏 id 不是字面量，静态校验不了", IssueSeverity.warning));
                return;
            }

            if (MacroStore.Load(id) == null)
                issues.Add(new ActionIssue($"{path}/in/macroId", label, step.Unit, "macroId",
                    $"宏不存在：{id}", IssueSeverity.error));
        }
    }

    /// <summary>
    /// 调用一个存在的子动作：参数名、没传值的输入、输出名、调用成环。
    /// 这些在运行时会直接失败（或撞上递归上限），能静态看出来就别等跑起来。
    /// </summary>
    private static void ValidateSubActionCall(SubActionDef def, Step step, string path, string label,
        Func<string, SubActionDef?>? resolveSubAction, List<ActionIssue> issues)
    {
        string name = string.IsNullOrWhiteSpace(def.Name) ? def.Id : def.Name;
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in def.Variables)
        {
            string key = (variable.Key ?? "").Trim();
            if (key.Length > 0) declared.Add(key);
        }

        // 运行时 <see cref="ActionEngine.RunSubAction"/> 对未知参数直接抛错。
        foreach (string key in step.In.Keys)
        {
            if (string.Equals(key, "subAction", StringComparison.OrdinalIgnoreCase)) continue;
            if (declared.Contains(key)) continue;

            issues.Add(new ActionIssue($"{path}/in/{key}", label, step.Unit, key,
                $"子动作「{name}」没有输入「{key}」（检查拼写，或子动作改过变量名）", IssueSeverity.error));
        }

        foreach (var variable in def.Variables)
        {
            if (variable.Direction != VariableDirection.input) continue;
            string key = (variable.Key ?? "").Trim();
            if (key.Length == 0 || step.In.ContainsKey(key)) continue;
            if (!IsEmptyValue(variable.Seed())) continue;

            issues.Add(new ActionIssue($"{path}/in/{key}", label, step.Unit, key,
                $"子动作「{name}」的输入「{key}」既没有传值，也没有默认值（运行时是空的）",
                IssueSeverity.warning));
        }

        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in def.Variables)
        {
            if (variable.Direction != VariableDirection.output) continue;
            string key = (variable.Key ?? "").Trim();
            if (key.Length > 0) outputs.Add(key);
        }

        foreach (string key in step.Out.Keys)
        {
            if (outputs.Contains(key)) continue;

            issues.Add(new ActionIssue($"{path}/out/{key}", label, step.Unit, key,
                $"子动作「{name}」没有输出「{key}」（检查拼写，或子动作改过变量名）", IssueSeverity.warning));
        }

        if (FindCallCycle(def.Id, resolveSubAction) is { Count: > 0 } chain)
        {
            issues.Add(new ActionIssue($"{path}/in/subAction", label, step.Unit, "subAction",
                $"子动作调用成环：{string.Join(" → ", chain)}（运行时会撞到层数上限）",
                IssueSeverity.error));
        }
    }

    /// <summary>从 <paramref name="start"/> 顺着 <c>sub.run</c> 找回到自己的路径；没成环返回 null。</summary>
    /// <param name="resolveSubAction">
    /// 按 id 取子动作定义；null = 只查 <see cref="SubActionStore"/>。写盘前的校验要传一个
    /// 「正在写的这一份优先」的解析器，否则还没落盘的自调用查不出来。
    /// </param>
    internal static List<string>? FindCallCycle(string start, Func<string, SubActionDef?>? resolveSubAction = null)
    {
        if (string.IsNullOrWhiteSpace(start)) return null;

        var chain = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<string>? Walk(string id)
        {
            if (chain.Count > 0 && string.Equals(id, start, StringComparison.OrdinalIgnoreCase))
            {
                var found = new List<string>(chain) { start };
                return found;
            }

            if (!seen.Add(id)) return null;
            var def = ResolveSubAction(id, resolveSubAction);
            if (def == null) return null;

            chain.Add(id);
            foreach (string next in CalledSubActions(def))
                if (Walk(next) is { } cycle) return cycle;

            chain.RemoveAt(chain.Count - 1);
            return null;
        }

        return Walk(start);
    }

    private static SubActionDef? ResolveSubAction(string id, Func<string, SubActionDef?>? resolveSubAction) =>
        resolveSubAction != null ? resolveSubAction(id) : SubActionStore.Load(id);

    private static IEnumerable<string> CalledSubActions(SubActionDef def)
    {
        var steps = new List<(Step Step, string Path, string Label, int Depth)>();
        ProgramWalker.CollectSteps(def.Steps, "/steps", "", 1, steps);

        foreach (var (step, _, _, _) in steps)
        {
            if (!step.Unit.Equals("sub.run", StringComparison.OrdinalIgnoreCase)) continue;
            string? id = ProgramWalker.LiteralText(step, "subAction")?.Trim();
            if (!string.IsNullOrEmpty(id)) yield return id;
        }
    }

    private static bool IsEmptyValue(Value value) => value.Kind switch
    {
        ValueKind.text => value.AsText().Length == 0,
        ValueKind.list => value.Items.Count == 0,
        ValueKind.@object => value.Members.Count == 0,
        _ => false,
    };

    private static void ValidateImage(string image, string issuePath, string label, string unit,
        string? resourceRoot, bool draftCheck, List<ActionIssue> issues)
    {
        try
        {
            if (Path.IsPathRooted(image))
            {
                issues.Add(new ActionIssue(issuePath, label, unit, "image",
                    File.Exists(image)
                        ? "引用了绝对路径图片（换机后可能失效）"
                        : $"图片不存在：{image}",
                    IssueSeverity.warning));
                return;
            }

            // files/x.png（也接受直接写 x.png）
            string relative = image.Replace('\\', '/');
            if (relative.StartsWith("files/", StringComparison.OrdinalIgnoreCase))
                relative = relative[6..];

            if (!string.IsNullOrWhiteSpace(resourceRoot))
            {
                string full = Path.Combine(resourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full)) return;

                if (draftCheck)
                {
                    issues.Add(new ActionIssue(issuePath, label, unit, "image",
                        $"草稿里找不到图片：{image}（应写在 files/ 下）", IssueSeverity.error));
                    return;
                }

                // 编辑器 / 正式动作：动作目录里没有就按常规顺序找（共享模板、用户素材）。
                if (!File.Exists(ImageLibrary.Resolve(image, resourceRoot)))
                    issues.Add(new ActionIssue(issuePath, label, unit, "image",
                        $"找不到图片：{image}", IssueSeverity.warning));
                return;
            }

            // 编辑器校验：既可能是已发布的模板图，也可能是用户素材。
            if (image.StartsWith("shot-", StringComparison.OrdinalIgnoreCase) ||
                image.StartsWith("resources/", StringComparison.OrdinalIgnoreCase) ||
                image.StartsWith("resources\\", StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(ImageLibrary.Resolve(image)))
                    issues.Add(new ActionIssue(issuePath, label, unit, "image",
                        $"图片不存在：{image}", IssueSeverity.warning));
                return;
            }

            if (!File.Exists(ImageLibrary.Resolve(image)))
                issues.Add(new ActionIssue(issuePath, label, unit, "image",
                    $"找不到图片：{image}", IssueSeverity.warning));
        }
        catch (Exception ex)
        {
            issues.Add(new ActionIssue(issuePath, label, unit, "image",
                $"图片路径无法解析：{ex.Message}", IssueSeverity.warning));
        }
    }

    // ============ 工具 ============

    /// <summary>变量路径 / 模板路径的根名字（<c>a.b[0]</c> → <c>a</c>）。</summary>
    private static string RootName(string? path)
    {
        string value = (path ?? "").Trim();
        int cut = value.IndexOfAny(new[] { '.', '[' });
        return (cut < 0 ? value : value[..cut]).Trim();
    }
}
