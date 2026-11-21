using System.Globalization;
using System.Text.Json;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>写一条速记（与 CapsLock+N 同一个目录）。</summary>
public sealed class NoteWriteUnit : IStepUnit
{
    private static readonly string[] Modes = { "create", "append" };

    private static readonly Dictionary<string, string> ModeLabels = new()
    {
        ["create"] = "新建一条",
        ["append"] = "追加到已有",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "data.noteWrite",
        Name = "写速记",
        Category = "文本与数据",
        Icon = "note",
        Description = "把正文按 Markdown 源码存进速记；标题留空时取正文第一行，分类留空 = 未分类；" +
                      "可把图片一起存成附件，或追加到已有速记",
        Inputs = new[]
        {
            new InputSpec("body", "正文", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("mode", "方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("create"), Options: Modes, Labels: ModeLabels,
                OptionParams: OptionMap.Of(
                    ("create", ["title"]),
                    ("append", ["title", "target", "stamp"]))),
            new InputSpec("title", "标题", ValueKind.text,
                Description: "留空则取正文第一行；追加时沿用目标速记的标题"),
            new InputSpec("target", "追加到", ValueKind.text, InputEditor.file,
                Pickers: PickerKind.variable | PickerKind.file | PickerKind.inline | PickerKind.clear,
                Description: "要追加的速记文件；留空 = 同分类下最近修改的一条，该分类还没有就新建一条"),
            new InputSpec("category", "分类", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.noteCategory | PickerKind.inline | PickerKind.clear),
            new InputSpec("stamp", "时间标题", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(true),
                Description: "追加时先写一行「## 年-月-日 时:分」；关掉就只是把正文接在后面"),
            new InputSpec("images", "图片", ValueKind.list, InputEditor.multiline,
                Pickers: PickerKind.variable | PickerKind.file | PickerKind.image | PickerKind.inline | PickerKind.clear,
                Description: "每行一个图片路径（可以填 ${截图.file}）：复制进这条速记的 <笔记名>.files/，" +
                             "正文里的原路径换成相对链接，正文里没出现的补在末尾"),
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

        string[]? images = null;
        if (args.TryGet("images") is { } imageValue)
        {
            var paths = imageValue.AsList()
                .Select(v => v.AsText().Trim())
                .Where(p => p.Length > 0)
                .ToArray();
            if (paths.Length > 0) images = paths;
        }

        var result = NoteActions.Write(new NoteWriteRequest
        {
            Body = body,
            Title = args.Text("title"),
            Category = args.Text("category"),
            Append = string.Equals(args.Text("mode"), "append", StringComparison.OrdinalIgnoreCase),
            Target = args.Has("target") ? args.Text("target") : null,
            Stamp = args.Bool("stamp"),
            Images = images,
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
