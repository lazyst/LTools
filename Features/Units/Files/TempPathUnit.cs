using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>生成一个还没被占用的临时文件路径（不创建文件）。</summary>
public sealed class TempPathUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "file.tempPath",
        Name = "临时文件路径",
        Category = "文件",
        Icon = "save",
        Description = "生成一个临时文件路径（只给路径，不创建文件）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("ext", "扩展名", ValueKind.text,
                Default: Value.FromText(".tmp"),
                Description: "例如 .txt / .png；不填就是 .tmp（只生成路径，不创建文件）"),
        },
        Outputs = new[]
        {
            new OutputSpec("path", "路径", ValueKind.text, "tempPath", Description: "临时目录下的完整路径"),
            new OutputSpec("folder", "临时目录", ValueKind.text, Description: "系统临时目录"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string folder = Path.GetTempPath();
        string ext = NormalizeExtension(args.Text("ext"));

        string path;
        do
        {
            path = Path.Combine(folder, $"ltools-{DateTime.Now:yyyyMMdd-HHmmss}-{IdFactory.ShortId(4)}" + ext);
        }
        while (File.Exists(path) || Directory.Exists(path));

        ctx.SetOutput("path", Value.FromText(path));
        // folder 去掉末尾的分隔符，与「路径信息 / 拆解路径」的 dir、以及「系统文件夹」的 temp 一致；
        // 否则用户写 ${folder}\a.txt 会拼出双反斜杠（能用但很难看）。
        ctx.SetOutput("folder", Value.FromText(Path.TrimEndingDirectorySeparator(folder)));
    }

    private static string NormalizeExtension(string raw)
    {
        string ext = (raw ?? "").Trim();
        if (ext.Length == 0) return ".tmp";
        if (ext.Contains('\\') || ext.Contains('/') || ext.Contains(':'))
            throw new ActionException($"扩展名里不能带路径分隔符（当前 {ext}）");
        return ext.StartsWith('.') ? ext : "." + ext;
    }
}

/// <summary>系统常用文件夹路径（桌面 / 下载 / 文档 / 临时 / AppData…）。</summary>
