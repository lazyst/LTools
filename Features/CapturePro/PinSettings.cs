using System;
using System.Windows.Media;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图窗口的外观与滚轮设置（<c>configs/settings.json</c> 的 <c>PinToScreen</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>configurationschema.cpp</c> 的 <c>pin_to_screen/*</c> 那几项：
/// <c>show_border_by_default</c>（true）/ <c>show_shadow_by_default</c>（false）/
/// 六个颜色（边框·活动·锁定 + 阴影·活动·锁定）/ <c>mouse_wheel_zoom_mode</c>（<c>mouse_position</c>）/
/// <c>mouse_wheel_zoom_step</c>（10）/ <c>mouse_wheel_opacity_step</c>（5）/
/// <c>show_window_buttons</c>（true）/ <c>confirm_before_closing_window</c>（false）。
/// </para>
/// <para>
/// **读盘只发生一次**：滚轮、拖动、绘制边框都在鼠标事件里问，每问一次读一遍 JSON 会把光标顶住
/// ——与 <see cref="PointerSettings"/> / <see cref="OverlayUiSettings"/> 同一套缓存口径。
/// 写盘只写自己这些字段，写完立刻更新缓存（不必重开贴图）。
/// </para>
/// <para>
/// 每张贴图自己的边框 / 阴影开关不在这里：那是 <c>pins/pins.json</c> 记录里的状态
/// （<see cref="PinRecord.ShowBorder"/> / <see cref="PinRecord.ShowShadow"/>），
/// 这里的「默认」只在新建贴图时读一次。
/// </para>
/// </remarks>
internal static class PinSettings
{
    public const string ZoomMousePosition = "mouse_position";
    public const string ZoomTopLeft = "top_left";
    public const string ZoomTopRight = "top_right";
    public const string ZoomBottomLeft = "bottom_left";
    public const string ZoomBottomRight = "bottom_right";
    public const string ZoomCenter = "center";

    /// <summary>滚轮缩放锚点的下拉表（键 = 上游英文键，标签 = 界面文案）。</summary>
    public static readonly (string Key, string Label)[] ZoomModes =
    {
        (ZoomMousePosition, "光标位置"),
        (ZoomTopLeft, "左上角"),
        (ZoomTopRight, "右上角"),
        (ZoomBottomLeft, "左下角"),
        (ZoomBottomRight, "右下角"),
        (ZoomCenter, "窗口中心"),
    };

    public const int DefaultZoomStep = 10;
    public const int DefaultOpacityStep = 5;
    public const int MinStep = 1;
    public const int MaxStep = 50;

    public static readonly Color DefaultBorderColor = Color.FromArgb(0xFF, 0xDB, 0xDB, 0xDB);
    public static readonly Color DefaultBorderActiveColor = Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF);
    public static readonly Color DefaultLockedBorderColor = Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14);
    public static readonly Color DefaultShadowColor = Color.FromArgb(0xFF, 0xBF, 0xBF, 0xBF);
    public static readonly Color DefaultShadowActiveColor = Color.FromArgb(0xFF, 0x69, 0xB1, 0xFF);
    public static readonly Color DefaultLockedShadowColor = Color.FromArgb(0xFF, 0xFA, 0xAD, 0x14);

    private static readonly object Gate = new();

    private static PinToScreenFile? _cached;

    private static string Anchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    // ============ 新贴图默认显示 ============

    /// <summary>新贴图默认显示边框（缺省 = 开）。</summary>
    public static bool ShowBorderByDefault => Snapshot().ShowBorderByDefault ?? true;

    public static void SetShowBorderByDefault(bool on) => Save(file => file.ShowBorderByDefault = on);

    /// <summary>新贴图默认显示阴影（缺省 = 关）。</summary>
    public static bool ShowShadowByDefault => Snapshot().ShowShadowByDefault ?? false;

    public static void SetShowShadowByDefault(bool on) => Save(file => file.ShowShadowByDefault = on);

    // ============ 六个颜色 ============

    public static Color BorderColor => ParseColor(Snapshot().BorderColor, DefaultBorderColor);

    public static void SetBorderColor(Color color) => Save(file => file.BorderColor = ColorPresets.Format(color));

    public static Color BorderActiveColor =>
        ParseColor(Snapshot().BorderActiveColor, DefaultBorderActiveColor);

    public static void SetBorderActiveColor(Color color) =>
        Save(file => file.BorderActiveColor = ColorPresets.Format(color));

    public static Color LockedBorderColor => ParseColor(Snapshot().LockedBorderColor, DefaultLockedBorderColor);

    public static void SetLockedBorderColor(Color color) =>
        Save(file => file.LockedBorderColor = ColorPresets.Format(color));

    public static Color ShadowColor => ParseColor(Snapshot().ShadowColor, DefaultShadowColor);

    public static void SetShadowColor(Color color) => Save(file => file.ShadowColor = ColorPresets.Format(color));

    public static Color ShadowActiveColor => ParseColor(Snapshot().ShadowActiveColor, DefaultShadowActiveColor);

    public static void SetShadowActiveColor(Color color) =>
        Save(file => file.ShadowActiveColor = ColorPresets.Format(color));

    public static Color LockedShadowColor => ParseColor(Snapshot().LockedShadowColor, DefaultLockedShadowColor);

    public static void SetLockedShadowColor(Color color) =>
        Save(file => file.LockedShadowColor = ColorPresets.Format(color));

    // ============ 滚轮 ============

    /// <summary>滚轮缩放锚点（<c>mouse_position</c> / 四角 / <c>center</c>）。</summary>
    public static string ZoomMode => NormalizeZoomMode(Snapshot().MouseWheelZoomMode);

    public static void SetZoomMode(string? key) => Save(file => file.MouseWheelZoomMode = NormalizeZoomMode(key));

    /// <summary>滚轮一格改多少缩放（百分点，1–50，缺省 10）。</summary>
    public static int ZoomStepPercent => NormalizeStep(Snapshot().MouseWheelZoomStep, DefaultZoomStep);

    public static void SetZoomStepPercent(int percent) =>
        Save(file => file.MouseWheelZoomStep = NormalizeStep(percent, DefaultZoomStep));

    /// <summary>滚轮一格改多少不透明度（百分点，1–50，缺省 5）。</summary>
    public static int OpacityStepPercent => NormalizeStep(Snapshot().MouseWheelOpacityStep, DefaultOpacityStep);

    public static void SetOpacityStepPercent(int percent) =>
        Save(file => file.MouseWheelOpacityStep = NormalizeStep(percent, DefaultOpacityStep));

    // ============ 窗口按钮 / 关闭确认 ============

    /// <summary>贴图右上角显示窗口按钮（关闭 ✕；标注按钮随后续批次补）。</summary>
    public static bool ShowWindowButtons => Snapshot().ShowWindowButtons ?? true;

    public static void SetShowWindowButtons(bool on) => Save(file => file.ShowWindowButtons = on);

    /// <summary>「关闭贴图」前先确认（缺省 = 关；与「删除贴图」的确认是两件事）。</summary>
    public static bool ConfirmBeforeClosingWindow => Snapshot().ConfirmBeforeClosingWindow ?? false;

    public static void SetConfirmBeforeClosingWindow(bool on) =>
        Save(file => file.ConfirmBeforeClosingWindow = on);

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    // ============ 归一化（纯函数，selftest 直接断言） ============

    /// <summary>滚轮锚点只认六个键，其余一律回落到光标位置。</summary>
    public static string NormalizeZoomMode(string? key)
    {
        string value = (key ?? "").Trim().ToLowerInvariant();
        return value switch
        {
            ZoomTopLeft => ZoomTopLeft,
            ZoomTopRight => ZoomTopRight,
            ZoomBottomLeft => ZoomBottomLeft,
            ZoomBottomRight => ZoomBottomRight,
            ZoomCenter => ZoomCenter,
            _ => ZoomMousePosition,
        };
    }

    /// <summary>步长夹到 1–50；没填（0 / 负数 / <c>null</c>）用缺省值。</summary>
    public static int NormalizeStep(int? value, int fallback)
    {
        int step = value ?? 0;
        if (step <= 0) step = fallback;
        return Math.Clamp(step, MinStep, MaxStep);
    }

    /// <summary>色值解析：认 <c>#RRGGBB</c> / <c>#RRGGBBAA</c>，坏值回落。</summary>
    public static Color ParseColor(string? text, Color fallback) =>
        ColorPresets.TryParse(text, out var color) ? color : fallback;

    /// <summary>某个滚轮锚点在窗口里的归一化位置（0 = 左 / 上，1 = 右 / 下，0.5 = 中心）。</summary>
    public static (double X, double Y) AnchorRatio(string? mode) => NormalizeZoomMode(mode) switch
    {
        ZoomTopLeft => (0, 0),
        ZoomTopRight => (1, 0),
        ZoomBottomLeft => (0, 1),
        ZoomBottomRight => (1, 1),
        ZoomCenter => (0.5, 0.5),
        _ => (double.NaN, double.NaN),      // 光标位置：由调用方按光标在窗口里的比例算
    };

    /// <summary>这个锚点是不是「跟光标」（<see cref="AnchorRatio"/> 给 NaN）。</summary>
    public static bool IsMouseAnchor(string? mode) => NormalizeZoomMode(mode) == ZoomMousePosition;

    /// <summary>下拉里的显示名（未知键原样返回）。</summary>
    public static string ZoomModeLabel(string? key)
    {
        string normalized = NormalizeZoomMode(key);
        foreach (var (mode, label) in ZoomModes)
            if (mode == normalized) return label;
        return normalized;
    }

    // ============ 读 / 写 ============

    private static PinToScreenFile Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        PinToScreenFile loaded;
        try
        {
            loaded = SettingsStore.Read(Anchor).PinToScreen ?? new PinToScreenFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinSettings", "读取贴图窗口设置失败：" + ex.Message);
            loaded = new PinToScreenFile();
        }

        Normalize(loaded);

        lock (Gate) return _cached = loaded;
    }

    private static void Save(Action<PinToScreenFile> mutate)
    {
        PinToScreenFile next;
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
                file.PinToScreen ??= new PinToScreenFile();
                var target = file.PinToScreen;

                target.ShowBorderByDefault = next.ShowBorderByDefault;
                target.ShowShadowByDefault = next.ShowShadowByDefault;
                target.BorderColor = next.BorderColor;
                target.BorderActiveColor = next.BorderActiveColor;
                target.LockedBorderColor = next.LockedBorderColor;
                target.ShadowColor = next.ShadowColor;
                target.ShadowActiveColor = next.ShadowActiveColor;
                target.LockedShadowColor = next.LockedShadowColor;
                target.MouseWheelZoomMode = next.MouseWheelZoomMode;
                target.MouseWheelZoomStep = next.MouseWheelZoomStep;
                target.MouseWheelOpacityStep = next.MouseWheelOpacityStep;
                target.ShowWindowButtons = next.ShowWindowButtons;
                target.ConfirmBeforeClosingWindow = next.ConfirmBeforeClosingWindow;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinSettings", "保存贴图窗口设置失败：" + ex.Message);
        }
    }

    private static void Normalize(PinToScreenFile file)
    {
        file.ShowBorderByDefault ??= true;
        file.ShowShadowByDefault ??= false;
        file.BorderColor = ColorPresets.Format(ParseColor(file.BorderColor, DefaultBorderColor));
        file.BorderActiveColor = ColorPresets.Format(ParseColor(file.BorderActiveColor, DefaultBorderActiveColor));
        file.LockedBorderColor = ColorPresets.Format(ParseColor(file.LockedBorderColor, DefaultLockedBorderColor));
        file.ShadowColor = ColorPresets.Format(ParseColor(file.ShadowColor, DefaultShadowColor));
        file.ShadowActiveColor = ColorPresets.Format(ParseColor(file.ShadowActiveColor, DefaultShadowActiveColor));
        file.LockedShadowColor = ColorPresets.Format(ParseColor(file.LockedShadowColor, DefaultLockedShadowColor));
        file.MouseWheelZoomMode = NormalizeZoomMode(file.MouseWheelZoomMode);
        file.MouseWheelZoomStep = NormalizeStep(file.MouseWheelZoomStep, DefaultZoomStep);
        file.MouseWheelOpacityStep = NormalizeStep(file.MouseWheelOpacityStep, DefaultOpacityStep);
        file.ShowWindowButtons ??= true;
        file.ConfirmBeforeClosingWindow ??= false;
    }

    private static PinToScreenFile Clone(PinToScreenFile file) => new()
    {
        ShakeWindowToHideOthers = file.ShakeWindowToHideOthers,
        DoubleClickAction = file.DoubleClickAction,
        MiddleButtonAction = file.MiddleButtonAction,
        ConfirmBeforeDestroyingWindow = file.ConfirmBeforeDestroyingWindow,
        ConfirmBeforeClosingWindow = file.ConfirmBeforeClosingWindow,
        ShowBorderByDefault = file.ShowBorderByDefault,
        ShowShadowByDefault = file.ShowShadowByDefault,
        BorderColor = file.BorderColor,
        BorderActiveColor = file.BorderActiveColor,
        LockedBorderColor = file.LockedBorderColor,
        ShadowColor = file.ShadowColor,
        ShadowActiveColor = file.ShadowActiveColor,
        LockedShadowColor = file.LockedShadowColor,
        MouseWheelZoomMode = file.MouseWheelZoomMode,
        MouseWheelOpacityStep = file.MouseWheelOpacityStep,
        MouseWheelZoomStep = file.MouseWheelZoomStep,
        ShowWindowButtons = file.ShowWindowButtons,
    };
}
