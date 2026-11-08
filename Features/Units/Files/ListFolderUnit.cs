using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>列出文件夹内容——「从磁盘造出一个列表」的唯一入口。</summary>
public sealed class ListFolderUnit : IStepUnit
{
    private static readonly string[] Includes = { "files", "folders", "both" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.list",
        Name = "列出文件夹",
        Category = "文件",
        Icon = "folder",
        Description = "按通配符列出目录里的文件 / 子文件夹（可递归）；「最多项数」填 0 = 不限",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "目录", ValueKind.text, InputEditor.folder, Required: true,
                Description: "要列出的文件夹；可写 ${变量}"),
            new InputSpec("pattern", "通配符", ValueKind.text, Default: Value.FromText("*"),
                Description: "多个用分号分隔，例如 *.txt;*.log；留空 = 全部"),
            new InputSpec("include", "包含", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("files"), Options: Includes,
                Description: "只要文件 / 只要子文件夹 / 都要"),
            new InputSpec("recursive", "包含子文件夹", ValueKind.@bool, InputEditor.boolean,
                Description: "连子文件夹里的内容一起列（大目录会慢）"),
            new InputSpec("limit", "最多项数", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(1000),
                Description: "超出就停下并置 truncated；填 0 = 不限"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder,
                Description: "「目录」填相对路径时的基准目录"),
        },
        Outputs = new[]
        {
            new OutputSpec("list", "列表", ValueKind.list, "files",
                Description: "匹配到的完整路径，直接给「遍历列表」用"),
            new OutputSpec("count", "数量", ValueKind.number, Description: "实际取到的项数"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "每行一个路径，方便拼进文本 / 命令"),
            new OutputSpec("truncated", "被截断", ValueKind.@bool,
                Description: "超出「最多项数」而没有全部返回时为真"),
            new OutputSpec("folder", "实际目录", ValueKind.text, Description: "解析后的目录绝对路径"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string root = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        if (!Directory.Exists(root))
            throw new ActionException($"目录不存在：{root}");

        string include = args.Text("include").Trim().ToLowerInvariant();
        if (Array.IndexOf(Includes, include) < 0)
            throw new ActionException($"「包含」只能是 files / folders / both（当前 {include}）");

        int limit = (int)Math.Round(args.Number("limit"));
        if (limit < 0) throw new ActionException($"「最多项数」不能是负数（当前 {limit}）");

        string patterns = args.Text("pattern").Trim();
        string[] globs = (patterns.Length == 0 ? "*" : patterns)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (globs.Length == 0) globs = new[] { "*" };

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = args.Bool("recursive"),
            AttributesToSkip = 0,
            MatchCasing = MatchCasing.CaseInsensitive,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();
        bool truncated = false;

        try
        {
            foreach (string glob in globs)
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(root, glob, options))
                {
                    if (!seen.Add(entry)) continue;

                    bool isFolder;
                    try { isFolder = (File.GetAttributes(entry) & FileAttributes.Directory) != 0; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                    bool keep = include switch
                    {
                        "files" => !isFolder,
                        "folders" => isFolder,
                        _ => true,
                    };
                    if (!keep) continue;

                    if (limit > 0 && found.Count >= limit)
                    {
                        truncated = true;
                        break;
                    }

                    found.Add(entry);
                }

                if (truncated) break;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new ActionException($"列目录失败：{ex.Message}", ex);
        }

        found.Sort(StringComparer.OrdinalIgnoreCase);

        ctx.SetOutput("list", Value.FromList(found.Select(path => Value.FromText(path))));
        ctx.SetOutput("count", Value.FromNumber(found.Count));
        ctx.SetOutput("text", Value.FromText(string.Join("\n", found)));
        ctx.SetOutput("truncated", Value.FromBool(truncated));
        ctx.SetOutput("folder", Value.FromText(root));
    }
}

/// <summary>文件文本读写的编码选择与路径解析。</summary>
