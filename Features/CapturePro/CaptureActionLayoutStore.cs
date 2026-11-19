using System;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 完成动作布局的读写：进程内缓存一份，改布局只走 <see cref="Save"/>
/// （只写 <c>configs/ui.json</c> 的那两个字段）。
/// </summary>
/// <remarks>
/// 与 <see cref="ToolLayoutStore"/> 同一套理由：覆盖层是常开的，每次截图都读一遍
/// <c>ui.json</c> 是白付磁盘 IO（鼠标钩子与 UI 同线程）。所以首次读盘后缓存，
/// 改布局时顺手更新缓存；别的地方改了 <c>ui.json</c>（导入备份）调 <see cref="Forget"/>。
/// </remarks>
internal static class CaptureActionLayoutStore
{
    private static CaptureActionLayout? _cached;

    /// <summary>当前布局（首次访问读盘，失败按默认）。</summary>
    public static CaptureActionLayout Current => _cached ??= Load();

    /// <summary>换一份布局并写盘（失败只记日志：布局是偏好，不该挡截图）。</summary>
    public static void Save(CaptureActionLayout layout)
    {
        _cached = layout;
        Write(layout);
    }

    /// <summary>丢掉缓存（导入备份 / 自检夹具改了 <c>ui.json</c> 之后调）。</summary>
    public static void Forget() => _cached = null;

    private static CaptureActionLayout Load()
    {
        try
        {
            string path = ConfigStore.ConfigPath;
            if (string.IsNullOrEmpty(path)) return CaptureActionLayout.Default();

            var ui = UiStore.Read(path);
            // 两个字段都空 = 还没保存过布局（首次启动走种子配置，拿到的是空集合、不是 null）。
            if (ui.CaptureActionGroups is not { Count: > 0 } && ui.CaptureActionHidden is not { Count: > 0 })
                return CaptureActionLayout.Default();
            return CaptureActionLayout.Parse(ui.CaptureActionGroups, ui.CaptureActionHidden);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "读取动作按钮布局失败：" + ex.Message);
            return CaptureActionLayout.Default();
        }
    }

    private static void Write(CaptureActionLayout layout)
    {
        try
        {
            void Change(UiStateFile ui)
            {
                ui.CaptureActionGroups = layout.ToLines();
                ui.CaptureActionHidden = layout.ToHidden();
            }

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) UiStore.Modify(Change);
            else UiStore.Modify(ConfigStore.ConfigPath, Change);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "保存动作按钮布局失败：" + ex.Message);
        }
    }
}
