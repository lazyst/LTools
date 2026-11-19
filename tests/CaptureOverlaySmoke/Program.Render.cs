using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LTools.Views.CapturePro;

namespace CaptureOverlaySmoke;

/// <summary>
/// 录屏渲染进度对话框（P8-c，照 Snow Shot 的 <c>RecordingRenderDialog</c> / <c>RecordingRenderJob</c>）：
/// 结构 / 状态机 / 按钮，以及三条真 ffmpeg 的端到端（成功 / 失败后丢弃 / 取消后保留源文件）。
/// </summary>
internal static partial class Program
{
    private const BindingFlags RenderAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags RenderStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private static void RecordRenderSection()
    {
        var asm = typeof(CaptureOverlayWindow).Assembly;
        var windowType = asm.GetType("LTools.Views.CapturePro.RecordRenderWindow")!;
        var stateType = asm.GetType("LTools.Features.CapturePro.RecordRenderState")!;
        var stageType = asm.GetType("LTools.Features.CapturePro.RecordRenderStage")!;
        var runnerType = asm.GetType("LTools.Features.CapturePro.RecordRenderRunner")!;
        var stepType = asm.GetType("LTools.Features.CapturePro.RecordRenderStep")!;
        var planType = asm.GetType("LTools.Features.CapturePro.RecordPlan")!;

        // ---- ① 结构 / 状态机 ----
        var window = (Window)Activator.CreateInstance(windowType, RenderAny, null,
            new object[] { @"C:\tmp\rec-source", new Int32Rect(200, 200, 320, 240) }, null)!;
        window.Show();
        Pump(window);

        Check(Math.Abs(window.Width - 500) < 0.5, $"渲染进度窗宽 500（实际 {window.Width:0}）");
        Check(window.WindowStyle == WindowStyle.None, "没有系统标题栏（照上游的自绘面板）");

        var status = (TextBlock)Field(window, "StatusText");
        var details = (TextBox)Field(window, "DetailsText");
        var progress = (ProgressBar)Field(window, "Progress");
        var cancel = (Button)Field(window, "CancelBtn");
        var retry = (Button)Field(window, "RetryBtn");
        var keep = (Button)Field(window, "KeepBtn");
        var discard = (Button)Field(window, "DiscardBtn");

        Check(status.Text == "正在准备导出录屏…", $"初始状态行是「准备导出」（{status.Text}）");
        Check(details.Visibility != Visibility.Visible, "渲染中不显示细节区");
        Check(cancel.Visibility == Visibility.Visible && cancel.IsEnabled, "渲染中只有「取消」且可点");
        Check(retry.Visibility != Visibility.Visible && keep.Visibility != Visibility.Visible
            && discard.Visibility != Visibility.Visible, "渲染中不显示重试 / 保留 / 丢弃");
        Check(progress.Minimum == 0 && progress.Maximum == 100, "进度条 0–100");

        object State(double percent, string stage, bool retained = false, string error = "",
            bool cancelRequested = false, bool busy = false, bool done = false, string path = @"C:\tmp\rec-source")
        {
            var state = Activator.CreateInstance(stateType)!;
            stateType.GetProperty("Percent")!.SetValue(state, percent);
            stateType.GetProperty("Stage")!.SetValue(state, Enum.Parse(stageType, stage));
            stateType.GetProperty("Retained")!.SetValue(state, retained);
            stateType.GetProperty("Error")!.SetValue(state, error);
            stateType.GetProperty("CancelRequested")!.SetValue(state, cancelRequested);
            stateType.GetProperty("Busy")!.SetValue(state, busy);
            stateType.GetProperty("TerminalSucceeded")!.SetValue(state, done);
            stateType.GetProperty("Path")!.SetValue(state, path);
            return state;
        }

        var apply = windowType.GetMethod("Apply", RenderAny)!;
        void Apply(object state) { apply.Invoke(window, new[] { state }); Pump(window); }

        Apply(State(42.7, "Render"));
        Check(status.Text == "正在渲染视频…", $"渲染阶段文案（{status.Text}）");
        Check(Math.Abs(progress.Value - 42) < 0.001, $"百分比向下取整（{progress.Value:0}）");
        Apply(State(18, "Render"));
        Check(Math.Abs(progress.Value - 18) < 0.001,
            "窗口照状态画（只增不减是 RecordRender.Advance 的事，自检里守着）");
        Apply(State(100, "Finalize"));
        Check(status.Text == "正在完成录屏…", $"收尾阶段文案（{status.Text}）");

        Apply(State(100, "Render", done: true));
        var successBrush = (System.Windows.Media.Brush)window.FindResource("CaptureSuccess");
        Check(Equals(progress.Foreground, successBrush), "成功后进度条转成功色");

        // 取消：按一下就置位 + 按钮禁用（渲染线程每 60ms 读一次）
        Apply(State(55, "Render"));
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check((bool)windowType.GetProperty("CancelRequested", RenderAny)!.GetValue(window)!, "点「取消」置位");
        Check(status.Text == "正在取消渲染…", $"取消中文案（{status.Text}）");
        Check(!cancel.IsEnabled, "取消中按钮禁用（不重复发）");

        // 失败 / 取消后停下来：细节区 + 三个按钮
        Apply(State(55, "Render", retained: true, error: "ffmpeg 炸了"));
        Check(status.Text == "渲染失败", $"失败后文案（{status.Text}）");
        Check(details.Visibility == Visibility.Visible, "失败后显示细节区");
        Check(details.Text.Contains("ffmpeg 炸了") && details.Text.Contains(@"C:\tmp\rec-source"),
            "细节区写明原因与源文件目录");
        Check(cancel.Visibility != Visibility.Visible, "失败后收起「取消」");
        Check(retry.Visibility == Visibility.Visible && keep.Visibility == Visibility.Visible
            && discard.Visibility == Visibility.Visible, "失败后显示重试 / 保留源文件 / 丢弃");
        Check(retry.IsEnabled && keep.IsEnabled && discard.IsEnabled, "停下来之后三个按钮都可点");

        Apply(State(10, "Render", retained: true, error: ""));
        Check(status.Text == "已取消渲染", $"主动取消（没有错误）时文案（{status.Text}）");

        // 用户的选择：点重试 → Choice = Retry；再复位然后点保留 → Keep
        retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check(windowType.GetProperty("Choice", RenderAny)!.GetValue(window)!.ToString() == "Retry",
            "点「重试」把选择交出去");
        Check(!retry.IsEnabled, "选过之后按钮禁用，等渲染线程接手");

        var beginAsk = windowType.GetMethod("BeginAsk", RenderAny)!;
        var resetAttempt = windowType.GetMethod("ResetAttempt", RenderAny)!;

        // 「进入等待」不该抹掉已经按下的选择（渲染线程推完状态到开始轮询之间，
        // 用户完全可能已经点了按钮——抹掉就永远等不到）
        beginAsk.Invoke(window, null);
        Pump(window);
        Check(windowType.GetProperty("Choice", RenderAny)!.GetValue(window)!.ToString() == "Retry",
            "开始等待时不清掉已经按下的选择");

        resetAttempt.Invoke(window, null);
        Pump(window);
        Check(windowType.GetProperty("Choice", RenderAny)!.GetValue(window)!.ToString() == "None",
            "重试前复位清掉上一次的选择");
        Check(retry.IsEnabled, "复位之后按钮又能点");

        keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Check(windowType.GetProperty("Choice", RenderAny)!.GetValue(window)!.ToString() == "Keep",
            "点「保留源文件」把选择交出去");

        // 手动关窗：停下来的态 = 保留源文件；渲染中 = 取消
        resetAttempt.Invoke(window, null);
        Pump(window);
        window.Close();
        Pump(window);
        Check(windowType.GetProperty("Choice", RenderAny)!.GetValue(window)!.ToString() == "Keep",
            "停下来的态直接关窗 = 保留源文件");
        Check(window.IsVisible, "关窗不会真的关掉（等渲染线程收尾）");

        windowType.GetMethod("CloseQuietly", RenderAny)!.Invoke(window, null);
        Pump(window);
        Check(!window.IsVisible, "收尾时安静关掉");

        // ---- ② 真 ffmpeg 端到端 ----
        string? ffmpeg = (string?)planType.GetMethod("FindFfmpeg")!.Invoke(null, null);
        if (ffmpeg == null)
        {
            Check(false, "找不到 ffmpeg，渲染端到端跑不了");
            return;
        }

        string src = Path.Combine(_outDir, "render-src.mp4");
        Check(RunFfmpeg(ffmpeg, new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "testsrc2=s=320x240:d=1:r=10", "-c:v", "libx264", "-pix_fmt", "yuv420p", src,
        }) == 0, "生成渲染测试源片");

        object Step(IEnumerable<string> args, string? expected) =>
            stepType.GetMethod("Of", RenderStatic)!.Invoke(null, new object[] { args.ToArray(), expected!, 1.0 })!;

        object Run(Int32Rect anchor, int durationMs, params object[] steps)
        {
            var typed = Array.CreateInstance(stepType, steps.Length);
            for (int i = 0; i < steps.Length; i++) typed.SetValue(steps[i], i);

            return runnerType.GetMethod("Run", RenderStatic)!.Invoke(null,
                new object[] { ffmpeg!, _outDir, anchor, durationMs, typed, true })!;
        }

        bool Ok(object outcome) => (bool)outcome.GetType().GetProperty("Ok")!.GetValue(outcome)!;
        bool Flag(object outcome, string name) => (bool)outcome.GetType().GetProperty(name)!.GetValue(outcome)!;
        string ErrorOf(object outcome) => (string)outcome.GetType().GetProperty("Error")!.GetValue(outcome)!;

        // ②-1 成功：真跑一遍重封装，对话框自己关掉
        string pass = Path.Combine(_outDir, "render-pass.mp4");
        File.Delete(pass);
        var passStep = Step(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", src, "-c", "copy", "-movflags", "+faststart", pass,
        }, pass);

        var task = Task.Run(() => Run(new Int32Rect(200, 200, 320, 240), 1000, passStep));

        bool sawDialog = false;
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 20000)
        {
            PumpFor(40);
            if (RenderWindow() != null) sawDialog = true;
        }

        object passOutcome = AwaitResult(task);
        Check(sawDialog, "渲染时真的弹出了进度窗");
        Check(Ok(passOutcome), "重封装真跑通：" + ErrorOf(passOutcome));
        Check(File.Exists(pass) && new FileInfo(pass).Length > 0, "成品文件真的写出来了");
        Check(Flag(passOutcome, "ShowedDialog") && (int)passOutcome.GetType().GetProperty("Attempts")!.GetValue(passOutcome)! == 1,
            "一轮就过（没有多余的重试）");
        Check(RenderWindow() == null, "跑完自动关窗");

        // ②-2 失败：停在对话框上，选「丢弃」
        string badOutput = Path.Combine(_outDir, "render-bad.mp4");
        File.Delete(badOutput);
        var badStep = Step(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-i", Path.Combine(_outDir, "no-such-input.mp4"), badOutput,
        }, badOutput);

        var badTask = Task.Run(() => Run(new Int32Rect(200, 200, 320, 240), 1000, badStep));
        Check(WaitUntil(() => RenderWindow() is { } w && Retained(w), 20000), "渲染失败后停在对话框上");
        Check(DiscardInWindow(), "点「丢弃」");
        object badOutcome = AwaitResult(badTask);
        Check(!Ok(badOutcome) && Flag(badOutcome, "Discarded") && Flag(badOutcome, "Cancelled"),
            "失败 + 丢弃 = Discarded / Cancelled");
        Check(ErrorOf(badOutcome).Length > 0, "失败原因带回去了：" + ErrorOf(badOutcome));
        Check(RenderWindow() == null, "丢弃之后关窗");

        // ②-3 取消 → 保留源文件：慢编码中途按取消，再选保留
        string slowOutput = Path.Combine(_outDir, "render-slow.mp4");
        File.Delete(slowOutput);
        var slowStep = Step(new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi",
            "-i", "testsrc2=s=1280x720:d=20:r=30", "-c:v", "libx264", "-preset", "veryslow", "-crf", "18", slowOutput,
        }, slowOutput);

        var slowTask = Task.Run(() => Run(new Int32Rect(200, 200, 320, 240), 20000, slowStep));
        Check(WaitUntil(() => RenderWindow() != null, 20000), "慢编码的进度窗出现了");
        PumpFor(300);
        Check(CancelInWindow(), "点「取消」");
        Check(WaitUntil(() => RenderWindow() is { } w && Retained(w), 30000), "取消之后停在对话框上");
        Window? cancelledWindow = RenderWindow();
        Check(cancelledWindow != null && !RetainedError(cancelledWindow),
            "主动取消的详情里没有错误（只有源文件目录）");
        Check(KeepInWindow(), "点「保留源文件」");
        object slowOutcome = AwaitResult(slowTask);
        Check(Flag(slowOutcome, "KeptSource") && !Flag(slowOutcome, "Discarded"),
            "取消后保留源文件 = KeptSource");
        Check(RenderWindow() == null, "保留之后关窗");

        // runner 的契约：**只负责跑与报**，不删任何文件（删不删由调用方按自己的产物决定）。
        // 所以取消后 ffmpeg 写了一半的产物可能还在——那是调用方（RecordTrimSession 的 .part）去清。
        string witness = Path.Combine(_outDir, "render-witness.txt");
        File.WriteAllText(witness, "x");
        Check(File.Exists(witness), "runner 没碰别人的文件（取消后的半成品也留给调用方清）");
        File.Delete(witness);
        if (File.Exists(slowOutput)) File.Delete(slowOutput);
    }

    // ============ 窗口小工具 ============

    /// <summary>
    /// 等后台渲染任务完成，**期间继续泵消息**：渲染线程要经 <c>Dispatcher.Invoke</c> 画进度 / 关窗，
    /// 主线程直接 <c>.Result</c> 阻塞会把自己锁死（冒烟里的死锁就是这么来的）。
    /// </summary>
    private static T AwaitResult<T>(System.Threading.Tasks.Task<T> task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted && clock.ElapsedMilliseconds < 120000) PumpFor(40);
        return task.GetAwaiter().GetResult();
    }

    private static Window? RenderWindow() =>
        Application.Current.Windows.OfType<Window>()
            .FirstOrDefault(w => w.GetType().Name == "RecordRenderWindow");

    private static bool Retained(Window window)
    {
        var state = window.GetType().GetProperty("State", RenderAny)!.GetValue(window)!;
        return (bool)state.GetType().GetProperty("Retained")!.GetValue(state)!;
    }

    private static bool RetainedError(Window window)
    {
        var state = window.GetType().GetProperty("State", RenderAny)!.GetValue(window)!;
        return ((string)state.GetType().GetProperty("Error")!.GetValue(state)!).Length > 0;
    }

    private static bool ClickInWindow(string buttonName)
    {
        var window = RenderWindow();
        if (window == null) return false;

        var button = (Button)Field(window, buttonName);
        if (button.Visibility != Visibility.Visible || !button.IsEnabled) return false;

        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        return true;
    }

    private static bool CancelInWindow() => ClickInWindow("CancelBtn");

    private static bool DiscardInWindow() => ClickInWindow("DiscardBtn");

    private static bool KeepInWindow() => ClickInWindow("KeepBtn");

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            PumpFor(60);
        }

        return condition();
    }

    /// <summary>
    /// 读剪贴板文本：OLE 写入可能晚一拍（系统忙 / 别的进程刚清空），等到 <paramref name="accept"/> 满意为止。
    /// 断言里直接 <c>Clipboard.GetText()</c> 在满负载跑全量时会偶发拿到空串（踩过）。
    /// </summary>
    private static string WaitClipboardText(Func<string, bool> accept, int timeoutMs = 1500)
    {
        string last = "";
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                last = System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : "";
            }
            catch (Exception)
            {
                last = "";
            }

            if (accept(last)) return last;
            PumpFor(40);
        }

        return last;
    }

    /// <summary>抽一会儿消息（没有窗口也要能跑：直接推 DispatcherFrame）。</summary>
    private static void PumpFor(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }
}
