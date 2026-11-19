using System;
using System.Windows;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录屏面板空闲期的摄像头画中画预览（照 Snow Shot 的 <c>screenrecordingareawindow.cpp</c>
/// 里 <c>setWebcamOverlay</c> / <c>layoutWebcamControl</c> 那段）。
/// </summary>
/// <remarks>
/// <para>
/// 上游把交互层（<c>RecordingWebcamControl</c>）摆在**预览画面上**：只要摄像头开着、还在摆区域、
/// 不在倒计时里，它就一直在。LTools 空闲期没有合成预览，所以这个窗自己显示实时画面
/// （摄像头打不开就只有深灰底 + 一行提示，位置照样能摆——上游交互层同样不依赖设备成功）。
/// </para>
/// <para>
/// 三件东西的生命周期：**预览摄像头**跟着「能不能摆」走（开录时交给会话，见 <see cref="TakeCamera"/>，
/// 不重开设备）；**预览窗口**跟着同一条件走（开录 / 倒计时 / 取消 / 收尾都收掉）；
/// **取帧计时器**只在窗口可见时跑（30fps，窗口一藏就停，不吃空闲 CPU）。
/// </para>
/// <para>
/// 拖动 / 缩放 / 换方向都只改 <see cref="RecordPlan.Options"/> 里那几个字段（合成器每帧读它），
/// 松手提交与换方向时各写一次 <c>settings.json</c> 的 <c>Record</c> 段（上游
/// <c>setWebcamLayout</c> / <c>setWebcamOrientation</c> 的对应位置）。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    private RecordWebcamWindow? _webcamWindow;
    private RecordCamera? _previewCamera;
    private DispatcherTimer? _webcamFrameTimer;

    /// <summary>预览摄像头是用哪个设备开的（「录屏选项」里换了设备就要重开）。</summary>
    private int _previewCameraDevice = -1;

    /// <summary>后台正在开摄像头（打开设备要几百毫秒，不能在 UI 线程上等）。</summary>
    private bool _previewOpening;

    /// <summary>点了「录制」之后：窗口收掉，但摄像头留着交给会话（别重复开设备）。</summary>
    private bool _webcamSuspended;

    /// <summary>打不开摄像头只提示一次。</summary>
    private bool _webcamWarned;

    /// <summary>画中画预览窗（没有就是 null；冒烟用来查几何与 ex-style）。</summary>
    internal RecordWebcamWindow? WebcamWindow => _webcamWindow;

    /// <summary>预览窗现在是不是真的显示着。</summary>
    internal bool WebcamPreviewVisible => _webcamWindow?.IsVisible == true;

    /// <summary>预览用的摄像头（开录时交给会话；冒烟用来确认没有重复开设备）。</summary>
    internal RecordCamera? PreviewCamera => _previewCamera;

    /// <summary>能不能摆画中画：还在等用户点录制、没在收尾、摄像头开着。</summary>
    internal bool WebcamEditable =>
        _gate != null && !_recording && !_closing && !_finished && !_webcamSuspended
        && _plan.Options.CameraDevice >= 0;

    private void InitWebcam()
    {
        _webcamFrameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _webcamFrameTimer.Tick += (_, _) => PumpWebcamFrame();
    }

    /// <summary>
    /// 重新算一次：该不该有预览、摆在哪儿。UI 心跳（200ms）、关掉 / 打开摄像头、
    /// 改完录屏选项、拖动区域框之后都调它。
    /// </summary>
    internal void ReconcileWebcam()
    {
        if (_closing || !IsLoaded) return;

        if (!WebcamEditable)
        {
            HideWebcamWindow();

            // 开录（或已经点过录制）时摄像头要留给会话；其它情况（关掉摄像头 / 取消）就地收掉
            if (!_webcamSuspended && !_recording) ReleasePreviewCamera();
            return;
        }

        var screen = ScreenOf(_area);
        if (screen == null)
        {
            HideWebcamWindow();
            ReleasePreviewCamera();
            return;
        }

        // 正拖着 / 正缩放：别在旁边重摆几何（会把用户的手指从目标上「抢走」）
        if (_webcamWindow?.IsInteracting == true) return;

        // 「录屏选项」里换了设备 → 重开；只改了方向 → 就地生效（不再开第二份设备）
        if (_previewCamera != null)
        {
            if (_previewCameraDevice != _plan.Options.CameraDevice) ReleasePreviewCamera();
            else if (_previewCamera.Orientation != _plan.Options.CameraOrientation)
                _previewCamera.SetOrientation(_plan.Options.CameraOrientation);
        }

        EnsurePreviewCamera();

        var work = new Int32Rect(screen.WorkLeft, screen.WorkTop, screen.WorkWidth, screen.WorkHeight);
        double scale = screen.Scale > 0 ? screen.Scale : 1;

        try
        {
            var window = EnsureWebcamWindow();
            window.SetPlaceholder(_previewCamera?.HasFrame == true ? "" : PlaceholderText());
            window.Present(_area, _plan.Options.CameraSize, _plan.Options.CameraOffsetLeft,
                _plan.Options.CameraOffsetBottom, _plan.Options.CameraShape, work, scale);

            if (!_webcamFrameTimer!.IsEnabled) _webcamFrameTimer.Start();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "显示画中画预览失败：" + ex.Message);
        }
    }

    private string PlaceholderText() =>
        _previewCamera == null && _webcamWarned ? "摄像头打不开" : "正在连接摄像头…";

    /// <summary>
    /// 预览摄像头还没开就开一次；打不开只提示一次，位置照样能摆。
    /// 开设备要几百毫秒（还会闪摄像头灯），所以放后台线程，开完再回 UI 线程接着摆。
    /// </summary>
    private void EnsurePreviewCamera()
    {
        if (_previewCamera != null || _previewOpening) return;

        _previewOpening = true;
        var options = _plan.Options.Clone();

        Task.Run(() =>
        {
            var camera = RecordCamera.TryStart(options, out string? problem);

            Dispatcher.BeginInvoke(() =>
            {
                _previewOpening = false;

                if (_closing)
                {
                    try { camera?.Dispose(); }
                    catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关预览摄像头失败：" + ex.Message); }
                    return;
                }

                if (camera != null)
                {
                    _previewCamera = camera;
                    _previewCameraDevice = options.CameraDevice;
                }
                else if (!_webcamWarned)
                {
                    _webcamWarned = true;
                    ToastService.Show((problem ?? "摄像头打不开") + "，画中画位置仍可先摆好", "录屏", 3600,
                        ToastKind.warning);
                }

                ReconcileWebcam();
            });
        });
    }

    private RecordWebcamWindow EnsureWebcamWindow()
    {
        if (_webcamWindow != null) return _webcamWindow;

        var window = new RecordWebcamWindow();
        window.Previewed += OnWebcamPreviewed;
        window.Committed += OnWebcamCommitted;
        window.OrientationRequested += OnWebcamOrientation;
        _webcamWindow = window;
        return window;
    }

    /// <summary>把预览窗收掉（摄像头不动）。</summary>
    private void HideWebcamWindow()
    {
        _webcamFrameTimer?.Stop();

        var window = _webcamWindow;
        if (window == null) return;

        _webcamWindow = null;
        window.Previewed -= OnWebcamPreviewed;
        window.Committed -= OnWebcamCommitted;
        window.OrientationRequested -= OnWebcamOrientation;

        try { window.HidePreview(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "收起画中画预览失败：" + ex.Message); }

        try { window.Close(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关画中画预览失败：" + ex.Message); }
    }

    /// <summary>把预览摄像头收掉（预览窗已经关了 / 用不上了）。</summary>
    private void ReleasePreviewCamera()
    {
        var camera = _previewCamera;
        _previewCamera = null;
        _previewCameraDevice = -1;
        if (camera == null) return;

        try { camera.Dispose(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Record", "关预览摄像头失败：" + ex.Message); }
    }

    /// <summary>开录之前：窗口收掉、摄像头留给会话（<see cref="RecordSession"/> 调 <see cref="TakeCamera"/> 取走）。</summary>
    internal void SuspendWebcam()
    {
        _webcamSuspended = true;
        HideWebcamWindow();
    }

    /// <summary>会话取走预览摄像头（<c>null</c> = 预览没开成，调用方自己再试一次）。</summary>
    internal RecordCamera? TakeCamera()
    {
        var camera = _previewCamera;
        _previewCamera = null;
        _previewCameraDevice = -1;
        return camera;
    }

    private void PumpWebcamFrame()
    {
        var window = _webcamWindow;
        var camera = _previewCamera;
        if (window == null || !window.IsVisible || camera == null) return;

        if (!camera.HasFrame) return;
        window.SetPlaceholder("");
        window.PushFrame(camera);
    }

    // ============ 窗口事件 ============

    private void OnWebcamPreviewed(int size, int offsetLeft, int offsetBottom)
    {
        _plan.Options.CameraSize = size;
        _plan.Options.CameraOffsetLeft = offsetLeft;
        _plan.Options.CameraOffsetBottom = offsetBottom;
    }

    private void OnWebcamCommitted(int size, int offsetLeft, int offsetBottom)
    {
        OnWebcamPreviewed(size, offsetLeft, offsetBottom);
        PersistWebcam();
    }

    private void OnWebcamOrientation(RecordWebcamAction action)
    {
        var next = RecordWebcamLayout.Next(_plan.Options.CameraOrientation, action);
        _plan.Options.CameraOrientation = next;
        _previewCamera?.SetOrientation(next);
        PersistWebcam();
    }

    /// <summary>把画中画布局 / 方向写进设置里的 <c>Record</c> 段（其余字段原样带过去）。</summary>
    private void PersistWebcam()
    {
        try
        {
            var options = AppState.Record.Clone();
            options.CameraSize = _plan.Options.CameraSize;
            options.CameraOffsetLeft = _plan.Options.CameraOffsetLeft;
            options.CameraOffsetBottom = _plan.Options.CameraOffsetBottom;
            options.CameraShape = _plan.Options.CameraShape;
            options.CameraOrientation = _plan.Options.CameraOrientation;
            options = options.Normalized();

            AppState.Record = options;
            SettingsStore.Modify(file => file.Record = options);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "保存画中画布局失败：" + ex.Message);
        }
    }
}
