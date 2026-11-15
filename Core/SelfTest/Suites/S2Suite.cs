using System.Security.Cryptography;
using System.Text;
using LTools.Core.Actions;
using LTools.Features.Units.Data;
using LTools.Features.Units.Input;
using LTools.Features.Units.Text;

namespace LTools.Core.SelfTest;

/// <summary>
/// S2（P0 两项）：哈希标准向量、加解密往返、声明（含 <c>OptionParams</c> 收起 iv）、等待类语义。
/// 只跑纯函数与声明，不碰键鼠 / 窗口 / 剪贴板 / 网络 / 用户配置。
/// </summary>
internal static class S2Suite
{
    public static void Register(List<SelfTestCase> cases)
    {
        cases.Add(new("s2", "text.hash 声明与标准向量", HashVectors));
        cases.Add(new("s2", "crypto 声明：OptionParams 收起 iv", CryptoDeclarations));
        cases.Add(new("s2", "AES / DES 加解密往返", SymmetricRoundTrip));
        cases.Add(new("s2", "RSA 加解密往返", RsaRoundTrip));
        cases.Add(new("s2", "密钥 / 初始向量不对按前置条件报错", CryptoPreconditions));
        cases.Add(new("s2", "input.waitClipboard 声明与等待类语义", WaitClipboard));
    }

    private static void HashVectors()
    {
        // RFC 1321 / FIPS 180 的标准向量（"abc"）。
        Check.Equal("900150983cd24fb0d6963f7d28e17f72",
            HashUnit.Compute("abc", "md5", "", "hex"), "MD5(abc)");
        Check.Equal("a9993e364706816aba3e25717850c26c9cd0d89d",
            HashUnit.Compute("abc", "sha1", "", "hex"), "SHA1(abc)");
        Check.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            HashUnit.Compute("abc", "sha256", "", "hex"), "SHA256(abc)");
        Check.Equal(
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a" +
            "2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
            HashUnit.Compute("abc", "sha512", "", "hex"), "SHA512(abc)");

        string expectedBase64 = Convert.ToBase64String(
            Convert.FromHexString("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"));
        Check.Equal(expectedBase64,
            HashUnit.Compute("abc", "sha256", "", "base64"), "Base64 输出格式");

        string hmac = HashUnit.Compute("abc", "sha256", "key", "hex");
        Check.Equal(64, hmac.Length, "HMAC-SHA256 十六进制长度");
        Check.NotEqual(HashUnit.Compute("abc", "sha256", "", "hex"), hmac, "HMAC 与纯摘要不同");

        UnitSpec spec = StepRegistry.Default.Get("text.hash").Spec;
        Check.Equal(UnitKind.pure, spec.Kind, "text.hash 是纯动作单元");
        spec.Validate();
    }

    private static void CryptoDeclarations()
    {
        foreach (string key in new[] { "crypto.encrypt", "crypto.decrypt" })
        {
            UnitSpec spec = StepRegistry.Default.Get(key).Spec;
            spec.Validate();

            InputSpec algorithm = spec.Inputs.Single(i => i.Key == "algorithm");
            Check.True(algorithm.OptionParams != null, $"{key} 用 OptionParams 收起 iv");
            Check.True(algorithm.OptionParams!["aes"].Contains("iv"), $"{key} AES 下显示 iv");
            Check.True(algorithm.OptionParams!["des"].Contains("iv"), $"{key} DES 下显示 iv");
            Check.False(algorithm.OptionParams!["rsa"].Contains("iv"), $"{key} RSA 下不显示 iv");

            foreach (string option in algorithm.Options!)
                Check.False(string.Equals(OptionLabels.For(algorithm, option), option, StringComparison.Ordinal),
                    $"{key} 的「{option}」有中文名");
        }
    }

    private static void SymmetricRoundTrip()
    {
        const string plain = "hello 中文";
        const string aesKey = "0123456789abcdef";   // 16 字节
        const string aesIv = "abcdef0123456789";    // 16 字节
        const string desKey = "12345678";           // 8 字节
        const string desIv = "87654321";            // 8 字节

        foreach (var (algorithm, key, iv) in new[]
                 { ("aes", aesKey, aesIv), ("des", desKey, desIv) })
        {
            byte[] cipher = CryptoAlgorithms.Encrypt(plain, algorithm, key, iv, "utf8");
            string base64 = Convert.ToBase64String(cipher);
            Check.Equal(plain, CryptoAlgorithms.Decrypt(base64, algorithm, key, iv, "utf8"),
                $"{algorithm} 往返");

            // hex 密钥 / 初始向量也能用（给精确字节）。
            string hexKey = Convert.ToHexString(Encoding.UTF8.GetBytes(key));
            string hexIv = Convert.ToHexString(Encoding.UTF8.GetBytes(iv));
            byte[] cipher2 = CryptoAlgorithms.Encrypt(plain, algorithm, hexKey, hexIv, "hex");
            Check.Equal(base64, Convert.ToBase64String(cipher2), $"{algorithm} hex 与 utf8 结果一致");
        }
    }

    private static void RsaRoundTrip()
    {
        using var rsa = RSA.Create(2048);
        string publicPem = rsa.ExportSubjectPublicKeyInfoPem();
        string privatePem = rsa.ExportPkcs8PrivateKeyPem();

        byte[] cipher = CryptoAlgorithms.Encrypt("hello", "rsa", publicPem, "", "utf8");
        Check.Equal("hello",
            CryptoAlgorithms.Decrypt(Convert.ToBase64String(cipher), "rsa", privatePem, "", "utf8"),
            "RSA 往返（PEM 公钥加密 / 私钥解密）");
    }

    private static void CryptoPreconditions()
    {
        ActionException badKey = Check.Throws<ActionException>(
            () => CryptoAlgorithms.Encrypt("x", "aes", "short", "abcdef0123456789", "utf8"),
            "AES 密钥太短应报错");
        Check.Equal(ActionErrorKind.precondition, badKey.Kind, "密钥长度不对标前置条件");

        ActionException badIv = Check.Throws<ActionException>(
            () => CryptoAlgorithms.Encrypt("x", "aes", "0123456789abcdef", "short", "utf8"),
            "AES 初始向量太短应报错");
        Check.Equal(ActionErrorKind.precondition, badIv.Kind, "IV 长度不对标前置条件");

        ActionException badCipher = Check.Throws<ActionException>(
            () => CryptoAlgorithms.Decrypt("不是Base64!!", "aes", "0123456789abcdef", "abcdef0123456789", "utf8"),
            "非法 Base64 密文应报错");
        Check.Equal(ActionErrorKind.precondition, badCipher.Kind, "密文损坏标前置条件");

        Check.Throws<ActionException>(
            () => HashUnit.Compute("x", "unknown", "", "hex"), "未知哈希算法应报错");
    }

    private static void WaitClipboard()
    {
        Check.False(WaitClipboardUnit.Changed("a", "a"), "内容相同不算变化");
        Check.True(WaitClipboardUnit.Changed("a", "b"), "内容不同算变化");
        Check.True(WaitClipboardUnit.Changed("", "x"), "空 → 有内容算变化");

        Check.Equal(0, Timeouts.Normalize(0), "timeoutMs=0 只判一次");
        Check.Equal(Timeouts.Unlimited, Timeouts.Normalize(-1), "timeoutMs=-1 一直等");
        Check.Equal(Timeouts.MaxMs, Timeouts.Normalize(999_999), "有限值夹到 10 分钟");

        UnitSpec spec = StepRegistry.Default.Get("input.waitClipboard").Spec;
        spec.Validate();
        Check.True(spec.RunsInDryRun, "干跑只判一次");
        Check.False(spec.Inputs.Single(i => i.Key == "timeoutMs").Advanced,
            "timeoutMs 必须是可见参数");
        Check.True(spec.Outputs.Any(o => o.Key == "changed"), "声明 changed 输出");
        Check.True(spec.Outputs.Any(o => o.Key == "found"), "声明 found 输出");
    }
}
