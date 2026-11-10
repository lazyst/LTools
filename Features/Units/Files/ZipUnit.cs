using System.IO.Compression;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>
/// 压缩 / 解压 zip（<c>file.zip</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 压一个文件夹（保留里面的目录结构，不含外层文件夹本身）或单个文件；解压按原样铺到目标目录。
/// 目标已存在且没勾「覆盖」时直接失败——「悄悄合并进旧目录」会让人搞不清里面是哪一版。
/// </para>
/// <para>
/// 走 <see cref="ZipFile"/>，不引入第三方压缩库：zip 够用，rar / 7z 属于外部工具的活。
/// </para>
/// </remarks>
public sealed class ZipUnit : IStepUnit
{
    private static readonly string[] Operations = { "zip", "unzip" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zip"] = "压缩成 zip",
        ["unzip"] = "解压 zip",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.zip",
        Name = "压缩解压",
        Category = "文件",
        Icon = "save",
        Description = "把文件夹或文件压成 zip，或把 zip 解压到目录；目标已存在时按「覆盖」决定",
        Inputs = new[]
        {
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("zip"),
                Options: Operations, Labels: OperationLabels),
            new InputSpec("path", "源", ValueKind.text, InputEditor.text,
                Required: true,
                Pickers: PickerKind.variable | PickerKind.file | PickerKind.folder | PickerKind.inline | PickerKind.clear,
                Description: "压缩：要打包的文件夹或文件；解压：要解开的 .zip"),
            new InputSpec("target", "目标", ValueKind.text, InputEditor.text,
                Pickers: PickerKind.variable | PickerKind.file | PickerKind.folder | PickerKind.inline | PickerKind.clear,
                Description: "压缩：输出的 .zip（留空 = 源旁边同名 .zip）；解压：解到哪个目录（必填）"),
            new InputSpec("overwrite", "覆盖已存在的目标", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "不勾时目标已存在就失败；解压时目标目录非空也算已存在"),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "结果路径", ValueKind.text, "zipPath",
                Description: "压缩：生成的 zip；解压：解到的目录"),
            new OutputSpec("count", "条目数", ValueKind.number,
                Description: "压缩：打包的文件数；解压：解出的文件数"),
            new OutputSpec("size", "字节数", ValueKind.number,
                Description: "压缩：zip 大小；解压：解出的总字节数"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string op = args.Text("operation").Trim().ToLowerInvariant();
        string source = FileText.Resolve(args.Text("path"), "");
        string target = args.Text("target").Trim();
        bool overwrite = args.Bool("overwrite");

        if (op == "unzip")
        {
            Unzip(ctx, source, target, overwrite);
            return;
        }

        Zip(ctx, source, target, overwrite);
    }

    private static void Zip(StepContext ctx, string source, string target, bool overwrite)
    {
        bool isDir = Directory.Exists(source);
        if (!isDir && !File.Exists(source))
            throw new ActionException($"源不存在：{source}");

        string zipPath = target.Length > 0
            ? FileText.Resolve(target, "")
            : Path.ChangeExtension(source.TrimEnd(Path.DirectorySeparatorChar), ".zip");

        if (File.Exists(zipPath))
        {
            if (!overwrite) throw new ActionException($"目标已存在：{zipPath}（要覆盖请勾上「覆盖」）");
            File.Delete(zipPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(zipPath) ?? ".");

        int count;
        if (isDir)
        {
            // includeBaseDirectory: false —— 解压出来直接是里面的内容，不会多套一层文件夹。
            ZipFile.CreateFromDirectory(source, zipPath, CompressionLevel.Optimal, false);
            count = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();
        }
        else
        {
            using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
            count = 1;
        }

        ctx.SetOutput("path", Value.FromText(zipPath));
        ctx.SetOutput("count", Value.FromNumber(count));
        ctx.SetOutput("size", Value.FromNumber(new FileInfo(zipPath).Length));
    }

    private static void Unzip(StepContext ctx, string zipPath, string target, bool overwrite)
    {
        if (!File.Exists(zipPath)) throw new ActionException($"文件不存在：{zipPath}");
        if (target.Length == 0) throw new ActionException("解压要填目标目录");

        string dir = FileText.Resolve(target, "");
        bool nonEmpty = Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();
        if (nonEmpty && !overwrite)
            throw new ActionException($"目标目录不为空：{dir}（要合并进去请勾上「覆盖」）");

        int count;
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            count = archive.Entries.Count(e => e.Name.Length > 0);
            archive.ExtractToDirectory(dir, overwrite);
        }

        long size = 0;
        foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { size += new FileInfo(file).Length; } catch { /* 单个文件取不到大小不影响结果 */ }
        }

        ctx.SetOutput("path", Value.FromText(dir));
        ctx.SetOutput("count", Value.FromNumber(count));
        ctx.SetOutput("size", Value.FromNumber(size));
    }
}
