using System.Text.RegularExpressions;

namespace LTools.Core.Actions;

/// <summary>
/// 内置流程单元。控制流不是节点图：<c>if</c> / 循环本身就是普通步骤，
/// 分支体放在步骤的 <c>then</c> / <c>else</c> / <c>body</c> 里。
/// </summary>
internal static class FlowUnits
{
    public static void RegisterAll(StepRegistry registry)
    {
        registry.Register(new SetUnit());
        registry.Register(new IfUnit());
        registry.Register(new RepeatUnit());
        registry.Register(new ForEachUnit());
        registry.Register(new WhileUnit());
        registry.Register(new BreakUnit());
        registry.Register(new ContinueUnit());
        registry.Register(new StopUnit());
        registry.Register(new DelayUnit());
        registry.Register(new SubRunUnit());
    }

    /// <summary>条件比较（<c>flow.if</c> / <c>flow.while</c> 共用）。</summary>
    internal static class Compare
    {
        /// <summary>需要「右值」的比较方式（其余是一元判断，右值不显示）。</summary>
        public static readonly string[] BinaryOps =
        {
            "equals", "notEquals", "contains", "notContains", "startsWith", "endsWith",
            "regex", "greater", "less", "greaterOrEqual", "lessOrEqual",
        };

        public static readonly string[] Ops =
        {
            "equals", "notEquals", "contains", "notContains", "startsWith", "endsWith",
            "regex", "isEmpty", "isNotEmpty", "greater", "less", "greaterOrEqual",
            "lessOrEqual", "isTrue", "isFalse", "found", "notFound", "active", "notActive",
        };

        public static bool Test(Value left, string op, Value right)
        {
            op = (op ?? "").Trim();
            string text = left.AsText();
            string operand = right.AsText();

            return op switch
            {
                "equals" => left.LooseEquals(right),
                "notEquals" => !left.LooseEquals(right),
                "contains" => text.Contains(operand, StringComparison.OrdinalIgnoreCase),
                "notContains" => !text.Contains(operand, StringComparison.OrdinalIgnoreCase),
                "startsWith" => text.StartsWith(operand, StringComparison.OrdinalIgnoreCase),
                "endsWith" => text.EndsWith(operand, StringComparison.OrdinalIgnoreCase),
                "regex" => RegexMatch(text, operand),
                "isEmpty" => IsEmpty(left),
                "isNotEmpty" => !IsEmpty(left),
                "greater" => left.AsNumber() > right.AsNumber(),
                "less" => left.AsNumber() < right.AsNumber(),
                "greaterOrEqual" => left.AsNumber() >= right.AsNumber(),
                "lessOrEqual" => left.AsNumber() <= right.AsNumber(),
                "isTrue" => Truthy(left),
                "isFalse" => !Truthy(left),
                "found" => MemberTruthy(left, "found"),
                "notFound" => !MemberTruthy(left, "found"),
                "active" => MemberTruthy(left, "active"),
                "notActive" => !MemberTruthy(left, "active"),
                _ => throw new ActionException($"未知比较方式：{op}"),
            };
        }

        /// <summary>
        /// 布尔真值：布尔按自身、数字非 0、列表 / 对象非空、对象先看 <c>found</c> / <c>flag</c>。
        /// </summary>
        /// <remarks>
        /// 文本只认 true/false/1/0/yes/no/on/off，别的文本一律报错。
        /// 以前这里退回「文本非空即真」，于是把变量名当文本写进左值（<c>"found"</c> 而不是
        /// <c>${found}</c>）就永远成立，看起来像「如果永远进 then」（用户报过）。
        /// 宁可报一句能照着改的错，也不要悄悄判成 true。
        /// </remarks>
        private static bool Truthy(Value value)
        {
            switch (value.Kind)
            {
                case ValueKind.list:
                    return value.Items.Count > 0;

                case ValueKind.@object:
                    if (value.Members.TryGetValue("found", out var found)) return Truthy(found);
                    if (value.Members.TryGetValue("flag", out var flag)) return Truthy(flag);
                    return value.Members.Count > 0;

                case ValueKind.text:
                {
                    string text = value.AsText().Trim();
                    if (text.Length == 0) return false;
                    try
                    {
                        return value.AsBool();
                    }
                    catch (ActionException)
                    {
                        throw new ActionException(
                            $"左值是文本「{text}」，不是布尔值。要判断变量，请把左值切到「变量」或写成 ${{{text}}}；" +
                            "要判断文本非空，请把比较方式改成「不为空」。");
                    }
                }

                default:
                    return value.AsBool();
            }
        }

        /// <summary>
        /// 取对象成员的真值（条件里的 found / active）；不是对象时退回布尔真值
        /// （文本要写成 true / 1 / no 这类，存了布尔值的对象也能判断）。
        /// </summary>
        private static bool MemberTruthy(Value value, string name)
        {
            if (value.Kind == ValueKind.@object && value.Members.TryGetValue(name, out var member))
                return Truthy(member);
            return Truthy(value);
        }

        private static bool IsEmpty(Value value) => value.Kind switch
        {
            ValueKind.text => value.AsText().Length == 0,
            ValueKind.list => value.Items.Count == 0,
            ValueKind.@object => value.Members.Count == 0,
            _ => value.AsText().Length == 0,
        };

        private static bool RegexMatch(string text, string pattern)
        {
            try
            {
                return Regex.IsMatch(text, pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new ActionException($"正则表达式无效：{pattern}", ex);
            }
        }
    }

    private static InputSpec ConditionLeft() =>
        new("left", "左值", Editor: InputEditor.condition, Required: true,
            Description: "要判断的值。判断变量就点右侧「变量」按钮插入 ${变量名}，或把来源切到「变量」（直接写变量名会被当成文本）");

    private static InputSpec ConditionOp() =>
        new("op", "比较", ValueKind.text, InputEditor.enumList, Required: true,
            Default: Value.FromText("equals"), Options: Compare.Ops);

    private static InputSpec ConditionRight() =>
        new("right", "右值", Editor: InputEditor.condition,
            VisibleWhen: RightVisibleWhen(),
            Description: "拿左值跟它比。同样可以点「变量」按钮 / 切到「变量」来比较另一个变量");

    private static InputSpec ConditionNegate() =>
        new("negate", "对结果取反", ValueKind.@bool, InputEditor.boolean,
            Default: Value.FromBool(false),
            Description: "勾上就把比较结果反过来：成立变不成立、不成立变成立");

    /// <summary>右值只在二元比较时显示（为真 / 为空 / 找到 这类一元判断不需要右值）。</summary>
    private static string RightVisibleWhen() =>
        string.Join(" || ", Compare.BinaryOps.Select(op => $"op == '{op}'"));

    /// <summary>赋值 / 追加：<c>flow.set</c>。</summary>
    private sealed class SetUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.set",
            Name = "设置变量",
            Category = "流程",
            Icon = "set",
            Description = "把值写入变量；可选择追加到旧值后面",
            Inputs = new[]
            {
                new InputSpec("name", "变量", ValueKind.text, InputEditor.variable, Required: true),
                new InputSpec("value", "值", Editor: InputEditor.condition),
                new InputSpec("append", "追加", ValueKind.@bool, InputEditor.boolean,
                    Default: Value.FromBool(false)),
            },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            string name = args.Text("name").Trim();
            if (name.Length == 0) throw new ActionException("设置变量的目标变量名为空");

            Value value = args.Get("value");
            if (args.Bool("append"))
            {
                string previous = ctx.Scope.TryGet(name, out var old) ? old.AsText() : "";
                value = Value.FromText(previous + value.AsText());
            }

            ctx.Scope.Set(name, value);
        }
    }

    /// <summary>条件分支：<c>flow.if</c>。</summary>
    private sealed class IfUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.if",
            Name = "如果",
            Category = "流程",
            Icon = "if",
            Description = "条件成立走 then，否则走 else",
            Body = BodyKind.thenElse,
            Inputs = new[] { ConditionLeft(), ConditionOp(), ConditionRight(), ConditionNegate() },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            bool ok = Compare.Test(args.Get("left"), args.Text("op"), args.Get("right"));
            if (args.Bool("negate")) ok = !ok;
            ctx.RunBody(ok ? ctx.Step.Then : ctx.Step.Else);
        }
    }

    /// <summary>固定次数循环：<c>flow.repeat</c>，下标从 0 开始。</summary>
    private sealed class RepeatUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.repeat",
            Name = "重复",
            Category = "流程",
            Icon = "repeat",
            Description = "重复执行循环体指定次数（下标从 0 开始）",
            Body = BodyKind.body,
            Inputs = new[]
            {
                new InputSpec("count", "次数", ValueKind.number, InputEditor.number, Required: true),
            },
            Outputs = new[]
            {
                new OutputSpec("index", "下标", ValueKind.number, "i", Description: "当前循环下标，从 0 开始；循环结束后保留最后一轮的值"),
            },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            double raw = args.Number("count");
            if (raw < 0 || raw > ctx.MaxLoopIterations)
                throw new ActionException(
                    $"循环次数必须在 0–{ctx.MaxLoopIterations} 之间（当前 {raw}）");

            int count = (int)Math.Round(raw);
            for (int i = 0; i < count; i++)
            {
                ctx.EnterIteration();
                ctx.SetOutput("index", Value.FromNumber(i));
                try { ctx.RunBody(ctx.Step.Body); }
                catch (ContinueSignal) { continue; }
                catch (BreakSignal) { break; }
            }
        }
    }

    /// <summary>遍历列表：<c>flow.forEach</c>。</summary>
    private sealed class ForEachUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.forEach",
            Name = "遍历列表",
            Category = "流程",
            Icon = "foreach",
            Description = "对列表每一项执行循环体",
            Body = BodyKind.body,
            Inputs = new[]
            {
                new InputSpec("items", "列表", ValueKind.list, InputEditor.variable, Required: true),
            },
            Outputs = new[]
            {
                new OutputSpec("item", "当前项", Description: "当前这一项"),
                new OutputSpec("index", "下标", ValueKind.number, "i", Description: "当前循环下标，从 0 开始"),
            },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            var items = args.List("items");
            for (int i = 0; i < items.Count; i++)
            {
                if (i >= ctx.MaxLoopIterations)
                    throw new ActionException(
                        $"循环已执行 {ctx.MaxLoopIterations} 轮，已自动停止（列表共 {items.Count} 项）");

                ctx.EnterIteration();
                ctx.SetOutput("item", items[i]);
                ctx.SetOutput("index", Value.FromNumber(i));
                try { ctx.RunBody(ctx.Step.Body); }
                catch (ContinueSignal) { continue; }
                catch (BreakSignal) { break; }
            }
        }
    }

    /// <summary>条件循环：<c>flow.while</c>。</summary>
    private sealed class WhileUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.while",
            Name = "条件循环",
            Category = "流程",
            Icon = "while",
            Description = "条件成立时重复执行循环体",
            Body = BodyKind.body,
            Inputs = new[] { ConditionLeft(), ConditionOp(), ConditionRight(), ConditionNegate() },
            Outputs = new[]
            {
                new OutputSpec("index", "下标", ValueKind.number, "i", Description: "当前循环下标，从 0 开始；循环结束后保留最后一轮的值"),
            },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            var current = args;
            for (int i = 0; ; i++)
            {
                if (i >= ctx.MaxLoopIterations)
                    throw new ActionException($"循环已执行 {ctx.MaxLoopIterations} 轮，已自动停止");

                ctx.EnterIteration();
                ctx.SetOutput("index", Value.FromNumber(i));

                bool ok = Compare.Test(current.Get("left"), current.Text("op"), current.Get("right"));
                if (current.Bool("negate")) ok = !ok;
                if (!ok) break;

                try { ctx.RunBody(ctx.Step.Body); }
                catch (ContinueSignal) { /* 进入下一轮 */ }
                catch (BreakSignal) { break; }

                // 条件要在每轮重新取值（变量可能已被循环体改掉）。
                current = ctx.ResolveArgs();
            }
        }
    }

    private sealed class BreakUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.break",
            Name = "跳出循环",
            Category = "流程",
            Icon = "break",
            Description = "结束当前循环",
        };

        public void Execute(StepContext ctx, StepArgs args) => throw new BreakSignal();
    }

    private sealed class ContinueUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.continue",
            Name = "继续下一次",
            Category = "流程",
            Icon = "continue",
            Description = "跳过本轮剩余步骤，进入下一轮",
        };

        public void Execute(StepContext ctx, StepArgs args) => throw new ContinueSignal();
    }

    private sealed class StopUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.stop",
            Name = "停止",
            Category = "流程",
            Icon = "stop",
            Description = "正常结束整个动作",
        };

        public void Execute(StepContext ctx, StepArgs args) => throw new StopSignal();
    }

    /// <summary>调用一个子动作（动作内 / 全局）：按输入变量名传值，输出按 out 映射写回。</summary>
    private sealed class SubRunUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "sub.run",
            Name = "调用子动作",
            Category = "子动作",
            Icon = "sub",
            Description = "调用动作内或全局子动作；参数名 = 子动作的输入变量名，输出按 out 映射写回",
            DynamicInputs = true,
            Inputs = new[]
            {
                new InputSpec("subAction", "子动作", ValueKind.text, InputEditor.text, Required: true),
            },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            string id = args.Text("subAction").Trim();
            if (id.Length == 0) throw new ActionException("调用子动作没有指定子动作 id");
            ctx.RunSubAction(id, args);
        }
    }

    private sealed class DelayUnit : IStepUnit
    {
        public UnitSpec Spec { get; } = new()
        {
            Key = "flow.delay",
            Name = "等待",
            Category = "流程",
            Icon = "clock",
            Description = "等待指定毫秒（可被停止热键打断）",
            Inputs = new[]
            {
                new InputSpec("ms", "毫秒", ValueKind.number, InputEditor.number, Required: true,
                    Default: Value.FromNumber(500)),
            },
        };

        public void Execute(StepContext ctx, StepArgs args)
        {
            int ms = (int)Math.Clamp(args.Number("ms"), 0, 600_000);
            if (!ctx.Wait(ms))
                throw new ActionAbortedException("已停止", userCancelled: true);
        }
    }
}
