using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LTools.Core;

namespace LTools.Core.Ai;

/// <summary>外部 MCP 的审批模式。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
internal enum AiApprovalMode
{
    /// <summary>只读：即使 <see cref="AiSettings.AllowWrites"/> 打开也不放行写操作。</summary>
    readOnly,

    /// <summary>沙箱（默认）：草稿 CRUD / save / run 放行；采纳与删除由 LTools 弹原生确认。</summary>
    sandbox,

    /// <summary>全部放行（采纳与删除仍写审计）。只建议用户自己临时打开。</summary>
    full,
}

/// <summary>
/// AI 接入配置（<c>ai/mcp.json</c>）：总开关、端口、令牌、写权限与审批模式。
/// </summary>
/// <remarks>
/// <para>
/// 这些字段**不进** <c>LTools.json</c>：它们是一个带令牌的本机服务配置，和用户动作清单是两回事；
/// 而且授权开关只能由用户在设置窗口里改，MCP 工具没有任何写它的入口。
/// </para>
/// <para>
/// 令牌只存本机、设置页可复制 / 重置、日志与 <c>health</c> 回执里永远打码。
/// </para>
/// </remarks>
internal sealed class AiSettings
{
    /// <summary>默认端口。避开 Quicker 的 18765，两个程序可以同时开。</summary>
    public const int DefaultPort = 18766;

    public bool Enabled { get; set; }

    public int Port { get; set; } = DefaultPort;

    /// <summary><c>lt_</c> + 32 字节十六进制；首次读取时自动生成。</summary>
    public string Token { get; set; } = "";

    /// <summary>写操作总闸（默认关）。</summary>
    public bool AllowWrites { get; set; }

    public AiApprovalMode ApprovalMode { get; set; } = AiApprovalMode.sandbox;

    /// <summary>最近一次启动 / 工具调用的错误（只给设置页和 health 看，不含敏感内容）。</summary>
    public string? LastError { get; set; }

    [JsonIgnore]
    public bool WritesAllowed => AllowWrites && ApprovalMode != AiApprovalMode.readOnly;

    [JsonIgnore]
    public string MaskedToken => Mask(Token);

    public static string PathOnDisk => System.IO.Path.Combine(AppPaths.DataRoot, "ai", "mcp.json");

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>读配置；不存在 / 损坏时给默认值，并确保令牌已生成。</summary>
    public static AiSettings Load()
    {
        lock (Gate)
        {
            AiSettings settings;
            try
            {
                string path = PathOnDisk;
                settings = File.Exists(path)
                    ? JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new AiSettings()
                    : new AiSettings();
            }
            catch (Exception ex)
            {
                AppLog.Warn("Ai", $"ai/mcp.json 解析失败，使用默认配置：{ex.Message}");
                settings = new AiSettings();
            }

            settings.Port = NormalizePort(settings.Port);
            if (string.IsNullOrWhiteSpace(settings.Token))
            {
                settings.Token = NewToken();
                settings.Save();
            }

            return settings;
        }
    }

    /// <summary>写回配置（原子替换）。</summary>
    public void Save()
    {
        lock (Gate)
        {
            string path = PathOnDisk;
            string dir = System.IO.Path.GetDirectoryName(path)!;
            System.IO.Directory.CreateDirectory(dir);

            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
    }

    /// <summary>设置页「重置令牌」。</summary>
    public string ResetToken()
    {
        Token = NewToken();
        Save();
        return Token;
    }

    /// <summary>端口规范到 1024–65535；非法值回默认。</summary>
    public static int NormalizePort(int port) =>
        port is >= 1024 and <= 65535 ? port : DefaultPort;

    /// <summary>生成令牌：<c>lt_</c> + 32 字节随机（64 位十六进制）。</summary>
    public static string NewToken() => "lt_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>定长时间的令牌比较（长度不同直接 false，仍不泄露前缀）。</summary>
    public static bool TokenEquals(string? provided, string? expected)
    {
        string a = provided ?? "";
        string b = expected ?? "";
        if (a.Length == 0 || b.Length == 0) return false;

        byte[] left = Encoding.UTF8.GetBytes(a);
        byte[] right = Encoding.UTF8.GetBytes(b);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    /// <summary>打码显示（日志 / health / 截图里都只出现这一种形态）。</summary>
    public static string Mask(string? token)
    {
        string value = (token ?? "").Trim();
        if (value.Length == 0) return "";
        if (value.Length <= 10) return value[..2] + "…";
        return value[..6] + "…" + value[^4..];
    }

    /// <summary>MCP 客户端配置片段（设置页「复制配置」）。</summary>
    public string ClientConfigJson(string kind)
    {
        string url = $"http://127.0.0.1:{Port}/mcp";
        string token = Token;

        return kind.ToLowerInvariant() switch
        {
            "opencode" => $$"""
{
  "mcp": {
    "ltools": {
      "type": "remote",
      "url": "{{url}}",
      "headers": { "Authorization": "Bearer {{token}}" },
      "enabled": true
    }
  }
}
""",
            "claude" => $$"""
{
  "mcpServers": {
    "ltools": {
      "type": "http",
      "url": "{{url}}",
      "headers": { "Authorization": "Bearer {{token}}" }
    }
  }
}
""",
            "codex" => $$"""
[mcp_servers.ltools]
url = "{{url}}"
http_headers = { Authorization = "Bearer {{token}}" }
""",
            // stdio：客户端拉起桥进程，桥自己读本机令牌，所以只要命令 + 参数
            "stdio" => $"\"{Environment.ProcessPath ?? "LTools.exe"}\" --mcp-stdio",
            _ => $$"""
{
  "mcpServers": {
    "ltools": {
      "url": "{{url}}",
      "headers": { "Authorization": "Bearer {{token}}" }
    }
  }
}
""",
        };
    }
}
