using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using LTools.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录制声音：系统环回（WASAPI loopback）+ 麦克风，各写一条 WAV，收尾时由 ffmpeg 混成 AAC。
/// </summary>
/// <remarks>
/// <para>
/// 走 WASAPI 环回而不是「立体声混音」设备：后者要在声音设置里手工启用、还得是特定驱动才有，
/// 而环回是系统级能力——只要有人放声音就录得到（上游 <c>snow-audio-recorder</c> 也是这条路）。
/// </para>
/// <para>
/// **时长必须和视频对齐**：视频时间轴用的是 <see cref="RecordController"/> 的「活动时长」（暂停不推进），
/// 所以音频也按它算目标位置——设备只在有声音时送数据时（环回静音期常见）补静音，
/// 暂停期间的缓冲直接丢掉，收尾再按活动时长补齐 / 截断到整块。
/// 这样暂停 / 静音都不会让音画错位。
/// </para>
/// <para>
/// 任何一路打不开（没麦克风 / 被独占 / 没有默认设备）都只记一条警告，另一路照录；
/// 两路都打不开就等同于「没有声音」，录制继续。
/// </para>
/// </remarks>
internal sealed class RecordAudio : IDisposable
{
    private readonly List<Track> _tracks = new();
    private readonly RecordController _controller;

    private RecordAudio(RecordController controller) => _controller = controller;

    /// <summary>打开的声音通道（收尾后仍保留路径，供 ffmpeg 混音）。</summary>
    public IReadOnlyList<string> Tracks
    {
        get
        {
            var list = new List<string>(_tracks.Count);
            foreach (var track in _tracks) list.Add(track.Path);
            return list;
        }
    }

    public bool Any => _tracks.Count > 0;

    /// <summary>打开失败时的说明（没有失败为 null）。</summary>
    public string? Warning { get; private set; }

    /// <summary>
    /// 按选项开始采集。<paramref name="directory"/> 是录制缓存目录（与视频同一个）。
    /// 一条都打不开时返回的对象 <see cref="Any"/> 为 false（调用方照常录画面）。
    /// </summary>
    public static RecordAudio Start(string directory, RecordOptions options, RecordController controller)
    {
        var audio = new RecordAudio(controller);
        var problems = new List<string>();

        if (options.SystemAudio)
        {
            try
            {
                audio._tracks.Add(Track.StartSystem(Path.Combine(directory, "audio-system.wav"), controller));
            }
            catch (Exception ex)
            {
                problems.Add("系统声音：" + ex.Message);
                AppLog.Warn("CapturePro.Record", "打开系统声音失败：" + ex.Message);
            }
        }

        if (options.Microphone)
        {
            try
            {
                audio._tracks.Add(Track.StartMicrophone(
                    Path.Combine(directory, "audio-mic.wav"), options.MicrophoneDevice, controller));
            }
            catch (Exception ex)
            {
                problems.Add("麦克风：" + ex.Message);
                AppLog.Warn("CapturePro.Record", "打开麦克风失败：" + ex.Message);
            }
        }

        if (problems.Count > 0) audio.Warning = string.Join("；", problems);
        return audio;
    }

    /// <summary>收尾：停设备、按活动时长补齐 / 截断、写完 WAV 头。</summary>
    public void Stop()
    {
        long activeMs = _controller.ElapsedMs;
        foreach (var track in _tracks) track.Stop(activeMs);
    }

    public void Dispose() => Stop();

    // ============ 单路 ============

    private sealed class Track
    {
        private readonly IWaveIn _capture;
        private readonly WaveFileWriter _writer;
        private readonly RecordController _controller;
        private readonly object _gate = new();
        private readonly ManualResetEventSlim _stopped = new(false);

        private long _written;
        private bool _closed;

        private Track(IWaveIn capture, WaveFileWriter writer, RecordController controller)
        {
            _capture = capture;
            _writer = writer;
            _controller = controller;
        }

        public string Path => _writer.Filename;

        public static Track StartSystem(string path, RecordController controller) =>
            Start(new WasapiLoopbackCapture(), path, controller);

        public static Track StartMicrophone(string path, string? deviceName, RecordController controller) =>
            Start(new WasapiCapture(PickMicrophone(deviceName)), path, controller);

        /// <summary>按名字挑麦克风；名字为空或找不到就用系统默认输入设备。</summary>
        private static MMDevice PickMicrophone(string? deviceName)
        {
            using var enumerator = new MMDeviceEnumerator();
            var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

            string? wanted = deviceName?.Trim();
            if (string.IsNullOrEmpty(wanted)) return fallback;

            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                string name = device.FriendlyName ?? "";
                if (name.Contains(wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(device.ID, wanted, StringComparison.OrdinalIgnoreCase))
                    return device;
            }

            AppLog.Warn("CapturePro.Record", $"没找到麦克风「{wanted}」，改用系统默认输入设备");
            return fallback;
        }

        private static Track Start(IWaveIn capture, string path, RecordController controller)
        {
            var writer = new WaveFileWriter(path, capture.WaveFormat);
            var track = new Track(capture, writer, controller);

            capture.DataAvailable += track.OnData;
            capture.RecordingStopped += track.OnStopped;
            capture.StartRecording();

            return track;
        }

        private void OnData(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            lock (_gate)
            {
                if (_closed) return;

                // 暂停：这段既不算进视频，也不该进音轨（与视频时间轴同一个判定）
                if (_controller.IsPaused) return;

                try
                {
                    long target = BytesAt(_controller.ElapsedMs);

                    // 设备在有声音时才送数据（环回静音期就是这样）：先把缺的那段补成静音，
                    // 否则后面的声音会整体往前挪，越录越不对口型
                    long gap = target - _written;
                    if (gap > Block * 25) WriteSilence(gap - Block);

                    int bytes = e.BytesRecorded;
                    _writer.Write(e.Buffer, 0, bytes);
                    _written += bytes;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("CapturePro.Record", "写音轨失败：" + ex.Message);
                }
            }
        }

        private void OnStopped(object? sender, StoppedEventArgs e)
        {
            if (e.Exception != null)
                AppLog.Warn("CapturePro.Record", "音频采集异常：" + e.Exception.Message);
            _stopped.Set();
        }

        public void Stop(long activeMs)
        {
            lock (_gate)
            {
                if (_closed) return;
                _closed = true;
            }

            try
            {
                _capture.StopRecording();
                _stopped.Wait(2000);
            }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.Record", "停音频采集失败：" + ex.Message);
            }

            lock (_gate)
            {
                try
                {
                    // 对齐到视频的「活动时长」：短了补静音、长了截到整块，音画才对得上
                    long target = Align(BytesAt(activeMs));
                    if (target > _written) WriteSilence(target - _written);

                    // 比视频长的一段不在这里截：混音用 -shortest 收口（截文件反而会毁掉 WAV 头）
                    _writer.Flush();
                }
                catch (Exception ex)
                {
                    AppLog.Warn("CapturePro.Record", "收尾音轨失败：" + ex.Message);
                }
                finally
                {
                    try { _writer.Dispose(); } catch { /* 已经关过 */ }
                    try { _capture.Dispose(); } catch { /* 已经关过 */ }
                    _stopped.Dispose();
                }
            }
        }

        private int Block => Math.Max(1, _writer.WaveFormat.BlockAlign);

        private long BytesAt(long ms) =>
            ms <= 0 ? 0 : ms * _writer.WaveFormat.AverageBytesPerSecond / 1000;

        private long Align(long bytes) => bytes - bytes % Block;

        private void WriteSilence(long bytes)
        {
            long aligned = Align(bytes);
            if (aligned <= 0) return;

            var silence = new byte[Math.Min(aligned, Block * 4096L)];
            while (aligned > 0)
            {
                int chunk = (int)Math.Min(aligned, silence.Length);
                _writer.Write(silence, 0, chunk);
                _written += chunk;
                aligned -= chunk;
            }
        }
    }
}
