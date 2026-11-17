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

    /// <summary>真正用上的编码器名（<c>h264_nvenc</c> / <c>libx265</c> …）：硬件开不起来时会回退。</summary>
    public string Encoder { get; init; } = "";

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

    /// <summary>
    /// 录一次。用户取消（框选 / 倒计时 / 控制条）时返回未保存的结果。
    /// </summary>
    /// <param name="area">
    /// 已经定好的录制区域（屏幕物理像素）。截图覆盖层的「录屏」用它——那一块就是用户刚框的选区，
    /// 不必再弹一次框选（也不该弹：覆盖层刚关掉）。null = 按 <paramref name="plan"/> 自己定区域。
    /// </param>
    public static RecordOutcome TryRun(Window? owner, RecordPlan plan, Int32Rect? area = null)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            ToastService.Show("已经在录屏了", "录屏", 1800, ToastKind.info);
            return new RecordOutcome();
        }

        try
        {
            return RunCore(owner, plan, area);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private static RecordOutcome RunCore(Window? owner, RecordPlan plan, Int32Rect? fixedArea)
    {
        var app = Application.Current;
        if (app == null) throw new InvalidOperationException("界面未初始化，无法录屏");

        string? ffmpeg = RecordPlan.FindFfmpeg();
        if (ffmpeg == null)
            throw new InvalidOperationException(
                "没找到 ffmpeg：发布包里的 runtime\\ffmpeg\\ffmpeg.exe 不在了，也可以装一个 ffmpeg 放进 PATH"
                + "（开发环境跑 tools\\fetch-ffmpeg.ps1 重新下载）");

        var area = fixedArea ?? PickArea(app, plan, owner);
        if (area.Width < 2 || area.Height < 2) return new RecordOutcome();   // 用户取消了框选

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
        var gate = plan.PromptStart ? new RecordStartGate() : null;
        var bar = ShowBar(area, plan, controller, gate);

        // 面板先摆着（Snow Shot 的录屏入口）：等用户点那个红圈；点 ✕ / 关窗都算取消。
        if (gate != null)
        {
            while (!gate.Started && !gate.Cancelled && !controller.Finished) Thread.Sleep(30);
            if (!gate.Started)
            {
                CloseBar(bar);
                return new RecordOutcome();
            }

            // 空闲期用户可以拖区域框改尺寸 / 挪位置：这里取定稿的选区（Area 只在 UI 线程上写）。
            var chosen = bar == null ? area : app.Dispatcher.Invoke(() => bar.Area);
            if (chosen.Width >= 2 && chosen.Height >= 2 && chosen != area)
            {
                area = chosen;
                if (bar != null) app.Dispatcher.Invoke(() => bar.ApplyArea(chosen));
            }
        }

        if (plan.Countdown > 0 && !RunCountdown(area, plan.Countdown))
        {
            CloseBar(bar);
            return new RecordOutcome();
        }

        controller.Start();
        bar?.BeginRecording();

        // 编码器：先探测这台机器哪些硬件编码器真能开（一次进程只探一次），再按意图挑
        string encoder = RecordEncoders.Pick(plan.Options.Codec, RecordEncoders.Probe(ffmpeg));

        // 摄像头：打不开只是少个画中画，录制继续
        var camera = RecordCamera.TryStart(plan.Options, out string? cameraProblem);
        if (cameraProblem != null)
            ToastService.Show(cameraProblem + "，只录画面", "录屏", 3200, ToastKind.warning);

        // 声音：同样「打不开就少一路」，不挡录制
        RecordAudio? audio = null;
        if (plan.Options.HasAudio)
        {
            audio = RecordAudio.Start(TempDir, plan.Options, controller);
            if (audio.Warning != null && !audio.Any)
                ToastService.Show("没有录到声音：" + audio.Warning, "录屏", 3600, ToastKind.warning);
            else if (audio.Warning != null)
                AppLog.Warn("CapturePro.Record", "部分声音通道打不开：" + audio.Warning);
        }

        var engine = new RecordEngine(ffmpeg, area, plan, temp, controller, encoder, camera);

        // 跟随鼠标：先定「第一帧抓哪儿」的视口（居中到光标、夹在显示器里）；
        // 成品尺寸仍是选区，抓到的画面按选区尺寸缩放后再编码。
        RecordFollowMotion? motion = null;
        var viewport = area;
        if (plan.FollowMouse) motion = CreateFollow(area, plan, out viewport);

        var border = ShowBorder(viewport);
        var tracker = new RecordTracker(border, controller, motion, area, viewport);
        if (motion != null) engine.FollowArea = () => tracker.CurrentArea;

        bool ok;
        try
        {
            RecordWheelFeed.SetActive(true);
            ok = engine.Run();
        }
        finally
        {
            RecordWheelFeed.SetActive(false);

            // 抓帧循环不论怎么退出（完成 / 取消 / 异常），看护线程与边框都得收掉
            tracker.Stop();
            CloseBorder(border);

            // 音轨先停：停完才有完整的 WAV（收尾要按活动时长补齐）
            try { audio?.Stop(); } catch (Exception ex) { AppLog.Warn("CapturePro.Record", "停音轨失败：" + ex.Message); }
            try { camera?.Dispose(); } catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关摄像头失败：" + ex.Message); }
        }

        CloseBar(bar);

        if (controller.Cancelled)
        {
            DeleteFile(temp);
            DeleteAudio(audio);
            ToastService.Show("已取消录屏", "录屏", 1600, ToastKind.info);
            return new RecordOutcome();
        }

        if (!ok || engine.Frames == 0)
        {
            DeleteFile(temp);
            DeleteAudio(audio);
            throw new InvalidOperationException("录屏失败：" + (engine.Error ?? "没有录到画面"));
        }

        string file = ToFaststart(ffmpeg, temp, plan.IsAnimated ? Array.Empty<string>() : audio?.Tracks);
        string extension = "mp4";

        if (plan.IsAnimated)
        {
            string target = TempPath(plan.Extension);
            if (TryConvertAnimated(ffmpeg, file, plan, target, out string convertError))
            {
                DeleteFile(temp);
                file = target;
                extension = plan.Extension;
            }
            else
            {
                AppLog.Warn("CapturePro.Record", $"{RecordPlan.FormatName(plan.Format)} 转换失败：" + convertError);
                ToastService.Show(
                    $"{RecordPlan.FormatName(plan.Format)} 转换失败，已保存为 MP4：" + convertError,
                    "录屏", 4200, ToastKind.warning);
            }
        }

        var entry = CaptureCatalog.CreateRecord(area.Width, area.Height, "录屏", extension,
            engine.DurationMs, file, engine.Poster);

        // 音轨只是混音素材，成品出来就删
        DeleteAudio(audio);

        if (entry == null)
        {
            DeleteFile(file);
            throw new InvalidOperationException("录制文件保存失败（写不进 captures/records）");
        }

        var extras = new List<string>();
        string summary = plan.Options.Summary();
        if (summary.Length > 0) extras.Add(summary);
        extras.Add(RecordEncoders.Label(engine.Encoder));

        ToastService.Show(
            $"{RecordPlan.FormatClock(engine.DurationMs)} · {area.Width} × {area.Height} · {string.Join(" · ", extras)}"
            + " · 已存进采集历史",
            "录屏", 3600, ToastKind.success,
            onClick: OpenRecordsFolder, actionText: "打开文件夹");

        if (plan.Options.TrimAfterRecord && extension == "mp4") OpenTrim(entry);

        return new RecordOutcome
        {
            Entry = entry,
            Path = CaptureCatalog.FilePath(entry),
            Encoder = engine.Encoder,
        };
    }

    /// <summary>录完直接开裁剪 / 倍速窗口（窗口只能在 UI 线程上建，这里只投递不等待）。</summary>
    private static void OpenTrim(CaptureEntry entry)
    {
        try
        {
            var app = Application.Current;
            if (app == null || app.Dispatcher.HasShutdownStarted) return;

            app.Dispatcher.BeginInvoke(() =>
            {
                try { RecordTrimSession.Open(entry); }
                catch (Exception ex) { AppLog.Warn("CapturePro.Trim", "打开裁剪窗口失败：" + ex.Message); }
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Trim", "投递裁剪窗口失败：" + ex.Message);
        }
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

    private static CaptureRecordWindow? ShowBar(Int32Rect area, RecordPlan plan,
        RecordController controller, RecordStartGate? gate)
    {
        var app = Application.Current;
        if (app == null) return null;

        CaptureRecordWindow? bar = null;
        app.Dispatcher.Invoke(() =>
        {
            bar = new CaptureRecordWindow(area, plan, controller, gate);
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

    // ============ 区域边框 / 跟随鼠标 ============

    /// <summary>
    /// 摆出录制区域边框（照 Snow Shot：红框告诉用户正在录哪一块）。
    /// 失败只记日志——边框是提示，不该因为它录不成。
    /// </summary>
    private static RecordBorderWindow? ShowBorder(Int32Rect area)
    {
        var app = Application.Current;
        if (app == null) return null;

        RecordBorderWindow? border = null;
        try
        {
            app.Dispatcher.Invoke(() =>
            {
                border = new RecordBorderWindow();
                border.ShowFor(area);
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "显示录制边框失败：" + ex.Message);
        }

        return border;
    }

    private static void CloseBorder(RecordBorderWindow? border)
    {
        if (border == null) return;

        try
        {
            var app = Application.Current;
            if (app != null) app.Dispatcher.Invoke(() => border.Close());
            else border.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "关录制边框失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 建跟随运动：选区等比塞进**选区中心所在显示器**（放得下就原样），
    /// 再把视口居中到光标（上游「Capture starts centered on the cursor and clamps to the monitor bounds」）。
    /// </summary>
    private static RecordFollowMotion? CreateFollow(Int32Rect selection, RecordPlan plan, out Int32Rect initial)
    {
        initial = selection;

        var bounds = MonitorBounds(selection);
        if (bounds.Width <= 0 || bounds.Height <= 0) return null;

        var viewport = RecordFollowMotion.Fit(selection, bounds);
        var motion = new RecordFollowMotion(viewport, bounds, plan.FollowOptions, Environment.TickCount64);
        initial = motion.Viewport;

        if (Win32.GetCursorPos(out var cursor))
            initial = motion.Center(cursor.X, cursor.Y, Environment.TickCount64);

        return motion;
    }

    /// <summary>选区中心所在显示器的矩形（物理像素）；找不到就退回光标所在那块。</summary>
    private static Int32Rect MonitorBounds(Int32Rect area)
    {
        int cx = area.X + area.Width / 2;
        int cy = area.Y + area.Height / 2;

        foreach (var screen in Screens.All())
        {
            if (cx >= screen.Left && cx < screen.Left + screen.Width &&
                cy >= screen.Top && cy < screen.Top + screen.Height)
                return new Int32Rect(screen.Left, screen.Top, screen.Width, screen.Height);
        }

        var monitor = ScreenPicker.MonitorUnderCursor();
        if (monitor.Width > 0 && monitor.Height > 0)
            return new Int32Rect(monitor.X, monitor.Y, monitor.Width, monitor.Height);

        return new Int32Rect(ScreenCapture.VirtualLeft, ScreenCapture.VirtualTop,
            Math.Max(1, ScreenCapture.VirtualWidth), Math.Max(1, ScreenCapture.VirtualHeight));
    }

    /// <summary>
    /// 录屏期间的看护线程：跟随鼠标推进视口、把边框挪过去、暂停时把边框换成琥珀色。
    /// </summary>
    /// <remarks>
    /// 抓帧循环（<see cref="RecordEngine"/>）与它只通过 <see cref="CurrentArea"/> 说一句话：
    /// 那个值是装箱的 <see cref="Int32Rect"/>，引用赋值是原子的，抓帧那边不会读到半截坐标。
    /// 颜色与位置改动一律投递到 UI 线程（<see cref="Dispatcher.BeginInvoke(Delegate)"/>，不等待）。
    /// </remarks>
    private sealed class RecordTracker
    {
        private const int SampleMs = 15;

        private readonly RecordBorderWindow? _border;
        private readonly RecordController _controller;
        private readonly RecordFollowMotion? _motion;
        private readonly Int32Rect _selection;
        private readonly Thread _thread;

        private volatile object? _viewport;
        private volatile int _paused = -1;
        private volatile bool _stop;
        private int _warned;

        public RecordTracker(RecordBorderWindow? border, RecordController controller,
            RecordFollowMotion? motion, Int32Rect selection, Int32Rect viewport)
        {
            _border = border;
            _controller = controller;
            _motion = motion;
            _selection = selection;
            _viewport = viewport;

            _thread = new Thread(Loop) { IsBackground = true, Name = "LTools.RecordTracker" };
            _thread.Start();
        }

        /// <summary>这一帧该抓哪儿（物理像素）；跟随关闭时就是选区本身。</summary>
        public Int32Rect CurrentArea => _viewport is Int32Rect area ? area : _selection;

        public void Stop()
        {
            _stop = true;
            try { if (_thread.IsAlive) _thread.Join(800); } catch { /* 已经退出 */ }
        }

        private void Loop()
        {
            while (!_stop)
            {
                try
                {
                    SyncPause();

                    if (_motion != null && Win32.GetCursorPos(out var cursor))
                    {
                        var viewport = _motion.Advance(cursor.X, cursor.Y, Environment.TickCount64);
                        _viewport = viewport;
                        Post(() => _border?.MoveTo(viewport));
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref _warned, 1) == 0)
                        AppLog.Warn("CapturePro.Record", "跟随鼠标出错（后续只重试不刷日志）：" + ex.Message);
                }

                Thread.Sleep(SampleMs);
            }
        }

        /// <summary>暂停 ↔ 继续：边框红 ↔ 琥珀（这条不依赖跟随，固定区域也要变色）。</summary>
        private void SyncPause()
        {
            int paused = _controller.IsPaused ? 1 : 0;
            if (paused == _paused) return;
            _paused = paused;
            Post(() => _border?.SetPaused(paused == 1));
        }

        private static void Post(Action action)
        {
            var app = Application.Current;
            if (app == null) return;

            try
            {
                if (!app.Dispatcher.HasShutdownStarted) app.Dispatcher.BeginInvoke(action);
            }
            catch (Exception)
            {
                // 窗口已经关了 / 应用正在退出：这一拍丢掉就行
            }
        }
    }

    // ============ 动图转码 ============

    /// <summary>
    /// 把刚录好的 mp4 转成动图：GIF 两遍调色板，APNG / WebP 单遍。
    /// 失败时目标文件删掉、返回 false（调用方沿用 MP4）。
    /// </summary>
    private static bool TryConvertAnimated(string ffmpeg, string input, RecordPlan plan,
        string output, out string error)
    {
        error = "";

        try
        {
            if (plan.Format == RecordFormat.Gif)
            {
                string palette = TempPath("png");
                try
                {
                    if (!RunFfmpeg(ffmpeg, RecordPlan.GifPaletteArgs(input, palette, plan.GifFps), out string paletteError))
                    {
                        error = "调色板：" + paletteError;
                        return false;
                    }

                    if (!RunFfmpeg(ffmpeg, RecordPlan.GifEncodeArgs(input, palette, output, plan.GifFps), out string encodeError))
                    {
                        error = "编码：" + encodeError;
                        return false;
                    }
                }
                finally
                {
                    DeleteFile(palette);
                }
            }
            else if (!RunFfmpeg(ffmpeg,
                         RecordPlan.AnimatedArgs(input, output, plan.Format, plan.GifFps),
                         out string convertError))
            {
                error = "编码：" + convertError;
                return false;
            }

            if (!File.Exists(output) || new FileInfo(output).Length == 0)
            {
                error = $"生成的 {RecordPlan.FormatName(plan.Format)} 是空的";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (error.Length > 0) DeleteFile(output);
        }
    }

    /// <summary>跑一次 ffmpeg（GIF 转码 / 重封装共用）；超时或非 0 退出返回 false 并带上 stderr 尾巴。</summary>
    internal static bool RunFfmpeg(string ffmpeg, IReadOnlyList<string> args, out string error)
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

    /// <summary>录制缓存目录（与 <see cref="RecordRecovery"/> 共用同一个，恢复残片时才找得到）。</summary>
    private static string TempDir => RecordRecovery.TempDir;

    private static string TempPath(string extension) =>
        Path.Combine(TempDir, "rec-" + IdFactory.ShortId() + "." + extension);

    /// <summary>
    /// 把录好的分片 MP4（+ 音轨）重新封装成普通 faststart MP4。
    /// 失败就沿用分片文件——它本身是完整的，别为了「格式更好」把已经录到的东西丢了。
    /// </summary>
    private static string ToFaststart(string ffmpeg, string temp, IReadOnlyList<string>? audio)
    {
        string final = Path.Combine(TempDir, "rec-" + IdFactory.ShortId() + ".final.mp4");

        if (!RunFfmpeg(ffmpeg, RecordPlan.FinalizeArgs(temp, final, audio), out string error)
            || !File.Exists(final) || new FileInfo(final).Length == 0)
        {
            DeleteFile(final);
            AppLog.Warn("CapturePro.Record", "重封装 MP4 失败（沿用分片文件）：" + error);
            return temp;
        }

        DeleteFile(temp);
        return final;
    }

    /// <summary>删掉这次录制的音轨（它们是混音素材，不是成品）。</summary>
    private static void DeleteAudio(RecordAudio? audio)
    {
        if (audio == null) return;

        foreach (string track in audio.Tracks) DeleteFile(track);
    }

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

    internal static void OpenRecordsFolder()
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
