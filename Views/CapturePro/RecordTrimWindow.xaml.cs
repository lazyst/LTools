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

    private double _dragMsPerPixel;

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
            SetBusy(false);
            return;
        }

        PreviewHint.Text = "正在打开视频…";
        Preview.Source = new Uri(path);
        Preview.Volume = 1;

        // 时长 / 音轨：记录里有就用记录的，没有才去问 ffmpeg（可能要一两百毫秒）
        if (_durationMs <= 0) _durationMs = RecordTrimSession.DurationOf(_entry);
        if (_durationMs <= 0) _durationMs = 1000;

        _hasAudio = RecordTrimSession.HasAudio(_entry);

        _startMs = 0;
        _endMs = _durationMs;

        LayoutTimeline();
        Seek(0);
        SetBusy(false);
    }

    private void Preview_MediaOpened(object sender, RoutedEventArgs e)
    {
        _ready = true;
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
        UpdateTexts();
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

        double position = Preview.Position.TotalMilliseconds;
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
        if (!_ready) return;

        try
        {
            Preview.Position = TimeSpan.FromMilliseconds(Math.Clamp(ms, 0, _durationMs));
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Trim", "预览定位失败：" + ex.Message);
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
        if (_busy || !_ready) return;

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
        CopyBtn.IsEnabled = !busy && _ready;
        SaveAsBtn.IsEnabled = !busy && _ready;
        KeepBtn.IsEnabled = !busy && _ready;
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
