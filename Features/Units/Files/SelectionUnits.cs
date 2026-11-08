using System.IO;
using LTools.Core.Actions;
using LTools.Features.Selection;

namespace LTools.Features.Units.Files;

/// <summary>读取资源管理器 / 桌面里选中的文件与文件夹。</summary>
public sealed class FileSelectionUnit : IStepUnit
{
    private static readonly string[] Sorts = { "none", "name", "size", "modified" };

    /// <summary><c>name</c> / <c>size</c> 这类通用词不进全局表，避免以后别处复用同一个键时被翻错。</summary>
    private static readonly Dictionary<string, string> SortLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = "不排序",
        ["name"] = "按名称",
        ["size"] = "按大小",
        ["modified"] = "按修改时间",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.selection",
        Name = "选中的文件",
        Category = "文件",
        Icon = "file",
        Description = "读取资源管理器 / 桌面里选中的文件或文件夹路径；拿不到只是 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("useClipboardFallback", "Ctrl+C 兜底", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "给前台发一次 Ctrl+C 读剪贴板；只服务第三方文件管理器，会临时占用剪贴板"),
            new InputSpec("waitMs", "等待剪贴板毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(200), VisibleWhen: "useClipboardFallback == true",
                Description: "等剪贴板出现文件的时长（50–3000）；太短会读不到大文件"),
            new InputSpec("sort", "排序", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("none"), Options: Sorts, Labels: SortLabels,
                Description: "拿到的路径按什么排；不排序 = 保持资源管理器里的选中顺序"),
            new InputSpec("descending", "倒序", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false)),
        },
        Outputs = new[]
        {
            new OutputSpec("list", "文件列表", ValueKind.list, "selectedFiles",
                Description: "选中的完整路径，可直接给「遍历列表」"),
            new OutputSpec("count", "数量", ValueKind.number, Description: "选中项个数"),
            new OutputSpec("first", "第一个", ValueKind.text, Description: "首个路径"),
            new OutputSpec("names", "文件名列表", ValueKind.list, Description: "只有文件名（不含目录）"),
            new OutputSpec("folder", "所在文件夹", ValueKind.text, Description: "资源管理器当前目录（兜底时取首个路径的父目录）"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "每行一个路径"),
            new OutputSpec("found", "有选中", ValueKind.@bool, Description: "是否拿到选中项"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var selection = ShellBridge.Foreground();
        var paths = selection?.Paths.ToList() ?? new List<string>();
        string folder = selection?.Folder ?? "";

        if (paths.Count == 0 && args.Bool("useClipboardFallback"))
        {
            int waitMs = (int)Math.Round(args.Number("waitMs"));
            paths = SelectionFallback.Capture(waitMs).ToList();
        }

        if (paths.Count > 0)
        {
            paths = Sort(paths, args.Text("sort").Trim().ToLowerInvariant(), args.Bool("descending"));

            if (folder.Length == 0)
                folder = Path.GetDirectoryName(paths[0]) ?? "";
        }

        PathListOutput.Write(ctx, paths);
        ctx.SetOutput("folder", Value.FromText(folder));
    }

    private static List<string> Sort(List<string> paths, string sort, bool descending)
    {
        IEnumerable<string> ordered = sort switch
        {
            "name" => paths.OrderBy(PathListOutput.NameOf, StringComparer.OrdinalIgnoreCase),
            "size" => paths.OrderBy(SafeSize),
            "modified" => paths.OrderBy(SafeModified),
            _ => paths,
        };

        if (descending && sort != "none") ordered = ordered.Reverse();
        return ordered.ToList();
    }

    private static long SafeSize(string path)
    {
        try { return File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    private static DateTime SafeModified(string path)
    {
        try
        {
            if (File.Exists(path)) return File.GetLastWriteTime(path);
            if (Directory.Exists(path)) return Directory.GetLastWriteTime(path);
        }
        catch { /* 无权限按最早处理 */ }
        return DateTime.MinValue;
    }
}

/// <summary>读取资源管理器当前打开的文件夹路径。</summary>
public sealed class ExplorerPathUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.explorerPath",
        Name = "资源管理器路径",
        Category = "文件",
        Icon = "folder",
        Description = "读取前台资源管理器窗口的当前文件夹（另有全部已打开窗口的路径）",
        Kind = UnitKind.pure,
        Outputs = new[]
        {
            new OutputSpec("path", "当前目录", ValueKind.text, "explorerPath",
                Description: "前台资源管理器窗口的文件夹；前台不是资源管理器时为空"),
            new OutputSpec("all", "全部打开的目录", ValueKind.list,
                Description: "所有资源管理器窗口的当前文件夹（去重）"),
            new OutputSpec("count", "数量", ValueKind.number, Description: "全部打开的目录个数"),
            new OutputSpec("found", "找到了", ValueKind.@bool, Description: "前台是否是资源管理器"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var selection = ShellBridge.Foreground();
        var all = ShellBridge.AllFolders();

        string path = selection?.Folder ?? "";
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("all", Value.FromList(all.Select(item => Value.FromText(item))));
        ctx.SetOutput("count", Value.FromNumber(all.Count));
        ctx.SetOutput("found", Value.FromBool(path.Length > 0));
    }
}
