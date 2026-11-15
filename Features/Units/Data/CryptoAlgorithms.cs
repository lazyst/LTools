using System.Security.Cryptography;
using System.Text;
using LTools.Core.Actions;

namespace LTools.Features.Units.Data;

/// <summary>
/// <c>crypto.encrypt</c> / <c>crypto.decrypt</c> 共用的纯计算（不碰 WPF / 配置 / 键鼠）。
/// 对称算法要求精确字节长度的密钥与 IV；RSA 的 <c>key</c> 填 PEM 公钥 / 私钥。
/// </summary>
internal static class CryptoAlgorithms
{
    public static readonly string[] Algorithms = { "aes", "des", "rsa" };
    public static readonly string[] KeyEncodings = { "utf8", "hex", "base64" };

    public static byte[] DecodeKey(string text, string encoding)
    {
        try
        {
            return encoding switch
            {
                "hex" => Convert.FromHexString(text ?? ""),
                "base64" => Convert.FromBase64String(text ?? ""),
                _ => Encoding.UTF8.GetBytes(text ?? ""),
            };
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            throw ActionErrors.Precondition($"密钥按 {encoding} 解码失败：{ex.Message}", ex);
        }
    }

    public static byte[] Encrypt(string text, string algorithm, string key, string iv, string keyEncoding)
    {
        CheckAlgorithm(algorithm);
        byte[] data = Encoding.UTF8.GetBytes(text ?? "");
        return algorithm == "rsa"
            ? Rsa(data, key, encrypt: true)
            : Symmetric(data, algorithm, key, iv, keyEncoding, encrypt: true);
    }

    public static string Decrypt(string text, string algorithm, string key, string iv, string keyEncoding)
    {
        CheckAlgorithm(algorithm);
        byte[] data;
        try { data = Convert.FromBase64String(text ?? ""); }
        catch (Exception ex) when (ex is FormatException)
        {
            throw ActionErrors.Precondition("密文不是合法的 Base64", ex);
        }

        byte[] plain = algorithm == "rsa"
            ? Rsa(data, key, encrypt: false)
            : Symmetric(data, algorithm, key, iv, keyEncoding, encrypt: false);
        return Encoding.UTF8.GetString(plain);
    }

    private static void CheckAlgorithm(string algorithm)
    {
        if (Array.IndexOf(Algorithms, algorithm) < 0)
            throw ActionErrors.Precondition($"加密算法只能是 aes / des / rsa（当前 {algorithm}）");
    }

    private static byte[] Symmetric(byte[] data, string algorithm, string key, string iv,
        string keyEncoding, bool encrypt)
    {
        byte[] keyBytes = DecodeKey(key, keyEncoding);
        byte[] ivBytes = DecodeKey(iv, keyEncoding);

        int[] allowedKeys = algorithm == "des" ? new[] { 8 } : new[] { 16, 24, 32 };
        if (Array.IndexOf(allowedKeys, keyBytes.Length) < 0)
            throw ActionErrors.Precondition(
                $"{Label(algorithm)} 密钥需 {string.Join(" / ", allowedKeys)} 字节（当前 {keyBytes.Length}）；" +
                "可把「密钥格式」换成 hex / base64 给精确字节");
        int ivLength = algorithm == "des" ? 8 : 16;
        if (ivBytes.Length != ivLength)
            throw ActionErrors.Precondition(
                $"{Label(algorithm)} 的初始向量需 {ivLength} 字节（当前 {ivBytes.Length}）");

        try
        {
            using SymmetricAlgorithm cipher = algorithm == "des" ? DES.Create() : Aes.Create();
            cipher.Key = keyBytes;
            cipher.IV = ivBytes;
            using ICryptoTransform transform = encrypt
                ? cipher.CreateEncryptor()
                : cipher.CreateDecryptor();
            return transform.TransformFinalBlock(data, 0, data.Length);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw ActionErrors.Precondition(
                $"{Label(algorithm)} {(encrypt ? "加密" : "解密")}失败：{ex.Message}", ex);
        }
    }

    private static byte[] Rsa(byte[] data, string key, bool encrypt)
    {
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(key ?? "");
            return encrypt
                ? rsa.Encrypt(data, RSAEncryptionPadding.Pkcs1)
                : rsa.Decrypt(data, RSAEncryptionPadding.Pkcs1);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            throw ActionErrors.Precondition(
                $"RSA {(encrypt ? "加密" : "解密")}失败（key 要填 PEM 公钥 / 私钥）：{ex.Message}", ex);
        }
    }

    private static string Label(string algorithm) => algorithm == "des" ? "DES" : "AES";
}
