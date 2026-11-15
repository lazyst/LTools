using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// 解密文本（<c>crypto.decrypt</c>）：AES / DES 对称解密，或 RSA（PEM 私钥）。
/// </summary>
/// <remarks>
/// 失败按 <see cref="ActionErrors.Precondition"/> 标性质（密钥 / 初始向量不对、密文损坏），
/// 不假装成功；失败时仍把已声明的输出写成空值，方便看流程跑到哪一步。
/// </remarks>
public sealed class CryptoDecryptUnit : IStepUnit
{
    private static readonly Dictionary<string, string> AlgorithmLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["aes"] = "AES",
        ["des"] = "DES",
        ["rsa"] = "RSA（PEM 私钥）",
    };

    private static readonly Dictionary<string, string> EncodingLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["utf8"] = "UTF-8 文本",
        ["hex"] = "十六进制",
        ["base64"] = "Base64",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "crypto.decrypt",
        Name = "解密",
        Category = "文本与数据",
        Icon = "script",
        Description = "AES / DES / RSA 解密 Base64 密文，输出明文",
        Kind = UnitKind.pure,
        Inputs = new[]
        {
            new InputSpec("text", "密文", ValueKind.text, InputEditor.multiline, Required: true,
                Description: "「加密」输出的 Base64 密文；可写 ${变量}"),
            new InputSpec("algorithm", "算法", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("aes"),
                Options: CryptoAlgorithms.Algorithms, Labels: AlgorithmLabels,
                OptionParams: OptionMap.Of(
                    ("aes", ["iv"]),
                    ("des", ["iv"]),
                    ("rsa", Array.Empty<string>())),
                Description: "与加密时选同一个算法；RSA 的「密钥」填 PEM 私钥"),
            new InputSpec("key", "密钥", ValueKind.text, Required: true,
                Description: "与加密时相同的密钥；RSA 填 PEM 私钥文本。按「密钥格式」解码"),
            new InputSpec("iv", "初始向量", ValueKind.text, Required: true,
                Description: "与加密时相同的初始向量。RSA 不需要"),
            new InputSpec("keyEncoding", "密钥格式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("utf8"), Options: CryptoAlgorithms.KeyEncodings,
                Labels: EncodingLabels, Advanced: true,
                Description: "与加密时相同；utf8 / hex / base64"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "明文", ValueKind.text, "decrypted", Description: "解密后的文本"),
            new OutputSpec("found", "成功", ValueKind.@bool, Description: "是否解密成功"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        // 先写空值：失败抛异常时变量表仍能看到这一步跑过（与 script.ahk 同口径）。
        ctx.SetOutput("text", Value.FromText(""));
        ctx.SetOutput("found", Value.FromBool(false));

        string plain = CryptoAlgorithms.Decrypt(
            args.Text("text"),
            args.Text("algorithm").Trim().ToLowerInvariant(),
            args.Text("key"),
            args.Text("iv"),
            args.Text("keyEncoding").Trim().ToLowerInvariant());

        ctx.SetOutput("text", Value.FromText(plain));
        ctx.SetOutput("found", Value.FromBool(true));
    }
}
