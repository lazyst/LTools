using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LTools.Core.Actions;

namespace LTools.Features.Panel;

/// <summary>
/// 简单动作的目录：一步就能用、建完即用的常用动作。
/// </summary>
/// <remarks>
/// <para>
/// 简单动作与组合动作**执行上没有区别**：都是 <see cref="ActionDto.Program"/> 里的普通步骤，
/// 区别只在「怎么改」——简单动作带 <see cref="ActionDto.SimpleKind"/>，面板里的「编辑」开
/// <c>SimpleActionDialog</c>（只改这一个动作单元的参数与名称），要加步骤得先
/// 「转换为组合动作」（清掉标记、交给组合动作编辑器）。
/// </para>
/// <para>
/// 新增一种简单动作只加一条 <see cref="Kind"/> + 在 <see cref="Apply"/> / <see cref="ValuesOf"/>
/// 各补一个分支；**不要为新种类新建动作单元**（它只是某个既有动作单元的一步预设）。
/// </para>
/// </remarks>
internal static class SimpleActions
{
    public const string OpenFile = "openFile";
    public const string OpenFolder = "openFolder";
    public const string LaunchApp = "launchApp";
    public const string OpenUrl = "openUrl";
    public const string ReplayMacro = "replayMacro";
    public const string RunCommand = "runCommand";
    public const string SendKeys = "sendKeys";

    /// <summary>一种简单动作的预设（名字 / 图标 / 步骤单元）。</summary>
    internal sealed record Kind(string Key, string Name, string Icon, string Unit, string Description);

    /// <summary>「新建简单动作」菜单里列的全部种类，顺序就是菜单顺序。</summary>
    public static readonly IReadOnlyList<Kind> Catalog = new Kind[]
    {
        new(OpenFile, "打开文件", "file", "file.openFile",
            "用默认程序打开一个文件；单击磁贴就打开"),
        new(OpenFolder, "打开文件夹", "folder", "file.explorer",
            "在资源管理器里打开一个文件夹"),
        new(LaunchApp, "启动程序", "play", "app.launch",
            "启动一个程序，可以带命令行参数"),
        new(OpenUrl, "打开网址", "globe", "web.open",
            "用默认浏览器打开一个网址"),
        new(ReplayMacro, "回放键鼠宏", "record", "input.macro",
            "回放一段已经录好的键鼠宏"),
        new(RunCommand, "运行命令", "terminal", "app.runCommand",
            "在终端里运行一条命令（窗口 / 后台两种方式）"),
        new(SendKeys, "模拟按键", "keyboard", "input.sendKeys",
            "发送一个按键或组合键，例如 Ctrl+S"),
    };

    /// <summary>按预设键找种类；不是已知简单动作返回 null。</summary>
    public static Kind? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        return Catalog.FirstOrDefault(k => string.Equals(k.Key, key.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>这个动作是不是简单动作（带已知的 <see cref="ActionDto.SimpleKind"/>）。</summary>
    public static Kind? KindOf(ActionDto? action) => Find(action?.SimpleKind);

    /// <summary>从动作读出这种简单动作的全部参数（读不到就是空串）。</summary>
    public static Dictionary<string, string> ValuesOf(Kind kind, ActionDto? action)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Step? step = action?.Program is { Steps.Count: 1 } program ? program.Steps[0] : null;

        switch (kind.Key)
        {
            case OpenFile:
            case OpenFolder:
                values["path"] = Text(step, "path");
                break;

            case LaunchApp:
                values["target"] = Text(step, "target");
                values["args"] = Text(step, "args");
                break;

            case OpenUrl:
                values["url"] = Text(step, "url");
                break;

            case ReplayMacro:
                values["macroId"] = Text(step, "macroId");
                break;

            case RunCommand:
                values["command"] = Text(step, "command");
                values["terminal"] = Or(Text(step, "terminal"), "direct");
                values["capture"] = Text(step, "capture") == "true" ? "true" : "false";
                break;

            case SendKeys:
                values["key"] = Text(step, "key");
                values["mods"] = Text(step, "mods");
                break;
        }

        return values;
    }

    /// <summary>
    /// 由参数生成新的动作体（恰好一步）；<paramref name="basedOn"/> 用来保留它原有、
    /// 这个表单不管的参数（如打开文件的工作目录、运行命令的工作目录），别在编辑时丢掉。
    /// </summary>
    public static ActionProgram Build(Kind kind, IReadOnlyDictionary<string, string> values,
        ActionProgram? basedOn = null)
    {
        Step? existing = basedOn is { Steps.Count: 1 } p
                          && string.Equals(p.Steps[0].Unit, kind.Unit, StringComparison.OrdinalIgnoreCase)
            ? p.Steps[0]
            : null;

        Step step = existing?.Clone() ?? Step.Of(kind.Unit);
        Apply(kind, step, values);

        var program = new ActionProgram
        {
            Id = basedOn?.Id ?? "",
            Name = basedOn?.Name ?? "",
            Icon = basedOn?.Icon ?? "",
            Description = Describe(kind, values),
        };
        program.Steps.Add(step);
        return program;
    }

    /// <summary>默认名称：路径取文件名（去扩展名）、网址取主机名、命令取第一行…</summary>
    public static string DefaultName(Kind kind, IReadOnlyDictionary<string, string> values,
        string? macroName = null)
    {
        string name = kind.Key switch
        {
            OpenFile => FileStem(Get(values, "path")),
            OpenFolder => Leaf(Get(values, "path")),
            LaunchApp => FileStem(Get(values, "target")),
            OpenUrl => Host(Get(values, "url")),
            ReplayMacro => macroName?.Trim() ?? "",
            RunCommand => FirstLine(Get(values, "command")),
            SendKeys => ChordText(values),
            _ => "",
        };

        return string.IsNullOrWhiteSpace(name) ? kind.Name : name.Trim();
    }

    /// <summary>动作体说明（动作选择器 / 导出 / 悬停提示都看它）。</summary>
    public static string Describe(Kind kind, IReadOnlyDictionary<string, string> values,
        string? macroName = null)
    {
        return kind.Key switch
        {
            OpenFile => Get(values, "path"),
            OpenFolder => Get(values, "path"),
            LaunchApp => Join(Get(values, "target"), Get(values, "args")),
            OpenUrl => Get(values, "url"),
            ReplayMacro => "回放宏：" + (string.IsNullOrWhiteSpace(macroName) ? Get(values, "macroId") : macroName!.Trim()),
            RunCommand => Get(values, "command"),
            SendKeys => "按键 " + ChordText(values),
            _ => "",
        };
    }

    private static void Apply(Kind kind, Step step, IReadOnlyDictionary<string, string> values)
    {
        switch (kind.Key)
        {
            case OpenFile:
                Optional(step, "path", Get(values, "path"), required: true);
                break;

            case OpenFolder:
                step.WithText("operation", "open");
                Optional(step, "path", Get(values, "path"), required: true);
                break;

            case LaunchApp:
                Optional(step, "target", Get(values, "target"), required: true);
                Optional(step, "args", Get(values, "args"));
                break;

            case OpenUrl:
                Optional(step, "url", Get(values, "url"), required: true);
                break;

            case ReplayMacro:
                Optional(step, "macroId", Get(values, "macroId"), required: true);
                break;

            case RunCommand:
            {
                Optional(step, "command", Get(values, "command"), required: true);
                step.WithText("terminal", Or(Get(values, "terminal"), "direct"));
                bool capture = Get(values, "capture") == "true";
                step.With("capture", StepArg.FromBool(capture));
                step.With("keepWindow", StepArg.FromBool(!capture));
                break;
            }

            case SendKeys:
                Optional(step, "key", Get(values, "key"), required: true);
                step.WithText("action", "tap");
                Optional(step, "mods", Get(values, "mods"));
                break;
        }
    }

    /// <summary>必填参数永远写（空也写，交给校验器 / 运行时报「没填」）；可选参数空就不落盘。</summary>
    private static void Optional(Step step, string key, string value, bool required = false)
    {
        if (required || value.Length > 0) step.WithText(key, value);
        else step.In.Remove(key);
    }

    private static string Text(Step? step, string key)
    {
        if (step == null || !step.In.TryGetValue(key, out var arg)) return "";
        return arg.Kind == StepArgKind.literal ? arg.Literal.AsText() : "";
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out string? value) ? (value ?? "").Trim() : "";

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string Join(string first, string second) =>
        second.Length > 0 ? first + " " + second : first;

    private static string Leaf(string path)
    {
        string text = path.Trim().Trim('"').TrimEnd('\\', '/');
        if (text.Length == 0) return "";

        string leaf = Path.GetFileName(text);
        return string.IsNullOrWhiteSpace(leaf) ? text : leaf;
    }

    private static string FileStem(string path)
    {
        string leaf = Leaf(path);
        if (leaf.Length == 0) return "";

        string stem = Path.GetFileNameWithoutExtension(leaf);
        return string.IsNullOrWhiteSpace(stem) ? leaf : stem;
    }

    private static string Host(string url)
    {
        string text = url.Trim();
        if (text.Length == 0) return "";
        return Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.Host
            : text;
    }

    private static string FirstLine(string command)
    {
        string line = command
            .Split('\n')
            .Select(s => s.Trim())
            .FirstOrDefault(s => s.Length > 0) ?? "";
        if (line.Length <= 24) return line;
        return line[..24] + "…";
    }

    private static string ChordText(IReadOnlyDictionary<string, string> values)
    {
        string key = Get(values, "key");
        if (key.Length == 0) return "";

        string mods = Get(values, "mods");
        string chord = mods.Length > 0 ? mods + "+" + key : key;
        return MacroSettings.FormatHotkey(MacroSettings.NormalizeHotkey(chord));
    }
}
