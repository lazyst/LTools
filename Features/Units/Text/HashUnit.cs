using System.Security.Cryptography;
using System.Text;
using LTools.Core.Actions;

namespace LTools.Features.Units.Text;

/// <summary>
/// 算文本的摘要（<c>text.hash</c>）：MD5 / SHA-1 / SHA-256 / SHA-512，填 HMAC 密钥即算 HMAC。
/// </summary>
/// <remarks>
/// 与 <c>file.hash</c> 分开：那个对文件流式哈希，这个对一段文本 / 变量值。
/// 按单一职责拆自 Quicker 的 <c>sys:enc</c>，不做 <c>operation</c> 三合一。
/// </remarks>
public sealed class HashUnit : IStepUnit
{
    private static readonly string[] Algorithms = { "md5", "sha1", "sha256", "sha512" };
    private static readonly string[] Encodings = { "hex", "base64" };

    private static readonly Dictionary<string, string> AlgorithmLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["md5"] = "MD5",
        ["sha1"] = "SHA-1",
        ["sha256"] = "SHA-256",
        ["sha512"] = "SHA-512",
    };

    private static readonly Dictionary<string, string> EncodingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hex"] = "十六进制（小写）",
        ["base64"] = "Base64",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "text.hash",
        Name = "文本哈希",
        Category = "文本与数据",
        Icon = "script",
        Description = "算文本的 MD5 / SHA-1 / SHA-256 / SHA-512；填 HMAC 密钥即算 HMAC",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("text", "文本", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "要算摘要的文本；可写 ${变量}"),
            new InputSpec("algorithm", "算法", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("sha256"), Options: Algorithms, Labels: AlgorithmLabels,
                Description: "MD5 / SHA-1 / SHA-256 / SHA-512"),
            new InputSpec("hmacKey", "HMAC 密钥", ValueKind.text,
                Description: "留空 = 纯摘要；填了就用这个密钥算 HMAC（仍属于「算摘要」）"),
            new InputSpec("encoding", "输出格式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("hex"), Options: Encodings, Labels: EncodingLabels,
                Description: "十六进制（小写）或 Base64"),
        },
        Outputs = new[]
        {
            new OutputSpec("hash", "摘要", ValueKind.text, "hash", Description: "摘要文本（按输出格式）"),
            new OutputSpec("text", "文本", ValueKind.text, Description: "同「摘要」，方便直接当文本用"),
            new OutputSpec("algorithm", "算法", ValueKind.text, Description: "实际使用的算法"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string algorithm = args.Text("algorithm").Trim().ToLowerInvariant();
        string encoding = args.Text("encoding").Trim().ToLowerInvariant();
        string hash = Compute(args.Text("text"), algorithm, args.Text("hmacKey"), encoding);

        ctx.SetOutput("hash", Value.FromText(hash));
        ctx.SetOutput("text", Value.FromText(hash));
        ctx.SetOutput("algorithm", Value.FromText(algorithm));
    }

    /// <summary>纯计算：动作单元与 <c>--selftest</c> 共用，不碰 WPF / 配置 / 键鼠。</summary>
    internal static string Compute(string text, string algorithm, string hmacKey, string encoding)
    {
        if (Array.IndexOf(Algorithms, algorithm) < 0)
            throw ActionErrors.Precondition(
                $"哈希算法只能是 md5 / sha1 / sha256 / sha512（当前 {algorithm}）");
        if (Array.IndexOf(Encodings, encoding) < 0)
            throw ActionErrors.Precondition($"输出格式只能是 hex / base64（当前 {encoding}）");

        byte[] data = Encoding.UTF8.GetBytes(text ?? "");
        byte[] digest;
        if (!string.IsNullOrEmpty(hmacKey))
        {
            byte[] key = Encoding.UTF8.GetBytes(hmacKey);
            using HMAC hmac = algorithm switch
            {
                "md5" => new HMACMD5(key),
                "sha1" => new HMACSHA1(key),
                "sha512" => new HMACSHA512(key),
                _ => new HMACSHA256(key),
            };
            digest = hmac.ComputeHash(data);
        }
        else
        {
            using HashAlgorithm hasher = algorithm switch
            {
                "md5" => MD5.Create(),
                "sha1" => SHA1.Create(),
                "sha512" => SHA512.Create(),
                _ => SHA256.Create(),
            };
            digest = hasher.ComputeHash(data);
        }

        return encoding == "base64"
            ? Convert.ToBase64String(digest)
            : Convert.ToHexString(digest).ToLowerInvariant();
    }
}
