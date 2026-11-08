using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

/// <summary>用默认程序打开文件 / 文件夹 / 网址。</summary>
public sealed class OpenPathUnit : IStepUnit
{
    private static readonly string[] Operations = { "file", "folder", "url" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.open",
        Name = "打开",
        Category = "文件",
        Icon = "open",
        Description = "用默认程序打开文件、在资源管理器里打开文件夹，或用浏览器打开网址",
        Inputs = new[]
        {
            new InputSpec("operation", "打开方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("file"), Options: Operations),
            new InputSpec("path", "路径 / 网址", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("target", "实际目标", ValueKind.text, Description: "实际打开的路径或网址"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否成功发起打开"),
            new OutputSpec("value", "完整结果", ValueKind.@object, Description: "整步结果（含以上各项），一般不需要，除非要整体传给子动作或打印调试"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();
        string workdir = AppPaths.ResolveWorkdir(args.Text("workdir"));
        string raw = args.Text("path").Trim();
        if (raw.Length == 0) throw new ActionException("打开的目标为空");

        string target = operation switch
        {
            "folder" => AppPaths.ResolvePath(raw, workdir, allowDirectory: true),
            "url" => raw,
            "file" => AppPaths.ResolvePath(raw, workdir, allowDirectory: false),
            _ => throw new ActionException($"打开方式只能是 file / folder / url（当前 {operation}）"),
        };

        var psi = new ProcessStartInfo(target) { UseShellExecute = true };
        if (operation != "url" && Directory.Exists(workdir))
            psi.WorkingDirectory = workdir;

        Process.Start(psi);
        ctx.SetOutput("target", Value.FromText(target));
        ctx.SetOutput("ok", Value.FromBool(true));
        ctx.SetOutput("value", Value.Object(
            ("text", Value.FromText(target)),
            ("path", Value.FromText(target)),
            ("file", Value.FromText(target)),
            ("found", Value.FromBool(true))));
    }
}

/// <summary>弹出系统的文件 / 文件夹选择框。</summary>
public sealed class SelectFileUnit : IStepUnit
{
    private static readonly string[] Kinds = { "openFile", "saveFile", "folder" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.select",
        Name = "选择文件",
        Category = "文件",
        Icon = "folder",
        Description = "弹出文件 / 文件夹选择框；取消时 found = false",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("kind", "选择", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("openFile"), Options: Kinds),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("filter", "文件类型", ValueKind.text,
                Default: Value.FromText("所有文件|*.*")),
            new InputSpec("initialDir", "起始目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "路径", ValueKind.text, "path", Description: "选中的文件路径；取消时为空"),
            new OutputSpec("found", "已选择", ValueKind.@bool, Description: "是否选了文件"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string kind = args.Text("kind").Trim();
        string title = args.Text("title").Trim();
        string filter = args.Text("filter").Trim();
        string initial = args.Text("initialDir").Trim();

        var (ok, path) = OnUiThread(() =>
        {
            Window? owner = Application.Current?.Windows.OfType<Window>()
                .FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

            if (string.Equals(kind, "folder", StringComparison.OrdinalIgnoreCase))
            {
                var dialog = new OpenFolderDialog();
                if (title.Length > 0) dialog.Title = title;
                if (initial.Length > 0) dialog.InitialDirectory = initial;
                bool picked = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
                return (picked, picked ? dialog.FolderName : "");
            }

            if (string.Equals(kind, "saveFile", StringComparison.OrdinalIgnoreCase))
            {
                var dialog = new SaveFileDialog
                {
                    Title = title.Length > 0 ? title : "保存文件",
                    Filter = filter.Length > 0 ? filter : "所有文件|*.*",
                };
                if (initial.Length > 0) dialog.InitialDirectory = initial;
                bool picked = owner == null ? dialog.ShowDialog() == true : dialog.ShowDialog(owner) == true;
                return (picked, picked ? dialog.FileName : "");
            }

            var open = new OpenFileDialog
            {
                Title = title.Length > 0 ? title : "选择文件",
                Filter = filter.Length > 0 ? filter : "所有文件|*.*",
            };
            if (initial.Length > 0) open.InitialDirectory = initial;
            bool okOpen = owner == null ? open.ShowDialog() == true : open.ShowDialog(owner) == true;
            return (okOpen, okOpen ? open.FileName : "");
        });

        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("found", Value.FromBool(ok));
    }

    private static T OnUiThread<T>(Func<T> work)
    {
        var app = Application.Current;
        if (app == null) return work();
        return app.Dispatcher.Invoke(work);
    }
}

/// <summary>读取文本文件。</summary>
public sealed class ReadFileUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.read",
        Name = "读取文件",
        Category = "文件",
        Icon = "read",
        Description = "读取文本文件；相对路径按数据根 / 工作目录解析",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "路径", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("encoding", "编码", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: FileText.Encodings),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "内容", ValueKind.text, "text", Description: "文件全部内容"),
            new OutputSpec("found", "读到", ValueKind.@bool, Description: "文件是否存在且读取成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        if (!File.Exists(path))
        {
            ctx.SetOutput("text", Value.FromText(""));
            ctx.SetOutput("found", Value.FromBool(false));
            return;
        }

        string text = FileText.Read(path, args.Text("encoding"));
        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("found", Value.FromBool(true));
    }
}

/// <summary>写入文本文件（覆盖 / 追加）。</summary>
public sealed class WriteFileUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.write",
        Name = "写入文件",
        Category = "文件",
        Icon = "save",
        Description = "把文本写入文件；目录不存在时自动创建",
        Inputs = new[]
        {
            new InputSpec("path", "路径", ValueKind.text, InputEditor.file, Required: true),
            new InputSpec("text", "内容", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("append", "追加", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false)),
            new InputSpec("encoding", "编码", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: FileText.Encodings),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "实际路径", ValueKind.text, "path", Description: "实际写入的文件路径"),
            new OutputSpec("ok", "成功", ValueKind.@bool, Description: "是否写入成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        FileText.Write(path, args.Text("text"), args.Text("encoding"), args.Bool("append"));
        ctx.SetOutput("path", Value.FromText(path));
        ctx.SetOutput("ok", Value.FromBool(true));
    }
}

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
public sealed class PathInfoUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.exists",
        Name = "路径信息",
        Category = "文件",
        Icon = "info",
        Description = "判断路径是否存在，取文件 / 目录类型、大小与修改时间",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "路径", ValueKind.text, InputEditor.file, Required: true,
                Description: "文件或文件夹路径；可写 ${变量}"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder,
                Description: "「路径」填相对路径时的基准目录"),
        },
        Outputs = new[]
        {
            new OutputSpec("exists", "存在", ValueKind.@bool, "exists", Description: "路径是否存在"),
            new OutputSpec("isFile", "是文件", ValueKind.@bool, Description: "是不是文件"),
            new OutputSpec("isFolder", "是目录", ValueKind.@bool, Description: "是不是目录"),
            new OutputSpec("size", "字节数", ValueKind.number, Description: "文件大小（字节）"),
            new OutputSpec("modified", "修改时间", ValueKind.text, Description: "最后修改时间"),
            new OutputSpec("createTime", "创建时间", ValueKind.text, Description: "创建时间"),
            new OutputSpec("isReadOnly", "只读", ValueKind.@bool, Description: "是否只读"),
            new OutputSpec("isHidden", "隐藏", ValueKind.@bool, Description: "是否隐藏"),
            new OutputSpec("isSystem", "系统", ValueKind.@bool, Description: "是否系统文件"),
            new OutputSpec("fileCount", "文件数", ValueKind.number,
                Description: "目录里顶层文件个数（文件恒为 0；不含子目录）"),
            new OutputSpec("totalSize", "总字节数", ValueKind.number,
                Description: "目录里顶层文件总大小（文件恒为 0；不含子目录）"),
            new OutputSpec("lnkTarget", "快捷方式目标", ValueKind.text,
                Description: "是 .lnk 时给出它指向的路径"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        bool isFile = File.Exists(path);
        bool isFolder = Directory.Exists(path);
        bool exists = isFile || isFolder;

        ctx.SetOutput("exists", Value.FromBool(exists));
        ctx.SetOutput("isFile", Value.FromBool(isFile));
        ctx.SetOutput("isFolder", Value.FromBool(isFolder));

        if (!exists)
        {
            ctx.SetOutput("size", Value.FromNumber(0));
            ctx.SetOutput("modified", Value.FromText(""));
            ctx.SetOutput("createTime", Value.FromText(""));
            ctx.SetOutput("isReadOnly", Value.FromBool(false));
            ctx.SetOutput("isHidden", Value.FromBool(false));
            ctx.SetOutput("isSystem", Value.FromBool(false));
            ctx.SetOutput("fileCount", Value.FromNumber(0));
            ctx.SetOutput("totalSize", Value.FromNumber(0));
            ctx.SetOutput("lnkTarget", Value.FromText(""));
            return;
        }

        FileAttributes attributes;
        DateTime created;
        DateTime modified;
        long size = 0;
        int fileCount = 0;
        long totalSize = 0;

        if (isFile)
        {
            var info = new FileInfo(path);
            attributes = info.Attributes;
            created = info.CreationTime;
            modified = info.LastWriteTime;
            size = info.Length;
        }
        else
        {
            var info = new DirectoryInfo(path);
            attributes = info.Attributes;
            created = info.CreationTime;
            modified = info.LastWriteTime;

            try
            {
                foreach (var file in info.EnumerateFiles())
                {
                    fileCount++;
                    totalSize += file.Length;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无权限的子目录按空处理
            }
        }

        ctx.SetOutput("size", Value.FromNumber(size));
        ctx.SetOutput("modified", Value.FromText(Format(modified)));
        ctx.SetOutput("createTime", Value.FromText(Format(created)));
        ctx.SetOutput("isReadOnly", Value.FromBool((attributes & FileAttributes.ReadOnly) != 0));
        ctx.SetOutput("isHidden", Value.FromBool((attributes & FileAttributes.Hidden) != 0));
        ctx.SetOutput("isSystem", Value.FromBool((attributes & FileAttributes.System) != 0));
        ctx.SetOutput("fileCount", Value.FromNumber(fileCount));
        ctx.SetOutput("totalSize", Value.FromNumber(totalSize));
        ctx.SetOutput("lnkTarget", Value.FromText(isFile ? ResolveShortcut(path) : ""));
    }

    private static string Format(DateTime time) =>
        time == DateTime.MinValue ? "" : time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>解析 .lnk 指向的路径；不是快捷方式 / 解析失败返回空串。</summary>
    private static string ResolveShortcut(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase))
            return "";

        object? shell = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return "";

            shell = Activator.CreateInstance(type);
            if (shell == null) return "";

            dynamic link = ((dynamic)shell).CreateShortcut(path);
            return (string?)link.TargetPath ?? "";
        }
        catch (Exception ex)
        {
            AppLog.Warn("File", $"解析快捷方式失败（{path}）：{ex.Message}");
            return "";
        }
        finally
        {
            try
            {
                if (shell != null && System.Runtime.InteropServices.Marshal.IsComObject(shell))
                    System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch { /* 已释放 */ }
        }
    }
}

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
internal static class FileText
{
    public static readonly string[] Encodings = { "utf8", "utf8bom", "unicode", "gbk", "auto" };

    public static string Resolve(string path, string workdir)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) throw new ActionException("文件路径为空");

        string wd = AppPaths.ResolveWorkdir(workdir);
        return Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(wd, path));
    }

    public static string Read(string path, string encoding) => File.ReadAllText(path, GetEncoding(encoding));

    public static void Write(string path, string text, string encoding, bool append)
    {
        var enc = GetEncoding(encoding);
        if (append) File.AppendAllText(path, text, enc);
        else File.WriteAllText(path, text, enc);
    }

    private static Encoding GetEncoding(string name)
    {
        switch ((name ?? "").Trim().ToLowerInvariant())
        {
            case "utf8bom":
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
            case "unicode":
                return Encoding.Unicode;
            case "gbk":
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    return Encoding.GetEncoding(936);
                }
                catch
                {
                    return Encoding.UTF8;
                }
            case "auto":
                // UTF-8 读取时 StreamReader 会按 BOM 自动切换（UTF-8 / UTF-16）。
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            default:
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
    }
}
