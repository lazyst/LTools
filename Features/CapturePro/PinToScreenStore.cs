using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 贴图行为开关的读写（<c>configs/settings.json</c> 的 <c>PinToScreen</c> 段）。
/// </summary>
/// <remarks>
/// <para>
/// 照上游 <c>PinToScreenSettings::shakeWindowToHideOthers()</c>：拖动贴图时抖动手势用来隐藏 / 显示
/// 同组其他贴图，默认开。**拖动路径上每帧都要问一次**，所以读盘只发生一次、之后走进程内缓存——
/// 在 <c>MouseMove</c> 里读 JSON 会把光标顶住。
/// </para>
/// <para>
/// 读写锚点与 <see cref="PinTextStore"/> 一致：优先 <c>ConfigStore.ConfigPath</c>，
/// 没初始化才回落 <see cref="ConfigLocator.FindPath"/>，读与写必须是同一个。
/// </para>
/// </remarks>
internal static class PinToScreenStore
{
    private static readonly object Gate = new();

    private static bool? _shake;

    private static string Anchor =>
        string.IsNullOrEmpty(ConfigStore.ConfigPath) ? ConfigLocator.FindPath() : ConfigStore.ConfigPath;

    /// <summary>抖动隐藏同组其他贴图（默认开；读失败也按开）。</summary>
    public static bool ShakeWindowToHideOthers
    {
        get
        {
            lock (Gate)
            {
                if (_shake != null) return _shake.Value;
            }

            bool value = true;
            try
            {
                value = SettingsStore.Read(Anchor).PinToScreen?.ShakeWindowToHideOthers ?? true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.PinToScreen", "读取贴图行为开关失败：" + ex.Message);
            }

            lock (Gate) _shake = value;
            return value;
        }
    }

    /// <summary>改抖动开关（写完立刻生效，不必重开贴图）。</summary>
    public static void SetShakeWindowToHideOthers(bool on)
    {
        lock (Gate) _shake = on;

        try
        {
            SettingsStore.Modify(Anchor, file =>
            {
                var section = file.PinToScreen ??= new PinToScreenFile();
                section.ShakeWindowToHideOthers = on;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.PinToScreen", "保存贴图行为开关失败：" + ex.Message);
        }
    }

    /// <summary>配置被外部改过（导入备份 / 冒烟切目录）时丢掉缓存。</summary>
    public static void Invalidate()
    {
        lock (Gate) _shake = null;
    }
}
