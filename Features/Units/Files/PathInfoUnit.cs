using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.Win32;

namespace LTools.Features.Units.Files;

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
