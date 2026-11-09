using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>读速记：按分类 + 关键字取最近修改的一条。</summary>
public sealed class NoteReadUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.noteRead",
        Name = "读速记",
        Category = "文本与数据",
        Icon = "noteRead",
        Description = "按分类与关键字取最近修改的一条；没有匹配时 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("category", "分类", ValueKind.text),
            new InputSpec("filter", "关键字", ValueKind.text),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "正文", ValueKind.text, "note", Description: "命中的速记正文"),
            new OutputSpec("title", "标题", ValueKind.text, Description: "速记标题"),
            new OutputSpec("time", "时间", ValueKind.text, Description: "最后修改时间"),
            new OutputSpec("path", "文件", ValueKind.text, Description: "速记文件路径"),
            new OutputSpec("found", "找到", ValueKind.@bool, Description: "是否找到匹配的速记；没找到不是错误"),
            new OutputSpec("count", "匹配数量", ValueKind.number, Description: "匹配到的条数"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var probe = new Probe
        {
            Kind = ProbeKind.note,
            NoteCategory = args.Text("category"),
            Text = args.Text("filter"),
        };

        var value = ProbeContext.Evaluate(ctx, legacy => NoteActions.Read(probe, legacy));
        string text = UnitValues.Member(value, "text")?.AsText() ?? "";
        string title = UnitValues.Member(value, "title")?.AsText() ?? "";
        string time = UnitValues.Member(value, "time")?.AsText() ?? "";
        string path = UnitValues.Member(value, "file")?.AsText() ?? "";
        bool found = UnitValues.Member(value, "found")?.AsBool() ?? false;
        int count = (int)Number(value, "number");

        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("title", Value.FromText(title));
        ctx.SetOutput("time", Value.FromText(time));
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("found", Value.FromBool(found));
        ctx.SetOutput("count", Value.FromNumber(count));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(text)),
            ("title", Value.FromText(title)),
            ("time", Value.FromText(time)),
            ("path", Value.FromText(path)),
            ("file", Value.FromText(path)),
            ("found", Value.FromBool(found)),
            ("count", Value.FromNumber(count)),
            ("number", Value.FromNumber(count))));
    }

    private static double Number(Value value, string name)
    {
        var member = UnitValues.Member(value, name);
        if (member == null) return 0;
        try { return member.AsNumber(); }
        catch (ActionException) { return 0; }
    }
}

/// <summary>列表处理：取项 / 拼接 / 拆分 / 查找 / 排序 / 增删。</summary>
