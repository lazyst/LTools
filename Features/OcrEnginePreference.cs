using LTools.Features.CapturePro;

namespace LTools.Features;

/// <summary>
/// 新建「文字识别」取值时预选的引擎：由全局模型档（<c>text_recognition/model_type</c>）派生。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>text_recognition/model_type</c>：设置里选的是全局模型档
/// （<c>extra_small</c> / <c>small</c> / <c>medium</c>），新建 OCR 步骤时把它翻成对应的引擎键
/// （<c>v6tiny</c> / <c>v6small</c> / <c>v6medium</c>）。
/// </para>
/// <para>
/// 该档模型还没就位（只有中档需要下载）时退回随包的超小档，连超小档都缺失才退回系统 OCR，
/// 保证新步骤总能跑。**已经保存过的 OCR 步骤保留各自的引擎**，不会因为改了全局档位而被改写。
/// </para>
/// </remarks>
internal static class OcrEnginePreference
{
    /// <summary>新建「文字识别」取值时预选的引擎；null = 系统 OCR。</summary>
    public static string? DefaultForNewProbe =>
        TextRecognitionStore.EngineForModelType(TextRecognitionStore.ModelType);
}
