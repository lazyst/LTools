namespace LTools.Config;

/// <summary>
/// 识别结果的在线翻译配置（<c>configs/settings.json</c> 的 <c>Translation</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshot_translation/*</c> 设置（源语言 / 目标语言 / 模型 / 端点）：
/// 上游自带多家翻译服务（DeepL / DeepLX / 百度 / 有道）与私有云的快速翻译，
/// 这里只留**一个 OpenAI 兼容的 chat completions 端点**（自建网关、代理、本地模型都走它）。
/// </para>
/// <para>
/// 空 <see cref="Endpoint"/> / <see cref="Model"/> = 没配，翻译按钮不假装能翻：
/// 点它直接弹设置框。密钥不写进日志、不进备份之外的地方，只落这一个字段。
/// </para>
/// </remarks>
public sealed class TranslationFile
{
    /// <summary>OpenAI 兼容端点（<c>https://api.openai.com/v1</c>；带不带 <c>/chat/completions</c> 都认）。</summary>
    public string? Endpoint { get; set; }

    /// <summary>模型名（<c>gpt-4o-mini</c> / <c>qwen-plus</c> 之类的对话模型）。</summary>
    public string? Model { get; set; }

    /// <summary>Bearer 密钥（本地无鉴权网关可以留空）。</summary>
    public string? ApiKey { get; set; }

    /// <summary>目标语言键（<c>zh-CN</c> / <c>en</c> / <c>ja</c> …，见 <c>TranslationLanguages</c>）。</summary>
    public string? Target { get; set; }

    /// <summary>源语言键；<c>auto</c>（默认）= 按文字判语言。</summary>
    public string? Source { get; set; }

    /// <summary>单次请求超时（毫秒，1000–120000，默认 30000）。</summary>
    public int TimeoutMs { get; set; } = 30000;
}
