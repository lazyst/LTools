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

    /// <summary>录系统声音（WASAPI 环回，不需要「立体声混音」设备）。默认开：Snow Shot 的 `enable_system_audio` 默认 true。</summary>
    public bool SystemAudio { get; set; } = true;

    /// <summary>录麦克风。</summary>
    public bool Microphone { get; set; }

    /// <summary>麦克风设备名；空 = 系统默认输入设备。</summary>
    public string? MicrophoneDevice { get; set; }

    /// <summary>摄像头序号（OpenCV 的设备号）；负数 = 不录。</summary>
    public int CameraDevice { get; set; } = -1;

    /// <summary>画中画边长（物理像素，夹到 50–500）。</summary>
    public int CameraSize { get; set; } = DefaultCameraSize;

    public RecordCameraShape CameraShape { get; set; } = RecordCameraShape.square;

    /// <summary>镜像（自拍视角）：多数摄像头默认就该是镜像的。</summary>
    public bool CameraMirrored { get; set; } = true;

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
        CameraDevice = other.CameraDevice;
        CameraSize = other.CameraSize;
        CameraShape = other.CameraShape;
        CameraMirrored = other.CameraMirrored;
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
    }

    /// <summary>夹紧数值字段（尺寸 / 半径 / 边距），并把负边距收成 0。</summary>
    public RecordOptions Normalized() => new()
    {
        SystemAudio = SystemAudio,
        Microphone = Microphone,
        MicrophoneDevice = string.IsNullOrWhiteSpace(MicrophoneDevice) ? null : MicrophoneDevice.Trim(),
        CameraDevice = CameraDevice < 0 ? -1 : CameraDevice,
        CameraSize = Math.Clamp(CameraSize, MinCameraSize, MaxCameraSize),
        CameraShape = CameraShape,
        CameraMirrored = CameraMirrored,
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
    };

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
