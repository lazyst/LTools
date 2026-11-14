using LTools.Core.Actions;

namespace LTools.Core.SelfTest;

/// <summary>
/// 引擎语义：预算、嵌套、失败策略、子动作、干跑、轨迹。
/// 用假单元跑，不注入键鼠、不读写磁盘。
/// </summary>
internal static class EngineSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("engine", "字面量与输出映射", LiteralAndOutput));
        cases.Add(new("engine", "模板渲染变量与列表下标", TemplateRender));
        cases.Add(new("engine", "整串变量保留列表类型", WholeVariableKeepsType));
        cases.Add(new("engine", "如果否则分支", IfElse));
        cases.Add(new("engine", "重复指定次数", Repeat));
        cases.Add(new("engine", "遍历列表项与下标", ForEach));
        cases.Add(new("engine", "条件循环", While));
        cases.Add(new("engine", "跳出与继续下一次", BreakContinue));
        cases.Add(new("engine", "顶层跳出循环报错", BreakAtTop));
        cases.Add(new("engine", "跳过的步骤不执行", DisabledStep));
        cases.Add(new("engine", "失败继续与上一步错误", FailContinue));
        cases.Add(new("engine", "失败中止带位置", FailLabel));
        cases.Add(new("engine", "嵌套失败不重复前缀", NestedLabel));
        cases.Add(new("engine", "步数预算", StepBudget));
        cases.Add(new("engine", "循环轮次预算", LoopBudget));
        cases.Add(new("engine", "子动作调用与作用域隔离", SubAction));
        cases.Add(new("engine", "子动作递归深度上限", SubActionDepth));
        cases.Add(new("engine", "从指定步骤开始", StartPath));
        cases.Add(new("engine", "干跑跳过动作单元", DryRun));
        cases.Add(new("engine", "初始变量按声明类型注入", InitialVariables));
        cases.Add(new("engine", "轨迹路径与解析后的输入", Trace));
        cases.Add(new("engine", "停止信号打断步骤延迟", StopCancelsDelay));
        cases.Add(new("engine", "流程停止正常收尾", FlowStop));
        cases.Add(new("engine", "返回只能用在子动作里", ReturnAtTop));
        cases.Add(new("engine", "未知动作单元报错", UnknownUnit));
        cases.Add(new("engine", "慢步骤报警一次", SlowStepReported));
        cases.Add(new("engine", "同一行慢步骤只报一次", SlowStepOncePerPath));
        cases.Add(new("engine", "快步骤不报警", SlowStepFast));
        cases.Add(new("engine", "关掉看门狗不报警", SlowStepOff));
    }

    // ============ 用例 ============

    private static void LiteralAndOutput()
    {
        ActionRunResult result = Run(Program(Echo("你好").Output("text", "v")));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal("你好", result.Variables["v"].AsText(), "输出应写入变量");
        Check.Equal(1, result.Steps, "执行步数");
    }

    private static void TemplateRender()
    {
        VariableDef[] variables =
        {
            Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a"), Value.FromText("b") })),
            Var("name", ValueKind.text, Value.FromText("阿呆")),
        };

        ActionRunResult result = Run(Program(variables,
            Echo("${name}-${items[1]}").Output("text", "v")));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal("阿呆-b", result.Variables["v"].AsText(), "模板里的变量与下标都应展开");
    }

    private static void WholeVariableKeepsType()
    {
        VariableDef[] variables =
        {
            Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a"), Value.FromText("b") })),
        };

        ActionRunResult result = Run(Program(variables,
            Step.Of("test.echo").WithText("text", "${items}").Output("value", "kind")));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal(ValueKind.list, result.Variables["kind"].Kind, "整串 ${items} 应按引用求值、保留列表类型");
    }

    private static void IfElse()
    {
        Step hit = Step.Of("flow.if")
            .With("left", StepArg.FromText("abc"))
            .WithText("op", "equals")
            .With("right", StepArg.FromText("abc"));
        hit.Then.Add(Write("flag", "then"));
        hit.Else.Add(Write("flag", "else"));

        ActionRunResult yes = Run(Program(hit));
        Check.True(yes.Ok, "条件成立应成功：" + yes.Error);
        Check.Equal("then", yes.Variables["flag"].AsText(), "条件成立应走满足时分支");

        Step miss = Step.Of("flow.if")
            .With("left", StepArg.FromText("abc"))
            .WithText("op", "equals")
            .With("right", StepArg.FromText("abd"));
        miss.Then.Add(Write("flag", "then"));
        miss.Else.Add(Write("flag", "else"));

        ActionRunResult no = Run(Program(miss));
        Check.True(no.Ok, "条件不成立应成功：" + no.Error);
        Check.Equal("else", no.Variables["flag"].AsText(), "条件不成立应走否则分支");
    }

    private static void Repeat()
    {
        FakeUnit mark = TestRegistry.Mark();
        Step loop = Step.Of("flow.repeat").With("count", StepArg.FromNumber(3));
        loop.Body.Add(Step.Of("test.mark"));

        ActionRunResult result = Run(Program(loop), TestRegistry.Create(mark));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal(3, mark.Calls, "重复 3 次应执行 3 轮");
    }

    private static void ForEach()
    {
        VariableDef[] variables =
        {
            Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a"), Value.FromText("b") })),
        };

        Step loop = Step.Of("flow.forEach")
            .WithVar("items", "items")
            .Output("item", "cur")
            .Output("index", "idx");
        loop.Body.Add(Step.Of("test.echo").WithVar("text", "cur").Output("text", "last"));

        ActionRunResult result = Run(Program(variables, loop));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal("b", result.Variables["last"].AsText(), "循环结束后应保留最后一项");
        Check.Equal(1d, result.Variables["idx"].AsNumber(), "下标应是最后一轮的下标");
    }

    private static void While()
    {
        VariableDef[] variables = { Var("go", ValueKind.@bool, Value.FromBool(true)) };
        FakeUnit mark = TestRegistry.Mark();

        Step loop = Step.Of("flow.while")
            .WithVar("left", "go")
            .WithText("op", "isTrue");
        loop.Body.Add(Step.Of("test.mark"));
        loop.Body.Add(Write("go", "false"));

        ActionRunResult result = Run(Program(variables, loop), TestRegistry.Create(mark));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal(1, mark.Calls, "条件在循环体里被改成假之后应只跑一轮");
    }

    private static void BreakContinue()
    {
        VariableDef[] variables = { Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a"), Value.FromText("b") })) };

        FakeUnit broken = TestRegistry.Mark("test.mark", "计数测试");
        Step breakLoop = Step.Of("flow.forEach").WithVar("items", "items");
        breakLoop.Body.Add(Step.Of("test.mark"));
        breakLoop.Body.Add(Step.Of("flow.break"));

        ActionRunResult stopped = Run(Program(variables, breakLoop), TestRegistry.Create(broken));
        Check.True(stopped.Ok, "跳出循环应正常结束：" + stopped.Error);
        Check.Equal(1, broken.Calls, "跳出循环前应先执行一轮");

        FakeUnit skipped = TestRegistry.Mark("test.mark", "计数测试");
        Step continueLoop = Step.Of("flow.forEach").WithVar("items", "items");
        continueLoop.Body.Add(Step.Of("flow.continue"));
        continueLoop.Body.Add(Step.Of("test.mark"));

        ActionRunResult continued = Run(Program(variables, continueLoop), TestRegistry.Create(skipped));
        Check.True(continued.Ok, "继续下一次应正常结束：" + continued.Error);
        Check.Equal(0, skipped.Calls, "「继续下一次」之后的步骤不该执行");
    }

    private static void BreakAtTop()
    {
        ActionRunResult result = Run(Program(Step.Of("flow.break")));

        Check.False(result.Ok, "顶层跳出循环应失败");
        Check.Contains(result.Error, "只能出现在循环体里");
    }

    private static void DisabledStep()
    {
        FakeUnit mark = TestRegistry.Mark();
        Step step = Step.Of("test.mark");
        step.Disabled = true;

        ActionRunResult result = Run(Program(step), TestRegistry.Create(mark));

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal(0, mark.Calls, "跳过的步骤不该执行");
        Check.Equal(0, result.Steps, "跳过的步骤不该计入步数");
    }

    private static void FailContinue()
    {
        Step fail = Step.Of("test.fail").WithText("message", "boom");
        fail.OnFail = FailMode.@continue;

        ActionRunResult result = Run(Program(fail, Step.Of("flow.lastError").Output("text", "err")));

        Check.True(result.Ok, "失败继续应让整个动作成功：" + result.Error);
        Check.Equal("boom", result.Variables["err"].AsText(), "上一步的错误应能读到");
    }

    private static void FailLabel()
    {
        ActionRunResult result = Run(Program(
            Echo("x").Output("text", "a"),
            Step.Of("test.fail").WithText("message", "boom")));

        Check.False(result.Ok, "失败应中止");
        Check.Equal("第 2 行", result.FailedStepLabel, "失败位置应是第 2 行");
        Check.Contains(result.Error, "第 2 行「失败测试」失败：boom");
    }

    private static void NestedLabel()
    {
        VariableDef[] variables = { Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a") })) };
        Step loop = Step.Of("flow.forEach").WithVar("items", "items");
        loop.Body.Add(Step.Of("test.fail").WithText("message", "boom"));

        ActionRunResult result = Run(Program(variables, loop));

        Check.False(result.Ok, "失败应中止");
        Check.Contains(result.Error, "循环体 › 第 1 行");
        Check.Equal(2, result.Error!.Split("失败：").Length, "失败前缀只能出现一次（内层已经带好位置）");
    }

    private static void StepBudget()
    {
        var options = new ActionRunOptions { MaxSteps = 2 };
        ActionRunResult result = Run(Program(Write("a", "1"), Write("b", "2"), Write("c", "3")), null, options);

        Check.False(result.Ok, "超过步数预算应失败");
        Check.True(result.Stopped, "预算用尽按「已停止」收场");
        Check.Contains(result.Error, "步数超过上限（2 步）");
    }

    private static void LoopBudget()
    {
        VariableDef[] variables =
        {
            Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a"), Value.FromText("b"), Value.FromText("c") })),
        };
        Step loop = Step.Of("flow.forEach").WithVar("items", "items");
        loop.Body.Add(Write("seen", "x"));

        var options = new ActionRunOptions { MaxLoopIterations = 2 };
        ActionRunResult result = Run(Program(variables, loop), null, options);

        Check.False(result.Ok, "超过循环轮次预算应失败");
        Check.Contains(result.Error, "循环已执行 2 轮");
    }

    private static void SubAction()
    {
        var sub = new SubActionDef { Id = "sub1", Name = "打招呼" };
        sub.Variables.Add(new VariableDef
        {
            Key = "who", Type = ValueKind.text, Direction = VariableDirection.input,
            Default = Value.FromText("世界"),
        });
        sub.Variables.Add(new VariableDef
        {
            Key = "msg", Type = ValueKind.text, Direction = VariableDirection.output,
        });
        sub.Steps.Add(Step.Of("test.echo").WithText("text", "你好 ${who}").Output("text", "msg"));
        sub.Steps.Add(Write("private", "only-inside"));

        Step call = Step.Of("sub.run")
            .WithText("subAction", "sub1")
            .WithText("who", "阿呆")
            .Output("msg", "out");

        var options = new ActionRunOptions
        {
            SubActionResolver = id => id == "sub1" ? sub : null,
        };
        ActionRunResult result = Run(Program(call), null, options);

        Check.True(result.Ok, "子动作应成功：" + result.Error);
        Check.Equal("你好 阿呆", result.Variables["out"].AsText(), "输入按名字传入、输出按 out 映射写回");
        Check.False(result.Variables.ContainsKey("private"), "子动作的内部变量不该漏到调用方作用域");
    }

    private static void SubActionDepth()
    {
        var recursive = new SubActionDef { Id = "loop", Name = "递归" };
        recursive.Steps.Add(Step.Of("sub.run").WithText("subAction", "loop"));

        var options = new ActionRunOptions
        {
            SubActionResolver = id => id == "loop" ? recursive : null,
        };
        ActionRunResult result = Run(Program(Step.Of("sub.run").WithText("subAction", "loop")), null, options);

        Check.False(result.Ok, "无限递归应失败");
        Check.Contains(result.Error, "子动作调用层数超过上限（8）");
    }

    private static void StartPath()
    {
        var options = new ActionRunOptions { StartPath = "/steps/1" };
        ActionRunResult result = Run(Program(Write("a", "1"), Write("b", "2")), null, options);

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.False(result.Variables.ContainsKey("a"), "起点之前的步骤应被跳过");
        Check.Equal("2", result.Variables["b"].AsText(), "起点那一步应执行");

        var bad = new ActionRunOptions { StartPath = "/steps/9" };
        ActionRunResult missing = Run(Program(Write("a", "1")), null, bad);
        Check.Contains(missing.Error, "找不到起点步骤");
    }

    private static void DryRun()
    {
        FakeUnit mark = TestRegistry.Mark();
        FakeUnit pure = TestRegistry.Pure();
        var traces = new List<StepTrace>();
        var options = new ActionRunOptions { DryRun = true, StepTrace = traces.Add };

        ActionRunResult result = Run(
            Program(Step.Of("test.mark"), Step.Of("test.pure").Output("text", "got")),
            TestRegistry.Create(mark, pure),
            options);

        Check.True(result.Ok, "干跑应成功：" + result.Error);
        Check.Equal(0, mark.Calls, "干跑不该执行普通动作单元");
        Check.Equal(1, pure.Calls, "干跑应执行取值动作单元");
        Check.Equal("pure", result.Variables["got"].AsText(), "取值动作单元的输出照常写回");
        Check.Equal(1, traces.Count(t => t.Status == StepTraceStatus.skipped), "被跳过的步骤记一条 skipped 轨迹");
    }

    private static void InitialVariables()
    {
        VariableDef[] variables = { Var("x", ValueKind.number) };
        var options = new ActionRunOptions
        {
            InitialVariables = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
            {
                ["x"] = Value.FromText("42"),
                ["undeclared"] = Value.FromText("忽略"),
            },
        };

        ActionRunResult result = Run(Program(variables, Write("done", "1")), null, options);

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal(ValueKind.number, result.Variables["x"].Kind, "初始变量应按声明类型转换");
        Check.Equal(42d, result.Variables["x"].AsNumber(), "初始变量值");
        Check.False(result.Variables.ContainsKey("undeclared"), "未声明的初始变量应被忽略");
    }

    private static void Trace()
    {
        VariableDef[] variables = { Var("items", ValueKind.list, Value.FromList(new[] { Value.FromText("a") })) };
        var traces = new List<StepTrace>();
        var options = new ActionRunOptions { StepTrace = traces.Add };

        Step loop = Step.Of("flow.forEach").WithVar("items", "items").Output("item", "cur");
        loop.Body.Add(Step.Of("test.echo").WithVar("text", "cur").Output("text", "last"));

        ActionRunResult result = Run(Program(variables, loop), TestRegistry.Create(), options);

        Check.True(result.Ok, "结果应成功：" + result.Error);
        Check.Equal(2, traces.Count, "一步循环体应各记一条轨迹");

        // 轨迹在步骤**跑完之后**回调（后序）：循环体先到，外层「遍历列表」后到。
        StepTrace body = traces[0];
        StepTrace owner = traces[1];

        Check.Equal("/steps/0/body/0", body.Path, "循环体步骤路径");
        Check.Equal("/steps/0", owner.Path, "顶层步骤路径");
        Check.Contains(body.Label, "循环体");
        Check.True(body.Inputs.ContainsKey("text"), "轨迹应带解析后的参数");
        Check.Equal("a", body.Inputs["text"].AsText(), "参数应是变量展开后的值");
        Check.True(body.Outputs.ContainsKey("text"), "轨迹应带这一步写出的输出键（`out` 的键，不是变量名）");
        Check.Equal("a", body.Outputs["text"].AsText(), "输出键对应运行时写出的值");
    }

    private static void StopCancelsDelay()
    {
        using var cts = new CancellationTokenSource();
        var cancel = new FakeUnit("test.cancel", "取消测试", run: (_, _) => cts.Cancel());
        Step step = Step.Of("test.cancel");
        step.DelayMs = 5_000;

        ActionRunResult result = Run(Program(step), TestRegistry.Create(cancel),
            new ActionRunOptions { Token = cts.Token });

        Check.True(result.Stopped, "token 取消后应记为已停止");
        Check.Contains(result.Error, "已停止");
    }

    private static void FlowStop()
    {
        FakeUnit mark = TestRegistry.Mark();
        ActionRunResult result = Run(Program(Step.Of("flow.stop"), Step.Of("test.mark")), TestRegistry.Create(mark));

        Check.True(result.Stopped, "流程停止应记为已停止");
        Check.False(result.Ok, "已停止不算成功");
        Check.Equal(0, mark.Calls, "停止之后的步骤不该执行");
    }

    private static void ReturnAtTop()
    {
        ActionRunResult result = Run(Program(Step.Of("flow.return")));

        Check.False(result.Ok, "动作里用返回应失败");
        Check.Contains(result.Error, "只能出现在子动作里");
    }

    private static void UnknownUnit()
    {
        ActionRunResult result = Run(Program(Step.Of("nope.nothing")));

        Check.False(result.Ok, "未知动作单元应失败");
        Check.Contains(result.Error, "未知的动作单元");
    }

    // ============ 慢步骤看门狗（P1） ============

    private static void SlowStepReported()
    {
        var reports = new List<SlowStepInfo>();
        var options = new ActionRunOptions
        {
            SlowStepWarn = TimeSpan.FromMilliseconds(250),
            OnSlowStep = info => { lock (reports) reports.Add(info); },
        };

        Step step = Step.Of("test.slow").With("ms", StepArg.FromNumber(1500));
        ActionRunResult result = Run(Program(step), null, options);

        Check.True(result.Ok, "慢步骤本身应正常跑完：" + result.Error);
        Check.Equal(1, reports.Count, "1.5 秒的步骤应恰好报一次");
        if (reports.Count == 0) return;

        Check.Equal("/steps/0", reports[0].Path, "步骤路径");
        Check.Contains(reports[0].Label, "第 1 行");
        Check.Equal("test.slow", reports[0].UnitKey, "动作单元键");
        Check.True(reports[0].Elapsed >= TimeSpan.FromSeconds(1), "报告时的耗时应该已经超过 1 秒");
    }

    private static void SlowStepOncePerPath()
    {
        var reports = new List<SlowStepInfo>();
        var options = new ActionRunOptions
        {
            SlowStepWarn = TimeSpan.FromMilliseconds(250),
            OnSlowStep = info => { lock (reports) reports.Add(info); },
        };

        Step loop = Step.Of("flow.repeat").With("count", StepArg.FromNumber(3));
        loop.Body.Add(Step.Of("test.slow").With("ms", StepArg.FromNumber(700)));

        ActionRunResult result = Run(Program(loop), null, options);

        Check.True(result.Ok, "循环应正常跑完：" + result.Error);
        Check.Equal(1, reports.Count, "同一行循环多轮只报一次（按步骤路径去重）");
        if (reports.Count > 0)
            Check.Equal("/steps/0/body/0", reports[0].Path, "报的是循环体里那一行");
    }

    private static void SlowStepFast()
    {
        var reports = new List<SlowStepInfo>();
        var options = new ActionRunOptions
        {
            SlowStepWarn = TimeSpan.FromMilliseconds(200),
            OnSlowStep = info => { lock (reports) reports.Add(info); },
        };

        Step step = Step.Of("test.slow").With("ms", StepArg.FromNumber(20));
        ActionRunResult result = Run(Program(step), null, options);

        Check.True(result.Ok, "快步骤应成功：" + result.Error);
        Check.Equal(0, reports.Count, "20 毫秒的步骤不该报慢");
    }

    private static void SlowStepOff()
    {
        var reports = new List<SlowStepInfo>();
        var options = new ActionRunOptions
        {
            SlowStepWarn = TimeSpan.Zero,   // 0 = 关掉看门狗
            OnSlowStep = info => { lock (reports) reports.Add(info); },
        };

        Step step = Step.Of("test.slow").With("ms", StepArg.FromNumber(1200));
        ActionRunResult result = Run(Program(step), null, options);

        Check.True(result.Ok, "关掉看门狗后慢步骤照常跑完：" + result.Error);
        Check.Equal(0, reports.Count, "SlowStepWarn = 0 时不该有任何回调");
    }

    // ============ 工具 ============

    private static ActionRunResult Run(ActionProgram program, StepRegistry? registry = null, ActionRunOptions? options = null) =>
        new ActionEngine(registry ?? TestRegistry.Create()).Run(program, options);

    private static ActionProgram Program(params Step[] steps) => Program(null, steps);

    private static ActionProgram Program(VariableDef[]? variables, params Step[] steps)
    {
        var program = new ActionProgram { Name = "测试动作" };
        if (variables != null) program.Variables.AddRange(variables);
        program.Steps.AddRange(steps);
        return program;
    }

    private static VariableDef Var(string key, ValueKind kind, Value? value = null) =>
        new() { Key = key, Type = kind, Default = value };

    private static Step Echo(string text) => Step.Of("test.echo").WithText("text", text);

    private static Step Write(string name, string value) =>
        Step.Of("test.scope").WithText("name", name).WithText("value", value);
}
