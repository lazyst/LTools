using System.Security.Cryptography;

namespace LTools.Core.Share;

/// <summary>
/// 局域网共享的配置（<c>configs/settings.json</c> 的 <c>Share</c> 段，见 <c>docs/LAN_SHARE.md</c>）。
/// </summary>
/// <remarks>
/// 只有 6 个字段，不单开配置文件：它不是带令牌的本机服务，和 <see cref="Ai.AiSettings"/> 不同。
/// 令牌可选、默认关；关掉不清空，再打开还是同一个地址。
/// </remarks>
public sealed class ShareOptions
{
    /// <summary>默认端口，避开 MCP 的 18766 与 Quicker 的 18765。</summary>
    public const int DefaultPort = 18767;

    /// <summary>令牌长度（10 进制位数），实际存成小写十六进制。</summary>
    public const int TokenHexLength = 16;

    public int Port { get; set; } = DefaultPort;

    /// <summary>上次是否在共享；启动时按它恢复。</summary>
    public bool Running { get; set; }

    /// <summary>是否允许对方发来内容。</summary>
    public bool AllowInbound { get; set; } = true;

    /// <summary>单文件上限（MB）；单次总量硬编码 1 GB。</summary>
    public int MaxUploadMb { get; set; } = 200;

    /// <summary>是否要求访问令牌（地址变成 <c>/s/&lt;令牌&gt;</c>）。</summary>
    public bool RequireToken { get; set; }

    /// <summary>16 位小写十六进制；空 = 还没生成过。关掉开关不清空。</summary>
    public string Token { get; set; } = "";

    /// <summary>钳掉非法值；空的令牌不会被补一个（只有真正打开开关时才生成）。</summary>
    public ShareOptions Normalized() => new()
    {
        Port = Port is >= 1024 and <= 65535 ? Port : DefaultPort,
        Running = Running,
        AllowInbound = AllowInbound,
        MaxUploadMb = Math.Clamp(MaxUploadMb, 1, 4096),
        RequireToken = RequireToken,
        Token = NormalizeToken(Token),
    };

    /// <summary>生成一个新令牌（16 位十六进制 = 64 bit）。</summary>
    public static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenHexLength / 2)).ToLowerInvariant();

    /// <summary>空串保持空；形状不对的旧值换成新的（不兼容旧格式）。</summary>
    public static string NormalizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return "";
        string t = token.Trim().ToLowerInvariant();
        return t.Length == TokenHexLength && t.All(Uri.IsHexDigit) ? t : NewToken();
    }
}
