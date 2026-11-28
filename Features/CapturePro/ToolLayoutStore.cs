using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 工具栏布局的读写：进程内缓存一份，改布局只走 <see cref="Save"/>（只写 <c>configs/ui.json</c> 那两个字段）。
/// </summary>
/// <remarks>
/// 覆盖层是常开的：每次截图都读一遍 <c>ui.json</c> 是白付磁盘 IO（鼠标钩子与 UI 同线程）。
/// 所以首次读盘后缓存，改布局时顺手更新缓存；别的地方改了 <c>ui.json</c>（导入备份）调 <see cref="Forget"/>。
/// </remarks>
internal static class ToolLayoutStore
{
    private static ToolLayout? _cached;

    /// <summary>当前布局（首次访问读盘，失败按目录默认）。</summary>
    public static ToolLayout Current => _cached ??= Load();

    /// <summary>换一份布局并写盘（失败只记日志：布局是偏好，不该挡截图）。</summary>
    public static void Save(ToolLayout layout)
    {
        _cached = layout;
        Write(layout);
    }

    /// <summary>丢掉缓存（导入备份 / 自检夹具改了 <c>ui.json</c> 之后调）。</summary>
    public static void Forget() => _cached = null;

    private static ToolLayout Load()
    {
        try
        {
            string path = ConfigStore.ConfigPath;
            if (string.IsNullOrEmpty(path)) return ToolLayout.Default();

            var ui = UiStore.Read(path);
            // 组列表为空 = 还没保存过布局（首次启动走种子配置，UiStateFile 拿到的是空集合、
            // 不是 null）。这时必须回到目录默认分组，否则会把 27 个工具摊成 27 格。
            // 「把工具全藏起来」是「组空 + hidden 非空」，那条仍照保存内容解析。
            if (ui.CaptureToolGroups is not { Count: > 0 } && ui.CaptureToolHidden is not { Count: > 0 })
                return ToolLayout.Default();

            // 从没改过布局的老配置（还是上一版 17 格默认）→ 跟新版默认走；
            // 用户动过一格就原样保留，别把人家排好的工具条冲掉。
            if (ToolLayout.IsLegacyDefault(ui.CaptureToolGroups, ui.CaptureToolHidden))
                return ToolLayout.Default();

            return ToolLayout.Parse(ui.CaptureToolGroups, ui.CaptureToolHidden);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "读取工具栏布局失败：" + ex.Message);
            return ToolLayout.Default();
        }
    }

    private static void Write(ToolLayout layout)
    {
        try
        {
            void Change(UiStateFile ui)
            {
                ui.CaptureToolGroups = layout.ToLines();
                ui.CaptureToolHidden = layout.ToHidden();
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) UiStore.Modify(Change);
            else UiStore.Modify(ConfigStore.ConfigPath, Change);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "保存工具栏布局失败：" + ex.Message);
        }
    }
}
