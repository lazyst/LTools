using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 加密文本（<c>crypto.encrypt</c>）：AES / DES 对称加密，或 RSA（PEM 公钥）。
/// </summary>
/// <remarks>
/// 与 <c>text.hash</c> / <c>crypto.decrypt</c> 拆开，别做一个 <c>operation</c> 三合一单元。
/// 「初始向量」由 <c>OptionParams</c> 收起：只在 AES / DES 下显示，RSA 不需要。
/// </remarks>
public sealed class CryptoEncryptUnit : IStepUnit
{
    private static readonly Dictionary<string, string> AlgorithmLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aes"] = "AES",
        ["des"] = "DES",
        ["rsa"] = "RSA（PEM 公钥）",
    };

    private static readonly Dictionary<string, string> EncodingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf8"] = "UTF-8 文本",
        ["hex"] = "十六进制",
        ["base64"] = "Base64",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "crypto.encrypt",
        Name = "加密",
        Category = "文本与数据",
        Icon = "script",
        Description = "AES / DES / RSA 加密文本；输出 Base64 密文",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("text", "明文", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "要加密的文本；可写 ${变量}"),
            new InputSpec("algorithm", "算法", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("aes"),
                Options: CryptoAlgorithms.Algorithms, Labels: AlgorithmLabels,
                OptionParams: OptionMap.Of(
                    ("aes", ["iv"]),
                    ("des", ["iv"]),
                    ("rsa", Array.Empty<string>())),
                Description: "AES / DES 为对称加密（需初始向量）；RSA 的「密钥」填 PEM 公钥"),
            new InputSpec("key", "密钥", ValueKind.text, Required: true,
                Description: "AES：16/24/32 字节；DES：8 字节；RSA：PEM 公钥文本。按「密钥格式」解码"),
            new InputSpec("iv", "初始向量", ValueKind.text, Required: true,
                Description: "AES 16 字节 / DES 8 字节；按「密钥格式」解码。RSA 不需要"),
            new InputSpec("keyEncoding", "密钥格式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: CryptoAlgorithms.KeyEncodings,
                Labels: EncodingLabels, Advanced: true,
                Description: "utf8 = 直接按文本取字节（AES 需 16/24/32 字符）；hex / base64 给精确字节"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "密文", ValueKind.text, "encrypted", Description: "Base64 密文"),
            new OutputSpec("hex", "十六进制", ValueKind.text, Description: "小写十六进制密文"),
            new OutputSpec("found", "成功", ValueKind.@bool, Description: "是否加密成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        byte[] cipher = CryptoAlgorithms.Encrypt(
            args.Text("text"),
            args.Text("algorithm").Trim().ToLowerInvariant(),
            args.Text("key"),
            args.Text("iv"),
            args.Text("keyEncoding").Trim().ToLowerInvariant());

        ctx.SetOutput("text", Value.FromText(Convert.ToBase64String(cipher)));
        ctx.SetOutput("hex", Value.FromText(Convert.ToHexString(cipher).ToLowerInvariant()));
        ctx.SetOutput("found", Value.FromBool(true));
    }
}
