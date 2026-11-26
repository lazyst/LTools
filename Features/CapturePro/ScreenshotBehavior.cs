using System;
using System.Collections.Generic;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图行为设置（<c>configs/settings.json</c> 的 <c>Screenshot</c> 段）的读写与归一化。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot/*</c>：默认值逐条对齐 <c>storage/configurationschema.cpp</c>
/// （自动滚动 200ms、不画光标、Esc 不确认、快速选择修改开、按位移改尺寸、
/// HiDPI 关闭、PDF 走 A4 竖版）。
/// </para>
/// <para>
/// 读盘只发生一次（覆盖层在鼠标钩子线程上也要问），写盘只写 settings 域。
/// 归一化都是纯函数，<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class ScreenshotBehavior
{
    public const int DefaultScrollingAutoScrollIntervalMs = 200;
    public const int MinScrollingAutoScrollIntervalMs = 128;
    public const int MaxScrollingAutoScrollIntervalMs = 1000;

    public const bool DefaultCaptureCursor = false;
    public const bool DefaultConfirmBeforeExitViaShortcut = false;
    public const bool DefaultQuickSelectionModification = true;

    public const string ResizeFollowMovement = "follow_mouse_movement";
    public const string ResizeFollowPosition = "follow_mouse_position";
    public const string DefaultSelectionResizeMode = ResizeFollowMovement;

    public const string HidpiDisabled = "disabled";
    public const string HidpiAuto = "auto";
    public const string DefaultHidpiImageScaling = HidpiDisabled;

    /// <summary>固定档位（上游 <c>kSchemaStrings</c> 里 125…350，每 25 一档）。</summary>
    public static readonly IReadOnlyList<int> HidpiPercentages =
        new[] { 125, 150, 175, 200, 225, 250, 275, 300, 325, 350 };

    public const string PdfImageSize = "image_size";
    public const string PdfA4Portrait = "a4_portrait";
    public const string PdfA4Landscape = "a4_landscape";
    public const string DefaultPdfPageSize = PdfA4Portrait;

    // 文字识别完成后自动执行（照上游 screenshot/auto_execute_after_text_recognition）
    public const string OcrActionNone = "no_action";
    public const string OcrActionCopy = "copy_text";
    public const string OcrActionCopyAndEnd = "copy_text_and_end_screenshot";
    public const string OcrActionQuickCopy = "quick_copy_text";
    public const string OcrActionQuickCopyAndEnd = "quick_copy_text_and_end_screenshot";
    public const string OcrActionEditMode = "enable_edit_mode";
    public const string DefaultOcrAction = OcrActionNone;

    private static readonly object Gate = new();

    private static ScreenshotFile? _cached;

    /// <summary>长截图自动滚动间隔毫秒（clamp 128–1000）。</summary>
    public static int ScrollingAutoScrollIntervalMs =>
        NormalizeInterval(Snapshot().ScrollingAutoScrollIntervalMs);

    public static bool CaptureCursor => Snapshot().CaptureCursor ?? DefaultCaptureCursor;

    public static bool ConfirmBeforeExitViaShortcut =>
        Snapshot().ConfirmBeforeExitViaShortcut ?? DefaultConfirmBeforeExitViaShortcut;

    public static bool QuickSelectionModification =>
        Snapshot().QuickSelectionModification ?? DefaultQuickSelectionModification;

    public static string SelectionResizeMode => NormalizeResizeMode(Snapshot().SelectionResizeMode);

    public static string HidpiImageScaling => NormalizeHidpi(Snapshot().HidpiImageScaling);

    public static string PdfPageSize => NormalizePdfPageSize(Snapshot().PdfPageSize);

    /// <summary>文字识别完成后自动做什么（六值，见 <see cref="ScreenshotFile"/>）。</summary>
    public static string AutoExecuteAfterTextRecognition =>
        NormalizeOcrAction(Snapshot().AutoExecuteAfterTextRecognition);

    public static void SetScrollingAutoScrollIntervalMs(int value) =>
        Save(file => file.ScrollingAutoScrollIntervalMs = NormalizeInterval(value));

    public static void SetCaptureCursor(bool on) => Save(file => file.CaptureCursor = on);

    public static void SetConfirmBeforeExitViaShortcut(bool on) =>
        Save(file => file.ConfirmBeforeExitViaShortcut = on);

    public static void SetQuickSelectionModification(bool on) =>
        Save(file => file.QuickSelectionModification = on);

    public static void SetSelectionResizeMode(string? mode) =>
        Save(file => file.SelectionResizeMode = NormalizeResizeMode(mode));

    public static void SetHidpiImageScaling(string? key) =>
        Save(file => file.HidpiImageScaling = NormalizeHidpi(key));

    public static void SetPdfPageSize(string? key) =>
        Save(file => file.PdfPageSize = NormalizePdfPageSize(key));

    public static void SetAutoExecuteAfterTextRecognition(string? key) =>
        Save(file => file.AutoExecuteAfterTextRecognition = NormalizeOcrAction(key));

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>自动滚动间隔归一到 128–1000（空 / 越界按默认 200）。</summary>
    public static int NormalizeInterval(int? value)
    {
        if (value is not { } raw) return DefaultScrollingAutoScrollIntervalMs;
        return Math.Clamp(raw, MinScrollingAutoScrollIntervalMs, MaxScrollingAutoScrollIntervalMs);
    }

    /// <summary>尺寸方式只认两个键，其余按「按位移」。</summary>
    public static string NormalizeResizeMode(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == ResizeFollowPosition
            ? ResizeFollowPosition
            : ResizeFollowMovement;

    /// <summary>HiDPI 只认 <c>disabled</c> / <c>auto</c> / 125–350 的 25 倍数，其余按关闭。</summary>
    public static string NormalizeHidpi(string? key)
    {
        string raw = (key ?? "").Trim().ToLowerInvariant();
        if (raw == HidpiAuto) return HidpiAuto;
        if (raw == HidpiDisabled) return HidpiDisabled;
        return TryParsePercentage(raw, out _) ? raw : HidpiDisabled;
    }

    /// <summary>PDF 纸张只认三个键，其余按 A4 竖版。</summary>
    public static string NormalizePdfPageSize(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() switch
        {
            PdfImageSize => PdfImageSize,
            PdfA4Landscape => PdfA4Landscape,
            _ => PdfA4Portrait,
        };

    /// <summary>识别后自动动作只认六个键，其余按「不执行操作」。</summary>
    public static string NormalizeOcrAction(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() switch
        {
            OcrActionCopy => OcrActionCopy,
            OcrActionCopyAndEnd => OcrActionCopyAndEnd,
            OcrActionQuickCopy => OcrActionQuickCopy,
            OcrActionQuickCopyAndEnd => OcrActionQuickCopyAndEnd,
            OcrActionEditMode => OcrActionEditMode,
            _ => OcrActionNone,
        };

    /// <summary>这个自动动作是不是「只在快速 OCR 入口生效」的 <c>quick_*</c>。</summary>
    public static bool IsQuickOnlyOcrAction(string? action) =>
        NormalizeOcrAction(action) is OcrActionQuickCopy or OcrActionQuickCopyAndEnd;

    /// <summary>这个自动动作会不会复制文本。</summary>
    public static bool CopiesOcrText(string? action) =>
        NormalizeOcrAction(action) is OcrActionCopy or OcrActionCopyAndEnd
            or OcrActionQuickCopy or OcrActionQuickCopyAndEnd;

    /// <summary>这个自动动作会不会顺手结束截图。</summary>
    public static bool EndsAfterOcr(string? action) =>
        NormalizeOcrAction(action) is OcrActionCopyAndEnd or OcrActionQuickCopyAndEnd;

    /// <summary>固定档位键（<c>"125"</c> … <c>"350"</c>）→ 百分比。</summary>
    public static bool TryParsePercentage(string? key, out int percentage)
    {
        percentage = 0;
        if (!int.TryParse((key ?? "").Trim(), out int value)) return false;
        if (value < 125 || value > 350 || value % 25 != 0) return false;
        percentage = value;
        return true;
    }

    private static ScreenshotFile Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        ScreenshotFile loaded;
        try
        {
            loaded = SettingsStore.Read(ConfigStore.Anchor).Screenshot ?? new ScreenshotFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.ScreenshotBehavior", "读取截图行为设置失败：" + ex.Message);
            loaded = new ScreenshotFile();
        }

        loaded.ScrollingAutoScrollIntervalMs = NormalizeInterval(loaded.ScrollingAutoScrollIntervalMs);
        loaded.CaptureCursor ??= DefaultCaptureCursor;
        loaded.ConfirmBeforeExitViaShortcut ??= DefaultConfirmBeforeExitViaShortcut;
        loaded.QuickSelectionModification ??= DefaultQuickSelectionModification;
        loaded.SelectionResizeMode = NormalizeResizeMode(loaded.SelectionResizeMode);
        loaded.HidpiImageScaling = NormalizeHidpi(loaded.HidpiImageScaling);
        loaded.PdfPageSize = NormalizePdfPageSize(loaded.PdfPageSize);
        loaded.AutoExecuteAfterTextRecognition =
            NormalizeOcrAction(loaded.AutoExecuteAfterTextRecognition);

        lock (Gate) return _cached = loaded;
    }

    private static void Save(Action<ScreenshotFile> mutate)
    {
        ScreenshotFile next;
        lock (Gate)
        {
            next = Clone(Snapshot());
            mutate(next);
            next.ScrollingAutoScrollIntervalMs = NormalizeInterval(next.ScrollingAutoScrollIntervalMs);
            next.SelectionResizeMode = NormalizeResizeMode(next.SelectionResizeMode);
            next.HidpiImageScaling = NormalizeHidpi(next.HidpiImageScaling);
            next.PdfPageSize = NormalizePdfPageSize(next.PdfPageSize);
            next.AutoExecuteAfterTextRecognition =
                NormalizeOcrAction(next.AutoExecuteAfterTextRecognition);
            _cached = Clone(next);
        }

        try
        {
            SettingsStore.Modify(ConfigStore.Anchor, file =>
            {
                file.Screenshot ??= new ScreenshotFile();
                file.Screenshot.ScrollingAutoScrollIntervalMs = next.ScrollingAutoScrollIntervalMs;
                file.Screenshot.CaptureCursor = next.CaptureCursor;
                file.Screenshot.ConfirmBeforeExitViaShortcut = next.ConfirmBeforeExitViaShortcut;
                file.Screenshot.QuickSelectionModification = next.QuickSelectionModification;
                file.Screenshot.SelectionResizeMode = next.SelectionResizeMode;
                file.Screenshot.HidpiImageScaling = next.HidpiImageScaling;
                file.Screenshot.PdfPageSize = next.PdfPageSize;
                file.Screenshot.AutoExecuteAfterTextRecognition =
                    next.AutoExecuteAfterTextRecognition;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.ScreenshotBehavior", "保存截图行为设置失败：" + ex.Message);
        }
    }

    private static ScreenshotFile Clone(ScreenshotFile file) => new()
    {
        DelaySeconds = file.DelaySeconds,
        DoubleClickAction = file.DoubleClickAction,
        MiddleButtonAction = file.MiddleButtonAction,
        AutoExecuteAfterTextRecognition = file.AutoExecuteAfterTextRecognition,
        ShutterSound = file.ShutterSound,
        ScreenshotSound = file.ScreenshotSound,
        StrokeColorPresets = file.StrokeColorPresets is null ? null : new List<string>(file.StrokeColorPresets),
        FillColorPresets = file.FillColorPresets is null ? null : new List<string>(file.FillColorPresets),
        HighlightColorPresets =
            file.HighlightColorPresets is null ? null : new List<string>(file.HighlightColorPresets),
        ScrollingAutoScrollIntervalMs = file.ScrollingAutoScrollIntervalMs,
        CaptureCursor = file.CaptureCursor,
        ConfirmBeforeExitViaShortcut = file.ConfirmBeforeExitViaShortcut,
        QuickSelectionModification = file.QuickSelectionModification,
        SelectionResizeMode = file.SelectionResizeMode,
        HidpiImageScaling = file.HidpiImageScaling,
        PdfPageSize = file.PdfPageSize,
    };
}
