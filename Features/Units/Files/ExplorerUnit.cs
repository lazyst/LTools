using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>在资源管理器里定位文件 / 打开文件夹（<c>file.explorer</c>）。</summary>
/// <remarks>
/// 这是「打开文件夹」的唯一入口：<c>file.openFile</c> 只管文件，不再接文件夹。
/// <c>show</c> 选中并定位某个文件，<c>open</c> 打开文件夹（传文件时打开它所在目录）。
/// </remarks>
public sealed class ExplorerUnit : IStepUnit
{
    private static readonly string[] Operations = { "show", "open" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["show"] = "定位文件",
        ["open"] = "打开文件夹",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.explorer",
        Name = "在资源管理器中定位 / 打开文件夹",
        Category = "文件",
        Icon = "folder",
        Description = "在资源管理器中定位一个文件，或打开文件夹（传文件时打开它所在目录）；这是「打开文件夹」的唯一入口，文件交给「用默认程序打开文件」",
        Inputs = new[]
        {
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("show"), Options: Operations,
                Labels: OperationLabels),
            new InputSpec("path", "文件 / 文件夹", ValueKind.text, InputEditor.file, Required: true,
                Pickers: PickerKind.variable | PickerKind.file | PickerKind.folder
                    | PickerKind.inline | PickerKind.clear,
                Description: "「定位文件」填文件路径（资源管理器会选中它）；「打开文件夹」填目录，或填文件由本步取它所在目录"),
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
