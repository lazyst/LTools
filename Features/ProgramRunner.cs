using System;
using System.Collections.Generic;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Core.Link;

namespace LTools.Features;

/// <summary>
/// 编辑器「▶ 测试」的唯一入口：谁该跑这一份程序。
/// </summary>
/// <remarks>
/// <para>
/// 试运行要真注入键鼠、与真实运行共用那把互斥、停止热键也只在一侧——所以**永远由提权进程跑**。
/// 执行器里直接调 <see cref="AutomationRunner.TryRunProgram"/>；宿主（编辑器已经搬过去）转给执行器
/// （<see cref="ProgramRunBridge"/>），回调形状与本地那套完全一致。
/// </para>
/// <para>
/// 别在调用点各写一次 <c>AppRole.IsUiHost</c>：编辑器、子动作试运行、将来的设置页试跑都调这里。
/// </para>
/// </remarks>
internal static class ProgramRunner
{
    /// <summary>
    /// 跑一份内存里的程序。<c>false</c> = 没跑起来，<paramref name="error"/> 是可显示的原因
    /// （已有运行 / 宿主没连上 / 链路失败）。
    /// </summary>
    public static bool TryRunProgram(string name, ActionProgram program, string? startPath,
        Action<StepTrace>? trace, Action<ActionRunResult>? completed, out string? error,
        string? resourceRoot = null, bool dryRun = false, RunSource source = RunSource.Manual,
        Action<SlowStepInfo>? slowStep = null)
    {
        error = null;

        if (AppRole.IsUiHost)
        {
            return ProgramRunBridge.TryStart(name, program, startPath, trace, completed, resourceRoot,
                dryRun, source, slowStep, out error);
        }

        if (AutomationRunner.TryRunProgram(name, program, startPath, trace, completed, resourceRoot,
                dryRun, source, slowStep))
        {
            return true;
        }

        error = "已有动作正在运行，先等它结束或按停止";
        return false;
    }

    /// <summary>停止当前试运行（停的是执行器里那一次）。</summary>
    public static void Stop()
    {
        if (AppRole.IsUiHost) ProgramRunBridge.Stop();
        else AutomationRunner.Stop();
    }
}
