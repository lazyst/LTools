namespace LTools.Core.Actions;

/// <summary>
/// 空动作时的起步模板（编辑器中间那块「从模板开始」）。
/// </summary>
/// <remarks>
/// <para>
/// 模板就是几个现成的步骤 + 变量声明，点一下**追加**到当前动作里，然后照常改参数——
/// 不是另一套「模板引擎」，也不落盘任何模板状态：<see cref="Build"/> 每次现建一份新的
/// <see cref="Step"/> 树，编辑器把它插进 <c>ActionProgram.Steps</c> 就完事。
/// </para>
/// <para>
/// 参数一定写全（必填项都有值，变量名在步骤之间对得上），所以点完就能直接试运行；
/// 想改的只有「本来就是用户才知道」的那几处（窗口标题、列表内容…），那写在 <see cref="Hint"/> 里。
/// 新增模板时照这样写：**能跑**优先于「看起来高级」，别塞一堆要用户先补参数的半成品。
/// </para>
/// </remarks>
internal sealed class ActionTemplate
{
    /// <summary>按钮上的名字（也是插进动作时的那一句撤销说明）。</summary>
    public required string Name { get; init; }

    /// <summary>一句说明：这个模板做什么、点完该改哪里。</summary>
    public required string Hint { get; init; }

    /// <summary>一次调用给一份全新的步骤（编辑器的撤销栈会比较步骤引用，别共享实例）。</summary>
    public required Func<IReadOnlyList<Step>> Build { get; init; }

    /// <summary>步骤要用到的变量声明（已经有同名变量时跳过）。</summary>
    public Func<IReadOnlyList<VariableDef>> Variables { get; init; } = () => Array.Empty<VariableDef>();
}

internal static class ActionTemplates
{
    /// <summary>编辑器里按顺序列出来的模板。</summary>
    public static IReadOnlyList<ActionTemplate> All { get; } = new[]
    {
        new ActionTemplate
        {
            Name = "处理选中文本",
            Hint = "读取当前选中的文字 → 去首尾空白 → 原样发回当前窗口",
            Build = () => new[]
            {
                Step.Of("text.selection").Out("text", "selection"),
                Step.Of("text.process")
                    .In("operation", "trim")
                    .In("text", "${selection}")
                    .Out("text", "result"),
                Step.Of("text.send").In("text", "${result}"),
            },
        },

        new ActionTemplate
        {
            Name = "截图 → 识别文字 → 粘贴",
            Hint = "框选一块屏幕 → 识别成文字 → 发到当前窗口",
            Build = () => new[]
            {
                Step.Of("screen.capture")
                    .In("mode", "interactive")
                    .Out("file", "shot"),
                Step.Of("screen.ocr")
                    .In("source", "image")
                    .In("image", "${shot}")
                    .Out("text", "text"),
                Step.Of("text.send").In("text", "${text}"),
            },
        },

        new ActionTemplate
        {
            Name = "列表逐项提示",
            Hint = "遍历「列表」变量，每项弹一条提示；内容在变量表里改",
            Build = () => new[]
            {
                Body(Step.Of("flow.forEach")
                        .WithVar("items", "items")
                        .Out("item", "item"),
                    Step.Of("ui.toast").In("text", "${item}")),
            },
            Variables = () => new[]
            {
                new VariableDef
                {
                    Key = "items",
                    Type = ValueKind.list,
                    Label = "列表",
                    Default = Value.FromList(new[]
                    {
                        Value.FromText("第一项"),
                        Value.FromText("第二项"),
                        Value.FromText("第三项"),
                    }),
                },
            },
        },

        new ActionTemplate
        {
            Name = "问一句再发送",
            Hint = "弹输入框要一段文本 → 发到当前焦点窗口",
            Build = () => new[]
            {
                Step.Of("ui.input")
                    .In("prompt", "请输入要发送的内容")
                    .In("title", "自动化")
                    .Out("text", "input"),
                Step.Of("text.send").In("text", "${input}"),
            },
        },
    };

    /// <summary>把 <paramref name="body"/> 塞进 <paramref name="owner"/> 的循环体 / 分支体。</summary>
    private static Step Body(Step owner, params Step[] body)
    {
        foreach (Step step in body) owner.Body.Add(step);
        return owner;
    }
}

/// <summary>构造步骤时的小语法糖：<c>Step.Of(key).In("mode", "screen").Out("file", "shot")</c>。</summary>
internal static class StepBuildExtensions
{
    public static Step In(this Step step, string key, string text)
    {
        step.In[key] = StepArg.FromText(text);
        return step;
    }

    public static Step Out(this Step step, string outputKey, string variable)
    {
        step.Out[outputKey] = variable;
        return step;
    }
}
