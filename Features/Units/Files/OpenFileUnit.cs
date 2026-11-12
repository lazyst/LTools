using System.Diagnostics;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>
/// 用默认程序打开一个文件（<c>file.openFile</c>）。
/// </summary>
/// <remarks>
/// 只管「文件」这一件事：文件夹走 <c>file.explorer</c>，网址走 <c>web.open</c>。
/// 旧的 <c>file.open</c> 是这三种职责合并出来的三合一动作单元，已整体退役，
/// 不认旧字段、不留别名、不写迁移器（见 <c>docs/ACTION_SYSTEM.md</c>）。
/// </remarks>
public sealed class OpenFileUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.openFile",
        Name = "用默认程序打开文件",
        Category = "文件",
        Icon = "file",
        Description = "按文件关联的默认程序打开文件（.xlsx 交给 Excel、.png 交给看图工具）；只处理文件，文件夹用「在资源管理器中定位 / 打开文件夹」，网址用「用浏览器打开网址」。相对路径按数据根 / 工作目录解析",
        Inputs = new[]
        {
            new InputSpec("path", "文件", ValueKind.text, InputEditor.file, Required: true,
                Description: "要打开的文件；可写数据根相对路径（如 resources\\报表.xlsx），或 ${变量} / 内联取值"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder,
                Description: "相对路径的解析起点，留空 = 数据根。要带命令行参数启动 .exe 请用「启动程序」"),
        },
        Outputs = new[]
        {
            new OutputSpec("target", "实际文件", ValueKind.text, "file",
                Description: "解析后的目标路径；打不开时本步直接失败，不会有输出"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string workdir = AppPaths.ResolveWorkdir(args.Text("workdir"));
        string raw = args.Text("path").Trim();
        if (raw.Length == 0) throw new ActionException("要打开的文件为空");

        string target = AppPaths.ResolvePath(raw, workdir, allowDirectory: false);
        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (Directory.Exists(workdir)) psi.WorkingDirectory = workdir;

        Process.Start(psi);
        ctx.SetOutput("target", Value.FromText(target));
    }
}
