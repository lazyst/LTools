using LTools.Core.Actions;

namespace LTools.Core.SelfTest;

/// <summary>跨动作单元共用的纯函数：比较、可见性、列表文本、转义、值转换、变量路径。</summary>
internal static class HelperSuite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("helpers", "比较方式矩阵", CompareMatrix));
        cases.Add(new("helpers", "可见性：选项门槛", VisibilityOptionGating));
        cases.Add(new("helpers", "可见性：附加条件与兜底", VisibilityFallbacks));
        cases.Add(new("helpers", "列表文本解析与显示", ListTextRules));
        cases.Add(new("helpers", "分隔符转义", TextEscapesRules));
        cases.Add(new("helpers", "值转换与真值", ValueRules));
        cases.Add(new("helpers", "变量路径解析", ScopePaths));
        cases.Add(new("helpers", "等待上限：0 / -1 / 有限", TimeoutRules));
    }

    /// <summary>「最多等待毫秒」的三种语义（<see cref="Timeouts"/>）：-1 是不限，0 是只做一次。</summary>
    private static void TimeoutRules()
    {
        Check.Equal(0, Timeouts.Normalize(0), "0 = 只做一次");
        Check.Equal(Timeouts.Unlimited, Timeouts.Normalize(-1), "-1 = 一直等");
        Check.Equal(Timeouts.Unlimited, Timeouts.Normalize(-90), "任何负数都当不限");
        Check.Equal(250, Timeouts.Normalize(250), "正常值原样");
        Check.Equal(Timeouts.MaxMs, Timeouts.Normalize(9_999_999), "超过 10 分钟夹到上限");
        Check.Equal(Timeouts.MaxMs, Timeouts.Normalize(600_000), "上限本身保留");

        Check.Equal(0, Timeouts.Loop(0), "循环期限：0 立刻到点");
        Check.Equal(int.MaxValue, Timeouts.Loop(-1), "循环期限：不限不会先到");
        Check.Equal(1000, Timeouts.Loop(1000), "循环期限：有限值原样");

        Check.Contains(Timeouts.DescribeWait(-1), "一直等");
        Check.Contains(Timeouts.DescribeWait(1500), "1500");
        Check.Equal("", Timeouts.DescribeWait(0), "只做一次时不说等了多久");

        // —— 命中类动作单元的「直到找到」开关（Timeouts.FromArgs）——
        var untilFound = new StepArgs(
            new Dictionary<string, Value>
            {
                ["untilFound"] = Value.FromBool(true),
                ["timeoutMs"] = Value.FromNumber(0),
            },
            new[] { "untilFound", "timeoutMs" });
        Check.Equal(Timeouts.Unlimited, Timeouts.FromArgs(untilFound), "勾「直到找到」= 一直找（盖过 timeoutMs）");

        var timed = new StepArgs(
            new Dictionary<string, Value>
            {
                ["untilFound"] = Value.FromBool(false),
                ["timeoutMs"] = Value.FromNumber(1500),
            },
            new[] { "untilFound", "timeoutMs" });
        Check.Equal(1500, Timeouts.FromArgs(timed), "不勾「直到找到」按 timeoutMs");

        var unset = new StepArgs(new Dictionary<string, Value>(), Array.Empty<string>());
        Check.Equal(0, Timeouts.FromArgs(unset), "两个都没写 = 只查一次");
        Check.Equal(Timeouts.Unlimited, Timeouts.FromArgs(new StepArgs(
            new Dictionary<string, Value> { ["untilFound"] = Value.FromBool(true) },
            new[] { "untilFound" })), "只勾开关、没写等待时间也一直找");
    }

    private static void CompareMatrix()
    {
        Check.True(FlowUnits.Compare.Test(Value.FromText("Hello"), "contains", Value.FromText("ell")), "包含");
        Check.True(FlowUnits.Compare.Test(Value.FromText("abc"), "startsWith", Value.FromText("AB")), "前缀忽略大小写");
        Check.True(FlowUnits.Compare.Test(Value.FromText("abc"), "endsWith", Value.FromText("bc")), "后缀");
        Check.True(FlowUnits.Compare.Test(Value.FromText("a1b2"), "regex", Value.FromText("\\d")), "正则");
        Check.True(FlowUnits.Compare.Test(Value.FromList(), "isEmpty", Value.Empty), "空列表");
        Check.True(FlowUnits.Compare.Test(Value.FromText("x"), "isNotEmpty", Value.Empty), "非空文本");
        Check.True(FlowUnits.Compare.Test(Value.FromNumber(3), "greater", Value.FromNumber(2)), "数字比较");
        Check.True(FlowUnits.Compare.Test(Value.Object(("found", Value.FromBool(true))), "found", Value.Empty), "对象 found 成员");
        Check.True(FlowUnits.Compare.Test(Value.FromText("yes"), "isTrue", Value.Empty), "yes 算真");

        ActionException bad = Check.Throws<ActionException>(
            () => FlowUnits.Compare.Test(Value.FromText("maybe"), "isTrue", Value.Empty), "非布尔文本应报错");
        Check.Contains(bad.Message, "不是布尔值");

        Check.Throws<ActionException>(
            () => FlowUnits.Compare.Test(Value.FromText("a"), "weird", Value.Empty), "未知比较方式应报错");
    }

    private static void VisibilityOptionGating()
    {
        UnitSpec spec = VisibilitySpec();
        var click = Args(("operation", "click"));
        var drag = Args(("operation", "drag"));

        Check.True(VisibleWhen.IsVisible(spec.Inputs[1], click, spec), "单击：x 可见");
        Check.False(VisibleWhen.IsVisible(spec.Inputs[2], click, spec), "单击：y 不可见");
        Check.True(VisibleWhen.IsVisible(spec.Inputs[2], drag, spec), "拖动：y 可见");

        var missing = Args();
        Check.False(VisibleWhen.IsVisible(spec.Inputs[2], missing, spec), "驱动键缺失时按默认值（单击）判定");

        var unknown = Args(("operation", "weird"));
        Check.True(VisibleWhen.IsVisible(spec.Inputs[2], unknown, spec), "认不出的旧值：受管参数按可见处理，不整片消失");
    }

    private static void VisibilityFallbacks()
    {
        UnitSpec spec = VisibilitySpec();
        var args = Args(("operation", "click"));

        Check.False(VisibleWhen.IsVisible(spec.Inputs[4], args, spec), "附加条件不成立时收起");
        args["flag"] = StepArg.FromBool(true);
        Check.True(VisibleWhen.IsVisible(spec.Inputs[4], args, spec), "附加条件成立时显示");

        var inArgs = Args(("operation", "drag"));
        Check.True(VisibleWhen.IsVisible(spec.Inputs[5], inArgs, spec), "in (...) 语法：命中");
        inArgs["operation"] = StepArg.FromText("weird");
        Check.False(VisibleWhen.IsVisible(spec.Inputs[5], inArgs, spec), "in (...) 语法：不命中");
    }

    private static void ListTextRules()
    {
        Check.Equal(2, ListText.Parse("a,b").Count, "逗号分隔");
        Check.Equal(2, ListText.Parse("a\nb").Count, "换行分隔");
        Check.Equal(0, ListText.Parse("   ").Count, "空白是空列表");
        Check.Equal("a\nb", ListText.Display(Value.FromList(new[] { Value.FromText("a"), Value.FromText("b") })),
            "列表每行一项");

        Check.True(ListText.TryParseDefault(ValueKind.list, "[1,2]", out Value? list, out string error),
            "JSON 数组应能解析：" + error);
        Check.Equal(2, list!.Items.Count, "JSON 数组项数");
        Check.Equal(ValueKind.number, list.Items[0].Kind, "JSON 数组保留类型");

        Check.False(ListText.TryParseDefault(ValueKind.number, "abc", out _, out string numberError), "坏数字应被拦住");
        Check.Contains(numberError, "不是数字");
    }

    private static void TextEscapesRules()
    {
        Check.Equal("\n", TextEscapes.Unescape(@"\n"), "\\n 是换行");
        Check.Equal("\r\n", TextEscapes.Unescape(@"\r\n"), "\\r\\n 是 Windows 换行");
        Check.Equal("\t", TextEscapes.Unescape(@"\t"), "\\t 是制表符");
        Check.Equal(@"\n", TextEscapes.Unescape(@"\\n"), "两个反斜杠是一个字面反斜杠 + n");
        Check.Equal(@"\d", TextEscapes.Unescape(@"\d"), "认不出的写法原样保留");
        Check.Equal(@"C:\new", TextEscapes.Unescape(@"C:\\new"), "路径里的转义只按约定还原");
    }

    private static void ValueRules()
    {
        Check.Equal(42d, Value.FromText("42").Coerce(ValueKind.number).AsNumber(), "文本转数字");
        Check.Equal(true, Value.FromText("yes").AsBool(), "yes 是 true");
        Check.Equal(false, Value.FromText("0").AsBool(), "0 是 false");
        Check.Equal("text-member",
            Value.Object(("text", Value.FromText("text-member")), ("x", Value.FromText("1"))).AsText(),
            "对象带 text 成员时按文本用");
        Check.Equal("1", Value.FromNumber(1).AsText(), "整数不带小数点");

        ActionException bad = Check.Throws<ActionException>(
            () => Value.FromText("abc").AsBool(), "非布尔文本转布尔应报错");
        Check.Contains(bad.Message, "不是布尔");
    }

    private static void ScopePaths()
    {
        var scope = new VariableScope();
        scope.Set("name", Value.FromText("阿呆"));
        scope.Set("obj", Value.Object(("a", Value.FromText("x"))));
        scope.Set("items", Value.FromList(new[] { Value.FromText("zero"), Value.FromText("one") }));

        Check.Equal("阿呆", scope.Resolve("name").AsText(), "直接取变量");
        Check.Equal("x", scope.Resolve("obj.a").AsText(), "对象成员");
        Check.Equal("one", scope.Resolve("items[1]").AsText(), "列表下标");
        Check.Equal(2d, scope.Resolve("items.count").AsNumber(), "列表 count");

        ActionException missing = Check.Throws<ActionException>(() => scope.Resolve("nope"), "未定义变量应报错");
        Check.Contains(missing.Message, "未定义变量");

        ActionException badIndex = Check.Throws<ActionException>(
            () => scope.Resolve("items[x]"), "下标不是整数应报错");
        Check.Contains(badIndex.Message, "下标必须是整数");

        var child = new VariableScope(scope);
        Check.Equal("阿呆", child.Resolve("name").AsText(), "子作用域能读到父作用域的变量");
        child.Set("name", Value.FromText("本地"));
        Check.Equal("本地", child.Resolve("name").AsText(), "子作用域同名变量覆盖父作用域");
        Check.Equal("阿呆", scope.Resolve("name").AsText(), "子作用域写入不影响父作用域");
    }

    // ============ 工具 ============

    private static UnitSpec VisibilitySpec() => new()
    {
        Key = "test.visibility",
        Name = "可见性测试",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("click"),
                Options: new[] { "click", "drag" },
                OptionParams: OptionMap.Of(
                    ("click", new[] { "x" }),
                    ("drag", new[] { "x", "y" }))),
            new InputSpec("x", "X", ValueKind.number, InputEditor.number),
            new InputSpec("y", "Y", ValueKind.number, InputEditor.number),
            new InputSpec("flag", "开关", ValueKind.@bool, InputEditor.boolean, Default: Value.FromBool(false)),
            new InputSpec("extra", "附加", ValueKind.text, VisibleWhen: "flag == true"),
            new InputSpec("inParam", "集合判断", ValueKind.text, VisibleWhen: "operation in ('click','drag')"),
        },
    };

    private static Dictionary<string, StepArg> Args(params (string Key, string Value)[] pairs)
    {
        var args = new Dictionary<string, StepArg>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in pairs) args[key] = StepArg.FromText(value);
        return args;
    }
}
