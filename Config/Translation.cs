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

    /// <summary>
    /// 译文画进原图（照上游 <c>screenshot_translation/original_image_translation</c>）：
    /// 打开后译文直接铺在截图的原图上（原文字被逐行底色盖住），适合把结果当图片看 / 贴走。
    /// </summary>
    public bool OriginalImage { get; set; }

    /// <summary>
    /// 复制译文时复制成图片（照上游 <c>screenshot_translation/copy_translation_results_as_image</c>）：
    /// 关着复制纯文本，打开把渲染好的图（原图 + 译文）放进剪贴板。
    /// </summary>
    public bool CopyAsImage { get; set; }

    /// <summary>
    /// 翻译前怎么处理断行（照上游 <c>screenshot_translation/layout_processing</c>）：
    /// <c>smart_merge</c>（默认）= 先按版面接成段落再翻，<c>original</c> = 逐行翻。
    /// </summary>
    public string? LayoutProcessing { get; set; }
}
