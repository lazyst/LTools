using System;
using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 宏录制的唯一 UI 入口：编辑器宏横幅上的「重新录制」用它。
/// </summary>
/// <remarks>
/// <para>
/// 执行器本地直接调 <see cref="MacroRecorder"/>；宿主经 <see cref="MacroBridge"/> 转给执行器
/// （录制要钩子）。事件也在这里归一，编辑器不需要知道自己在哪个进程。
/// </para>
/// <para>
/// 只有编辑器走这里：宏页（设置 → 键鼠宏）与托盘热键仍在执行器，直接用 <c>MacroRecorder</c>。
/// </para>
/// </remarks>
internal static class MacroRecording
{
    /// <summary>录制结束（没录到内容也算结束）。</summary>
    public static event Action? Stopped;

    /// <summary>宏已保存（宏 Id）。</summary>
    public static event Action<string>? Saved;

    static MacroRecording()
    {
        if (AppRole.IsUiHost)
        {
            MacroBridge.Stopped += () => Stopped?.Invoke();
            MacroBridge.Saved += id => Saved?.Invoke(id);
        }
        else
        {
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

        if (MacroRecorder.IsRecording) return true;

        // 具体原因 MacroRecorder 已经弹过托盘提示（宏功能关了 / 正在回放 / 有动作在跑）。
        error = "没能开始录制";
        return false;
    }

    public static void Stop(bool force = false)
    {
        if (AppRole.IsUiHost) MacroBridge.Stop();
        else MacroRecorder.Stop(force);
    }
}
