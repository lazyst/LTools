using System.Collections.Generic;
using LTools.Core.Actions;

namespace LTools.Features;

/// <summary>
/// 动作里可能指向数据根 <c>resources/</c> 的路径（备份导入预览用来提示「包里有可执行文件被动作引用」）。
/// </summary>
/// <remarks>
/// 路径在动作体的步骤参数里（启动程序 / 运行命令 / 打开文件）。
/// 只认字面量参数：变量 / 内联取值的实际值要运行后才知道。
/// </remarks>
internal static class ActionReferences
{
    /// <summary>动作里所有静态可读的路径 / 命令字符串。</summary>
    public static IEnumerable<string> ResourcePaths(ActionDto a)
    {
        foreach (var step in ProgramWalker.AllSteps(a.Program))
        {
            string? text = step.Unit switch
            {
                "app.launch" => ProgramWalker.LiteralText(step, "target"),
                "app.runCommand" => ProgramWalker.LiteralText(step, "command"),
                "file.open" => ProgramWalker.LiteralText(step, "path"),
                _ => null,
            };

            if (!string.IsNullOrWhiteSpace(text)) yield return text;
        }
    }
}
