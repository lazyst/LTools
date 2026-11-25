using System;
using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 宏录制的唯一 UI 入口：编辑器宏横幅与「键鼠宏」页的录制按钮都走它。
/// </summary>
/// <remarks>
/// <para>
/// 执行器本地直接调 <see cref="MacroRecorder"/>；宿主经 <see cref="MacroBridge"/> 转给执行器
/// （录制要钩子）。事件也在这里归一，界面不需要知道自己在哪个进程。
/// </para>
/// <para>
/// 事件是**广播**：热键 / 托盘 / 面板菜单开始的录制同样会通知到已经打开的窗口，
/// 所以「录制一开始就把自己藏起来」这件事只写在这里，别各页自己去问状态。
/// </para>
/// </remarks>
internal static class MacroRecording
{
    /// <summary>录制开始（谁发起的都算）。</summary>
    public static event Action? Started;

    /// <summary>录制结束（没录到内容也算结束）。</summary>
    public static event Action? Stopped;

    /// <summary>宏已保存（宏 Id）。</summary>
    public static event Action<string>? Saved;

    static MacroRecording()
    {
        if (AppRole.IsUiHost)
        {
            MacroBridge.Started += () => Started?.Invoke();
            MacroBridge.Stopped += () => Stopped?.Invoke();
            MacroBridge.Saved += id => Saved?.Invoke(id);
        }
        else
        {
            MacroRecorder.RecordingStarted += () => Started?.Invoke();
            MacroRecorder.RecordingStopped += () => Stopped?.Invoke();
            MacroRecorder.MacroSaved += id => Saved?.Invoke(id);
        }
    }

    public static bool IsRecording => AppRole.IsUiHost ? MacroBridge.IsRecording : MacroRecorder.IsRecording;

    /// <summary>开始录制（<paramref name="id"/> 非空 = 重新录制到那个宏）。</summary>
    public static bool StartInto(string? id, string? name, out string? error)
    {
        error = null;

        if (AppRole.IsUiHost) return MacroBridge.StartInto(id, name, out error);

        if (string.IsNullOrWhiteSpace(id)) MacroRecorder.Start();
        else MacroRecorder.StartInto(id, name);

        // 具体原因 MacroRecorder 已经弹过托盘提示（宏功能关了 / 正在回放 / 有动作在跑）：
        // 返回 false + 空原因，调用方就别再弹第二张卡片。
        return MacroRecorder.IsRecording;
    }

    public static void Stop(bool force = false)
    {
        if (AppRole.IsUiHost) MacroBridge.Stop();
        else MacroRecorder.Stop(force);
    }
}
