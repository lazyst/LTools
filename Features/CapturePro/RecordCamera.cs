using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using LTools.Core;
using OpenCvSharp;

namespace LTools.Features.CapturePro;

/// <summary>
/// 摄像头画中画：后台线程抓帧，缩成方形（或圆形）贴在录制画面左下角。
/// </summary>
/// <remarks>
/// <para>
/// 抓帧和合成分开：摄像头按自己的帧率（通常 30）在自己的线程上取帧，抓屏线程每帧只读最新一份
/// 像素（<see cref="RecordCamera.TryCopyFrame"/>），慢摄像头不会拖累视频帧率——画中画掉帧就重复上一帧，
/// 跟上游「摄像头时间轴独立于屏幕时间轴」是一个意思。
/// </para>
/// <para>
/// 打不开摄像头（没有设备 / 被别的程序独占）只记一条警告，录制照常继续；
/// 中途掉线也保留最后一帧，不会让整段录像失败。
/// </para>
/// </remarks>
internal sealed class RecordCamera : IDisposable
{
    private readonly VideoCapture _capture;
    private Thread? _thread;
    private readonly byte[] _frame;

    private readonly object _gate = new();
    private volatile bool _stop;
    private bool _hasFrame;

    private RecordCamera(VideoCapture capture, int side)
    {
        _capture = capture;
        Side = side;
        _frame = new byte[side * side * 4];
    }

    /// <summary>画中画边长（输出像素）。</summary>
    public int Side { get; }

    /// <summary>最近是否真的拿到过画面（拿不到时调用方就不画）。</summary>
    public bool HasFrame
    {
        get { lock (_gate) return _hasFrame; }
    }

    /// <summary>
    /// 打开摄像头。失败返回 null 并给出一句人话（录制继续，不抛）。
    /// </summary>
    public static RecordCamera? TryStart(RecordOptions options, out string? warning)
    {
        warning = null;
        if (!options.HasCamera) return null;

        int index = Math.Max(0, options.CameraDevice);
        int side = Math.Clamp(options.CameraSize, RecordOptions.MinCameraSize, RecordOptions.MaxCameraSize);

        VideoCapture? capture = null;
        foreach (var api in new[] { VideoCaptureAPIs.DSHOW, VideoCaptureAPIs.MSMF, VideoCaptureAPIs.ANY })
        {
            try
            {
                var candidate = new VideoCapture(index, api);
                if (candidate.IsOpened())
                {
                    candidate.Set(VideoCaptureProperties.BufferSize, 1);
                    capture = candidate;
                    break;
                }
                candidate.Dispose();
            }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.Record", $"打开摄像头 {index}（{api}）失败：" + ex.Message);
            }
        }

        if (capture == null)
        {
            warning = $"摄像头 {index} 打不开（没有这个设备，或被别的程序占用）";
            return null;
        }

        var camera = new RecordCamera(capture, side);
        camera.Start(options.CameraMirrored);
        return camera;
    }

    private void Start(bool mirrored)
    {
        _thread = new Thread(() => Loop(mirrored))
        {
            IsBackground = true,
            Name = "LTools.RecordCamera",
        };
        _thread.Start();
    }

    private void Loop(bool mirrored)
    {
        using var frame = new Mat();
        using var square = new Mat();
        using var bgra = new Mat();

        int warned = 0;
        var clock = Stopwatch.StartNew();

        while (!_stop)
        {
            try
            {
                if (!_capture.Read(frame) || frame.Empty())
                {
                    if (Interlocked.Exchange(ref warned, 1) == 0)
                        AppLog.Warn("CapturePro.Record", "摄像头没有送回画面（后面只重试，不再刷日志）");
                    Thread.Sleep(30);
                    continue;
                }

                // 取中间的正方形（和上游一样：先保比例裁，再缩到边长）
                int side = Math.Min(frame.Width, frame.Height);
                var crop = new Rect((frame.Width - side) / 2, (frame.Height - side) / 2, side, side);
                using (var view = new Mat(frame, crop)) Cv2.Resize(view, square, new OpenCvSharp.Size(Side, Side));

                if (mirrored) Cv2.Flip(square, square, FlipMode.Y);
                Cv2.CvtColor(square, bgra, ColorConversionCodes.BGR2BGRA);

                lock (_gate)
                {
                    if (!_stop)
                    {
                        Marshal.Copy(bgra.Data, _frame, 0, _frame.Length);
                        _hasFrame = true;
                    }
                }

                // 摄像头一般 30fps，屏幕可能 60fps：按自己的节奏睡，别空转
                int wait = 33 - (int)(clock.ElapsedMilliseconds % 33);
                Thread.Sleep(Math.Clamp(wait, 2, 33));
            }
            catch (Exception ex)
            {
                if (Interlocked.Exchange(ref warned, 1) == 0)
                    AppLog.Warn("CapturePro.Record", "摄像头抓帧失败（后面只重试）：" + ex.Message);
                Thread.Sleep(100);
            }
        }
    }

    /// <summary>把最新一帧拷进 <paramref name="destination"/>（紧排 BGRA、边长 <see cref="Side"/>）。</summary>
    public bool TryCopyFrame(byte[] destination)
    {
        lock (_gate)
        {
            if (!_hasFrame || destination.Length < _frame.Length) return false;
            Buffer.BlockCopy(_frame, 0, destination, 0, _frame.Length);
            return true;
        }
    }

    public void Dispose()
    {
        _stop = true;

        try { if (_thread is { IsAlive: true }) _thread.Join(600); } catch { /* 已经退出 */ }

        try { _capture.Release(); } catch { /* 已经释放 */ }
        try { _capture.Dispose(); } catch { /* 已经释放 */ }
    }
}
