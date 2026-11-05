namespace LTools.Features;

/// <summary>
/// 动作结果的说明：哪些动作能「把结果存入变量」，以及每个类型会存什么。
/// 供动作编辑器的「输出」面板显示提示；实际求值在 <see cref="ActionExecutor"/>。
/// </summary>
internal static class ActionResults
{
    /// <summary>该动作类型执行后是否有可存入变量的结果。</summary>
    public static bool SupportsOutput(ActionType type) => type is
        ActionType.launchApp or
        ActionType.openFile or
        ActionType.openFolder or
        ActionType.openUrl or
        ActionType.runCommand or
        ActionType.setClipboard or
        ActionType.mouse or
        ActionType.window;

    /// <summary>输出变量可用的属性后缀（供变量面板 / 模板提示列出 <c>${变量.exit}</c> 之类写法）。</summary>
    public static IReadOnlyList<string> Properties(ActionType type) => type switch
    {
        ActionType.launchApp => new[] { "number", "found" },
        ActionType.runCommand => new[] { "exit", "err", "found" },
        ActionType.mouse => new[] { "x", "y" },
        ActionType.window => new[] { "x", "y", "w", "h", "found" },
        _ => Array.Empty<string>(),
    };

    /// <summary>输出变量的内容说明（编辑器提示用）。</summary>
    public static string Hint(ActionType type) => type switch
    {
        ActionType.launchApp => "写入新进程的 PID。引用 ${变量} 取 PID。",
        ActionType.openFile or ActionType.openFolder => "写入实际打开的绝对路径（相对路径已解析）。",
        ActionType.openUrl => "写入实际打开的网址。",
        ActionType.runCommand => "填写后命令在后台执行并捕获输出（不打开窗口，所以「保持窗口」不生效）：${变量} = 标准输出，"
                                 + "${变量.exit} = 退出码，${变量.err} = 错误输出。命令结束才继续下一步；"
                                 + "上面的「终端」仍决定由哪个 shell 执行（PowerShell / Git Bash / WSL / CMD）。",
        ActionType.setClipboard => "写入实际写入剪贴板的文本。",
        ActionType.mouse => "写入操作后的光标坐标，可用 ${变量.x} / ${变量.y} 引用。",
        ActionType.window => "写入操作后的窗口矩形：${变量} = x,y,w,h，另有 ${变量.x} / ${变量.y} / ${变量.w} / ${变量.h}。"
                             + "调整大小时这里是窗口**实际**的样子（可能被应用的最小尺寸夹住）。",
        _ => "",
    };
}
