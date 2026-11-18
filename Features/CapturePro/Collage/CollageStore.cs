using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro.Collage;

/// <summary>
/// 拼图设置的读写：<c>configs/settings.json</c> 的 <c>Collage</c> 段（照上游
/// <c>collagesettingsstore</c> 的键名）。
/// </summary>
/// <remarks>
/// 和 <see cref="TranslationStore"/> 同一套办法：不缓存、不防抖，只在开窗 / 关窗和改设置时
/// 读一次、写一次，永远只写这一个域。**就地改字段**，别整体替换 <see cref="CollageFile"/>
/// （以后加的字段会被抹掉）。
/// </remarks>
internal static class CollageStore
{
    /// <summary>当前设置（没配过的字段按默认值补齐）。</summary>
    public static CollageSettings Load()
    {
        CollageFile file = Read();
        return new CollageSettings(
            CollageSettings.ParseDirection(file.Direction),
            CollageSettings.ParseSizing(file.Sizing),
            Math.Max(0, file.BorderPx),
            Math.Max(0, file.SpacingPx),
            CollageColor.Parse(file.Background),
            Math.Max(1, file.Rows),
            Math.Max(1, file.Columns),
            file.CustomGridInitialized);
    }

    /// <summary>整份写回（方向 / 尺寸方式 / 外边距 / 间距 / 背景色 / 宫格行列）。</summary>
    public static void Save(CollageSettings settings)
    {
        try
        {
            void Apply(SettingsFile file)
            {
                file.Collage ??= new CollageFile();
                file.Collage.Direction = settings.DirectionKey;
                file.Collage.Sizing = settings.SizingKey;
                file.Collage.BorderPx = Math.Max(0, settings.Border);
                file.Collage.SpacingPx = Math.Max(0, settings.Spacing);
                file.Collage.Background = CollageColor.Format(settings.Background);
                file.Collage.Rows = Math.Max(1, settings.Rows);
                file.Collage.Columns = Math.Max(1, settings.Columns);
                file.Collage.CustomGridInitialized = settings.CustomGridInitialized;
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) SettingsStore.Modify(Apply);
            else SettingsStore.Modify(ConfigStore.ConfigPath, Apply);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Collage", "保存拼图设置失败：" + ex.Message);
        }
    }

    private static CollageFile Read()
    {
        try
        {
            string path = ConfigStore.ConfigPath;
            return string.IsNullOrEmpty(path)
                ? new CollageFile()
                : SettingsStore.Read(path).Collage ?? new CollageFile();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Collage", "读取拼图设置失败：" + ex.Message);
            return new CollageFile();
        }
    }
}
