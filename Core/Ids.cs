using System.Security.Cryptography;

namespace LTools.Core;

/// <summary>
/// 全项目唯一的 Id 生成器（见 <c>AGENTS.md</c>「Id 只有一处生成器」）。
///
/// 落盘 / 跨文件引用的实体一律用 <see cref="NewId()"/>：ULID = 48 bit 毫秒时间戳 + 80 bit 随机，
/// 编成 26 位小写 Crockford Base32。时间有序、大小写不敏感、不含路径字符，Windows 目录名与
/// JSON 字符串都能直接用；跨备份合并 / 多实例也不靠中心计数器。
///
/// 运行期 / 临时路径用 <see cref="ShortId()"/>：短随机十六进制，不落盘、不当长期主键。
///
/// **不做向后兼容**：旧 <c>a1..aN</c> / <c>sub-xxxxxxxx</c> / 12 位 hex 在读取侧只是普通字符串键，
/// 不解析、不重命名、不写迁移器、不留别名；只有「新生成」的 Id 是新格式。
/// </summary>
internal static class IdFactory
{
    /// <summary>Crockford Base32：去掉 i / l / o / u，避免手写与 OCR 混淆；全小写。</summary>
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    /// <summary>ULID 固定 26 字符。</summary>
    public const int Length = 26;

    /// <summary>
    /// 生成一个新 ULID。<paramref name="isUsed"/> 由调用域提供（内存集合 / 磁盘文件 / 目录），
    /// 命中就重摇，直到不冲突；32 次都撞上说明调用域的判定有问题，直接抛而不是默默覆盖。
    /// </summary>
    public static string NewId(Func<string, bool>? isUsed = null)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string id = Create();
            if (isUsed == null || !isUsed(id)) return id;
        }
        throw new InvalidOperationException("IdFactory：连续 32 次生成的 Id 都撞上已有值，请检查 isUsed 判定");
    }

    /// <summary>运行期短 Id（默认 6 字节 = 12 位小写 hex）。不落盘、不当跨文件引用键。</summary>
    public static string ShortId(int bytes = 6)
    {
        if (bytes is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(bytes));
        Span<byte> buffer = stackalloc byte[bytes];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToHexString(buffer).ToLowerInvariant();
    }

    /// <summary>只认新格式：26 位 Crockford 小写，首字符 0–7（48 bit 时间戳的高位）。</summary>
    public static bool IsValid(string? id)
    {
        if (id is null || id.Length != Length) return false;
        if (Alphabet.IndexOf(id[0]) is < 0 or > 7) return false;
        foreach (char c in id)
            if (Alphabet.IndexOf(c) < 0) return false;
        return true;
    }

    private static string Create()
    {
        // 时间戳部分：10 个字符 = 50 bit，最高字符天然只有 0–7（Unix 毫秒目前 < 2^48）。
        ulong ms = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        // 随机部分：80 bit 大端，编成 16 个字符。
        UInt128 rand = 0;
        foreach (byte b in random) rand = (rand << 8) | b;

        Span<char> chars = stackalloc char[Length];
        // 大端：最高 5 bit 必须落在最左字符。写成 chars[i] = ms >> (5*i) 会把时间戳反着存，
        // 首位变成低位（可能 > 7）、时间序也倒过来，IsValid 连自己生成的 Id 都认不出（冒烟守着）。
        for (int i = 0; i < 10; i++)
            chars[i] = Alphabet[(int)((ms >> (5 * (9 - i))) & 31)];
        for (int i = 0; i < 16; i++)
            chars[10 + i] = Alphabet[(int)((rand >> (5 * (15 - i))) & 31)];
        return new string(chars);
    }
}
