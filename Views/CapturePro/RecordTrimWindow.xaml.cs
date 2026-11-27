using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using LTools.Core;
using LTools.Features.CapturePro;
using Microsoft.Win32;

namespace LTools.Views.CapturePro;

/// <summary>
/// 录完之后的裁剪 / 倍速窗口：源时间轴上定起止，选倍速，导出（复制 / 另存为 / 存进采集历史）。
/// </summary>
/// <remarks>
/// <para>
/// 时间轴是**源时间**：拖抓手只改起止，不改倍速；倍速只影响成品时长与预览速度
/// （照 Snow Shot：<c>The source timeline and trim-grip timecodes retain their original times</c>）。
/// </para>
/// <para>
/// 导出**永远写新文件**，原片不动；导出期间按钮禁用并显示「正在导出…」，
/// 失败只把错误显示在状态栏（不弹模态框，用户可以改一下区间再试）。
/// </para>
/// </remarks>
public partial class RecordTrimWindow : Window
{
    private readonly CaptureEntry _entry;
    private readonly DispatcherTimer _timer;

    private int _durationMs;
    private int _startMs;
    private int _endMs;
    private double _speed = 1.0;

    private bool _hasAudio;
    private bool _playing;
    private bool _busy;
    private bool _ready;

    /// <summary>能不能导出：只看「原片在 + 有片长」，**不看预览能不能放**。
    /// 预览依赖系统解码器（HEVC 之类常常没有），导出走 ffmpeg 完全不受影响。</summary>
    private bool _canExport;

    /// <summary>播放头位置（毫秒）。预览放不出来时它就是唯一的「用户拖到哪」的记录，
    /// 不能去读 <c>Preview.Position</c>（那样会永远停在 0，看起来就像时间轴拖不动）。</summary>
    private int _playheadMs;

    private double _dragMsPerPixel;

    /// <summary>预览「打开超时」看门狗：4 秒还没 MediaOpened 就把原因写出来。</summary>
    private DispatcherTimer? _openWatchdog;

    internal RecordTrimWindow(CaptureEntry entry)
    {
        InitializeComponent();

        _entry = entry;

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => SyncPlayhead();

        foreach (double speed in RecordTrim.Speeds)
            SpeedBox.Items.Add(RecordTrim.SpeedText(speed));
        SpeedBox.SelectedIndex = Math.Max(0, Array.IndexOf(RecordTrim.Speeds, 1.0));

        _durationMs = Math.Max(0, entry.DurationMs);
        _startMs = 0;
        _endMs = _durationMs;

        Loaded += Window_Loaded;
        Closed += (_, _) => _openWatchdog?.Stop();
        SizeChanged += (_, _) => LayoutTimeline();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !_busy) Close(); };

        CancelBtn.IsEnabled = true;
    }

    private string PathOfEntry => CaptureCatalog.FilePath(_entry);

    // ============ 打开 ============

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        string path = PathOfEntry;
        if (!File.Exists(path))
        {
            PreviewHint.Text = "原片不在了：" + path;
            _canExport = false;
            SetBusy(false);
            return;
        }

        PreviewHint.Text = "正在打开视频…";

        // 必须停在 Manual 上（Pause 状态下 WPF 禁止 Play/Pause/Stop，点「试看」会抛
        // NotSupportedException），但 Manual 下光设 Source 不会去打开文件 —— 得手动踢一脚。
        // 实测：只设 Source 8 秒都不来 MediaOpened；设完再 Pause() 约 285ms 就开，
        // 而且之后 Play() 照常可用。
        Preview.Source = new Uri(path);
        Preview.Volume = 1;
        try { Preview.Pause(); }
        catch (Exception ex) { AppLog.Warn("CapturePro.Trim", "预览起播失败：" + ex.Message); }

        // 4 秒还没打开就说明预览这条路大概走不通（某些容器 / 编码系统放不了），
        // 与其留一块永远空着的画面，不如把原因写出来——导出不受影响。
        _openWatchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _openWatchdog.Tick += (_, _) =>
        {
            _openWatchdog?.Stop();
            if (_ready) return;

            PreviewHint.Text = "预览打不开（可能缺系统解码器，例如 HEVC）——裁剪和导出照常可用";
            PreviewHint.Visibility = Visibility.Visible;
        };
        _openWatchdog.Start();

        // 时长 / 音轨：记录里有就用记录的，没有才去问 ffmpeg（可能要一两百毫秒）
        if (_durationMs <= 0) _durationMs = RecordTrimSession.DurationOf(_entry);
        if (_durationMs <= 0) _durationMs = 1000;

        _hasAudio = RecordTrimSession.HasAudio(_entry);
        _canExport = true;

        _startMs = 0;
        _endMs = _durationMs;

        LayoutTimeline();
        Seek(0);
        SetBusy(false);
    }

    private void Preview_MediaOpened(object sender, RoutedEventArgs e)
    {
        _ready = true;
        _openWatchdog?.Stop();
        PreviewHint.Visibility = Visibility.Collapsed;

        // 媒体自己报的时长最准（采集历史里记的是帧数换算出来的）
        if (Preview.NaturalDuration.HasTimeSpan)
        {
            int real = (int)Preview.NaturalDuration.TimeSpan.TotalMilliseconds;
            if (real > 0 && Math.Abs(real - _durationMs) > 500)
            {
                _durationMs = real;
                _endMs = Math.Min(_endMs <= 0 ? real : _endMs, real);
                if (_endMs <= _startMs) _endMs = real;
                LayoutTimeline();
            }
        }

        Preview.SpeedRatio = _speed;

        // 定格第一帧：Pause 起播不会自己播，先把播放头摆到选区起点（否则停在 0 也行，但这样更直观）
        Seek(_playheadMs > 0 ? _playheadMs : _startMs);

        UpdateTexts();

        // 预览就绪后必须重算一次按钮：「试看」的可用性挂在 _ready 上，
        // 而 Window_Loaded 那次 SetBusy 是在 MediaOpened 之前跑的（那时它还该是灰的）。
        SetBusy(_busy);
    }

    private void Preview_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        // 预览是「锦上添花」：编码 / 容器系统放不了（HEVC 没装扩展、动图格式…）时
        // 只收起播放，导出照常——否则整扇窗口会因为一个预览而变成死的。
        _ready = false;
        _openWatchdog?.Stop();
        PreviewHint.Text = "这个文件预览放不了（可能缺系统解码器，例如 HEVC）——裁剪和导出照常可用";
        PreviewHint.Visibility = Visibility.Visible;
        AppLog.Warn("CapturePro.Trim", "预览打不开：" + e.ErrorException?.Message);
        SetBusy(false);
    }

    private void Preview_MediaEnded(object sender, RoutedEventArgs e)
    {
        Stop();
        Seek(_startMs);
    }

    // ============ 时间轴 ============

    /// <summary>按当前时长 / 选区摆放轨道、抓手、播放头与时间码。</summary>
    private void LayoutTimeline()
    {
        double width = Timeline.ActualWidth;
        if (width <= 0 || _durationMs <= 0) return;

        _dragMsPerPixel = _durationMs / width;

        double startX = width * _startMs / _durationMs;
        double endX = width * _endMs / _durationMs;

        TrackBar.Width = width;
        Canvas.SetLeft(TrackBar, 0);
        Canvas.SetTop(TrackBar, (Timeline.Height - TrackBar.Height) / 2);

        SelectionBar.Width = Math.Max(2, endX - startX);
        Canvas.SetLeft(SelectionBar, startX);
        Canvas.SetTop(SelectionBar, (Timeline.Height - SelectionBar.Height) / 2);

        Canvas.SetLeft(StartGrip, startX - StartGrip.Width / 2);
        Canvas.SetTop(StartGrip, (Timeline.Height - StartGrip.Height) / 2);
        Canvas.SetLeft(EndGrip, endX - EndGrip.Width / 2);
        Canvas.SetTop(EndGrip, (Timeline.Height - EndGrip.Height) / 2);

        PlayheadBar.Height = Timeline.Height;
        Canvas.SetTop(PlayheadBar, 0);
        UpdatePlayhead();

        UpdateTexts();
    }

    private void UpdatePlayhead()
    {
        double width = Timeline.ActualWidth;
        if (width <= 0 || _durationMs <= 0) return;

        // 播着的时候以播放器的真实位置为准；没播 / 放不出来时用用户拖到的位置
        if (_playing && _ready) _playheadMs = (int)Preview.Position.TotalMilliseconds;
        double position = Math.Clamp(_playheadMs, 0, _durationMs);
        Canvas.SetLeft(PlayheadBar, Math.Clamp(width * position / _durationMs, 0, width - 2));
    }

    private void UpdateTexts()
    {
        var range = Range;
        StartText.Text = RecordTrim.FormatSeconds(_startMs);
        EndText.Text = RecordTrim.FormatSeconds(_endMs);
        RangeText.Text =
            $"选区 {RecordTrim.FormatSeconds(range.SourceMs)} · 成品 {RecordTrim.FormatSeconds(range.OutputMs)}";
        OutputText.Text =
            $"{_entry.Width} × {_entry.Height} · {RecordTrim.SpeedText(range.Speed)} · {_hasAudio switch { true => "带声音", _ => "无音轨" }}";
        BadgeText.Text = $"{RecordTrim.SpeedText(range.Speed)} · {RecordPlan.FormatClock(range.OutputMs)}";
        BadgeBox.Visibility = Visibility.Visible;
    }

    private TrimRange Range => RecordTrim.Normalize(_startMs, _endMs, _speed, _durationMs);

    private void StartGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        int delta = (int)Math.Round(e.HorizontalChange * _dragMsPerPixel);
        _startMs = Math.Clamp(_startMs + delta, 0, Math.Max(0, _endMs - RecordTrim.MinDurationMs));

        LayoutTimeline();
        Seek(_startMs);
    }

    private void EndGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        int delta = (int)Math.Round(e.HorizontalChange * _dragMsPerPixel);
        _endMs = Math.Clamp(_endMs + delta, _startMs + RecordTrim.MinDurationMs, _durationMs);

        LayoutTimeline();
        Seek(_endMs);
    }

    /// <summary>点 / 拖时间轴 = 把预览定位到那儿（抓手自己捕获鼠标，这里的移动不会打断它）。</summary>
    private void Timeline_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SeekTo(e.GetPosition(Timeline).X);
    }

    private void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        SeekTo(e.GetPosition(Timeline).X);
    }

    private void SeekTo(double x)
    {
        double width = Timeline.ActualWidth;
        if (width <= 0 || _durationMs <= 0) return;

        int ms = (int)Math.Round(Math.Clamp(x / width, 0, 1) * _durationMs);
        Seek(ms);
    }

    private void Seek(int ms)
    {
        _playheadMs = Math.Clamp(ms, 0, Math.Max(0, _durationMs));

        // 只有「真能放」的时候才去动 MediaElement；预览放不出来时，
        // 时间轴、选区、播放头照样要跟着指针走（否则整扇窗口看起来是死的）
        if (_ready)
        {
            try
            {
                Preview.Position = TimeSpan.FromMilliseconds(_playheadMs);
            }
            catch (Exception ex)
            {
                AppLog.Warn("CapturePro.Trim", "预览定位失败：" + ex.Message);
            }
        }

        UpdatePlayhead();
    }

    // ============ 播放 ============

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready || _busy) return;

        if (_playing)
        {
            Stop();
            return;
        }

        Seek(_startMs);
        Preview.SpeedRatio = _speed;
        Preview.Play();

        _playing = true;
        PlayBtn.Content = "⏸ 暂停";
        _timer.Start();
    }

    private void SyncPlayhead()
    {
        if (!_playing || _busy) return;

        UpdatePlayhead();

        // 只播选区：到头就回到起点（不越界，也不把用户丢进没选中的部分）
        if (Preview.Position.TotalMilliseconds >= _endMs) Seek(_startMs);
    }

    private void Stop()
    {
        try { Preview.Pause(); } catch { /* 已经停了 */ }

        _playing = false;
        PlayBtn.Content = "▶ 试看";
        _timer.Stop();
    }

    private void Speed_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        int index = SpeedBox.SelectedIndex;
        if (index < 0 || index >= RecordTrim.Speeds.Length) return;

        _speed = RecordTrim.Speeds[index];
        if (_ready) Preview.SpeedRatio = _speed;

        UpdateTexts();
    }

    // ============ 出口 ============

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Close();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Export(path =>
        {
            string final = RecordTrimSession.Remember(_entry, path, Range) ?? path;
            if (RecordTrimSession.CopyToClipboard(final))
                Toast("已复制裁剪结果到剪贴板", "复制成功");
            else
                Toast("复制到剪贴板失败（可以改用「另存为…」）", "复制失败");
        });
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "保存裁剪结果",
            Filter = "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = RecordTrimSession.BaseName(_entry) + "-裁剪.mp4",
            InitialDirectory = CaptureCatalog.RecordsDir,
        };

        if (dialog.ShowDialog(this) != true) return;

        Export(path => Toast("已保存：" + path, "裁剪完成", path), destination: dialog.FileName);
    }

    private void Keep_Click(object sender, RoutedEventArgs e)
    {
        Export(path =>
        {
            string? saved = RecordTrimSession.Remember(_entry, path, Range);
            Toast(saved == null ? "登记失败（写不进 captures/records）" : "已存进采集历史（"
                    + RecordPlan.FormatClock(Range.OutputMs) + "）", "裁剪完成");
        });
    }

    /// <summary>
    /// 导出：<paramref name="destination"/> 为空时先导到临时文件（成功后再交给 <paramref name="done"/> 处置）。
    /// </summary>
    private void Export(Action<string> done, string? destination = null)
    {
        if (_busy || !_canExport) return;

        var range = Range;
        bool hasAudio = _hasAudio;

        string output = destination ?? RecordTrimSession.TempOutput();
        SetBusy(true);

        System.Threading.Tasks.Task.Run(() =>
        {
            // 渲染进度窗摆在裁剪窗所在那块屏上（窗口只能在 UI 线程上问坐标）
            Int32Rect anchor = default;
            try
            {
                Dispatcher.Invoke(() =>
                {
                    var center = PointToScreen(new Point(ActualWidth / 2, ActualHeight / 2));
                    anchor = new Int32Rect((int)center.X, (int)center.Y, 0, 0);
                });
            }
            catch (Exception) { /* 窗口已经关了：用主屏 */ }

            bool ok = RecordTrimSession.Export(_entry, output, range, hasAudio, out string error, anchor);

            Dispatcher.BeginInvoke(() =>
            {
                SetBusy(false);

                if (!ok)
                {
                    OutputText.Text = "导出失败：" + error;
                    AppLog.Warn("CapturePro.Trim", "导出失败：" + error);
                    Toast("导出失败：" + error, "裁剪失败");
                    try { if (File.Exists(output)) File.Delete(output); } catch { /* 删不掉就算了 */ }
                    return;
                }

                done(output);
            });
        });
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;

        CancelBtn.IsEnabled = !busy;
        CopyBtn.IsEnabled = !busy && _canExport;
        SaveAsBtn.IsEnabled = !busy && _canExport;
        KeepBtn.IsEnabled = !busy && _canExport;

        // 只有「播放预览」需要解码器；导出走 ffmpeg，预览放不出来也照样能导
        PlayBtn.IsEnabled = !busy && _ready;

        if (busy)
        {
            OutputText.Text = "正在导出…";
            Stop();
        }
        else
        {
            UpdateTexts();
        }
    }

    private static void Toast(string message, string title, string? path = null)
    {
        ToastService.Show(message, title, 3200, ToastKind.success,
            onClick: path == null ? null : () => OpenFolder(System.IO.Path.GetDirectoryName(path)!),
            actionText: path == null ? null : "打开文件夹");
    }

    private static void OpenFolder(string directory)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Trim", "打开目录失败：" + ex.Message);
        }
    }
}
