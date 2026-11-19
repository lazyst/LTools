using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LTools.Config;
using LTools.Core;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 麦克风 / 系统声音的增益弹层与电平表（照 Snow Shot 的 <c>RecordingAudioGainPopover</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 上游：两个声音按钮各挂一个悬停弹层（宽 180、Top 方向、悬停 150ms 开 / 250ms 关），弹层里一根
/// −24…+24 dB 的滑块，滑块轨道就是**处理后的电平表**（绿 = 正常、红 = 削波）。这里同样两根滑块，
/// 电平表画在滑块下面一条 4px 的细条上（WPF 滑块模板没有「填充比例」，另起一条最直观）。
/// </para>
/// <para>
/// 交互照上游：空闲时点按钮 = 开 / 关这一路声音；**录制中点按钮 = 打开增益弹层**（不切开关，
/// 录制中也不允许切换）。悬停始终能打开弹层。空闲时没开的那一路按钮禁用（上游
/// <c>enabled = (idle || enabled) &amp;&amp; !busy &amp;&amp; !animatedFormat</c>）。
/// </para>
/// <para>
/// 增益写回 <see cref="RecordOptions"/>（录制中改 = 整条轨生效，收尾的 ffmpeg 按它套
/// <c>volume</c>），并防抖落盘到 <c>configs/settings.json</c> 的 <c>Record</c> 段。
/// </para>
/// </remarks>
public partial class CaptureRecordWindow
{
    /// <summary>弹层宽度（上游 <c>content-&gt;setFixedWidth(180)</c>）。</summary>
    private const double GainPanelWidth = 180;

    private HoverPopover? _micGainPop;
    private HoverPopover? _speakerGainPop;
    private Slider? _micGainSlider;
    private Slider? _speakerGainSlider;
    private TextBlock? _micGainText;
    private TextBlock? _speakerGainText;
    private Grid? _micLevelBar;
    private Grid? _speakerLevelBar;
    private Border? _micLevelFill;
    private Border? _speakerLevelFill;

    private RecordAudio? _audio;
    private DispatcherTimer? _gainPersistTimer;

    /// <summary>会话把这次的两条音轨交给面板（电平表读数用）；没开声音就是 null。</summary>
    internal void AttachAudio(RecordAudio? audio) => _audio = audio;

    /// <summary>在两个声音按钮上挂增益弹层（<c>BuildActionRow</c> 末尾调一次）。</summary>
    private void InitGainPopovers()
    {
        _micGainPop = new HoverPopover(_micBtn, BuildGainPanel(RecordAudioSource.Microphone),
            RefreshGainPanels, HookPopupWindow, openDelayMs: 150, closeDelayMs: 250);
        _speakerGainPop = new HoverPopover(_speakerBtn, BuildGainPanel(RecordAudioSource.System),
            RefreshGainPanels, HookPopupWindow, openDelayMs: 150, closeDelayMs: 250);

        // 录制中点一下（而不是悬停）也打开弹层：上游 idle 时点击才切开关
        _micBtn.PreviewMouseLeftButtonDown += (_, e) => OpenGainWhileRecording(_micGainPop, e);
        _speakerBtn.PreviewMouseLeftButtonDown += (_, e) => OpenGainWhileRecording(_speakerGainPop, e);
    }

    private void OpenGainWhileRecording(HoverPopover? popover, MouseButtonEventArgs e)
    {
        if (!_recording || popover == null) return;

        popover.Show();
        e.Handled = true;
    }

    /// <summary>弹层内容：标题 + 当前增益 / 滑块 + 电平条。</summary>
    private StackPanel BuildGainPanel(RecordAudioSource source)
    {
        bool mic = source == RecordAudioSource.Microphone;
        int gain = GainOf(source);

        var value = new TextBlock
        {
            Text = RecordGain.Format(gain),
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            Foreground = (Brush)FindResource("CaptureTextSecondary"),
        };

        var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        var caption = new TextBlock
        {
            Text = mic ? "麦克风增益" : "系统声音增益",
            FontSize = 12,
            Foreground = (Brush)FindResource("CaptureTextSecondary"),
        };
        DockPanel.SetDock(caption, Dock.Left);
        header.Children.Add(caption);
        header.Children.Add(value);

        var slider = new Slider
        {
            Minimum = RecordGain.MinDb,
            Maximum = RecordGain.MaxDb,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Height = 20,
            Value = gain,
            ToolTip = RecordGain.Format(gain),
        };

        slider.ValueChanged += (_, e) =>
        {
            if (_syncing) return;

            int db = RecordGain.Clamp((int)Math.Round(e.NewValue));
            value.Text = RecordGain.Format(db);
            slider.ToolTip = RecordGain.Format(db);
            SetGain(source, db);
        };

        // 电平条：底轨 + 按峰值伸缩的填充（绿 / 红）
        var meter = new Grid { Height = 4, Margin = new Thickness(0, 6, 0, 0) };
        meter.Children.Add(new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            Background = (Brush)FindResource("CaptureBorder"),
        });

        var fill = new Border
        {
            Height = 4,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)FindResource("CaptureSuccess"),
            Width = 0,
        };
        meter.Children.Add(fill);

        var panel = new StackPanel
        {
            Width = GainPanelWidth,
            Orientation = Orientation.Vertical,
        };
        panel.Children.Add(header);
        panel.Children.Add(slider);
        panel.Children.Add(meter);

        if (mic)
        {
            _micGainSlider = slider;
            _micGainText = value;
            _micLevelBar = meter;
            _micLevelFill = fill;
        }
        else
        {
            _speakerGainSlider = slider;
            _speakerGainText = value;
            _speakerLevelBar = meter;
            _speakerLevelFill = fill;
        }

        return panel;
    }

    private int GainOf(RecordAudioSource source) => source == RecordAudioSource.Microphone
        ? _plan.Options.MicrophoneGainDb
        : _plan.Options.SystemAudioGainDb;

    private void SetGain(RecordAudioSource source, int db)
    {
        int value = RecordGain.Clamp(db);
        if (source == RecordAudioSource.Microphone) _plan.Options.MicrophoneGainDb = value;
        else _plan.Options.SystemAudioGainDb = value;

        ScheduleGainPersist();
    }

    /// <summary>每次 <c>SyncUi</c> 与弹层打开时重画两根滑块与电平条。</summary>
    private void RefreshGainPanels()
    {
        if (_plan.IsAnimated || !_micBtn.IsEnabled) _micGainPop?.Hide();
        if (_plan.IsAnimated || !_speakerBtn.IsEnabled) _speakerGainPop?.Hide();

        UpdateGainPanel(RecordAudioSource.Microphone, _micGainSlider, _micGainText,
            _micLevelBar, _micLevelFill);
        UpdateGainPanel(RecordAudioSource.System, _speakerGainSlider, _speakerGainText,
            _speakerLevelBar, _speakerLevelFill);
    }

    private void UpdateGainPanel(RecordAudioSource source, Slider? slider, TextBlock? text,
        Grid? bar, Border? fill)
    {
        if (slider == null) return;

        int db = GainOf(source);

        if (Math.Abs(slider.Value - db) > 0.01)
        {
            bool was = _syncing;
            _syncing = true;
            try { slider.Value = db; }
            finally { _syncing = was; }
        }

        if (text != null) text.Text = RecordGain.Format(db);
        slider.ToolTip = RecordGain.Format(db);

        bool on = source == RecordAudioSource.Microphone
            ? _plan.Options.Microphone
            : _plan.Options.SystemAudio;

        float raw = on && _audio != null ? _audio.RawPeakOf(source) : 0f;
        var (level, clipping) = RecordGain.Process(raw, db);

        if (fill != null)
        {
            double width = bar != null && bar.ActualWidth > 0 ? bar.ActualWidth : GainPanelWidth;
            fill.Width = Math.Round(width * level);
            fill.Background = (Brush)FindResource(clipping ? "CaptureDanger" : "CaptureSuccess");
        }

        if (text != null)
        {
            text.Foreground = (Brush)FindResource(clipping
                ? "CaptureDanger"
                : "CaptureTextSecondary");
        }
    }

    /// <summary>拖动滑块会连着发很多次，400ms 防抖后只写一次 <c>settings.json</c>。</summary>
    private void ScheduleGainPersist()
    {
        if (_gainPersistTimer == null)
        {
            _gainPersistTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(400),
            };
            _gainPersistTimer.Tick += (_, _) =>
            {
                _gainPersistTimer?.Stop();
                PersistGains();
            };
        }

        _gainPersistTimer.Stop();
        _gainPersistTimer.Start();
    }

    /// <summary>把增益写进设置里的 <c>Record</c> 段（其余字段原样带过去）。</summary>
    private void PersistGains()
    {
        try
        {
            var options = AppState.Record.Clone();
            options.MicrophoneGainDb = _plan.Options.MicrophoneGainDb;
            options.SystemAudioGainDb = _plan.Options.SystemAudioGainDb;
            options = options.Normalized();

            AppState.Record = options;
            SettingsStore.Modify(file => file.Record = options);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "保存声音增益失败：" + ex.Message);
        }
    }

    /// <summary>关窗 / 收尾时补写一次还没落盘的增益。</summary>
    private void FlushGains()
    {
        if (_gainPersistTimer?.IsEnabled != true) return;

        _gainPersistTimer.Stop();
        PersistGains();
    }
}
