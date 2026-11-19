using System;
using System.Collections.Generic;
using System.Globalization;

namespace LTools.Features.CapturePro;

/// <summary>录屏编码器意图：<c>auto</c> 有硬件就用硬件，<c>h264</c> / <c>hevc</c> 指定编码格式。</summary>
public enum RecordCodec
{
    auto,
    h264,
    hevc,
}

/// <summary>摄像头画中画的形状（照 Snow Shot：方形 / 圆形）。</summary>
public enum RecordCameraShape
{
    square,
    circle,
}

/// <summary>
/// 摄像头画面的方向（照上游 <c>snow-recording-model/src/webcam.rs</c> 的 <c>WebcamOrientation</c>）：
/// 低两位是**顺时针**四分之一圈数，第 3 位（值 4）= 先左右翻转再旋转。
/// </summary>
/// <remarks>
/// 数值就是落盘值（<c>settings.json</c> 的 <c>Record.CameraOrientation</c>），
/// 顺序照上游 <c>from_raw</c>，别重排。面板上的四个按钮在
/// <see cref="RecordWebcamLayout.Next"/> 里换算。
/// </remarks>
public enum RecordCameraOrientation
{
    identity = 0,
    clockwise90 = 1,
    clockwise180 = 2,
    clockwise270 = 3,
    mirrored = 4,
    mirroredClockwise90 = 5,
    mirroredClockwise180 = 6,
    mirroredClockwise270 = 7,
}

/// <summary>
/// 一次录屏的「录什么以外」的那些开关：声音、摄像头、键鼠特效、编码器、录完是否进裁剪。
/// </summary>
/// <remarks>
/// <para>
/// 这是**落盘形状**（<c>configs/settings.json</c> 的 <c>Record</c> 段），也是运行期参数：
/// 覆盖层工具条的「录屏」用这里的默认值，内置功能 <c>common.record</c> 的参数按次覆盖。
/// </para>
/// <para>
/// 所有夹取都在 <see cref="Normalized"/> 一处（尺寸 50–500、高亮半径 8–128），
/// <c>--selftest</c> 直接断言；实际采集在 <see cref="RecordAudio"/> / <see cref="RecordCamera"/> /
/// <see cref="RecordEffects"/>，编码器选择在 <see cref="RecordEncoders"/>。
/// </para>
/// </remarks>
public sealed class RecordOptions
{
    /// <summary>摄像头画中画的默认边长（与上游 <c>WebcamStyle::default().size</c> 一致）。</summary>
    public const int DefaultCameraSize = 150;

    /// <summary>画中画离左下角的默认距离（上游 <c>offset_left</c> / <c>offset_bottom</c>）。</summary>
    public const int DefaultCameraOffset = 48;

    public const int MinCameraSize = 50;
    public const int MaxCameraSize = 500;

    /// <summary>光标高亮的默认半径（上游 <c>DEFAULT_MOUSE_HIGHLIGHT_RADIUS</c>）。</summary>
    public const int DefaultHighlightRadius = 41;

    public const int MinHighlightRadius = 8;
    public const int MaxHighlightRadius = 128;

    /// <summary>录制帧率（上游 <c>screen_recording/frame_rate</c>，默认 30）。</summary>
    public const int DefaultFrameRate = 30;
    public const int MinFrameRate = 5;
    public const int MaxFrameRate = 60;

    /// <summary>动图（GIF / APNG / WebP）的帧率（上游 <c>animated_image_frame_rate</c>，默认 10）。</summary>
    public const int DefaultAnimatedFrameRate = 10;
    public const int MinAnimatedFrameRate = 1;
    public const int MaxAnimatedFrameRate = 30;

    /// <summary>激光拖尾寿命（上游 <c>mouse_trail_duration_ms</c>，100–2000 步长 100，默认 500）。</summary>
    public const int DefaultTrailDurationMs = 500;
    public const int MinTrailDurationMs = 100;
    public const int MaxTrailDurationMs = 2000;

    /// <summary>按键浮层的高度（上游 <c>keyboard_size</c>，32–128 步长 8，默认 64）。</summary>
    public const int DefaultKeyboardSize = 64;
    public const int MinKeyboardSize = 32;
    public const int MaxKeyboardSize = 128;

    /// <summary>画面质量（上游 <c>video_quality</c>，0–100，默认 80）。</summary>
    public const int DefaultVideoQuality = 80;

    /// <summary>开录前的等待秒数（上游 <c>start_delay_seconds</c>，0–10，默认 0）。</summary>
    public const int DefaultStartDelaySeconds = 0;
    public const int MaxStartDelaySeconds = 10;

    /// <summary>清晰度档位（上游 <c>clarity</c>）。</summary>
    public static readonly string[] ClarityOptions = { "4k", "2k", "1080p", "720p", "480p" };

    /// <summary>动图清晰度档位（上游 <c>animated_image_clarity</c>）。</summary>
    public static readonly string[] AnimatedClarityOptions = { "1080p", "720p", "480p" };

    /// <summary>成品格式（上游 <c>output_format</c>）。</summary>
    public static readonly string[] FormatOptions = { "mp4", "gif", "apng", "webp" };

    /// <summary>x264 / x265 的速度档（上游 <c>encoding_preset</c>）。</summary>
    public static readonly string[] PresetOptions =
        { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };

    /// <summary>录系统声音（WASAPI 环回，不需要「立体声混音」设备）。默认开：Snow Shot 的 `enable_system_audio` 默认 true。</summary>
    public bool SystemAudio { get; set; } = true;

    /// <summary>录麦克风。</summary>
    public bool Microphone { get; set; }

    /// <summary>麦克风设备名；空 = 系统默认输入设备。</summary>
    public string? MicrophoneDevice { get; set; }

    /// <summary>
    /// 麦克风增益（dB，−24…+24，默认 0）。收尾时按轨套 ffmpeg <c>volume</c>；
    /// 面板上悬停麦克风按钮弹出的增益弹层改的就是它（照上游 <c>microphone_gain_db</c>）。
    /// </summary>
    public int MicrophoneGainDb { get; set; } = RecordGain.DefaultDb;

    /// <summary>系统声音增益（dB，−24…+24，默认 0；上游 <c>system_audio_gain_db</c>）。</summary>
    public int SystemAudioGainDb { get; set; } = RecordGain.DefaultDb;

    /// <summary>摄像头序号（OpenCV 的设备号）；负数 = 不录。</summary>
    public int CameraDevice { get; set; } = -1;

    /// <summary>画中画边长（物理像素，夹到 50–500）。</summary>
    public int CameraSize { get; set; } = DefaultCameraSize;

    public RecordCameraShape CameraShape { get; set; } = RecordCameraShape.square;

    /// <summary>
    /// 摄像头画面的方向（旋转 / 镜像）。默认 <c>identity</c>，与上游 <c>webcam_orientation</c> 默认 0 一致；
    /// 面板空闲期预览上的四个按钮改的就是它（旋转 / 左右翻 / 上下翻）。
    /// </summary>
    public RecordCameraOrientation CameraOrientation { get; set; } = RecordCameraOrientation.identity;

    public int CameraOffsetLeft { get; set; } = DefaultCameraOffset;

    public int CameraOffsetBottom { get; set; } = DefaultCameraOffset;

    /// <summary>把系统光标画进画面（Snow Shot 第二行的「光标」按钮，默认开）。</summary>
    public bool ShowCursor { get; set; } = true;

    /// <summary>光标高亮（一个跟着鼠标的圆环；第二行「光标」弹层里的第一项）。</summary>
    public bool MouseHighlight { get; set; }

    public int MouseHighlightRadius { get; set; } = DefaultHighlightRadius;

    /// <summary>记录鼠标点击：按键浮层里多出「左键 / 右键 / 中键」（上游 <c>record_mouse_clicks</c>）。</summary>
    public bool RecordClicks { get; set; }

    /// <summary>按键浮层（按下的键以小胶囊显示在左下角）。</summary>
    public bool Keystroke { get; set; }

    /// <summary>按键浮层里收哪些键（照上游 <c>keyboard_filter.rs</c> 的四个开关）。</summary>
    public bool RecordNumberKeys { get; set; } = true;

    public bool RecordLetterKeys { get; set; } = true;

    public bool RecordControlKeys { get; set; } = true;

    /// <summary>按住 Ctrl / Alt / Win 时，本来被上面三条排除的键也显示（快捷键）。</summary>
    public bool PreserveShortcuts { get; set; } = true;

    /// <summary>鼠标滚动指示（按键浮层里多出「向上滚动 / 向下滚动」，上游 <c>record_mouse_scrolling</c>）。</summary>
    public bool RecordScroll { get; set; }

    /// <summary>后期处理特效：进度条 / 播放时间（第二行的「后期处理」按钮 + 弹层）。</summary>
    public bool PostProcessing { get; set; }

    /// <summary><c>true</c> = 底部进度条；<c>false</c> = 右下角播放时间（上游两个单选）。</summary>
    public bool PostProcessingProgressBar { get; set; } = true;

    /// <summary>
    /// 激光笔拖尾颜色（<c>#AARRGGBB</c>）。**拖尾没有开关**：alpha 为 0 = 不画（上游的透明色卡），
    /// 默认就是透明色，和 Snow Shot 一致。
    /// </summary>
    public string LaserColor { get; set; } = "#00000000";

    /// <summary>点击涟漪颜色（同上：alpha 为 0 = 不画）。</summary>
    public string ClickColor { get; set; } = "#00000000";

    /// <summary>拖尾开着（颜色不是透明的）。</summary>
    public bool HasLaser => AlphaOf(LaserColor) > 0;

    /// <summary>点击涟漪开着（颜色不是透明的）。</summary>
    public bool HasClickRipple => AlphaOf(ClickColor) > 0;

    public RecordCodec Codec { get; set; } = RecordCodec.auto;

    /// <summary>录完直接打开裁剪 / 倍速窗口。</summary>
    public bool TrimAfterRecord { get; set; }

    /// <summary>
    /// 工具条贴到屏幕左 / 右边缘时自动收起成一条小条、悬停唤出
    /// （上游 <c>screen_recording/auto_hide_toolbar</c>，默认关）。
    /// </summary>
    public bool AutoHideToolbar { get; set; }

    /// <summary>
    /// 两路声音各自成一条音轨（上游 <c>separate_audio_tracks</c>，默认关）。
    /// 关着时收尾按 <c>amix</c> 混成一条；开着时保留两条（系统声音 + 麦克风）各调各的。
    /// </summary>
    public bool SeparateAudioTracks { get; set; }

    /// <summary>输出分辨率档位（上游 <c>clarity</c>，默认 <c>1080p</c>）：只往下缩，小画面不放大。</summary>
    public string Clarity { get; set; } = "1080p";

    /// <summary>录制帧率（上游 <c>frame_rate</c>，默认 30）。</summary>
    public int FrameRate { get; set; } = DefaultFrameRate;

    /// <summary>动图清晰度（上游 <c>animated_image_clarity</c>，默认 <c>720p</c>）。</summary>
    public string AnimatedClarity { get; set; } = "720p";

    /// <summary>动图帧率（上游 <c>animated_image_frame_rate</c>，默认 10）。</summary>
    public int AnimatedFrameRate { get; set; } = DefaultAnimatedFrameRate;

    /// <summary>动图无限循环（上游 <c>loop_animated_images</c>，默认开）。</summary>
    public bool LoopAnimated { get; set; } = true;

    /// <summary>默认成品格式（上游 <c>output_format</c>，默认 <c>mp4</c>）；面板上仍可逐次改。</summary>
    public string OutputFormat { get; set; } = "mp4";

    /// <summary>激光拖尾寿命（毫秒，100–2000，默认 500）。</summary>
    public int TrailDurationMs { get; set; } = DefaultTrailDurationMs;

    /// <summary>按键浮层高度（物理像素，32–128，默认 64）。</summary>
    public int KeyboardSize { get; set; } = DefaultKeyboardSize;

    /// <summary>按键浮层底色（<c>#AARRGGBB</c>，上游 <c>keyboard_background_color</c>）。</summary>
    public string KeyboardBackgroundColor { get; set; } = "#000000CC";

    /// <summary>按键浮层字色（<c>#AARRGGBB</c>，上游 <c>keyboard_foreground_color</c>）。</summary>
    public string KeyboardForegroundColor { get; set; } = "#FFFFFFFF";

    /// <summary>光标高亮的颜色（<c>#AARRGGBB</c>，上游 <c>mouse_highlight_color</c>）。</summary>
    public string MouseHighlightColor { get; set; } = "#FFFF0080";

    /// <summary>底部进度条颜色（<c>#AARRGGBB</c>，上游 <c>progress_bar_color</c>）。</summary>
    public string ProgressBarColor { get; set; } = "#1677FFFF";

    /// <summary>跟随鼠标（上游 <c>follow_mouse</c>，默认关）。</summary>
    public bool FollowMouse { get; set; }

    /// <summary>跟随鼠标时平滑移动（上游 <c>follow_mouse_smooth_movement</c>，默认开）。</summary>
    public bool FollowMouseSmooth { get; set; } = true;

    /// <summary>跟随鼠标时抑制抖动（上游 <c>follow_mouse_reduce_jitter</c>，默认开）。</summary>
    public bool FollowMouseReduceJitter { get; set; } = true;

    /// <summary>画面质量（0–100，默认 80）：换算成各编码器的 crf / cq / qp。</summary>
    public int VideoQuality { get; set; } = DefaultVideoQuality;

    /// <summary>x264 / x265 的速度档（默认 <c>veryfast</c>）。</summary>
    public string EncodingPreset { get; set; } = "veryfast";

    /// <summary>录制时把录屏工具条也录进画面（上游 <c>capture_toolbar_in_recording</c>，默认开）。</summary>
    public bool CaptureToolbarInRecording { get; set; } = true;

    /// <summary>录制过程中显示状态提示（上游 <c>show_status_prompts</c>，默认开）。</summary>
    public bool ShowStatusPrompts { get; set; } = true;

    /// <summary>录完导出成功后自动退出录屏（上游 <c>auto_exit_after_recording_ends</c>，默认关）。</summary>
    public bool AutoExitAfterRecording { get; set; }

    /// <summary>导出完成后弹通知（上游 <c>notify_after_export_completes</c>，默认关）。</summary>
    public bool NotifyAfterExport { get; set; }

    /// <summary>关闭录屏面板时删掉刚录的文件（上游 <c>delete_files_on_recording_close</c>，默认关）。</summary>
    public bool DeleteFilesOnClose { get; set; }

    /// <summary>开录前的等待秒数（0–10，默认 0；面板上的倒计时用它当默认值）。</summary>
    public int StartDelaySeconds { get; set; } = DefaultStartDelaySeconds;

    /// <summary>录屏保存目录；空 = 系统「视频」目录（上游 <c>video_save_directory</c>）。</summary>
    public string SaveDirectory { get; set; } = "";

    /// <summary>录屏文件名模板（上游 <c>video_filename_format</c>）。</summary>
    public string FilenameFormat { get; set; } = "SnowShot_Video_{YYYY-MM-DD_HH-mm-ss}";

    public bool HasAudio => SystemAudio || Microphone;

    public bool HasCamera => CameraDevice >= 0;

    public bool HasEffects => MouseHighlight || RecordClicks || RecordScroll || Keystroke
        || HasLaser || HasClickRipple || PostProcessing;

    /// <summary>要录的东西一样都没有（纯画面、默认编码）：控制条上就不用列那几个角标。</summary>
    public bool IsPlain => !HasAudio && !HasCamera && !HasEffects;

    public RecordOptions Clone() => (RecordOptions)MemberwiseClone();

    /// <summary>
    /// 把另一份的值搬进来（就地改）：录屏面板持有一份 <see cref="RecordPlan.Options"/>，
    /// 「录屏选项」窗口保存后要让**同一个实例**立刻生效（composer 每帧读它）。
    /// </summary>
    public void CopyFrom(RecordOptions other)
    {
        if (other == null || ReferenceEquals(other, this)) return;

        SystemAudio = other.SystemAudio;
        Microphone = other.Microphone;
        MicrophoneDevice = other.MicrophoneDevice;
        MicrophoneGainDb = other.MicrophoneGainDb;
        SystemAudioGainDb = other.SystemAudioGainDb;
        CameraDevice = other.CameraDevice;
        CameraSize = other.CameraSize;
        CameraShape = other.CameraShape;
        CameraOrientation = other.CameraOrientation;
        CameraOffsetLeft = other.CameraOffsetLeft;
        CameraOffsetBottom = other.CameraOffsetBottom;
        ShowCursor = other.ShowCursor;
        MouseHighlight = other.MouseHighlight;
        MouseHighlightRadius = other.MouseHighlightRadius;
        RecordClicks = other.RecordClicks;
        Keystroke = other.Keystroke;
        RecordNumberKeys = other.RecordNumberKeys;
        RecordLetterKeys = other.RecordLetterKeys;
        RecordControlKeys = other.RecordControlKeys;
        PreserveShortcuts = other.PreserveShortcuts;
        RecordScroll = other.RecordScroll;
        PostProcessing = other.PostProcessing;
        PostProcessingProgressBar = other.PostProcessingProgressBar;
        LaserColor = other.LaserColor;
        ClickColor = other.ClickColor;
        Codec = other.Codec;
        TrimAfterRecord = other.TrimAfterRecord;
        AutoHideToolbar = other.AutoHideToolbar;
        SeparateAudioTracks = other.SeparateAudioTracks;
        Clarity = other.Clarity;
        FrameRate = other.FrameRate;
        AnimatedClarity = other.AnimatedClarity;
        AnimatedFrameRate = other.AnimatedFrameRate;
        LoopAnimated = other.LoopAnimated;
        OutputFormat = other.OutputFormat;
        TrailDurationMs = other.TrailDurationMs;
        KeyboardSize = other.KeyboardSize;
        KeyboardBackgroundColor = other.KeyboardBackgroundColor;
        KeyboardForegroundColor = other.KeyboardForegroundColor;
        MouseHighlightColor = other.MouseHighlightColor;
        ProgressBarColor = other.ProgressBarColor;
        FollowMouse = other.FollowMouse;
        FollowMouseSmooth = other.FollowMouseSmooth;
        FollowMouseReduceJitter = other.FollowMouseReduceJitter;
        VideoQuality = other.VideoQuality;
        EncodingPreset = other.EncodingPreset;
        CaptureToolbarInRecording = other.CaptureToolbarInRecording;
        ShowStatusPrompts = other.ShowStatusPrompts;
        AutoExitAfterRecording = other.AutoExitAfterRecording;
        NotifyAfterExport = other.NotifyAfterExport;
        DeleteFilesOnClose = other.DeleteFilesOnClose;
        StartDelaySeconds = other.StartDelaySeconds;
        SaveDirectory = other.SaveDirectory;
        FilenameFormat = other.FilenameFormat;
    }

    /// <summary>
    /// 只搬「全局设置」那一批字段（清晰度 / 帧率 / 质量 / 特效尺寸配色 / 跟随鼠标 / 保存目录…）。
    /// 录屏选项窗口只改「这次录什么」，这些不归它管——保存时从旧值搬回来，别被默认值冲掉。
    /// </summary>
    public void CopySettingsFrom(RecordOptions other)
    {
        if (other == null || ReferenceEquals(this, other)) return;

        SeparateAudioTracks = other.SeparateAudioTracks;
        Clarity = other.Clarity;
        FrameRate = other.FrameRate;
        AnimatedClarity = other.AnimatedClarity;
        AnimatedFrameRate = other.AnimatedFrameRate;
        LoopAnimated = other.LoopAnimated;
        OutputFormat = other.OutputFormat;
        TrailDurationMs = other.TrailDurationMs;
        KeyboardSize = other.KeyboardSize;
        KeyboardBackgroundColor = other.KeyboardBackgroundColor;
        KeyboardForegroundColor = other.KeyboardForegroundColor;
        MouseHighlightColor = other.MouseHighlightColor;
        ProgressBarColor = other.ProgressBarColor;
        FollowMouse = other.FollowMouse;
        FollowMouseSmooth = other.FollowMouseSmooth;
        FollowMouseReduceJitter = other.FollowMouseReduceJitter;
        VideoQuality = other.VideoQuality;
        EncodingPreset = other.EncodingPreset;
        CaptureToolbarInRecording = other.CaptureToolbarInRecording;
        ShowStatusPrompts = other.ShowStatusPrompts;
        AutoExitAfterRecording = other.AutoExitAfterRecording;
        NotifyAfterExport = other.NotifyAfterExport;
        DeleteFilesOnClose = other.DeleteFilesOnClose;
        StartDelaySeconds = other.StartDelaySeconds;
        SaveDirectory = other.SaveDirectory;
        FilenameFormat = other.FilenameFormat;
    }

    /// <summary>夹紧数值字段（尺寸 / 半径 / 边距），并把负边距收成 0。</summary>
    public RecordOptions Normalized() => new()
    {
        SystemAudio = SystemAudio,
        Microphone = Microphone,
        MicrophoneDevice = string.IsNullOrWhiteSpace(MicrophoneDevice) ? null : MicrophoneDevice.Trim(),
        MicrophoneGainDb = RecordGain.Clamp(MicrophoneGainDb),
        SystemAudioGainDb = RecordGain.Clamp(SystemAudioGainDb),
        CameraDevice = CameraDevice < 0 ? -1 : CameraDevice,
        CameraSize = Math.Clamp(CameraSize, MinCameraSize, MaxCameraSize),
        CameraShape = CameraShape,
        CameraOrientation = (RecordCameraOrientation)((int)CameraOrientation & 7),
        CameraOffsetLeft = Math.Max(0, CameraOffsetLeft),
        CameraOffsetBottom = Math.Max(0, CameraOffsetBottom),
        MouseHighlight = MouseHighlight,
        MouseHighlightRadius = Math.Clamp(MouseHighlightRadius, MinHighlightRadius, MaxHighlightRadius),
        ShowCursor = ShowCursor,
        RecordClicks = RecordClicks,
        Keystroke = Keystroke,
        RecordNumberKeys = RecordNumberKeys,
        RecordLetterKeys = RecordLetterKeys,
        RecordControlKeys = RecordControlKeys,
        PreserveShortcuts = PreserveShortcuts,
        RecordScroll = RecordScroll,
        PostProcessing = PostProcessing,
        PostProcessingProgressBar = PostProcessingProgressBar,
        LaserColor = NormalizeColor(LaserColor, "#00000000"),
        ClickColor = NormalizeColor(ClickColor, "#00000000"),
        Codec = Codec,
        TrimAfterRecord = TrimAfterRecord,
        AutoHideToolbar = AutoHideToolbar,
        SeparateAudioTracks = SeparateAudioTracks,
        Clarity = NormalizeClarity(Clarity, animated: false),
        FrameRate = NormalizeFrameRate(FrameRate),
        AnimatedClarity = NormalizeClarity(AnimatedClarity, animated: true),
        AnimatedFrameRate = NormalizeAnimatedFrameRate(AnimatedFrameRate),
        LoopAnimated = LoopAnimated,
        OutputFormat = NormalizeFormat(OutputFormat),
        TrailDurationMs = NormalizeTrailDuration(TrailDurationMs),
        KeyboardSize = NormalizeKeyboardSize(KeyboardSize),
        KeyboardBackgroundColor = NormalizeColor(KeyboardBackgroundColor, "#000000CC"),
        KeyboardForegroundColor = NormalizeColor(KeyboardForegroundColor, "#FFFFFFFF"),
        MouseHighlightColor = NormalizeColor(MouseHighlightColor, "#FFFF0080"),
        ProgressBarColor = NormalizeColor(ProgressBarColor, "#1677FFFF"),
        FollowMouse = FollowMouse,
        FollowMouseSmooth = FollowMouseSmooth,
        FollowMouseReduceJitter = FollowMouseReduceJitter,
        VideoQuality = NormalizeQuality(VideoQuality),
        EncodingPreset = NormalizePreset(EncodingPreset),
        CaptureToolbarInRecording = CaptureToolbarInRecording,
        ShowStatusPrompts = ShowStatusPrompts,
        AutoExitAfterRecording = AutoExitAfterRecording,
        NotifyAfterExport = NotifyAfterExport,
        DeleteFilesOnClose = DeleteFilesOnClose,
        StartDelaySeconds = NormalizeStartDelay(StartDelaySeconds),
        SaveDirectory = (SaveDirectory ?? "").Trim(),
        FilenameFormat = string.IsNullOrWhiteSpace(FilenameFormat)
            ? "SnowShot_Video_{YYYY-MM-DD_HH-mm-ss}"
            : FilenameFormat.Trim(),
    };

    /// <summary>清晰度归一（认不出回默认：视频 <c>1080p</c> / 动图 <c>720p</c>）。</summary>
    public static string NormalizeClarity(string? value, bool animated)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        foreach (string option in animated ? AnimatedClarityOptions : ClarityOptions)
        {
            if (text == option) return option;
        }

        return animated ? "720p" : "1080p";
    }

    /// <summary>成品格式归一（认不出回 <c>mp4</c>）。</summary>
    public static string NormalizeFormat(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        foreach (string option in FormatOptions)
        {
            if (text == option) return option;
        }

        return "mp4";
    }

    /// <summary>速度档归一（认不出回 <c>veryfast</c>）。</summary>
    public static string NormalizePreset(string? value)
    {
        string text = (value ?? "").Trim().ToLowerInvariant();
        foreach (string option in PresetOptions)
        {
            if (text == option) return option;
        }

        return "veryfast";
    }

    public static int NormalizeFrameRate(int fps) =>
        Math.Clamp(fps <= 0 ? DefaultFrameRate : fps, MinFrameRate, MaxFrameRate);

    public static int NormalizeAnimatedFrameRate(int fps) =>
        Math.Clamp(fps <= 0 ? DefaultAnimatedFrameRate : fps, MinAnimatedFrameRate, MaxAnimatedFrameRate);

    /// <summary>拖尾寿命按 100ms 步长夹到 100–2000。</summary>
    public static int NormalizeTrailDuration(int ms)
    {
        int value = ms <= 0 ? DefaultTrailDurationMs : ms;
        int stepped = (int)Math.Round(value / 100.0) * 100;
        return Math.Clamp(stepped, MinTrailDurationMs, MaxTrailDurationMs);
    }

    /// <summary>按键浮层高度按 8px 步长夹到 32–128。</summary>
    public static int NormalizeKeyboardSize(int size)
    {
        int value = size <= 0 ? DefaultKeyboardSize : size;
        int stepped = (int)Math.Round(value / 8.0) * 8;
        return Math.Clamp(stepped, MinKeyboardSize, MaxKeyboardSize);
    }

    public static int NormalizeQuality(int quality) => Math.Clamp(quality, 0, 100);

    public static int NormalizeStartDelay(int seconds) =>
        Math.Clamp(seconds, 0, MaxStartDelaySeconds);

    /// <summary>清晰度档位对应的高度（<c>4k</c> = 2160，<c>2k</c> = 1440，…）。</summary>
    public static int ClarityHeight(string? clarity) => NormalizeClarity(clarity, animated: false) switch
    {
        "4k" => 2160,
        "2k" => 1440,
        "720p" => 720,
        "480p" => 480,
        _ => 1080,
    };

    /// <summary>动图清晰度档位对应的高度。</summary>
    public static int AnimatedClarityHeight(string? clarity) =>
        NormalizeClarity(clarity, animated: true) switch
        {
            "1080p" => 1080,
            "480p" => 480,
            _ => 720,
        };

    /// <summary>
    /// 质量（0–100）换成 crf / cq / qp：100 → 16（最好），0 → 51（最差），80 → 23。
    /// 各家编码器都认这个数（crf / cq / global_quality / qp 的刻度基本一致）。
    /// </summary>
    public static int CrfOf(int quality) => 51 - (int)Math.Round(NormalizeQuality(quality) * 0.35);

    /// <summary>ffmpeg 的缩放滤镜：只往下缩，小画面不放大（<c>min(ih, 高度)</c>）。</summary>
    public static string ScaleFilter(int height) =>
        $"scale=-2:'min(ih,{Math.Max(2, height)})'";

    /// <summary>
    /// 录屏文件名：按上游 <c>video_filename_format</c> 展开——**每一对花括号里是一段日期格式**
    /// （<c>{YYYY-MM-DD_HH-mm-ss}</c> → <c>2026-03-04_05-06-07</c>），照
    /// <c>storage/timevariables.cpp</c> 的 <c>expandTimeVariables</c>：只把 Qt 的
    /// <c>YYYY</c> / <c>YY</c> / <c>DD</c> 换成 .NET 的 <c>yyyy</c> / <c>yy</c> / <c>dd</c>，
    /// 其余（<c>MM</c> / <c>HH</c> / <c>mm</c> / <c>ss</c>）两边写法一致。
    /// 展开完把文件名里不能出现的字符换成下划线。
    /// </summary>
    public static string FileNameFor(string? template, DateTime when)
    {
        string text = string.IsNullOrWhiteSpace(template)
            ? "SnowShot_Video_{YYYY-MM-DD_HH-mm-ss}"
            : template.Trim();

        string name = TimeVariablePattern.Replace(text, match => ExpandTime(match.Groups[1].Value, when));

        foreach (char bad in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');

        name = name.Trim();
        return name.Length == 0
            ? "SnowShot_Video_" + when.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)
            : name;
    }

    private static readonly System.Text.RegularExpressions.Regex TimeVariablePattern =
        new(@"\{([^{}]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string ExpandTime(string pattern, DateTime when)
    {
        string format = pattern
            .Replace("YYYY", "yyyy")
            .Replace("YY", "yy")
            .Replace("DD", "dd");

        try
        {
            return when.ToString(format, CultureInfo.InvariantCulture);
        }
        catch (FormatException)
        {
            // 用户写了 .NET 不认的记号：原样留着，别让「另存录屏」弹异常
            return "{" + pattern + "}";
        }
    }

    /// <summary>颜色里的 alpha（认不出按 0 = 透明）。</summary>
    public static int AlphaOf(string? value)
    {
        string text = NormalizeColor(value, "");
        return text.Length == 9 ? Convert.ToInt32(text.Substring(1, 2), 16) : 0;
    }

    /// <summary>颜色写法收成 <c>#AARRGGBB</c>（认不出就用兜底；色卡与选项窗口都写这个形状）。</summary>
    public static string NormalizeColor(string? value, string fallback)    {
        string text = (value ?? "").Trim();
        if (text.Length == 0) return fallback;
        if (!text.StartsWith('#')) text = "#" + text;
        if (text.Length == 7) text = "#FF" + text[1..];
        if (text.Length != 9) return fallback;

        for (int i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i])) return fallback;

        return text.ToUpperInvariant();
    }

    /// <summary>控制条 / 日志 / 提示用的一句话摘要（没有附加能力时返回空串）。</summary>
    public string Summary()
    {
        var parts = new List<string>();

        if (SystemAudio && Microphone) parts.Add("系统声音 + 麦克风");
        else if (SystemAudio) parts.Add("系统声音");
        else if (Microphone) parts.Add("麦克风");

        if (HasCamera) parts.Add($"摄像头 {CameraDevice}");

        var effects = new List<string>();
        if (MouseHighlight) effects.Add("光标高亮");
        if (HasClickRipple) effects.Add("点击涟漪");
        if (HasLaser) effects.Add("激光拖尾");
        if (Keystroke) effects.Add("按键浮层");
        if (RecordClicks) effects.Add("鼠标点击");
        if (RecordScroll) effects.Add("滚动");
        if (PostProcessing) effects.Add(PostProcessingProgressBar ? "进度条" : "播放时间");
        if (effects.Count > 0) parts.Add(string.Join(" / ", effects));

        if (Codec != RecordCodec.auto)
            parts.Add(Codec == RecordCodec.hevc ? "HEVC" : "H.264");

        if (TrimAfterRecord) parts.Add("录完裁剪");

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 从内置功能参数拼一份（<c>common.record</c>）：只覆盖在 <paramref name="baseOptions"/> 上提到的项，
    /// 没写的项跟随设置里的默认值（参数里 <c>""</c> / 认不出的值都当「没提到」）。
    /// </summary>
    public static RecordOptions Apply(RecordOptions baseOptions,
        string? audio = null, string? camera = null, string? effects = null,
        string? codec = null, string? trim = null, int cameraDevice = -1)
    {
        var options = (baseOptions ?? new RecordOptions()).Clone();

        switch (audio?.Trim().ToLowerInvariant())
        {
            case "off":
                options.SystemAudio = false;
                options.Microphone = false;
                break;
            case "system":
                options.SystemAudio = true;
                options.Microphone = false;
                break;
            case "mic":
                options.SystemAudio = false;
                options.Microphone = true;
                break;
            case "both":
                options.SystemAudio = true;
                options.Microphone = true;
                break;
        }

        switch (camera?.Trim().ToLowerInvariant())
        {
            case "off":
                options.CameraDevice = -1;
                break;
            case "on":
                if (cameraDevice >= 0) options.CameraDevice = cameraDevice;
                else if (options.CameraDevice < 0) options.CameraDevice = 0;
                break;
        }

        switch (effects?.Trim().ToLowerInvariant())
        {
            case "off":
                options.ShowCursor = false;
                options.MouseHighlight = false;
                options.RecordClicks = false;
                options.RecordScroll = false;
                options.Keystroke = false;
                options.PostProcessing = false;
                options.LaserColor = "#00000000";
                options.ClickColor = "#00000000";
                break;
            case "on":
                options.ShowCursor = true;
                options.MouseHighlight = true;
                options.RecordClicks = true;
                options.Keystroke = true;
                options.LaserColor = "#FFFF4040";
                options.ClickColor = "#80F5222D";
                break;
        }

        switch (codec?.Trim().ToLowerInvariant())
        {
            case "h264":
                options.Codec = RecordCodec.h264;
                break;
            case "hevc":
            case "h265":
                options.Codec = RecordCodec.hevc;
                break;
            case "auto":
                options.Codec = RecordCodec.auto;
                break;
        }

        switch (trim?.Trim().ToLowerInvariant())
        {
            case "off":
                options.TrimAfterRecord = false;
                break;
            case "on":
                options.TrimAfterRecord = true;
                break;
        }

        return options.Normalized();
    }

    /// <summary>画中画左上角的落点（输出像素）：先贴左 / 下，放不下就收进来（上游 <c>placement</c>）。</summary>
    public static (int X, int Y, int Side) CameraPlacement(
        int width, int height, int size, int offsetLeft, int offsetBottom)
    {
        int side = Math.Max(1, Math.Min(size, Math.Min(width, height)));
        int x = Math.Max(0, Math.Min(offsetLeft, width - side));
        int bottom = Math.Max(0, Math.Min(offsetBottom, height - side));
        int y = Math.Max(0, height - bottom - side);
        return (x, y, side);
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture,
            $"sound={SystemAudio}/{Microphone} cam={CameraDevice} fx={HasEffects} codec={Codec}");
}
