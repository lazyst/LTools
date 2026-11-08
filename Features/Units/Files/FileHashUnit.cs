using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Units.Files;

/// <summary>算文件的哈希（MD5 / SHA-1 / SHA-256 / CRC32）。</summary>
public sealed class FileHashUnit : IStepUnit
{
    private static readonly string[] Algorithms = { "md5", "sha1", "sha256", "crc32" };

    private static readonly Dictionary<string, string> AlgorithmLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md5"] = "MD5",
        ["sha1"] = "SHA-1",
        ["sha256"] = "SHA-256",
        ["crc32"] = "CRC32",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "file.hash",
        Name = "文件哈希",
        Category = "文件",
        Icon = "search",
        Description = "算文件的 MD5 / SHA-1 / SHA-256 / CRC32（流式读取，大文件也行）",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("path", "文件", ValueKind.text, InputEditor.file, Required: true,
                Description: "要算哈希的文件；可写 ${变量}"),
            new InputSpec("algorithm", "算法", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("sha256"), Options: Algorithms, Labels: AlgorithmLabels,
                Description: "MD5 / SHA-1 / SHA-256 / CRC32（CRC32 与 ZIP 一致）"),
            new InputSpec("workdir", "工作目录", ValueKind.text, InputEditor.folder,
                Description: "「文件」填相对路径时的基准目录；填了绝对路径就不用管"),
        },
        Outputs = new[]
        {
            new OutputSpec("hash", "哈希", ValueKind.text, "hash", Description: "小写十六进制"),
            new OutputSpec("algorithm", "算法", ValueKind.text, Description: "实际使用的算法"),
            new OutputSpec("size", "字节数", ValueKind.number, Description: "文件大小"),
            new OutputSpec("found", "读到了", ValueKind.@bool, Description: "文件是否存在且读取成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string path = FileText.Resolve(args.Text("path"), args.Text("workdir"));
        string algorithm = args.Text("algorithm").Trim().ToLowerInvariant();
        if (Array.IndexOf(Algorithms, algorithm) < 0)
            throw new ActionException($"算法只能是 md5 / sha1 / sha256 / crc32（当前 {algorithm}）");

        if (!File.Exists(path))
        {
            ctx.SetOutput("hash", Value.FromText(""));
            ctx.SetOutput("algorithm", Value.FromText(algorithm));
            ctx.SetOutput("size", Value.FromNumber(0));
            ctx.SetOutput("found", Value.FromBool(false));
            return;
        }

        long size;
        string hash;
        try
        {
            using var stream = File.OpenRead(path);
            size = stream.Length;
            hash = algorithm == "crc32" ? Crc32.Compute(stream) : Hashed(stream, algorithm);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActionException($"读不了文件（{Path.GetFileName(path)}）：{ex.Message}", ex);
        }

        ctx.SetOutput("hash", Value.FromText(hash));
        ctx.SetOutput("algorithm", Value.FromText(algorithm));
        ctx.SetOutput("size", Value.FromNumber(size));
        ctx.SetOutput("found", Value.FromBool(true));
    }

    private static string Hashed(Stream stream, string algorithm)
    {
        using HashAlgorithm hasher = algorithm switch
        {
            "md5" => MD5.Create(),
            "sha1" => SHA1.Create(),
            _ => SHA256.Create(),
        };
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }
}

/// <summary>CRC-32（IEEE 802.3，与 ZIP / Quicker 的 crc32 一致）。</summary>
