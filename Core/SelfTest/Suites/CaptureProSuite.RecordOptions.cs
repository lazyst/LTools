using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LTools.Features;
using LTools.Features.CapturePro;
using LTools.Views.CapturePro;

namespace LTools.Core.SelfTest;

/// <summary>CaptureProSuite 的「录屏：选项 / 覆盖层规则」部分。</summary>
internal static partial class CaptureProSuite
{
    // ============ 录屏：选项 / 编码器 ============

    /// <summary>
    /// 录屏选项：夹取、从内置功能参数覆盖、摘要文案；编码器挑选 / 回退 / 参数表；
    /// 收尾混音参数（无音轨 / 单轨 / 双轨）。
    /// </summary>
    private static void RecordOptionsRules()
    {
        // ---- 夹取 ----
        var clamped = new RecordOptions
        {
            CameraSize = 5000,
            CameraOffsetLeft = -30,
            MouseHighlightRadius = 1,
            MicrophoneDevice = "  ",
        }.Normalized();

        Check.Equal(RecordOptions.MaxCameraSize, clamped.CameraSize, "画中画边长夹到上限");
        Check.Equal(0, clamped.CameraOffsetLeft, "负边距收成 0");
        Check.Equal(RecordOptions.MinHighlightRadius, clamped.MouseHighlightRadius, "高亮半径夹到下限");
        Check.True(clamped.MicrophoneDevice == null, "全空白的设备名当没填");
        Check.Equal(RecordOptions.MinCameraSize,
            new RecordOptions { CameraSize = 1 }.Normalized().CameraSize, "画中画边长夹到下限");

        // ---- 声音增益：夹取 / 文案 / 倍数 / 峰值换算（照上游 −24…+24 dB）----
        Check.Equal(-24, RecordGain.Clamp(-99), "增益夹到下限");
        Check.Equal(24, RecordGain.Clamp(99), "增益夹到上限");
        Check.Equal(0, RecordGain.Clamp(0), "0 dB 原样");
        Check.Equal("0 dB", RecordGain.Format(0), "0 dB 文案");
        Check.Equal("+6 dB", RecordGain.Format(6), "正增益带加号");
        Check.Equal("-6 dB", RecordGain.Format(-6), "负增益带减号");
        Check.Equal("+24 dB", RecordGain.Format(99), "文案也跟着夹取");
        Check.True(Math.Abs(RecordGain.Factor(0) - 1.0) < 1e-9, "0 dB = 1 倍");
        Check.True(Math.Abs(RecordGain.Factor(6) - 1.99526) < 0.0005, "+6 dB ≈ 2 倍");
        Check.True(Math.Abs(RecordGain.Factor(-6) - 0.50119) < 0.0005, "-6 dB ≈ 0.5 倍");
        Check.Equal("6dB", RecordGain.FilterValue(6), "ffmpeg volume 写法（正数不带加号）");
        Check.Equal("-6dB", RecordGain.FilterValue(-6), "ffmpeg volume 写法（负数）");

        var pcm16 = new byte[] { 0xFF, 0x7F, 0x00, 0x00, 0x00, 0xC0 };
        Check.True(Math.Abs(RecordGain.PeakOf(pcm16, pcm16.Length, 16, false) - 1f) < 0.001f,
            "16 位整数峰值（含负半周绝对值）");

        var floats = new byte[8];
        BitConverter.GetBytes(0.25f).CopyTo(floats, 0);
        BitConverter.GetBytes(-0.75f).CopyTo(floats, 4);
        Check.True(Math.Abs(RecordGain.PeakOf(floats, floats.Length, 32, true) - 0.75f) < 1e-6,
            "32 位浮点峰值（环回常见格式）");

        var pcm32 = new byte[4];
        BitConverter.GetBytes(int.MinValue).CopyTo(pcm32, 0);
        Check.True(Math.Abs(RecordGain.PeakOf(pcm32, pcm32.Length, 32, false) - 1f) < 1e-6,
            "32 位整数峰值");

        var gained = RecordGain.Process(0.5f, 6);
        Check.True(Math.Abs(gained.Level - 0.99763) < 0.001 && !gained.Clipping,
            "+6 dB 把 0.5 推到近满刻度，还没削波");
        var loud = RecordGain.Process(0.9f, 6);
        Check.True(loud.Clipping && loud.Level == 1.0, "过载时电平封顶 1 并标削波");
        var silent = RecordGain.Process(0f, 12);
        Check.Equal(0d, silent.Level, "没有声音时电平为 0");
        Check.False(silent.Clipping, "静音不标削波");

        var gainClamped = new RecordOptions
        {
            MicrophoneGainDb = -99,
            SystemAudioGainDb = 99,
        }.Normalized();
        Check.Equal(-24, gainClamped.MicrophoneGainDb, "麦克风增益夹到下限");
        Check.Equal(24, gainClamped.SystemAudioGainDb, "系统声音增益夹到上限");

        var carried = new RecordOptions();
        carried.CopyFrom(new RecordOptions { MicrophoneGainDb = 5, SystemAudioGainDb = -7 });
        Check.Equal(5, carried.MicrophoneGainDb, "CopyFrom 带麦克风增益");
        Check.Equal(-7, carried.SystemAudioGainDb, "CopyFrom 带系统声音增益");

        // ---- 从参数覆盖：没写 / 认不出的项跟随设置 ----
        var baseOptions = new RecordOptions
        {
            SystemAudio = false,
            Microphone = false,
            CameraDevice = -1,
            ShowCursor = false,
            MouseHighlight = false,
            RecordClicks = false,
            RecordScroll = false,
            Keystroke = false,
            LaserColor = "#00000000",
            ClickColor = "#00000000",
            Codec = RecordCodec.h264,
            TrimAfterRecord = false,
        };

        var both = RecordOptions.Apply(baseOptions, audio: "both");
        Check.True(both.SystemAudio && both.Microphone, "audio=both 两路都开");

        var mic = RecordOptions.Apply(baseOptions, audio: "MIC");
        Check.True(!mic.SystemAudio && mic.Microphone, "audio 值不分大小写");

        var untouched = RecordOptions.Apply(baseOptions, audio: "", camera: "???", codec: "???");
        Check.True(!untouched.SystemAudio && !untouched.Microphone, "空值 / 认不出的值不动原选项");
        Check.Equal(RecordCodec.h264, untouched.Codec, "认不出的编码保持原值");

        var cameraOn = RecordOptions.Apply(baseOptions, camera: "on", cameraDevice: 2);
        Check.Equal(2, cameraOn.CameraDevice, "camera=on 用点名设备号");

        var cameraDefault = RecordOptions.Apply(baseOptions, camera: "on");
        Check.Equal(0, cameraDefault.CameraDevice, "没点名就用 0 号摄像头");

        var cameraOff = RecordOptions.Apply(new RecordOptions { CameraDevice = 1 }, camera: "off");
        Check.Equal(-1, cameraOff.CameraDevice, "camera=off 关掉画中画");

        var fxOn = RecordOptions.Apply(baseOptions, effects: "on");
        Check.True(fxOn.HasEffects, "effects=on 打开特效");
        Check.True(fxOn.ShowCursor && fxOn.MouseHighlight && fxOn.RecordClicks && fxOn.Keystroke,
            "光标 / 高亮 / 点击 / 按键逐个都开");
        Check.True(fxOn.HasLaser && fxOn.HasClickRipple, "effects=on 同时给激光笔与点击上色");

        var fxOff = RecordOptions.Apply(new RecordOptions
        {
            ShowCursor = true,
            MouseHighlight = true,
            RecordClicks = true,
            RecordScroll = true,
            Keystroke = true,
            PostProcessing = true,
            LaserColor = "#FFFF4040",
            ClickColor = "#80F5222D",
        }, effects: "off");
        Check.False(fxOff.HasEffects, "effects=off 全部关闭");
        Check.False(fxOff.ShowCursor, "effects=off 连光标覆盖也关");

        Check.Equal(RecordCodec.hevc, RecordOptions.Apply(baseOptions, codec: "h265").Codec,
            "h265 也认作 HEVC");
        Check.Equal(RecordCodec.auto, RecordOptions.Apply(baseOptions, codec: "auto").Codec, "auto");
        Check.True(RecordOptions.Apply(baseOptions, trim: "on").TrimAfterRecord, "trim=on");
        Check.False(RecordOptions.Apply(new RecordOptions { TrimAfterRecord = true }, trim: "off")
            .TrimAfterRecord, "trim=off");

        // ---- 摘要 ----
        Check.Equal("", new RecordOptions { SystemAudio = false }.Summary(), "纯画面没有摘要");
        Check.Equal("系统声音", new RecordOptions().Summary(), "默认只录系统声音（Snow Shot 默认开）");
        Check.Equal("系统声音 + 麦克风", new RecordOptions { SystemAudio = true, Microphone = true }.Summary(),
            "两路声音文案");
        Check.Equal("麦克风", new RecordOptions { SystemAudio = false, Microphone = true }.Summary(), "单路声音文案");
        Check.Equal("HEVC", new RecordOptions { SystemAudio = false, Codec = RecordCodec.hevc }.Summary(), "编码进摘要");
        Check.Equal("录完裁剪", new RecordOptions { SystemAudio = false, TrimAfterRecord = true }.Summary(), "裁剪进摘要");

        var full = new RecordOptions
        {
            SystemAudio = true,
            CameraDevice = 0,
            MouseHighlight = true,
            ClickColor = "#80F5222D",
            Codec = RecordCodec.hevc,
        }.Summary();
        Check.True(full.Contains("系统声音") && full.Contains("摄像头 0") && full.Contains("光标高亮")
            && full.Contains("点击涟漪") && full.Contains("HEVC"), "全开时摘要逐项都在：" + full);
        Check.True(!full.Contains("麦克风") && !full.Contains("拖尾"), "没开的项不进摘要：" + full);
        Check.True(!new RecordOptions().IsPlain, "默认带系统声音，不是纯画面");
        Check.True(new RecordOptions { SystemAudio = false }.IsPlain, "两路都关是纯画面");
        Check.False(new RecordOptions { Keystroke = true }.IsPlain, "有特效就不是纯画面");

        // ---- 编码器挑选（纯函数：硬件清单注入）----
        var nvenc = new[] { "h264_nvenc", "hevc_nvenc" };

        Check.Equal("h264_nvenc", RecordEncoders.Pick(RecordCodec.auto, nvenc), "auto 优先 H.264 硬件");
        Check.Equal("hevc_nvenc", RecordEncoders.Pick(RecordCodec.hevc, nvenc), "指定 HEVC 就用硬件 HEVC");
        Check.Equal("h264_nvenc", RecordEncoders.Pick(RecordCodec.h264, nvenc), "指定 H.264 就用硬件 H.264");
        Check.Equal(RecordEncoders.X264, RecordEncoders.Pick(RecordCodec.auto, Array.Empty<string>()),
            "没有硬件退回 libx264");
        Check.Equal(RecordEncoders.X265, RecordEncoders.Pick(RecordCodec.hevc, Array.Empty<string>()),
            "没有硬件退回 libx265");
        Check.Equal("hevc_nvenc", RecordEncoders.Pick(RecordCodec.auto, new[] { "hevc_nvenc" }),
            "只有 HEVC 硬件时 auto 也用它（别平白掉回软件）");
        Check.Equal("h264_amf", RecordEncoders.Pick(RecordCodec.h264, new[] { "h264_qsv", "h264_amf" }),
            "同代硬件按 NVENC → AMF → QSV 的优先顺序");

        // 回退：硬件开不起来 → 同格式的软件编码器
        Check.Equal(RecordEncoders.X264, RecordEncoders.Fallback("h264_nvenc"), "NVENC H.264 退回 libx264");
        Check.Equal(RecordEncoders.X265, RecordEncoders.Fallback("hevc_qsv"), "QSV HEVC 退回 libx265");
        Check.Equal(RecordEncoders.X264, RecordEncoders.Fallback("h264_amf"), "AMF H.264 退回 libx264");

        Check.True(RecordEncoders.IsHardware("hevc_nvenc"), "硬件编码器判定");
        Check.False(RecordEncoders.IsHardware(RecordEncoders.X264), "libx264 不算硬件");
        Check.False(RecordEncoders.IsHardware("made_up"), "认不出的不算硬件");
        Check.True(RecordEncoders.IsHevc(RecordEncoders.X265), "libx265 是 HEVC");
        Check.True(RecordEncoders.IsHevc("hevc_amf"), "AMF HEVC 是 HEVC");
        Check.False(RecordEncoders.IsHevc("h264_nvenc"), "H.264 不是 HEVC");

        // 参数表：编解码器名必须和「用哪个编码器」一致
        foreach (string encoder in new[]
                 {
                     RecordEncoders.X264, RecordEncoders.X265, "h264_nvenc", "hevc_nvenc",
                     "h264_amf", "hevc_amf", "h264_qsv", "hevc_qsv",
                 })
        {
            var args = RecordEncoders.Arguments(encoder);
            Check.True(args.Count >= 2, encoder + " 至少有 -c:v 和编码器名");
            Check.Equal("-c:v", args[0], encoder + " 第一个参数是 -c:v");
            Check.Equal(encoder, args[1], encoder + " 参数表用的是它自己");
        }

        Check.True(RecordEncoders.Arguments(RecordEncoders.X265).Contains("hvc1"),
            "HEVC 打 hvc1 标签（QuickTime / 浏览器才认）");
        Check.True(RecordEncoders.Arguments("hevc_nvenc").Contains("hvc1"), "硬件 HEVC 也打 hvc1");
        Check.False(RecordEncoders.Arguments(RecordEncoders.X264).Contains("hvc1"),
            "H.264 不打 hvc1");

        Check.Equal("H.264", RecordEncoders.Label(RecordEncoders.X264), "软件 H.264 文案");
        Check.Equal("HEVC · NVENC", RecordEncoders.Label("hevc_nvenc"), "硬件文案带厂商");

        // ---- 收尾混音 ----
        var mute = RecordPlan.FinalizeArgs("in.mp4", "out.mp4");
        string muteLine = string.Join(" ", mute);
        Check.True(muteLine.Contains("-c copy"), "没有音轨就是纯容器搬运");
        Check.True(muteLine.Contains("+faststart"), "收尾转 faststart");
        Check.False(muteLine.Contains("-filter_complex"), "没有音轨不用混流");
        Check.Equal("out.mp4", mute[^1], "输出在最后一个参数");

        var single = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "a.wav" });
        string singleLine = string.Join(" ", single);
        Check.True(singleLine.Contains("-i in.mp4") && singleLine.Contains("-i a.wav"), "原片 + 一条音轨");
        Check.True(singleLine.Contains("-map 0:v") && singleLine.Contains("-map 1:a"), "画面取原片、声音取音轨");
        Check.True(singleLine.Contains("-c:v copy"), "画面不重编码");
        Check.True(singleLine.Contains("-c:a aac") && singleLine.Contains("-b:a 192k"), "声音编成 AAC 192k");
        Check.True(singleLine.Contains("-ar 48000") && singleLine.Contains("-ac 2"), "统一成 48kHz 立体声");
        Check.True(singleLine.Contains("-shortest"), "以画面长度收口");
        Check.False(singleLine.Contains("amix"), "单音轨不用混音");

        var dual = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "sys.wav", "mic.wav" });
        string dualLine = string.Join(" ", dual);
        Check.True(dualLine.Contains("amix=inputs=2"), "两路用 amix 混");
        Check.True(dualLine.Contains("aresample=48000"), "混前先统一采样率（amix 不做格式统一）");
        Check.True(dualLine.Contains("channel_layouts=stereo"), "混前统一声道");
        Check.True(dualLine.Contains("-map [aout]"), "混音结果映射成音轨");
        Check.Equal("out.mp4", dual[^1], "输出在最后一个参数");

        Check.Equal(string.Join(" ", mute), string.Join(" ", RecordPlan.RemuxArgs("in.mp4", "out.mp4")),
            "RemuxArgs 等价于「没有音轨的收尾」");

        // ---- 按轨增益（面板两个增益弹层）----
        var singleGain = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "mic.wav" }, new[] { 6 });
        string singleGainLine = string.Join(" ", singleGain);
        Check.True(singleGainLine.Contains("-af volume=6dB"), "单轨用输出侧 -af 套增益");
        Check.True(singleGainLine.Contains("-c:v copy"), "套增益不影响画面直拷");

        var singleZero = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "mic.wav" }, new[] { 0 });
        Check.False(string.Join(" ", singleZero).Contains("volume="), "0 dB 不挂滤镜");

        var dualGain = RecordPlan.FinalizeArgs("in.mp4", "out.mp4",
            new[] { "sys.wav", "mic.wav" }, new[] { -6, 6 });
        string dualGainLine = string.Join(" ", dualGain);
        Check.True(dualGainLine.Contains("volume=-6dB[a0]"), "第一路增益套在它自己的重采样之后");
        Check.True(dualGainLine.Contains("volume=6dB[a1]"), "第二路增益各调各的（混音前）");
        Check.Equal(2, dualGainLine.Split("volume=").Length - 1, "每一路各挂一次增益");

        var overGain = RecordPlan.FinalizeArgs("in.mp4", "out.mp4", new[] { "a.wav" }, new[] { 99 });
        Check.True(string.Join(" ", overGain).Contains("volume=24dB"), "增益超出范围也按 ±24 收口");
    }

}
