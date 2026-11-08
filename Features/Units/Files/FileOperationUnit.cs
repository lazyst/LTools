using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>文件操作：复制 / 移动 / 删除 / 建目录 / 重命名。</summary>
public sealed class FileOperationUnit : IStepUnit
{
    private static readonly string[] Operations = { "copy", "move", "delete", "mkdir", "rename" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.operation",
        Name = "文件操作",
        Category = "文件",
        Icon = "files",
        Description = "复制、移动、删除、新建目录、重命名",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("copy"), Options: Operations),
            new InputSpec("path", "源路径", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("target", "目标路径", ValueKind.text, InputEditor.file,
                VisibleWhen: "operation == 'copy' || operation == 'move' || operation == 'rename'"),
            new InputSpec("overwrite", "覆盖已有", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false)),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "结果路径", ValueKind.text, "path", Description: "操作后的路径"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        string target = args.Has("target") && args.Text("target").Trim().Length > 0
            ? FileText.Resolve(args.Text("target"), args.Text("workdir"))
            : "";
        bool overwrite = args.Bool("overwrite");
        string result = path;

        switch (op)
        {
            case "copy":
                RequireTarget(target);
                Copy(path, target, overwrite);
                result = target;
                break;

            case "move":
            case "rename":
                RequireTarget(target);
                Move(path, target, overwrite);
                result = target;
                break;

            case "delete":
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                else throw new ActionException($"要删除的路径不存在：{path}");
                break;

            case "mkdir":
                Directory.CreateDirectory(path);
                break;

            default:
                throw new ActionException($"未知文件操作：{op}");
        }

        ctx.SetOutput("path", Value.FromText(result));
        ctx.SetOutput("ok", Value.FromBool(true));
    }

    private static void RequireTarget(string target)
    {
        if (target.Length == 0) throw new ActionException("这个文件操作需要「目标路径」");
    }

    private static void Copy(string source, string target, bool overwrite)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target, overwrite);
            return;
        }

        if (!File.Exists(source)) throw new ActionException($"源文件不存在：{source}");
        EnsureParent(target);
        File.Copy(source, target, overwrite);
    }

    private static void Move(string source, string target, bool overwrite)
    {
        if (overwrite)
        {
            if (File.Exists(target)) File.Delete(target);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }

        EnsureParent(target);
        if (Directory.Exists(source)) Directory.Move(source, target);
        else if (File.Exists(source)) File.Move(source, target, overwrite);
        else throw new ActionException($"源路径不存在：{source}");
    }

    private static void EnsureParent(string target)
    {
        string? parent = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
    }

    private static void CopyDirectory(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite);
        foreach (string dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)), overwrite);
    }
}

/// <summary>检查路径是否存在 / 是文件还是目录，并读取大小与修改时间。</summary>
