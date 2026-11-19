using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>PNG 的压缩力度（照 Snow Shot 的 <c>screenshot/compression_level</c>）。</summary>
internal enum ExportCompression
{
    Low,
    Medium,
    High,
}

/// <summary>
/// 「截图输出」设置的运行期缓存 + 读写入口（<see cref="ExportFile"/> 是磁盘形状）。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 <c>screenshot/image_format</c> / <c>image_quality</c> /
/// <c>compression_level</c> / <c>image_save_directory</c> / <c>*_filename_format</c> /
/// <c>save_as_file_dialog</c> / <c>show_save_result</c> / <c>copy_image_file_to_clipboard</c> /
/// <c>auto_save_after_execution_actions</c>。启动时 <see cref="Apply"/> 灌进静态字段，
/// 设置页改值走 <see cref="Save"/>（只写 settings 域、后台落盘）。
/// </para>
/// <para>
/// 这里只做**规则**（解析、夹取、默认值、场景判定）；真正写文件在 <see cref="CaptureSave"/>，
/// 文件名模板展开在 <see cref="ExportNames"/>，PNG 编码在 <see cref="PngWriter"/>。
/// </para>
/// </remarks>
internal static class ExportSettings
{
    /// <summary>默认文件名模板（照上游 <c>SnowShot_{YYYY-MM-DD_HH-mm-ss}</c> 的形状）。</summary>
    public const string DefaultNameFormat = "截图_{YYYY-MM-DD_HH-mm-ss}";

    // 「执行完自动保存」的场景键（与上游 screenshot/auto_save_after_execution_actions 一致）
    public const string ScenarioCopy = "copy_screenshot";
    public const string ScenarioCopyScrolling = "copy_scrolling_screenshot";
    public const string ScenarioCopyPinned = "copy_pinned_image";
    public const string ScenarioCurrentMonitor = "capture_current_monitor";
    public const string ScenarioFocusedWindow = "capture_focused_window";
    public const string ScenarioPin = "pin_to_screen";

    /// <summary>「执行完自动保存」的勾选项（顺序就是设置页里的顺序）。</summary>
    public static readonly IReadOnlyList<(string Key, string Label)> Scenarios = new[]
    {
        (ScenarioCopy, "复制截图"),
        (ScenarioCopyScrolling, "复制长截图"),
        (ScenarioCopyPinned, "复制贴图"),
        (ScenarioCurrentMonitor, "截取当前显示器"),
        (ScenarioFocusedWindow, "截取前台窗口"),
        (ScenarioPin, "贴图到屏幕"),
    };

    /// <summary>图片格式（png / jpeg / bmp / tiff 的英文键；未认识的值回落 png）。</summary>
    public static string Format = "png";

    /// <summary>JPEG 质量（0–100）。</summary>
    public static int Quality = 100;

    /// <summary>PNG 压缩力度。</summary>
    public static ExportCompression Compression = ExportCompression.Medium;

    /// <summary>保存目录；空 = <see cref="DefaultDirectory"/>。</summary>
    public static string SaveDirectory = "";

    /// <summary>手动另存的文件名模板。</summary>
    public static string ManualNameFormat = DefaultNameFormat;

    /// <summary>自动保存（快速保存 / 复制时存盘）的文件名模板。</summary>
    public static string AutoNameFormat = DefaultNameFormat;

    /// <summary>另存为对话框：<c>builtin</c>（自绘预览框）/ <c>system</c>（Windows 另存为）。</summary>
    public static string SaveAsDialog = "builtin";

    /// <summary>保存之后留着对话框显示结果。</summary>
    public static bool ShowSaveResult;

    /// <summary>复制截图时把导出的那个文件也一起放进剪贴板。</summary>
    public static bool CopyImageFile;

    private static readonly HashSet<string> AutoSave = new(StringComparer.Ordinal);

    /// <summary>系统「图片」文件夹（上游 <c>image_save_directory</c> 的默认值）。</summary>
    public static string DefaultDirectory =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>实际落盘目录（空配置回落「图片」文件夹）。</summary>
    public static string Directory =>
        string.IsNullOrWhiteSpace(SaveDirectory) ? DefaultDirectory : SaveDirectory.Trim();

    /// <summary>另存为是否走 Windows 自带的对话框。</summary>
    public static bool UseSystemDialog =>
        string.Equals(SaveAsDialog, "system", StringComparison.Ordinal);

    /// <summary>当前格式（枚举形态）。</summary>
    public static SaveFormat CurrentFormat => ParseFormat(Format);

    /// <summary>当前压缩力度（枚举形态）。</summary>
    public static ExportCompression CurrentCompression => Compression;

    /// <summary>这个场景要不要顺带存一份图（场景键不分大小写）。</summary>
    public static bool ShouldAutoSave(string scenario) =>
        !string.IsNullOrEmpty(scenario) && AutoSave.Contains(scenario.Trim().ToLowerInvariant());

    /// <summary>已勾选的场景（拷贝，给设置页回填用）。</summary>
    public static IReadOnlyCollection<string> AutoSaveScenarios => AutoSave;

    // ============ 规范化 ============

    /// <summary>格式英文键 → 枚举（认不出的回落 PNG）。</summary>
    public static SaveFormat ParseFormat(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "jpeg" or "jpg" => SaveFormat.Jpeg,
        "bmp" => SaveFormat.Bmp,
        "tiff" or "tif" => SaveFormat.Tiff,
        "pdf" => SaveFormat.Pdf,
        _ => SaveFormat.Png,
    };

    /// <summary>枚举 → 格式英文键。</summary>
    public static string FormatKey(SaveFormat format) => format switch
    {
        SaveFormat.Jpeg => "jpeg",
        SaveFormat.Bmp => "bmp",
        SaveFormat.Tiff => "tiff",
        SaveFormat.Pdf => "pdf",
        _ => "png",
    };

    /// <summary>压缩力度英文键 → 枚举。</summary>
    public static ExportCompression ParseCompression(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => ExportCompression.Low,
        "high" => ExportCompression.High,
        _ => ExportCompression.Medium,
    };

    /// <summary>枚举 → 压缩力度英文键。</summary>
    public static string CompressionKey(ExportCompression value) => value switch
    {
        ExportCompression.Low => "low",
        ExportCompression.High => "high",
        _ => "medium",
    };

    /// <summary>夹到 0–100 的质量；缺省 100。</summary>
    public static int NormalizeQuality(int? quality) =>
        quality is int value ? Math.Clamp(value, 0, 100) : 100;

    /// <summary>文件名模板：空白回落默认；多余花括号 / 控制字符由 <see cref="ExportNames"/> 处理。</summary>
    public static string NormalizeNameFormat(string? format) =>
        string.IsNullOrWhiteSpace(format) ? DefaultNameFormat : format.Trim();

    /// <summary>目录：去掉首尾空白与多余的分隔符（空 = 默认目录）。</summary>
    public static string NormalizeDirectory(string? dir)
    {
        string text = (dir ?? "").Trim().Trim('"');
        if (text.Length == 0) return "";
        try { return Path.GetFullPath(text); }
        catch { return text; }
    }

    // ============ 读写 ============

    /// <summary>启动时把磁盘配置灌进静态缓存（读不到就用默认值）。</summary>
    public static void Apply(ExportFile? file)
    {
        file ??= new ExportFile();

        Format = FormatKey(ParseFormat(file.Format));
        Quality = NormalizeQuality(file.Quality);
        Compression = ParseCompression(file.Compression);
        SaveDirectory = NormalizeDirectory(file.SaveDirectory);
        ManualNameFormat = NormalizeNameFormat(file.ManualFileNameFormat);
        AutoNameFormat = NormalizeNameFormat(file.AutoFileNameFormat);
        SaveAsDialog = string.Equals(file.SaveAsDialog, "system", StringComparison.OrdinalIgnoreCase)
            ? "system"
            : "builtin";
        ShowSaveResult = file.ShowSaveResult == true;
        CopyImageFile = file.CopyImageFileToClipboard == true;

        AutoSave.Clear();
        foreach (string key in file.AutoSaveAfterExecutionActions ?? new List<string>())
        {
            string normalized = (key ?? "").Trim().ToLowerInvariant();
            if (Scenarios.Any(s => s.Key == normalized)) AutoSave.Add(normalized);
        }
    }

    /// <summary>把当前缓存写回磁盘形状（<see cref="Save"/> 用）。</summary>
    public static void Capture(ExportFile file)
    {
        file.Format = Format;
        file.Quality = Quality;
        file.Compression = CompressionKey(Compression);
        file.SaveDirectory = SaveDirectory;
        file.ManualFileNameFormat = ManualNameFormat;
        file.AutoFileNameFormat = AutoNameFormat;
        file.SaveAsDialog = SaveAsDialog;
        file.ShowSaveResult = ShowSaveResult;
        file.CopyImageFileToClipboard = CopyImageFile;
        file.AutoSaveAfterExecutionActions = Scenarios
            .Select(s => s.Key)
            .Where(AutoSave.Contains)
            .ToList();
    }

    /// <summary>改一项就落盘一次（只写 settings 域）。<paramref name="change"/> 直接改静态缓存。</summary>
    public static void Save(Action change)
    {
        change();
        AppLog.Info("ExportSettings",
            $"输出 = {Format} / 质量 {Quality} / 压缩 {CompressionKey(Compression)} / 目录 {(SaveDirectory.Length == 0 ? "（默认）" : SaveDirectory)}");

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                SettingsStore.Modify(s =>
                {
                    s.Export ??= new ExportFile();
                    Capture(s.Export);
                });
            }
            catch (Exception ex)
            {
                CrashLog.Write("ExportSettings.Save", ex);
            }
        });
    }

    /// <summary>勾 / 取消一个「执行完自动保存」场景。</summary>
    public static void SetAutoSave(string scenario, bool enabled)
    {
        if (enabled) AutoSave.Add(scenario);
        else AutoSave.Remove(scenario);
    }
}
