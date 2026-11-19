using System;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>切换元素层级时的过渡动画（照上游 <c>SelectionTransitionAnimation</c>）。</summary>
internal enum SelectionTransition
{
    /// <summary>不做动画，直接落位。</summary>
    None,

    /// <summary>柔和：101ms。</summary>
    Soft,

    /// <summary>快速：67ms。</summary>
    Fast,
}

/// <summary>
/// 截图界面与参考线（<c>configs/settings.json</c> 的 <c>Screenshot</c> 段里
/// <c>screenshot_ui/*</c> 那 18 项）的读写与归一化。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>storage/configurationschema.cpp</c> 的 <c>screenshot_ui/*</c> +
/// <c>presentation/screenshotuipreferences.h</c>：默认值逐字对齐
/// （正常尺寸 / 柔和过渡 32 / 物理像素 / 不隐藏信息条 / 选区外不显示取色器 /
/// 全局坐标 / HEX / <c>#4096FF</c> 边框 / <c>#00000080</c> 压暗 / 提示不透明 100 /
/// 区域类型提示开 / 默认不显示参考线 / 参考线自适应开）。
/// </para>
/// <para>
/// 与 <see cref="ScreenshotBehavior"/> 同一套节奏：缓存一份内存快照（覆盖层在鼠标钩子线程上
/// 也会问），写盘只写 settings 域。归一化全是纯函数，<c>--selftest</c> 直接断言。
/// </para>
/// </remarks>
internal static class OverlayUiSettings
{
    public const string ToolbarNormal = "normal";
    public const string ToolbarSmall = "small";

    /// <summary>「小」档的内边距 / 字号 / 图标统一乘 0.8（照上游 <c>setPaletteScaleMultiplier</c>）。</summary>
    public const double SmallToolbarScale = 0.8;

    public const string UnitPhysical = "physical_pixels";
    public const string UnitLogical = "logical_pixels";

    public const string PickerHideOutsideSelection = "hide_outside_selection";
    public const string PickerAlwaysShow = "always_show";
    public const string PickerAlwaysHide = "always_hide";

    public const string CoordGlobal = "global";
    public const string CoordRelative = "relative";

    public const string FormatHex = "hex";
    public const string FormatHexWithoutHash = "hex_without_hash";
    public const string FormatRgb = "rgb";
    public const string FormatHsl = "hsl";

    public const int TransitionNone = 0;
    public const int TransitionSoft = 32;
    public const int TransitionFast = 72;

    public const int MinHintOpacity = 0;
    public const int MaxHintOpacity = 100;

    public static readonly Color DefaultSelectionBorderColor = Color.FromArgb(0xFF, 0x40, 0x96, 0xFF);
    public static readonly Color DefaultSelectionMaskColor = Color.FromArgb(0x80, 0x00, 0x00, 0x00);
    public static readonly Color DefaultCursorGuideColor = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
    public static readonly Color DefaultSelectionCenterGuideColor = Color.FromArgb(0xFF, 0x40, 0x96, 0xFF);
    public static readonly Color DefaultMonitorCenterGuideColor = Color.FromArgb(0xFF, 0xFF, 0x00, 0x00);
    public static readonly Color DefaultPickerCenterGuideColor = Color.FromArgb(0x00, 0x00, 0x00, 0x00);

    private static readonly object Gate = new();

    private static ScreenshotFile? _cached;

    private static string Anchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    // ============ 工具条尺寸 ============

    public static string ToolbarSize => NormalizeToolbarSize(Snapshot().ToolbarSize);

    public static double ToolbarScale => ToolbarSize == ToolbarSmall ? SmallToolbarScale : 1.0;

    public static void SetToolbarSize(string? key) => Save(file => file.ToolbarSize = NormalizeToolbarSize(key));

    /// <summary>给工具条挂的缩放变换（每次现建：<see cref="Transform"/> 不能挂到两个元素上）。</summary>
    public static ScaleTransform ToolbarTransform() => new(ToolbarScale, ToolbarScale);

    // ============ 过渡动画 / 单位 / 两个信息条开关 ============

    public static SelectionTransition SelectionTransition =>
        NormalizeTransition(Snapshot().SelectionTransitionAnimation);

    public static void SetSelectionTransition(SelectionTransition transition) =>
        Save(file => file.SelectionTransitionAnimation = TransitionValue(transition));

    public static string SelectionDisplayUnit => NormalizeUnit(Snapshot().SelectionDisplayUnit);

    /// <summary>信息条显示逻辑像素（dp）而不是物理像素（px）。</summary>
    public static bool LogicalUnits => SelectionDisplayUnit == UnitLogical;

    /// <summary>信息条上的单位文字（上游 <c>screenshotSelectionDisplayUnitText</c>）。</summary>
    public static string UnitLabel => UnitLabelFor(SelectionDisplayUnit);

    /// <summary>纯函数版：<c>px</c> / <c>dp</c>。</summary>
    public static string UnitLabelFor(string? unit) => NormalizeUnit(unit) == UnitLogical ? "dp" : "px";

    public static void SetSelectionDisplayUnit(string? key) =>
        Save(file => file.SelectionDisplayUnit = NormalizeUnit(key));

    public static bool SelectionToolbarHidden => Snapshot().SelectionToolbarHidden ?? false;

    public static void SetSelectionToolbarHidden(bool hidden) =>
        Save(file => file.SelectionToolbarHidden = hidden);

    public static bool ShowEditSelectionToolbar => Snapshot().ShowEditSelectionToolbar ?? true;

    public static void SetShowEditSelectionToolbar(bool show) =>
        Save(file => file.ShowEditSelectionToolbar = show);

    /// <summary>信息条显不显示：总开关优先，其次「选择工具下显示」。</summary>
    public static bool ShouldShowSelectionBar(CaptureTool tool) =>
        ShouldShowSelectionBar(SelectionToolbarHidden, ShowEditSelectionToolbar, tool);

    /// <summary>纯函数版（selftest 直接断言，不读盘）。</summary>
    public static bool ShouldShowSelectionBar(bool hidden, bool showForEditTools, CaptureTool tool) =>
        !hidden && (showForEditTools || tool != CaptureTool.Select);

    /// <summary>物理像素 → 信息条上的显示值（逻辑像素按 DPI 换算，上游按四舍五入）。</summary>
    public static int DisplayValue(int physical, double scale) => DisplayValue(SelectionDisplayUnit, physical, scale);

    /// <summary>纯函数版：<paramref name="unit"/> = <see cref="UnitPhysical"/> / <see cref="UnitLogical"/>。</summary>
    public static int DisplayValue(string? unit, int physical, double scale) =>
        NormalizeUnit(unit) == UnitLogical
            ? (int)Math.Round(physical / (scale > 0 ? scale : 1.0))
            : physical;

    /// <summary>信息条 ±1 步长换算回物理像素（逻辑单位下一格 = 1 dp）。</summary>
    public static int StepPixels(int physical, int direction, double scale) =>
        StepPixels(SelectionDisplayUnit, physical, direction, scale);

    /// <summary>纯函数版：一格在物理像素下是 ±1、在逻辑像素下是「±1 dp 换算回物理像素」。</summary>
    public static int StepPixels(string? unit, int physical, int direction, double scale)
    {
        if (direction == 0) return 0;
        if (NormalizeUnit(unit) != UnitLogical || scale <= 0) return direction;

        int display = DisplayValue(unit, physical, scale);
        int next = (int)Math.Round((display + direction) * scale);
        return next - physical;
    }

    // ============ 取色器 ============

    public static string ColorPickerDisplayMode => NormalizePickerMode(Snapshot().ColorPickerDisplayMode);

    public static void SetColorPickerDisplayMode(string? key) =>
        Save(file => file.ColorPickerDisplayMode = NormalizePickerMode(key));

    public static string ColorPickerCoordinateMode => NormalizeCoordinateMode(Snapshot().ColorPickerCoordinateMode);

    /// <summary>取色器显示相对选区左上角的坐标（上游 <c>relative</c>）。</summary>
    public static bool RelativePickerCoordinates => ColorPickerCoordinateMode == CoordRelative;

    public static void SetColorPickerCoordinateMode(string? key) =>
        Save(file => file.ColorPickerCoordinateMode = NormalizeCoordinateMode(key));

    public static string ColorPickerFormat => NormalizeColorFormat(Snapshot().ColorPickerFormat);

    public static void SetColorPickerFormat(string? key) =>
        Save(file => file.ColorPickerFormat = NormalizeColorFormat(key));

    /// <summary>下一个色值格式（照上游 <c>cycleColorFormat</c>：hex → 无井号 → rgb → hsl → hex）。</summary>
    public static string NextColorFormat(string? key) => NormalizeColorFormat(key) switch
    {
        FormatHex => FormatHexWithoutHash,
        FormatHexWithoutHash => FormatRgb,
        FormatRgb => FormatHsl,
        _ => FormatHex,
    };

    /// <summary>
    /// 取色器此刻该不该显示（照上游 <c>screenshotColorPickerVisible</c>）。
    /// LTools 的取色是独立工具，所以「手动选择中」恒为 true。
    /// </summary>
    public static bool PickerVisible(bool hasSelection, bool pointInsideSelection, bool intelligentSelecting,
        bool dragging = false, bool manualSelecting = true) =>
        PickerVisible(ColorPickerDisplayMode, hasSelection, pointInsideSelection, intelligentSelecting,
            dragging, manualSelecting);

    /// <summary>纯函数版（selftest 直接断言，不读盘）。</summary>
    public static bool PickerVisible(string? mode, bool hasSelection, bool pointInsideSelection,
        bool intelligentSelecting, bool dragging = false, bool manualSelecting = true)
    {
        mode = NormalizePickerMode(mode);
        if (mode == PickerAlwaysHide) return false;
        if (manualSelecting && dragging) return true;
        if (!hasSelection) return intelligentSelecting || manualSelecting;
        if (mode == PickerHideOutsideSelection && !intelligentSelecting && !pointInsideSelection) return false;
        return true;
    }

    // ============ 两条颜色 / 提示 / 参考线 ============

    public static Color SelectionBorderColor =>
        ParseColor(Snapshot().SelectionBorderColor, DefaultSelectionBorderColor);

    public static void SetSelectionBorderColor(Color color) =>
        Save(file => file.SelectionBorderColor = ColorPresets.Format(color));

    public static Color SelectionMaskColor => ParseColor(Snapshot().SelectionMaskColor, DefaultSelectionMaskColor);

    public static void SetSelectionMaskColor(Color color) =>
        Save(file => file.SelectionMaskColor = ColorPresets.Format(color));

    public static int ShortcutHintOpacity => NormalizeOpacity(Snapshot().ShortcutHintOpacity);

    /// <summary>提示条不透明度（0–1），直接喂给 <c>UIElement.Opacity</c>。</summary>
    public static double ShortcutHintAlpha => ShortcutHintOpacity / 100.0;

    public static void SetShortcutHintOpacity(int percent) =>
        Save(file => file.ShortcutHintOpacity = NormalizeOpacity(percent));

    public static bool AreaTypeHintEnabled => Snapshot().AreaTypeHintEnabled ?? true;

    public static void SetAreaTypeHintEnabled(bool enabled) =>
        Save(file => file.AreaTypeHintEnabled = enabled);

    public static bool ShowGuidesByDefault => Snapshot().ShowGuidesByDefault ?? false;

    public static void SetShowGuidesByDefault(bool show) => Save(file => file.ShowGuidesByDefault = show);

    public static bool AdaptiveGuideColors => Snapshot().AdaptiveGuideColors ?? true;

    public static void SetAdaptiveGuideColors(bool adaptive) => Save(file => file.AdaptiveGuideColors = adaptive);

    public static Color CursorGuideColor => ParseColor(Snapshot().CursorGuideLineColor, DefaultCursorGuideColor);

    public static void SetCursorGuideColor(Color color) =>
        Save(file => file.CursorGuideLineColor = ColorPresets.Format(color));

    public static Color SelectionCenterGuideColor =>
        ParseColor(Snapshot().SelectionCenterGuideLineColor, DefaultSelectionCenterGuideColor);

    public static void SetSelectionCenterGuideColor(Color color) =>
        Save(file => file.SelectionCenterGuideLineColor = ColorPresets.Format(color));

    public static Color MonitorCenterGuideColor =>
        ParseColor(Snapshot().MonitorCenterGuideLineColor, DefaultMonitorCenterGuideColor);

    public static void SetMonitorCenterGuideColor(Color color) =>
        Save(file => file.MonitorCenterGuideLineColor = ColorPresets.Format(color));

    public static Color PickerCenterGuideColor =>
        ParseColor(Snapshot().ColorPickerCenterGuideLineColor, DefaultPickerCenterGuideColor);

    public static void SetPickerCenterGuideColor(Color color) =>
        Save(file => file.ColorPickerCenterGuideLineColor = ColorPresets.Format(color));

    // ============ 归一化（纯函数，selftest 直接断言） ============

    public static string NormalizeToolbarSize(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == ToolbarSmall ? ToolbarSmall : ToolbarNormal;

    public static SelectionTransition NormalizeTransition(int? value) => value switch
    {
        TransitionNone => SelectionTransition.None,
        TransitionFast => SelectionTransition.Fast,
        _ => SelectionTransition.Soft,
    };

    public static int TransitionValue(SelectionTransition transition) => transition switch
    {
        SelectionTransition.None => TransitionNone,
        SelectionTransition.Fast => TransitionFast,
        _ => TransitionSoft,
    };

    public static string NormalizeUnit(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == UnitLogical ? UnitLogical : UnitPhysical;

    public static string NormalizePickerMode(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        PickerAlwaysShow => PickerAlwaysShow,
        PickerAlwaysHide => PickerAlwaysHide,
        _ => PickerHideOutsideSelection,
    };

    public static string NormalizeCoordinateMode(string? key) =>
        (key ?? "").Trim().ToLowerInvariant() == CoordRelative ? CoordRelative : CoordGlobal;

    public static string NormalizeColorFormat(string? key) => (key ?? "").Trim().ToLowerInvariant() switch
    {
        FormatHexWithoutHash => FormatHexWithoutHash,
        FormatRgb => FormatRgb,
        FormatHsl => FormatHsl,
        _ => FormatHex,
    };

    public static int NormalizeOpacity(int? percent) =>
        percent is not { } raw ? MaxHintOpacity : Math.Clamp(raw, MinHintOpacity, MaxHintOpacity);

    /// <summary>认 <c>#RRGGBBAA</c> / <c>#RRGGBB</c>；坏值（含空）按给定默认色。</summary>
    public static Color ParseColor(string? text, Color fallback) =>
        ColorPresets.TryParse(text, out var color) ? color : fallback;

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
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
            loaded = SettingsStore.Read(Anchor).Screenshot ?? new ScreenshotFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.OverlayUiSettings", "读取截图界面设置失败：" + ex.Message);
            loaded = new ScreenshotFile();
        }

        Normalize(loaded);

        lock (Gate) return _cached = loaded;
    }

    private static void Save(Action<ScreenshotFile> mutate)
    {
        ScreenshotFile next;
        lock (Gate)
        {
            next = Clone(Snapshot());
            mutate(next);
            Normalize(next);
            _cached = Clone(next);
        }

        try
        {
            SettingsStore.Modify(Anchor, file =>
            {
                file.Screenshot ??= new ScreenshotFile();
                var target = file.Screenshot;

                target.ToolbarSize = next.ToolbarSize;
                target.SelectionTransitionAnimation = next.SelectionTransitionAnimation;
                target.SelectionDisplayUnit = next.SelectionDisplayUnit;
                target.SelectionToolbarHidden = next.SelectionToolbarHidden;
                target.ShowEditSelectionToolbar = next.ShowEditSelectionToolbar;
                target.ColorPickerDisplayMode = next.ColorPickerDisplayMode;
                target.ColorPickerCoordinateMode = next.ColorPickerCoordinateMode;
                target.ColorPickerFormat = next.ColorPickerFormat;
                target.SelectionBorderColor = next.SelectionBorderColor;
                target.SelectionMaskColor = next.SelectionMaskColor;
                target.ShortcutHintOpacity = next.ShortcutHintOpacity;
                target.AreaTypeHintEnabled = next.AreaTypeHintEnabled;
                target.ShowGuidesByDefault = next.ShowGuidesByDefault;
                target.AdaptiveGuideColors = next.AdaptiveGuideColors;
                target.CursorGuideLineColor = next.CursorGuideLineColor;
                target.SelectionCenterGuideLineColor = next.SelectionCenterGuideLineColor;
                target.MonitorCenterGuideLineColor = next.MonitorCenterGuideLineColor;
                target.ColorPickerCenterGuideLineColor = next.ColorPickerCenterGuideLineColor;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.OverlayUiSettings", "保存截图界面设置失败：" + ex.Message);
        }
    }

    private static void Normalize(ScreenshotFile file)
    {
        file.ToolbarSize = NormalizeToolbarSize(file.ToolbarSize);
        file.SelectionTransitionAnimation = TransitionValue(NormalizeTransition(file.SelectionTransitionAnimation));
        file.SelectionDisplayUnit = NormalizeUnit(file.SelectionDisplayUnit);
        file.SelectionToolbarHidden ??= false;
        file.ShowEditSelectionToolbar ??= true;
        file.ColorPickerDisplayMode = NormalizePickerMode(file.ColorPickerDisplayMode);
        file.ColorPickerCoordinateMode = NormalizeCoordinateMode(file.ColorPickerCoordinateMode);
        file.ColorPickerFormat = NormalizeColorFormat(file.ColorPickerFormat);
        file.SelectionBorderColor = ColorPresets.Format(ParseColor(file.SelectionBorderColor, DefaultSelectionBorderColor));
        file.SelectionMaskColor = ColorPresets.Format(ParseColor(file.SelectionMaskColor, DefaultSelectionMaskColor));
        file.ShortcutHintOpacity = NormalizeOpacity(file.ShortcutHintOpacity);
        file.AreaTypeHintEnabled ??= true;
        file.ShowGuidesByDefault ??= false;
        file.AdaptiveGuideColors ??= true;
        file.CursorGuideLineColor = ColorPresets.Format(ParseColor(file.CursorGuideLineColor, DefaultCursorGuideColor));
        file.SelectionCenterGuideLineColor =
            ColorPresets.Format(ParseColor(file.SelectionCenterGuideLineColor, DefaultSelectionCenterGuideColor));
        file.MonitorCenterGuideLineColor =
            ColorPresets.Format(ParseColor(file.MonitorCenterGuideLineColor, DefaultMonitorCenterGuideColor));
        file.ColorPickerCenterGuideLineColor =
            ColorPresets.Format(ParseColor(file.ColorPickerCenterGuideLineColor, DefaultPickerCenterGuideColor));
    }

    private static ScreenshotFile Clone(ScreenshotFile file) => new()
    {
        DelaySeconds = file.DelaySeconds,
        DoubleClickAction = file.DoubleClickAction,
        MiddleButtonAction = file.MiddleButtonAction,
        ShutterSound = file.ShutterSound,
        ScreenshotSound = file.ScreenshotSound,
        StrokeColorPresets = file.StrokeColorPresets is null ? null : new(file.StrokeColorPresets),
        FillColorPresets = file.FillColorPresets is null ? null : new(file.FillColorPresets),
        HighlightColorPresets = file.HighlightColorPresets is null ? null : new(file.HighlightColorPresets),
        ScrollingAutoScrollIntervalMs = file.ScrollingAutoScrollIntervalMs,
        CaptureCursor = file.CaptureCursor,
        ConfirmBeforeExitViaShortcut = file.ConfirmBeforeExitViaShortcut,
        QuickSelectionModification = file.QuickSelectionModification,
        SelectionResizeMode = file.SelectionResizeMode,
        HidpiImageScaling = file.HidpiImageScaling,
        PdfPageSize = file.PdfPageSize,
        ToolbarSize = file.ToolbarSize,
        SelectionTransitionAnimation = file.SelectionTransitionAnimation,
        SelectionDisplayUnit = file.SelectionDisplayUnit,
        SelectionToolbarHidden = file.SelectionToolbarHidden,
        ShowEditSelectionToolbar = file.ShowEditSelectionToolbar,
        ColorPickerDisplayMode = file.ColorPickerDisplayMode,
        ColorPickerCoordinateMode = file.ColorPickerCoordinateMode,
        ColorPickerFormat = file.ColorPickerFormat,
        SelectionBorderColor = file.SelectionBorderColor,
        SelectionMaskColor = file.SelectionMaskColor,
        ShortcutHintOpacity = file.ShortcutHintOpacity,
        AreaTypeHintEnabled = file.AreaTypeHintEnabled,
        ShowGuidesByDefault = file.ShowGuidesByDefault,
        AdaptiveGuideColors = file.AdaptiveGuideColors,
        CursorGuideLineColor = file.CursorGuideLineColor,
        SelectionCenterGuideLineColor = file.SelectionCenterGuideLineColor,
        MonitorCenterGuideLineColor = file.MonitorCenterGuideLineColor,
        ColorPickerCenterGuideLineColor = file.ColorPickerCenterGuideLineColor,
    };
}
