using System.Text;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Apps;

/// <summary>
/// 往动作日志里写一行（<c>app.log</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 流程里的「看看这一步到底算出了什么」以前只能靠提示弹窗（会消失）或文本窗口（要开窗）——
/// 这里落盘，跑完再翻，也能给用户贴日志排查。
/// </para>
/// <para>
/// 写两个地方：<c>logs/action-yyyyMMdd.log</c> <b>始终写</b>（动作自己的日志，不受设置影响），
/// 同时进运行日志 <see cref="AppLog"/>（跟随「设置 → 通用 → 日志级别」，用来跟流程事件对时间）。
/// </para>
/// </remarks>
public sealed class LogUnit : IStepUnit
{
    private static readonly string[] Levels = { "info", "warn", "error", "debug" };

    private static readonly Dictionary<string, string> LevelLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = "信息",
        ["warn"] = "警告",
        ["error"] = "错误",
        ["debug"] = "调试",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "app.log",
        Name = "输出日志",
        Category = "流程",
        Icon = "note",
        Description = "往 logs/action-日期.log 写一行（始终写），同时进运行日志；输出日志文件路径",
        Inputs = new[]
        {
            new InputSpec("level", "级别", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("info"), Options: Levels, Labels: LevelLabels,
                Description: "只影响行首标签与运行日志的过滤，动作日志文件里都会写"),
            new InputSpec("text", "内容", null, InputEditor.multiline, Required: true,
                Description: "要记的内容；多行会按续行缩进写入"),
            new InputSpec("source", "来源", ValueKind.text,
                Description: "行首方括号里的名字；留空用当前动作名"),
        },
        Outputs = new[]
        {
            new OutputSpec("file", "日志文件", ValueKind.text, "logFile",
                Description: "今天这份动作日志的路径（logs/action-yyyyMMdd.log）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string level = args.Text("level").Trim().ToLowerInvariant();
        string text = args.Text("text");
        string source = args.Text("source").Trim();
        if (source.Length == 0) source = ctx.State.Program.Name;

        string path;
        try
        {
            path = Append(source, level, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActionException($"写日志失败：{ex.Message}", ex);
        }

        // 运行日志跟随设置里的级别；动作日志文件已经无条件写过了。
        switch (level)
        {
            case "error": AppLog.Error(source, text); break;
            case "warn": AppLog.Warn(source, text); break;
            case "debug": AppLog.Debug(source, text); break;
            default: AppLog.Info(source, text); break;
        }

        ctx.SetOutput("file", Value.FromText(path));
    }

    private static string Append(string source, string level, string text)
    {
        string dir = AppPaths.LogsDir;
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, $"action-{DateTime.Now:yyyyMMdd}.log");

        string tag = level switch
        {
            "error" => "ERROR",
            "warn" => "WARN ",
            "debug" => "DEBUG",
            _ => "INFO ",
        };

        string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var sb = new StringBuilder();
        sb.Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(" [").Append(tag).Append("] [")
          .Append(source).Append("] ").AppendLine(lines[0]);
        for (int i = 1; i < lines.Length; i++)
            sb.Append("    ").AppendLine(lines[i]);

        File.AppendAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }
}
