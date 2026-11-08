using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>用默认程序打开文件 / 文件夹 / 网址。</summary>
public sealed class OpenPathUnit : IStepUnit
{
    private static readonly string[] Operations = { "file", "folder", "url" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.open",
        Name = "打开",
        Category = "文件",
        Icon = "open",
        Description = "用默认程序打开文件、在资源管理器里打开文件夹，或用浏览器打开网址",
        Inputs = new[]
        {
            new InputSpec("operation", "打开方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("file"), Options: Operations),
            new InputSpec("path", "路径 / 网址", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("target", "实际目标", ValueKind.text, Description: "实际打开的路径或网址"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否成功发起打开"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();
        string workdir = AppPaths.ResolveWorkdir(args.Text("workdir"));
        string raw = args.Text("path").Trim();
        if (raw.Length == 0) throw new ActionException("打开的目标为空");

        string target = operation switch
        {
            "folder" => AppPaths.ResolvePath(raw, workdir, allowDirectory: true),
            "url" => raw,
            "file" => AppPaths.ResolvePath(raw, workdir, allowDirectory: false),
            _ => throw new ActionException($"打开方式只能是 file / folder / url（当前 {operation}）"),
        };

        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (operation != "url" && Directory.Exists(workdir))
            psi.WorkingDirectory = workdir;

        Process.Start(psi);
        ctx.SetOutput("target", Value.FromText(target));
        ctx.SetOutput("ok", Value.FromBool(true));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(target)),
            ("path", Value.FromText(target)),
            ("file", Value.FromText(target)),
            ("found", Value.FromBool(true))));
    }
}

/// <summary>弹出系统的文件 / 文件夹选择框。</summary>
