using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features;

/// <summary>
/// 记住上次选用的「文字识别」引擎：新建取值默认用它，不必每加一个 OCR 步骤都重选。
/// </summary>
/// <remarks>
/// <para>
/// 存进 <c>configs/ui.json</c> 的 <see cref="AppConfig.LastOcrEngine"/>（<c>windows</c> / <c>v6tiny</c> / <c>v6small</c>），
/// 与「上次导航页」「超级面板上次页」同一个思路：界面上的选择记一次，下次沿用。
/// </para>
/// <para>
/// 还没选过时默认 PP-OCRv6 快速（随包内置、离线可用、比系统 OCR 准得多）；
/// 内置模型缺失（安装包不完整）才退回系统 OCR，保证新步骤总能跑。
/// 已经保存过的 OCR 步骤保留各自的引擎，不会因为改了全局偏好而被改写。
/// </para>
/// </remarks>
internal static class OcrEnginePreference
{
    private const string Windows = "windows";

    /// <summary>新建「文字识别」取值时预选的引擎；null = 系统 OCR。</summary>
    public static string? DefaultForNewProbe
    {
        get
        {
            string? saved = Load();
            if (!string.IsNullOrEmpty(saved)) return saved == Windows ? null : saved;
            return PaddleOcr.IsReady(PaddleOcr.Tier.Tiny) ? "v6tiny" : null;
        }
    }

    /// <summary>读上次的选择（原始值，含 <c>windows</c>）；没存过返回 null。</summary>
    public static string? Load()
    {
        try
        {
            string? value = AppConfig.Load(ResolveConfigPath()).LastOcrEngine;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex)
        {
            AppLog.Warn("OCR", "读取上次的 OCR 引擎失败，按默认处理：" + ex.Message);
            return null;
        }
    }

    /// <summary>记住这次的选择（值未变不落盘；失败只记日志，不影响编辑）。</summary>
    public static void Save(string? engine)
    {
        string value = engine ?? Windows;
        try
        {
            if (string.Equals(Load(), value, StringComparison.OrdinalIgnoreCase)) return;
            ConfigIO.Modify(ResolveConfigPath(), cfg => cfg.LastOcrEngine = value);
        }
        catch (Exception ex)
        {
            AppLog.Warn("OCR", "记住 OCR 引擎失败：" + ex.Message);
        }
    }

    private static string ResolveConfigPath() =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;
}
