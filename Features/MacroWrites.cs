using System;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 宏写入的**唯一 UI 入口**：改名 / 删宏 / 试放。
/// </summary>
/// <remarks>
/// <para>
/// 改名与删宏会连带改动作（一宏一动作），动作的内容属主是执行器，所以宿主里的「键鼠宏」页
/// 只表达意图（<see cref="MacroBridge"/>），执行器改内存、落盘并回收；执行器自己的入口走本地
/// <see cref="MacroStore"/>。两条分支只在这里分，别在调用点各判一次（同 <see cref="ActionWrites"/>）。
/// </para>
/// <para>
/// 试放要键鼠注入，只能在执行器做：宿主经链路转过去，跑完由 <see cref="Played"/> 回来
/// （调用方据此把藏起来的窗口显示回来）。**失败提示只在这里的本地分支弹一次**——
/// 面板试放与设置页试放走的是同一个 <see cref="Play"/>，别在两个调用点各弹一套。
/// </para>
/// <para>
/// 「回放参数」（每宏的倒计时 / 速度 / 循环）与全局设置都是纯 <c>configs/macros.json</c> 读写，
/// 不经这里：宿主是那个文件的物理写者，写入后执行器靠 <c>config.changed</c> 重读。
/// </para>
/// </remarks>
internal static class MacroWrites
{
    /// <summary>试放跑完（失败原因，null = 正常放完）。</summary>
    public static event Action<string?>? Played;

    static MacroWrites()
    {
        if (AppRole.IsUiHost) MacroBridge.Played += error => Played?.Invoke(error);
    }

    /// <summary>改名（元数据 + 引用它的动作名）。</summary>
    public static bool Rename(string id, string name, out string? error)
    {
        error = null;

        if (!AppRole.IsUiHost)
        {
            MacroStore.Rename(id, name);
            return true;
        }

        if (!MacroBridge.Rename(id, name, out error)) return false;
        Refresh();
        return true;
    }

    /// <summary>删宏（事件文件 + 元数据 + 引用它的动作）；<paramref name="removedActions"/> = 一起删掉的动作数。</summary>
    public static bool Delete(string id, out int removedActions, out string? error)
    {
        removedActions = 0;
        error = null;

        if (!AppRole.IsUiHost)
        {
            removedActions = MacroStore.Delete(id);
            return true;
        }

        if (!MacroBridge.Delete(id, out removedActions, out error)) return false;
        Refresh();
        return true;
    }

    /// <summary>试放一个宏（后台回放，跑完发 <see cref="Played"/>）。</summary>
    public static bool Play(string id, out string? error)
    {
        if (AppRole.IsUiHost) return MacroBridge.Play(id, out error);
        return PlayLocal(id, out error);
    }

    private static bool PlayLocal(string id, out string? error)
    {
        error = null;

        if (MacroRecorder.IsRecording)
        {
            error = "正在录制，先停止录制再试放";
            return false;
        }
        if (MacroPlayer.IsPlaying)
        {
            error = "已有宏正在回放";
            return false;
        }

        MacroDto? macro = MacroStore.Load(id);
        if (macro == null)
        {
            error = "宏文件已不存在（通常随引用它的动作一起删除），请重新录制";
            return false;
        }

        _ = Task.Run(() =>
        {
            string? failure = null;
            try
            {
                MacroPlayer.PlaySync(macro, MacroStore.PlaybackOf(id));
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                AppLog.Error("Macro", $"试放失败「{macro.Name}」", ex);
                ToastService.Show("试放失败：" + ex.Message, "键鼠宏", ToastService.LongMs,
                    ToastKind.error, key: "macro:play:hint");
            }
            finally
            {
                Played?.Invoke(failure);
            }
        });
        return true;
    }

    /// <summary>执行器改完 <c>configs/macros.json</c>：宿主这份元数据缓存作废，已开的设置页跟着刷。</summary>
    private static void Refresh()
    {
        MacroStore.Invalidate();
        ConfigStore.NotifyChanged();
    }
}
