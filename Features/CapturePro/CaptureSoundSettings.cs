using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 快门音的两个开关（<c>configs/settings.json</c> 的 <c>Screenshot</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>screenshot/shutter_sound_notification</c>（直接截前台窗口 / 当前显示器，默认<b>开</b>）
/// 与 <c>screenshot/screenshot_sound_notification</c>（覆盖层开始截图，默认<b>关</b>）。
/// 为什么默认值不一样：直接截图没有可见的框选过程，用户需要一声确认；覆盖层本来就「啪」地盖上来，
/// 再响一声反而吵（上游同一个取舍）。
/// </para>
/// <para>
/// 读盘只发生一次（截图入口在钩子 / 面板线程上问，不能每次读 JSON），写盘只写 settings 域。
/// 音频文件本身与播放实现在 <see cref="ShutterSound"/>。
/// </para>
/// </remarks>
internal static class CaptureSoundSettings
{
    /// <summary>直接截前台窗口 / 当前显示器的默认值（上游默认开）。</summary>
    public const bool DefaultShutter = true;

    /// <summary>覆盖层开始截图的默认值（上游默认关）。</summary>
    public const bool DefaultScreenshot = false;

    private static readonly object Gate = new();

    private static bool? _shutter;
    private static bool? _screenshot;

    /// <summary>直接截图时响不响（默认开）。</summary>
    public static bool Shutter
    {
        get { LoadAll(); lock (Gate) return _shutter!.Value; }
    }

    /// <summary>覆盖层开始截图时响不响（默认关）。</summary>
    public static bool Screenshot
    {
        get { LoadAll(); lock (Gate) return _screenshot!.Value; }
    }

    public static void SetShutter(bool on)
    {
        LoadAll();
        lock (Gate) _shutter = on;
        Save(file => (file.Screenshot ??= new ScreenshotFile()).ShutterSound = on);
    }

    public static void SetScreenshot(bool on)
    {
        LoadAll();
        lock (Gate) _screenshot = on;
        Save(file => (file.Screenshot ??= new ScreenshotFile()).ScreenshotSound = on);
    }

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _shutter = null;
            _screenshot = null;
        }
    }

    private static void LoadAll()
    {
        lock (Gate)
        {
            if (_shutter != null && _screenshot != null) return;
        }

        bool shutter = DefaultShutter;
        bool screenshot = DefaultScreenshot;

        try
        {
            var file = SettingsStore.Read(ConfigStore.Anchor);
            shutter = file.Screenshot?.ShutterSound ?? DefaultShutter;
            screenshot = file.Screenshot?.ScreenshotSound ?? DefaultScreenshot;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.CaptureSoundSettings", "读取快门音设置失败：" + ex.Message);
        }

        lock (Gate)
        {
            _shutter ??= shutter;
            _screenshot ??= screenshot;
        }
    }

    private static void Save(Action<SettingsFile> mutate)
    {
        try
        {
            SettingsStore.Modify(ConfigStore.Anchor, mutate);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.CaptureSoundSettings", "保存快门音设置失败：" + ex.Message);
        }
    }
}
