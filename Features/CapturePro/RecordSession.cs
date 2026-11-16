using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using LTools.Core;
using LTools.Features;
using LTools.Hooks;
using LTools.Native;
using LTools.Views;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>一次录屏的结果：成功时带着采集历史里的那条记录。</summary>
internal sealed class RecordOutcome
{
    public CaptureEntry? Entry { get; init; }

    public string Path { get; init; } = "";

    public bool Saved => Entry != null;
}

/// <summary>
/// 录屏的唯一入口：定区域 → 倒计时 → 控制条 + 抓帧编码 → 进采集历史。
/// </summary>
/// <remarks>
/// <para>
/// 跑在**后台线程**（内置功能由 <c>FeatureRunner</c> 丢到线程池）：所有窗口都在
/// <c>Dispatcher</c> 上建 / 关，抓帧与编码不占 UI 线程（UI 线程还挂着低级鼠标钩子）。
/// </para>
/// <para>
/// 帧率、时长的权威在 <see cref="RecordEngine"/>；这里只管「录哪儿 / 录成什么 / 存到哪」。
/// 录屏文件先落在 <c>cache/record/</c>（运行缓存），成功后由
/// <see cref="CaptureCatalog.CreateRecord"/> 搬进 <c>captures/records/</c> 并记一条。
/// </para>
/// </remarks>
internal static class RecordSession
{
    private static int _busy;

    /// <summary>正在录（同一时间只允许一次，避免两条管道抢同一块屏幕）。</summary>
    public static bool IsRecording => Volatile.Read(ref _busy) != 0;

    /// <summary>录一次。用户取消（框选 / 倒计时 / 控制条）时返回未保存的结果。</summary>
    public static RecordOutcome TryRun(Window? owner, RecordPlan plan)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            ToastService.Show("已经在录屏了", "录屏", 1800, ToastKind.info);
            return new RecordOutcome();
        }

        try
        {
            return RunCore(owner, plan);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static RecordOutcome RunCore(Window? owner, RecordPlan plan)
    {
        var app = Application.Current;
        if (app == null) throw new InvalidOperationException("界面未初始化，无法录屏");

        string? ffmpeg = RecordPlan.FindFfmpeg();
        if (ffmpeg == null)
            throw new InvalidOperationException(
                "没找到 ffmpeg：发布包里的 runtime\\ffmpeg\\ffmpeg.exe 不在了，也可以装一个 ffmpeg 放进 PATH"
                + "（开发环境跑 tools\\fetch-ffmpeg.ps1 重新下载）");

        var area = PickArea(app, plan, owner);
        if (area.Width < 2 || area.Height < 2) return new RecordOutcome();   // 用户取消了框选

        if (plan.Countdown > 0 && !RunCountdown(area, plan.Countdown)) return new RecordOutcome();

        string temp = TempPath("mp4");
        try
        {
            Directory.CreateDirectory(TempDir);
            CleanTemp();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "准备录制缓存失败：" + ex.Message);
        }

        var controller = new RecordController();
        var bar = ShowBar(area, plan, controller);

        var engine = new RecordEngine(ffmpeg, area, plan.EffectiveFps, temp, controller);
        bool ok = engine.Run();

        CloseBar(bar);

        if (controller.Cancelled)
        {
            DeleteFile(temp);
            ToastService.Show("已取消录屏", "录屏", 1600, ToastKind.info);
            return new RecordOutcome();
        }

        if (!ok || engine.Frames == 0)
        {
            DeleteFile(temp);
            throw new InvalidOperationException("录屏失败：" + (engine.Error ?? "没有录到画面"));
        }

        string file = temp;
        string extension = "mp4";

        if (plan.IsGif)
        {
            if (TryConvertToGif(ffmpeg, temp, plan, out string gifPath, out string convertError))
            {
                DeleteFile(temp);
                file = gifPath;
                extension = "gif";
            }
            else
            {
                AppLog.Warn("CapturePro.Record", "GIF 转换失败：" + convertError);
                ToastService.Show("GIF 转换失败，已保存为 MP4：" + convertError, "录屏",
                    4200, ToastKind.warning);
            }
        }

        var entry = CaptureCatalog.CreateRecord(area.Width, area.Height, "录屏", extension,
            engine.DurationMs, file, engine.Poster);

        if (entry == null)
        {
            DeleteFile(file);
            throw new InvalidOperationException("录制文件保存失败（写不进 captures/records）");
        }

        ToastService.Show(
            $"{RecordPlan.FormatClock(engine.DurationMs)} · {area.Width} × {area.Height} · 已存进采集历史",
            "录屏", 3600, ToastKind.success,
            onClick: OpenRecordsFolder, actionText: "打开文件夹");

        return new RecordOutcome { Entry = entry, Path = CaptureCatalog.FilePath(entry) };
    }

    // ============ 区域 ============

    /// <summary>
    /// 定录制区域。框选会开覆盖层：先把热键挂起、鼠标钩子让路，不然单键绑定会把框选里的按键吞掉。
    /// </summary>
    private static Int32Rect PickArea(Application app, RecordPlan plan, Window? owner)
    {
        var area = Int32Rect.Empty;
        string? error = null;
        bool guard = plan.Mode == RecordMode.Region;

        bool wasSuspended = HotkeyRegistry.Suspended;
        if (guard)
        {
            MouseHook.SetWindowPickerActive(true);
            HotkeyRegistry.Suspended = true;
        }

        try
        {
            app.Dispatcher.Invoke(() =>
            {
                try { area = ResolveArea(plan, owner); }
                catch (Exception ex) { error = ex.Message; }
            });
        }
        finally
        {
            if (guard)
            {
                HotkeyRegistry.Suspended = wasSuspended;
                MouseHook.SetWindowPickerActive(false);
            }
        }

        if (area.Width >= 2 && area.Height >= 2) return area;
        if (error != null) throw new InvalidOperationException(error);
        return Int32Rect.Empty;
    }

    private static Int32Rect ResolveArea(RecordPlan plan, Window? owner)
    {
        switch (plan.Mode)
        {
            case RecordMode.Primary:
            {
                var monitor = ScreenPicker.MonitorUnderCursor();
                return new Int32Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height);
            }

            case RecordMode.All:
                return new Int32Rect(ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
                    Math.Max(1, ScreenCapture.VirtualWidth), Math.Max(1, ScreenCapture.VirtualHeight));

            case RecordMode.Window:
            {
                IntPtr hwnd = Win32.GetForegroundWindow();
                if (hwnd == IntPtr.Zero) throw new InvalidOperationException("没有前台窗口可录");

                var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);
                if (w <= 0 || h <= 0) throw new InvalidOperationException("前台窗口不可见，无法录制");
                return new Int32Rect(x, y, w, h);
            }

            default:
                return ScreenPicker.TryPickRegion(owner, out var area, out _) ? area : Int32Rect.Empty;
        }
    }

    // ============ 倒计时 ============

    private static bool RunCountdown(Int32Rect area, int seconds)
    {
        var app = Application.Current;
        if (app == null) return false;

        RecordCountdownWindow? window = null;

        // 右键是倒计时的唯一逃生口：让鼠标钩子让路，否则「裸右键长按」手势会把右键吞掉。
        bool wasSuspended = HotkeyRegistry.Suspended;
        MouseHook.SetWindowPickerActive(true);
        HotkeyRegistry.Suspended = true;

        try
        {
            app.Dispatcher.Invoke(() =>
            {
                window = new RecordCountdownWindow(area, seconds);
                window.Show();
            });

            if (window == null) return false;

            // 倒计时跑在 UI 线程的 DispatcherTimer 上；这里只是等它结束
            while (!window.Finished) Thread.Sleep(40);
            return !window.Cancelled;
        }
        finally
        {
            HotkeyRegistry.Suspended = wasSuspended;
            MouseHook.SetWindowPickerActive(false);
        }
    }

    // ============ 控制条 ============

    private static CaptureRecordWindow? ShowBar(Int32Rect area, RecordPlan plan, RecordController controller)
    {
        var app = Application.Current;
        if (app == null) return null;

        CaptureRecordWindow? bar = null;
        app.Dispatcher.Invoke(() =>
        {
            bar = new CaptureRecordWindow(area, plan, controller);
            bar.Show();
        });
        return bar;
    }

    private static void CloseBar(CaptureRecordWindow? bar)
    {
        if (bar == null) return;

        try
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.Invoke(() => bar.CloseQuietly());
            else bar.CloseQuietly();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "关控制条失败：" + ex.Message);
        }
    }

    // ============ GIF ============

    /// <summary>把刚录好的 mp4 转成 GIF（两遍：调色板 + 编码）。失败时 <paramref name="gifPath"/> 会删掉。</summary>
    private static bool TryConvertToGif(string ffmpeg, string input, RecordPlan plan,
        out string gifPath, out string error)
    {
        gifPath = TempPath("gif");
        string palette = TempPath("png");
        error = "";

        try
        {
            if (!RunFfmpeg(ffmpeg, RecordPlan.GifPaletteArgs(input, palette, plan.GifFps), out string paletteError))
            {
                error = "调色板：" + paletteError;
                DeleteFile(gifPath);
                return false;
            }

            if (!RunFfmpeg(ffmpeg, RecordPlan.GifEncodeArgs(input, palette, gifPath, plan.GifFps), out string encodeError))
            {
                error = "编码：" + encodeError;
                DeleteFile(gifPath);
                return false;
            }

            if (!File.Exists(gifPath) || new FileInfo(gifPath).Length == 0)
            {
                error = "生成的 GIF 是空的";
                DeleteFile(gifPath);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            DeleteFile(gifPath);
            return false;
        }
        finally
        {
            DeleteFile(palette);
        }
    }

    private static bool RunFfmpeg(string ffmpeg, IReadOnlyList<string> args, out string error)
    {
        error = "";

        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (string arg in args) info.ArgumentList.Add(arg);

        using var process = Process.Start(info);
        if (process == null)
        {
            error = "启动失败";
            return false;
        }

        var errorTask = process.StandardError.ReadToEndAsync();
        process.StandardOutput.ReadToEndAsync();

        if (!process.WaitForExit(180000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
            error = "转换超时";
            return false;
        }

        string stderr = errorTask.GetAwaiter().GetResult().Trim();
        if (process.ExitCode == 0) return true;

        error = stderr.Length <= 300 ? stderr : stderr[^300..];
        return false;
    }

    // ============ 文件 ============

    private static string TempDir => Path.Combine(AppPaths.DataRoot, "cache", "record");

    private static string TempPath(string extension) =>
        Path.Combine(TempDir, "rec-" + IdFactory.ShortId() + "." + extension);

    /// <summary>清掉上一次没删干净的临时文件（进程被杀 / 断电）。</summary>
    private static void CleanTemp()
    {
        foreach (string file in Directory.EnumerateFiles(TempDir))
        {
            try { File.Delete(file); }
            catch (Exception ex) { AppLog.Warn("CapturePro.Record", "清理录制缓存失败：" + ex.Message); }
        }
    }

    private static void DeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "删临时文件失败：" + ex.Message); }
    }

    private static void OpenRecordsFolder()
    {
        try
        {
            Directory.CreateDirectory(CaptureCatalog.RecordsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CaptureCatalog.RecordsDir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "打开录屏目录失败：" + ex.Message);
        }
    }
}
