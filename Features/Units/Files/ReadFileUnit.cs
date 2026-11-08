using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>读取文本文件。</summary>
public sealed class ReadFileUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.read",
        Name = "读取文件",
        Category = "文件",
        Icon = "read",
        Description = "读取文本文件；相对路径按数据根 / 工作目录解析",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "路径", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("encoding", "编码", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: FileText.Encodings),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "内容", ValueKind.text, "text", Description: "文件全部内容"),
            new OutputSpec("found", "读到", ValueKind.@bool, Description: "文件是否存在且读取成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        if (!File.Exists(path))
        {
            ctx.SetOutput("text", Value.FromText(""));
            ctx.SetOutput("found", Value.FromBool(false));
            return;
        }

        string text = FileText.Read(path, args.Text("encoding"));
        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("found", Value.FromBool(true));
    }
}

/// <summary>写入文本文件（覆盖 / 追加）。</summary>
