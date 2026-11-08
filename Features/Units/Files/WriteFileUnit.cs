using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>写入文本文件（覆盖 / 追加）。</summary>
public sealed class WriteFileUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.write",
        Name = "写入文件",
        Category = "文件",
        Icon = "save",
        Description = "把文本写入文件；目录不存在时自动创建",
        Inputs = new[]
        {
            new InputSpec("path", "路径", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("text", "内容", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("append", "追加", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false)),
            new InputSpec("encoding", "编码", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: FileText.Encodings),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "实际路径", ValueKind.text, "path", Description: "实际写入的文件路径"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否写入成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        FileText.Write(path, args.Text("text"), args.Text("encoding"), args.Bool("append"));
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("ok", Value.FromBool(true));
    }
}

/// <summary>文件操作：复制 / 移动 / 删除 / 建目录 / 重命名。</summary>
