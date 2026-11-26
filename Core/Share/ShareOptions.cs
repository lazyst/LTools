using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace LTools.Core.Share;

/// <summary>对方发来内容时本机的态度（<see cref="ShareOptions.Inbound"/>）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ShareInbound
{
    /// <summary>直接收下：文字进剪贴板 + <c>inbox/</c>，文件落 <c>inbox/</c>。</summary>
    allow,

    /// <summary>一次一确认：每次发来先在本机弹框问，同意才收；文件在同意之后才开始上传。</summary>
    confirm,

    /// <summary>一律不收：浏览器发送区变灰，强行发送 403。</summary>
    block,
}

/// <summary>
/// 局域网共享的配置（<c>configs/settings.json</c> 的 <c>Share</c> 段，见 <c>docs/LAN_SHARE.md</c>）。
/// </summary>
/// <remarks>
/// 只有 7 个字段，不单开配置文件：它不是带令牌的本机服务，和 <see cref="Ai.AiSettings"/> 不同。
/// 令牌可选、默认关；关掉不清空，再打开还是同一个地址。
/// </remarks>
public sealed class ShareOptions
{
    /// <summary>默认端口，避开 MCP 的 18766 与 Quicker 的 18765。</summary>
    public const int DefaultPort = 18767;

    /// <summary>令牌长度（16 进制位数），实际存成小写十六进制。</summary>
    public const int TokenHexLength = 16;

    public int Port { get; set; } = DefaultPort;

    /// <summary>上次是否在共享；启动时按它恢复。</summary>
    public bool Running { get; set; }

    /// <summary>对方发来内容时怎么办：直接收 / 一次一确认 / 不收。</summary>
    public ShareInbound Inbound { get; set; } = ShareInbound.allow;

    /// <summary>单文件上限（MB）；单次总量硬编码 1 GB。</summary>
    public int MaxUploadMb { get; set; } = 200;

    /// <summary>是否要求访问令牌（地址变成 <c>/s/&lt;令牌&gt;</c>）。</summary>
    public bool RequireToken { get; set; }

    /// <summary>16 位小写十六进制；空 = 还没生成过。关掉开关不清空。</summary>
    public string Token { get; set; } = "";

    /// <summary>三档「接收」的界面文案（窗口下拉、右栏说明与日志共用这一份）。</summary>
    public static IReadOnlyList<KeyValuePair<ShareInbound, string>> InboundChoices { get; } =
    [
        new(ShareInbound.allow, "自动接收"),
        new(ShareInbound.confirm, "需要确认"),
        new(ShareInbound.block, "不接受"),
    ];

    /// <summary>某一档的一句话说明（下拉下方那行灰字）。</summary>
    public static string DescribeInbound(ShareInbound inbound) => inbound switch
    {
        ShareInbound.confirm => "对方每次发来内容，本机先弹框问一次；没人点允许就不收。",
        ShareInbound.block => "浏览器发送区变灰，强行发送也会被拒。",
        _ => "对方发来的文字进剪贴板、文件落 inbox/，不用问就收下。",
    };

    /// <summary>日志里那一档的短名。</summary>
    public static string LabelInbound(ShareInbound inbound) => inbound switch
    {
        ShareInbound.confirm => "需确认",
        ShareInbound.block => "不接受",
        _ => "自动",
    };

    /// <summary>钳掉非法值；空的令牌不会被补一个（只有真正打开开关时才生成）。</summary>
    public ShareOptions Normalized() => new()
    {
        Port = Port is >= 1024 and <= 65535 ? Port : DefaultPort,
        Running = Running,
        Inbound = Enum.IsDefined(Inbound) ? Inbound : ShareInbound.allow,
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
