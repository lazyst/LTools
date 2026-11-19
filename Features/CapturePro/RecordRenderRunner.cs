using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using LTools.Core;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>渲染里的一步（一条 ffmpeg 命令）：占多少进度、成功后该存在哪个文件。</summary>
internal sealed class RecordRenderStep
{
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();

    /// <summary>成功后应存在且非空的文件；失败 / 取消时由调用方决定删不删。</summary>
    public string? ExpectedFile { get; init; }

    /// <summary>这一步在总进度里占多少（相对权重）。</summary>
    public double Weight { get; init; } = 1;

    /// <summary>建一步（<c>init</c> 属性在反射里不方便，给调用方与冒烟一个入口）。</summary>
    public static RecordRenderStep Of(IReadOnlyList<string> args, string? expectedFile = null, double weight = 1) =>
        new() { Args = args, ExpectedFile = expectedFile, Weight = weight };
}

/// <summary>一次渲染的结果。</summary>
internal sealed class RecordRenderOutcome
{
    /// <summary>全部步骤跑成功。</summary>
    public bool Ok { get; set; }

    /// <summary>用户取消了渲染（或选了丢弃）。</summary>
    public bool Cancelled { get; set; }

    /// <summary>用户选了「丢弃」——源文件也不要了。</summary>
    public bool Discarded { get; set; }

    /// <summary>用户选了「保留源文件」——调用方应沿用源文件（别当成失败丢掉录制）。</summary>
    public bool KeptSource { get; set; }

    public string Error { get; set; } = "";

    /// <summary>真的弹出过对话框（冒烟断言用）。</summary>
    public bool ShowedDialog { get; set; }

    /// <summary>跑了几轮（重试过一次就是 2）。</summary>
    public int Attempts { get; set; }
}

/// <summary>
/// 渲染任务的执行器（照 Snow Shot 的 <c>RecordingRenderJob</c>）：跑一组 ffmpeg 命令、把进度推给
/// <see cref="RecordRenderWindow"/>、支持取消与失败重试。
/// </summary>
/// <remarks>
/// <para>
/// 失败 / 取消之后**不自己丢源文件**——停在对话框上等用户选「重试 / 保留源文件 / 丢弃」，
/// 选什么由 <see cref="RecordRenderOutcome"/> 带回去，删不删由调用方按自己的产物说话。
/// 没有界面（应用正在退出 / 冒烟）时退回「沿用源文件」。
/// </para>
/// <para>
/// 进度走 ffmpeg 的 <c>-progress pipe:1</c>：每步按权重换算到总百分比，只增不减；
/// 取消每 60ms 问一次对话框，按了就杀进程树。
/// </para>
/// </remarks>
internal static class RecordRenderRunner
{
    public static RecordRenderOutcome Run(string ffmpeg, string sourcePath, Int32Rect anchor,
        int durationMs, IReadOnlyList<RecordRenderStep> steps, bool showDialog = true)
    {
        var outcome = new RecordRenderOutcome();
        if (steps.Count == 0)
        {
            outcome.Ok = true;
            return outcome;
        }

        var state = new RecordRenderState { Path = sourcePath, Stage = RecordRenderStage.Prepare };
        var window = showDialog ? Show(sourcePath, anchor) : null;
        if (window != null) outcome.ShowedDialog = true;

        try
        {
            while (true)
            {
                outcome.Attempts++;
                state.Retained = false;
                state.Error = "";
                state.CancelRequested = false;
                state.Percent = 0;
                state.Stage = RecordRenderStage.Prepare;
                Reset(window);
                Push(window, state);

                bool cancelled = false;
                string error = "";

                for (int i = 0; i < steps.Count; i++)
                {
                    state.Stage = RecordRenderStage.Render;
                    Push(window, state);

                    double before = PercentBefore(steps, i);
                    double span = PercentSpan(steps, i);

                    var result = RunStep(ffmpeg, steps[i], durationMs, before, span, state, window);
                    if (result.Ok) continue;

                    cancelled = result.Cancelled;
                    error = result.Error;
                    break;
                }

                if (cancelled || error.Length > 0)
                {
                    // 失败原因先记进结果：之后无论用户选「丢弃」还是「保留源文件」，
                    // 调用方都还能说清「为什么没成」（以前丢弃分支把它丢了）。
                    outcome.Error = error;

                    if (window == null)
                    {
                        outcome.Cancelled = cancelled;
                        break;
                    }

                    state.Retained = true;
                    state.Error = error;
                    state.CancelRequested = false;
                    state.Stage = RecordRenderStage.Render;
                    Push(window, state);

                    var choice = WaitForChoice(window);
                    if (choice == RecordRenderChoice.Retry) continue;
                    if (choice == RecordRenderChoice.Keep)
                    {
                        outcome.KeptSource = true;
                        break;
                    }

                    outcome.Discarded = true;
                    outcome.Cancelled = true;
                    break;
                }

                state.Stage = RecordRenderStage.Finalize;
                state.Percent = 100;
                state.TerminalSucceeded = true;
                Push(window, state);
                outcome.Ok = true;
                break;
            }
        }
        finally
        {
            Close(window);
        }

        return outcome;
    }

    // ============ 单步 ============

    private static (bool Ok, bool Cancelled, string Error) RunStep(string ffmpeg, RecordRenderStep step,
        int durationMs, double before, double span, RecordRenderState state, RecordRenderWindow? window)
    {
        var args = RecordRender.WithProgress(step.Args);

        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);

        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex)
        {
            return (false, false, ex.Message);
        }

        if (process == null) return (false, false, "启动 ffmpeg 失败");

        using (process)
        {
            var errorText = process.StandardError.ReadToEndAsync();
            bool cancelled = false;
            double percent = before;

            process.OutputDataReceived += (_, e) =>
            {
                if (!RecordRender.TryReadProgress(e.Data, out long micros)) return;
                double inner = RecordRender.PercentOf(micros, durationMs) / 100.0 * span;
                percent = Math.Max(percent, before + inner);
            };
            process.BeginOutputReadLine();

            while (!process.WaitForExit(60))
            {
                state.Percent = RecordRender.Advance(state.Percent, percent);
                Push(window, state);

                if (window != null && ReadCancel(window))
                {
                    cancelled = true;
                    try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
                    process.WaitForExit(5000);
                    break;
                }
            }

            // WaitForExit(ms) 只保证进程退出，异步输出还要再等一次
            try { process.WaitForExit(); } catch { /* 已经退出 */ }

            string stderr = "";
            try { stderr = errorText.GetAwaiter().GetResult().Trim(); } catch { /* 读不到就算了 */ }

            state.Percent = RecordRender.Advance(state.Percent, cancelled ? state.Percent : before + span);
            Push(window, state);

            if (cancelled) return (false, true, "");
            if (process.ExitCode == 0) return (true, false, "");

            string error = stderr.Length <= 300 ? stderr : stderr[^300..];
            return (false, false, error.Length == 0 ? "ffmpeg 退出码 " + process.ExitCode : error);
        }
    }

    /// <summary>重试前清掉窗口里上一次的取消 / 选择（否则新一轮开工就被上一轮的取消顶掉）。</summary>
    private static void Reset(RecordRenderWindow? window)
    {
        if (window == null) return;

        try { window.Dispatcher.Invoke(window.ResetAttempt); }
        catch (Exception) { /* 窗口已经关了 */ }
    }

    private static double PercentBefore(IReadOnlyList<RecordRenderStep> steps, int index)
    {
        double total = 0;
        for (int i = 0; i < steps.Count; i++) total += Math.Max(0.0001, steps[i].Weight);

        double before = 0;
        for (int i = 0; i < index; i++) before += Math.Max(0.0001, steps[i].Weight);
        return before / total * 100;
    }

    private static double PercentSpan(IReadOnlyList<RecordRenderStep> steps, int index)
    {
        double total = 0;
        for (int i = 0; i < steps.Count; i++) total += Math.Max(0.0001, steps[i].Weight);
        return Math.Max(0.0001, steps[index].Weight) / total * 100;
    }

    // ============ 窗口 ============

    private static RecordRenderWindow? Show(string sourcePath, Int32Rect anchor)
    {
        try
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return null;

            return app.Dispatcher.Invoke(() =>
            {
                var window = new RecordRenderWindow(sourcePath, anchor);
                window.Show();
                return window;
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "打开渲染进度窗失败：" + ex.Message);
            return null;
        }
    }

    private static void Push(RecordRenderWindow? window, RecordRenderState state)
    {
        if (window == null) return;

        try
        {
            var copy = state.Clone();
            window.Dispatcher.Invoke(() => window.Apply(copy));
        }
        catch (Exception)
        {
            // 窗口已经关了 / 应用正在退出：这一拍丢掉就行
        }
    }

    /// <summary>等用户按一个按钮（背景线程轮询，80ms 一次）。</summary>
    private static RecordRenderChoice WaitForChoice(RecordRenderWindow window)
    {
        try { window.Dispatcher.Invoke(window.BeginAsk); }
        catch { return RecordRenderChoice.Keep; }

        while (true)
        {
            try
            {
                var choice = window.Dispatcher.Invoke(() => window.Choice);
                if (choice != RecordRenderChoice.None) return choice;
            }
            catch (Exception)
            {
                return RecordRenderChoice.Keep;
            }

            Thread.Sleep(80);
        }
    }

    private static bool ReadCancel(RecordRenderWindow window)
    {
        try
        {
            return window.Dispatcher.Invoke(() => window.CancelRequested);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void Close(RecordRenderWindow? window)
    {
        if (window == null) return;

        try { window.Dispatcher.Invoke(window.CloseQuietly); }
        catch (Exception) { /* 已经关了 */ }
    }
}
