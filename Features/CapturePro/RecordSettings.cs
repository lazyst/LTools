using System;
using LTools.Config;
using LTools.Core;
using LTools.Features;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录屏的全局默认值（上游 <c>screen_recording/*</c> 里那些「设置页改一次、之后每次录都用」的项）。
/// </summary>
/// <remarks>
/// <para>
/// 真源是 <c>configs/settings.json</c> 的 <c>Record</c> 段，<see cref="AppState.Record"/> 是运行期缓存
/// （录屏线程与面板直接读它，避免每次开录都读盘）。这里只负责「改一项 → 归一 → 落盘 + 刷缓存」，
/// 与 <see cref="DrawingSettings"/> / <see cref="ColorPresets"/> 同一套写法。
/// </para>
/// <para>
/// 与「录屏选项」窗口（<c>Views/CapturePro/RecordOptionsWindow</c>）的分工：那个窗口只改**这次录什么**
/// （声音 / 摄像头 / 光标 / 点击 / 按键 / 编码器 / 裁剪），保存时用
/// <see cref="RecordOptions.CopySettingsFrom"/> 把这里的字段原样搬回去；反过来本类也只动自己这批字段。
/// </para>
/// </remarks>
internal static class RecordSettings
{
    /// <summary>当前设置（首次访问时给一份默认值，<see cref="AppState.Record"/> 永远非 null）。</summary>
    public static RecordOptions Current => AppState.Record ??= new RecordOptions();

    /// <summary>改一批字段：先归一，再刷运行期缓存，最后只写 <c>configs/settings.json</c>。</summary>
    public static void Modify(Action<RecordOptions> change)
    {
        if (change == null) return;

        var options = Current.Clone();
        change(options);
        var normalized = options.Normalized();
        AppState.Record = normalized;

        try
        {
            // 锚点跟 ConfigStore 走：开发时是仓库根、发布后是 exe 同级，
            // 冒烟把 ConfigStore.ConfigPath 指到临时目录时也就写到那儿。
            string anchor = ConfigStore.ConfigPath;
            if (string.IsNullOrEmpty(anchor)) SettingsStore.Modify(file => file.Record = normalized);
            else SettingsStore.Modify(anchor, file => file.Record = normalized);
        }
        catch (Exception ex)
        {
            // 设置页改一项写不进去不该弹框打断；录屏本身照常能用（内存里已经是新值）
            AppLog.Warn("CapturePro.Record", "保存录屏设置失败：" + ex.Message);
        }
    }
}
