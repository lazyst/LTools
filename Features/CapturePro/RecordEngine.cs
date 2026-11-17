using System;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Core;
using LTools.Native;

namespace LTools.Features.CapturePro;

/// <summary>
/// 抓帧 + 编码：按帧率抓屏幕，把原始 BGRA 像素灌给 ffmpeg 的 stdin，直接写成 MP4。
/// </summary>
/// <remarks>
/// <para>
/// 一帧都不落盘：<c>pipe:0</c> 是原始视频，ffmpeg 边收边编码。所以录多久都只占成品那么大，
/// 不会在磁盘上堆几万个中间帧。
/// </para>
/// <para>
/// 节奏由 <see cref="RecordController"/> 的活动时钟决定（暂停不推进）；某帧抓慢了就补写上一帧，
/// 这样成品时长和现实一致，而不是「掉帧后视频跑快」。落后太多时直接对齐，避免补帧雪崩。
/// </para>
/// <para>
/// 抓帧源是字段（<see cref="FrameSource"/>）：冒烟注入合成画面，不真录用户的桌面。
/// </para>
/// </remarks>
internal sealed class RecordEngine
{
    private readonly string _ffmpeg;
    private readonly Int32Rect _area;
    private readonly int _fps;
    private readonly string _output;
    private readonly RecordController _controller;

    private readonly object _errGate = new();
    private readonly StringBuilder _errors = new();

    private Process? _process;
    private Stream? _input;
    private volatile string? _error;

    /// <summary>装箱的 <see cref="Int32Rect"/>：引用赋值是原子的，跨线程读不会读到半截。</summary>
    private volatile object? _currentArea;

    /// <summary>抓一帧（默认真抓屏幕那块区域；冒烟注入合成画面）。调用方负责 Dispose。</summary>
    internal Func<Int32Rect, System.Drawing.Bitmap> FrameSource =
        area => ScreenCapture.Capture(area.X, area.Y, area.Width, area.Height);

    /// <summary>
    /// 跟随鼠标时的「这一帧抓哪儿」（物理像素）。null = 固定抓 <c>area</c>。
    /// 返回的尺寸可以和成品不同：<see cref="CaptureLoop"/> 会缩放到 <c>area</c> 的尺寸再写进编码器。
    /// </summary>
    internal Func<Int32Rect>? FollowArea { get; set; }

    public RecordEngine(string ffmpeg, Int32Rect area, int fps, string output, RecordController controller)
    {
        _ffmpeg = ffmpeg;
        _area = area;
        _fps = RecordPlan.NormalizeFps(fps);
        _output = output;
        _controller = controller;
    }

    /// <summary>最近一帧实际抓的区域（跟随鼠标时会走）。</summary>
    public Int32Rect CurrentArea => _currentArea is Int32Rect area ? area : _area;

    /// <summary>写进视频的帧数（含补帧）。</summary>
    public int Frames { get; private set; }

    /// <summary>第一帧（已 Freeze）：采集历史里的封面 / 缩略图。</summary>
    public BitmapSource? Poster { get; private set; }

    /// <summary>失败原因；成功为 null。</summary>
    public string? Error => _error;

    public int DurationMs => Frames * 1000 / Math.Max(1, _fps);

    /// <summary>跑完整段（阻塞）。返回 true = 成品写好；用户取消时返回 false。</summary>
    public bool Run()
    {
        try
        {
            var info = new ProcessStartInfo(_ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
            };
            foreach (string arg in RecordPlan.Mp4Args(_area, _fps, _output)) info.ArgumentList.Add(arg);

            _process = new Process { StartInfo = info };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) AppendError(e.Data!);
            };

            if (!_process.Start())
            {
                _error = "编码器启动失败";
                return false;
            }

            _process.BeginErrorReadLine();
            _input = _process.StandardInput.BaseStream;

            CaptureLoop();

            bool ok = Finish();
            return ok && _error == null;
        }
        catch (Exception ex)
        {
            _error ??= ex.Message;
            AppLog.Warn("CapturePro.Record", "录屏失败：" + ex.Message);
            Kill();
            return false;
        }
        finally
        {
            try { _input?.Dispose(); } catch { /* 已经关过 */ }
            _input = null;
            _process?.Dispose();
            _process = null;
        }
    }

    // ============ 抓帧 ============

    private void CaptureLoop()
    {
        int width = Math.Max(2, _area.Width);
        int height = Math.Max(2, _area.Height);
        int stride = width * 4;

        var buffer = new byte[stride * height];
        byte[]? last = null;

        double ticksPerFrame = (double)Stopwatch.Frequency / _fps;
        long frame = 0;
        double nextAt = 0;

        while (!_controller.Finished)
        {
            if (_controller.IsPaused)
            {
                Thread.Sleep(40);
                continue;
            }

            double active = _controller.ActiveTicks;
            if (active + 0.5 < nextAt)
            {
                int wait = (int)Math.Ceiling((nextAt - active) * 1000 / Stopwatch.Frequency);
                Thread.Sleep(Math.Clamp(wait, 1, 20));
                continue;
            }

            // 跟随鼠标：每帧重新算抓哪儿；夹到最小尺寸，别让编码器收到空画面
            Int32Rect rect = _area;
            if (FollowArea != null)
            {
                var next = FollowArea();
                if (next.Width >= 2 && next.Height >= 2) rect = next;
            }

            if (!Grab(buffer, width, height, rect)) return;
            _currentArea = rect;

            Poster ??= ToSource(buffer, width, height);
            last ??= buffer;

            if (!Write(buffer)) return;

            Frames++;
            frame++;
            nextAt = frame * ticksPerFrame;

            // 抓帧慢了（大屏 / 别处占 CPU）：补上一帧，别让时间轴跑快
            int dup = 0;
            while (dup < 4 && !_controller.Finished && _controller.ActiveTicks > nextAt + ticksPerFrame)
            {
                if (!Write(last)) return;
                Frames++;
                frame++;
                nextAt = frame * ticksPerFrame;
                dup++;
            }

            if (_controller.ActiveTicks > nextAt + ticksPerFrame)
            {
                // 机器实在跟不上：直接对齐当前时刻，别无限补帧
                frame = (long)Math.Ceiling(_controller.ActiveTicks / ticksPerFrame);
                nextAt = frame * ticksPerFrame;
            }
        }
    }

    /// <summary>
    /// 抓一帧塞进紧排的 BGRA 缓冲（锁定位图的 stride 可能比 <c>宽 × 4</c> 大）。
    /// 抓到的尺寸和成品不同（跟随鼠标换到更大的区域 / 显示器比选区小时）先缩放到成品尺寸。
    /// </summary>
    private bool Grab(byte[] buffer, int width, int height, Int32Rect rect)
    {
        try
        {
            using var captured = FrameSource(rect);
            System.Drawing.Bitmap? resized = captured.Width == width && captured.Height == height
                ? null
                : Resize(captured, width, height);

            try
            {
                var bitmap = resized ?? captured;
                var box = new System.Drawing.Rectangle(0, 0, width, height);
                var data = bitmap.LockBits(box, ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                try
                {
                    int stride = width * 4;
                    if (data.Stride == stride)
                    {
                        Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buffer, y * stride, stride);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }
            finally
            {
                resized?.Dispose();
            }

            return true;
        }
        catch (Exception ex)
        {
            _error = "抓帧失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>等比拉伸到成品尺寸（只在跟随鼠标跨显示器 / 选区比屏幕大时用）。</summary>
    private static System.Drawing.Bitmap Resize(System.Drawing.Bitmap source, int width, int height)
    {
        var target = new System.Drawing.Bitmap(width, height,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        using var graphics = System.Drawing.Graphics.FromImage(target);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighSpeed;
        graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, width, height));

        return target;
    }

    private bool Write(byte[] buffer)
    {
        var input = _input;
        if (input == null)
        {
            _error ??= "编码器已退出";
            return false;
        }

        try
        {
            input.Write(buffer, 0, buffer.Length);
            return true;
        }
        catch (Exception ex)
        {
            _error ??= "写入编码器失败：" + ex.Message;
            return false;
        }
    }

    // ============ 收尾 ============

    /// <summary>关管道（EOF）→ 等 ffmpeg 写完容器。</summary>
    private bool Finish()
    {
        try
        {
            _input?.Flush();
            _input?.Close();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "关闭编码器管道失败：" + ex.Message);
        }

        _input = null;

        var process = _process;
        if (process == null) return false;

        if (!process.WaitForExit(20000))
        {
            _error ??= "编码器收尾超时";
            Kill();
            return false;
        }

        if (process.ExitCode != 0)
        {
            _error ??= $"ffmpeg 退出码 {process.ExitCode}：{Tail()}";
            return false;
        }

        return !_controller.Cancelled;
    }

    private void Kill()
    {
        try
        {
            if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "结束编码器失败：" + ex.Message);
        }
    }

    private void AppendError(string line)
    {
        lock (_errGate)
        {
            if (_errors.Length > 8000) _errors.Remove(0, 4000);
            _errors.AppendLine(line);
        }
    }

    private string Tail()
    {
        lock (_errGate)
        {
            string text = _errors.ToString().Trim();
            return text.Length <= 400 ? text : text[^400..];
        }
    }

    private static BitmapSource ToSource(byte[] bgra, int width, int height)
    {
        // BitmapSource.Create 会保留这份数组，而缓冲每帧复用：先复制一份
        var copy = new byte[bgra.Length];
        Buffer.BlockCopy(bgra, 0, copy, 0, bgra.Length);

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, copy, width * 4);
        source.Freeze();
        return source;
    }
}
