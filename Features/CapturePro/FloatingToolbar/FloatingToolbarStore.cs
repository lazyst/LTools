using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 悬浮工具条的配置（照 Snow Shot 的 <c>floating_toolbar/*</c>）：开关 / 模式 / 不透明度 /
/// 拖放行为 / 全屏隐藏 / 截图时隐藏。
/// </summary>
internal sealed record FloatingToolbarSettings(bool Enabled, string Mode, int Opacity,
    string DropBehavior, bool HideInFullscreen, bool HideDuringCapture)
{
    public const string ModeIcon = "icon";
    public const string ModeToolbar = "toolbar";

    public const string DropPin = "pin_to_screen";
    public const string DropStitch = "image_stitching";
    public const string DropSplit = "image_splitting";
    public const string DropEdit = "image_editing";

    public const int MinOpacity = 10;
    public const int MaxOpacity = 100;

    public static readonly FloatingToolbarSettings Default =
        new(false, ModeIcon, 50, DropPin, true, true);

    public bool IsIconMode => Mode == ModeIcon;
}

/// <summary>悬浮工具条配置的读写（只写 <c>settings.json</c> 的 <c>FloatingToolbar</c> 段）。</summary>
internal static class FloatingToolbarStore
{
    private static readonly object Gate = new();
    private static FloatingToolbarFile? _cached;

    /// <summary>配置变了（服务据此重建 / 收起工具条）。</summary>
    public static event Action? Changed;

    public static FloatingToolbarSettings Current => Read(Snapshot());

    /// <summary>布局（归一过）。</summary>
    public static FloatingToolbarLayout Layout =>
        FloatingToolbarLayout.Parse(Snapshot().Positions, Snapshot().Hidden);

    /// <summary>落点；<c>null</c> = 还没摆过。</summary>
    public static FloatingToolbarPlacement? Placement => Snapshot().Placement;

    public static string ModeOf(string? mode) =>
        (mode ?? "").Trim().ToLowerInvariant() == FloatingToolbarSettings.ModeToolbar
            ? FloatingToolbarSettings.ModeToolbar
            : FloatingToolbarSettings.ModeIcon;

    public static string DropBehaviorOf(string? behavior) =>
        (behavior ?? "").Trim().ToLowerInvariant() switch
        {
            FloatingToolbarSettings.DropStitch => FloatingToolbarSettings.DropStitch,
            FloatingToolbarSettings.DropSplit => FloatingToolbarSettings.DropSplit,
            FloatingToolbarSettings.DropEdit => FloatingToolbarSettings.DropEdit,
            _ => FloatingToolbarSettings.DropPin,
        };

    public static int NormalizeOpacity(int percent) =>
        Math.Clamp(percent <= 0 ? FloatingToolbarSettings.Default.Opacity : percent,
            FloatingToolbarSettings.MinOpacity, FloatingToolbarSettings.MaxOpacity);

    public static string ModeLabel(string? mode) => ModeOf(mode) == FloatingToolbarSettings.ModeToolbar
        ? "一直展开"
        : "图标（悬停展开）";

    public static string DropLabel(string? behavior) => DropBehaviorOf(behavior) switch
    {
        FloatingToolbarSettings.DropStitch => "拼长图",
        FloatingToolbarSettings.DropSplit => "拆分图片",
        FloatingToolbarSettings.DropEdit => "打开图片编辑",
        _ => "贴到屏幕",
    };

    public static void SetEnabled(bool on) => Save(file => file.Enabled = on);
    public static void SetMode(string? mode) => Save(file => file.Mode = ModeOf(mode));
    public static void SetOpacity(int percent) => Save(file => file.Opacity = NormalizeOpacity(percent));
    public static void SetDropBehavior(string? behavior) =>
        Save(file => file.DropBehavior = DropBehaviorOf(behavior));
    public static void SetHideInFullscreen(bool on) => Save(file => file.HideInFullscreen = on);
    public static void SetHideDuringCapture(bool on) => Save(file => file.HideDuringCapture = on);

    /// <summary>布局（只动这两个字段）。</summary>
    public static void SaveLayout(FloatingToolbarLayout layout) => Save(file =>
    {
        file.Positions = layout.ToPositions();
        file.Hidden = layout.ToHidden();
    });

    /// <summary>落点（拖动结束写一次；不触发重建，工具条已经在那个位置）。</summary>
    public static void SavePlacement(double left, double top)
    {
        lock (Gate)
        {
            if (_cached != null) _cached.Placement = new FloatingToolbarPlacement { Left = left, Top = top };
        }

        WriteUnsafe(file => file.Placement = new FloatingToolbarPlacement { Left = left, Top = top });
    }

    /// <summary>外部改了 <c>settings.json</c>（导入备份 / 冒烟换锚点）后调一次。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    private static void Save(Action<FloatingToolbarFile> change)
    {
        lock (Gate) _cached = null;
        WriteUnsafe(change);
        Changed?.Invoke();
    }

    private static void WriteUnsafe(Action<FloatingToolbarFile> change)
    {
        try
        {
            void Apply(SettingsFile settings)
            {
                settings.FloatingToolbar ??= new FloatingToolbarFile();
                change(settings.FloatingToolbar);
            }

            SettingsStore.Modify(ConfigStore.Anchor, Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.FloatingBar", "保存悬浮工具条设置失败：" + ex.Message);
        }
    }

    private static FloatingToolbarFile Snapshot()
    {
        lock (Gate)
        {
            if (_cached != null) return _cached;
        }

        FloatingToolbarFile file;
        try
        {
            file = SettingsStore.Read(ConfigStore.Anchor).FloatingToolbar ?? new FloatingToolbarFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.FloatingBar", "读取悬浮工具条设置失败：" + ex.Message);
            file = new FloatingToolbarFile();
        }

        lock (Gate) _cached = file;
        return file;
    }

    /// <summary>归一后的设置（默认值逐字照上游）。</summary>
    public static FloatingToolbarSettings Read(FloatingToolbarFile file) => new(
        file.Enabled,
        ModeOf(file.Mode),
        NormalizeOpacity(file.Opacity),
        DropBehaviorOf(file.DropBehavior),
        file.HideInFullscreen,
        file.HideDuringCapture);
}
