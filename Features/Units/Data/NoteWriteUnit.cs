using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>写一条速记（与 CapsLock+N 同一个目录）。</summary>
public sealed class NoteWriteUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "data.noteWrite",
        Name = "写速记",
        Category = "数据",
        Icon = "note",
        Description = "把正文存进速记；标题留空时取正文第一行，分类留空 = 未分类",
        Inputs = new[]
        {
            new InputSpec("body", "正文", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("category", "分类", ValueKind.text),
        },
        Outputs = new[]
        {
            new OutputSpec("title", "标题", ValueKind.text, "noteTitle", Description: "最终标题（没填标题时按时间生成）"),
            new OutputSpec("time", "时间", ValueKind.text, Description: "写入时间"),
            new OutputSpec("path", "文件", ValueKind.text, Description: "速记文件路径"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string body = args.Text("body");
        if (string.IsNullOrWhiteSpace(body)) throw new ActionException("写速记缺少正文");

        var result = NoteActions.Write(new NoteWriteRequest
        {
            Body = body,
            Title = args.Text("title"),
            Category = args.Text("category"),
        });

        ctx.SetOutput("title", Value.FromText(result.Title));
        ctx.SetOutput("time", Value.FromText(result.Time));
        ctx.SetOutput("path", Value.FromText(result.File));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(result.File)),
            ("path", Value.FromText(result.File)),
            ("file", Value.FromText(result.File)),
            ("title", Value.FromText(result.Title)),
            ("time", Value.FromText(result.Time)),
            ("found", Value.FromBool(true))));
    }
}

/// <summary>读速记：按分类 + 关键字取最近修改的一条。</summary>
