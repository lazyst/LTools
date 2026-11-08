using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>在资源管理器里定位文件 / 打开所在文件夹。</summary>
public sealed class ExplorerUnit : IStepUnit
{
    private static readonly string[] Operations = { "show", "open" };

    /// <summary>这里的 <c>open</c> 是「打开文件夹」，不是「用默认程序打开」。</summary>
    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["show"] = "定位文件",
        ["open"] = "打开文件夹",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.explorer",
        Name = "在资源管理器中显示",
        Category = "文件",
        Icon = "explorer",
        Description = "在资源管理器中定位文件，或打开文件夹",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("show"), Options: Operations,
                Labels: OperationLabels),
            new InputSpec("path", "路径", ValueKind.text, InputEditor.file, Required: true),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "路径", ValueKind.text, Description: "定位到的路径"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否成功打开资源管理器"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        string path = FileText.Resolve(args.Text("path"), "");

        if (!File.Exists(path) && !Directory.Exists(path))
            throw new ActionException($"路径不存在：{path}");

        if (op == "open")
        {
            string folder = Directory.Exists(path) ? path : (Path.GetDirectoryName(path) ?? path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        else if (op == "show")
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        else
        {
            throw new ActionException($"操作只能是 show / open（当前 {op}）");
        }

        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("ok", Value.FromBool(true));
    }
}

/// <summary>列出文件夹内容——「从磁盘造出一个列表」的唯一入口。</summary>
