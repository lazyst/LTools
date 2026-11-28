using System;
using System.Windows;
using System.Windows.Threading;
using LTools.Core.Actions;
using LTools.Features.Running;
using LTools.Views;

namespace IndicatorSmoke;

/// <summary>
/// 运行指示器（屏幕顶上那条）的窗口行为：
/// 没有运行时「收起 / 恢复」必须是空操作——截图 Pro 的识别在收尾时恢复了一次，
/// 结果在屏幕顶部留下一条空指示条（用户报过）。有运行时才显示，运行都结束就收起。
/// </summary>
internal static class Program
{
    private static int _pass;
    private static int _fail;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/LTools;component/Themes/Modern.xaml", UriKind.Absolute),
        });

        int code = 0;
        app.Startup += (_, _) =>
        {
            try { code = Run(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); code = 1; }
            app.Shutdown(code);
        };
        app.Run();
        return code;
    }

    private static int Run()
    {
        // ---- 1. 没有运行时：收起返回 false、恢复是空操作（本 bug 的回归点） ----
        Check("没有运行时：本来就不显示", !RecordingIndicatorWindow.IsAutomationShowing);
        Check("没有运行时：HideAutomationForCapture 返回 false",
            !RecordingIndicatorWindow.HideAutomationForCapture());

        using (RecordingIndicatorWindow.SuppressForCapture())
        {
        }
        Pump(120);
        Check("没有运行时：收起再恢复不会留一条空指示条", !RecordingIndicatorWindow.IsAutomationShowing);

        RecordingIndicatorWindow.RestoreAutomation(false);
        Pump(120);
        Check("没有运行时：RestoreAutomation(false) 什么都不做", !RecordingIndicatorWindow.IsAutomationShowing);

        // 「正在停止」这类说明没有运行可挂：直接收起，不留窗口。
        RecordingIndicatorWindow.ShowAutomation("正在停止 · 等待当前步骤返回");
        Pump(120);
        Check("没有运行时的说明不会开出空窗口", !RecordingIndicatorWindow.IsAutomationShowing);

        // ---- 2. 有运行：显示、收起、恢复 ----
        ActionRunSession run = FakeRun("冒烟动作");
        RunningActions.TryAdd(run);
        try
        {
            RecordingIndicatorWindow.ShowAutomation();
            Pump(200);
            Check("有运行时显示指示器", RecordingIndicatorWindow.IsAutomationShowing);
            Check("指示器在屏幕顶部", TopOfWindow() <= 60, $"Top = {TopOfWindow()}");

            bool wasShowing = RecordingIndicatorWindow.HideAutomationForCapture();
            Pump(120);
            Check("截图前收起：返回 true", wasShowing);
            Check("截图期间不显示", !RecordingIndicatorWindow.IsAutomationShowing);

            RecordingIndicatorWindow.RestoreAutomation(wasShowing);
            Pump(200);
            Check("截图后恢复显示（运行还在）", RecordingIndicatorWindow.IsAutomationShowing);

            using (RecordingIndicatorWindow.SuppressForCapture())
            {
                Pump(80);
                Check("作用域内保持收起", !RecordingIndicatorWindow.IsAutomationShowing);
            }
            Pump(200);
            Check("作用域结束后恢复", RecordingIndicatorWindow.IsAutomationShowing);

            // 运行结束：指示器必须自己收起（不靠调用方记得）。
            RunningActions.Remove(run);
            RecordingIndicatorWindow.HideAutomation();
            Pump(200);
            Check("运行结束后收起", !RecordingIndicatorWindow.IsAutomationShowing);
        }
        finally
        {
            if (RunningActions.CountOf(run.ActionId) > 0) RunningActions.Remove(run);
            RecordingIndicatorWindow.HideAutomation();
        }

        // ---- 3. 最后再确认一次：计时器不会把空窗口又画回来 ----
        Pump(700);
        Check("空跑 0.7 秒后仍然没有指示器", !RecordingIndicatorWindow.IsAutomationShowing);

        Console.WriteLine();
        Console.WriteLine($"通过 {_pass} / 失败 {_fail}");
        return _fail == 0 ? 0 : 1;
    }

    private static ActionRunSession FakeRun(string name)
    {
        var job = new RunJob
        {
            Program = new ActionProgram { Name = name },
            Name = name,
            ActionId = "indicator-smoke-" + Guid.NewGuid().ToString("N")[..6],
        };
        return new ActionRunSession(job, "run-indicator-smoke", 1, false);
    }

    /// <summary>指示器窗口现在在屏幕上的 Top（没窗口时返回 -1）。</summary>
    private static double TopOfWindow()
    {
        foreach (Window window in Application.Current.Windows)
        {
            if (window is RecordingIndicatorWindow && window.IsVisible) return window.Top;
        }
        return -1;
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(ms), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private static void Check(string what, bool ok, string? detail = null)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine($"ok   {what}");
            return;
        }

        _fail++;
        Console.WriteLine($"FAIL {what}{(detail == null ? "" : " · " + detail)}");
    }
}
