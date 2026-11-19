using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>录什么：框选一块 / 光标所在主屏 / 全部显示器 / 前台窗口。</summary>
internal enum RecordMode
{
    Region,
    Primary,
    All,
    Window,
}

/// <summary>
/// 录成什么（照 Snow Shot 的四个格式）：MP4（H.264）、GIF（自动调色板）、APNG、WebP 动图。
/// </summary>
internal enum RecordFormat
{
    Mp4,
    Gif,
    Apng,
    Webp,
}

/// <summary>
/// 一次录屏的参数：录哪儿、帧率、倒计时、格式，以及据此拼出来的 ffmpeg 命令行。
/// </summary>
/// <remarks>
/// <para>
/// 全是**纯数据 + 纯函数**：帧率 / 倒计时夹紧、GIF 的帧率与宽度上限、MP4 的 raw 管道参数、
/// GIF 的两遍（调色板 + 编码）参数、时长文案都在这里，<c>--selftest</c> 直接断言。
/// </para>
/// <para>
/// 抓帧与进程管理在 <see cref="RecordEngine"/>，窗口与保存流程在 <see cref="RecordSession"/>。
/// </para>
/// </remarks>
internal sealed class RecordPlan
{
    public const int DefaultFps = 30;
    public const int MinFps = 5;
    public const int MaxFps = 60;
    public const int DefaultCountdown = 3;
    public const int MaxCountdown = 10;

    /// <summary>GIF 的帧率上限（再高文件大得离谱、看起来也没区别）。</summary>
    public const int GifMaxFps = 15;

    /// <summary>GIF 的宽度上限（超过就等比缩下来）。</summary>
    public const int GifMaxWidth = 960;

    // 这几个是「面板上还能改」的：录屏工具条的第二行直接写回 plan（`{ get; set; }`）。
    public RecordMode Mode { get; set; } = RecordMode.Region;

    public RecordFormat Format { get; set; } = RecordFormat.Mp4;

    public int Fps { get; set; } = DefaultFps;

    public int Countdown { get; set; } = DefaultCountdown;

    /// <summary>声音 / 摄像头 / 特效 / 编码器 / 录完裁剪这些开关（见 <see cref="RecordOptions"/>）。</summary>
    public RecordOptions Options { get; set; } = new();

    /// <summary>跟随鼠标：选区只决定成品尺寸，画面跟着光标在显示器里平移。</summary>
    public bool FollowMouse { get; set; }

    /// <summary>跟随鼠标的两个独立开关（平滑移动 / 抑制抖动）。</summary>
    public RecordFollowOptions FollowOptions { get; set; } = RecordFollowOptions.Default;

    /// <summary>
    /// 先把控制面板摆出来、等用户点「录制」再开始（照 Snow Shot 的录屏入口）。
    /// 内置功能 <c>common.record</c> 与端到端 harness 用默认 <c>false</c>：倒计时完直接开录，
    /// 不需要有人去点按钮。
    /// </summary>
    public bool PromptStart { get; init; }

    public bool IsGif => Format == RecordFormat.Gif;

    /// <summary>动图格式（GIF / APNG / WebP）：不带音轨，帧率压到 <see cref="GifMaxFps"/>。</summary>
    public bool IsAnimated => Format != RecordFormat.Mp4;

    /// <summary>实际录制帧率（夹到 5–60）。</summary>
    public int EffectiveFps => NormalizeFps(Fps);

    /// <summary>动图转码时用的帧率。</summary>
    public int GifFps => Math.Min(EffectiveFps, GifMaxFps);

    /// <summary>成品扩展名（不含点）。</summary>
    public string Extension => Format switch
    {
        RecordFormat.Gif => "gif",
        RecordFormat.Apng => "apng",
        RecordFormat.Webp => "webp",
        _ => "mp4",
    };

    public static RecordPlan FromValues(string? mode, string? format, int fps, int countdown,
        bool followMouse = false, RecordOptions? options = null) => new()
    {
        Mode = ParseMode(mode),
        Format = ParseFormat(format),
        Fps = NormalizeFps(fps),
        Countdown = NormalizeCountdown(countdown),
        FollowMouse = followMouse,
        Options = (options ?? new RecordOptions()).Normalized(),
    };

    public static RecordMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "primary" => RecordMode.Primary,
        "all" => RecordMode.All,
        "window" => RecordMode.Window,
        _ => RecordMode.Region,
    };

    public static RecordFormat ParseFormat(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "gif" => RecordFormat.Gif,
        "apng" => RecordFormat.Apng,
        "webp" => RecordFormat.Webp,
        _ => RecordFormat.Mp4,
    };

    public static int NormalizeFps(int fps) =>
        Math.Clamp(fps <= 0 ? DefaultFps : fps, MinFps, MaxFps);

    public static int NormalizeCountdown(int seconds) =>
        Math.Clamp(seconds, 0, MaxCountdown);

    public static string ModeLabel(RecordMode mode) => mode switch
    {
        RecordMode.Primary => "主屏",
        RecordMode.All => "全部屏幕",
        RecordMode.Window => "前台窗口",
        _ => "选区",
    };

    public static string FormatLabel(string extension) =>
        extension?.Trim().ToLowerInvariant() switch
        {
            "gif" => "GIF",
            "apng" => "APNG",
            "webp" => "WebP",
            _ => "MP4",
        };

    /// <summary>格式枚举 → 下拉里显示的名字（与 <see cref="FormatLabel(string)"/> 同一套）。</summary>
    public static string FormatName(RecordFormat format) => format switch
    {
        RecordFormat.Gif => "GIF",
        RecordFormat.Apng => "APNG",
        RecordFormat.Webp => "WebP",
        _ => "MP4",
    };

    /// <summary>下拉选项顺序（照 Snow Shot：MP4 / GIF / APNG / WebP）。</summary>
    public static readonly IReadOnlyList<RecordFormat> Formats = new[]
    {
        RecordFormat.Mp4, RecordFormat.Gif, RecordFormat.Apng, RecordFormat.Webp,
    };

    /// <summary>面板时长：始终 <c>HH:MM:SS</c>（上游 `setRecordingDuration`）。</summary>
    public static string FormatDuration(int totalMs)
    {
        int seconds = Math.Max(0, totalMs) / 1000;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}",
            seconds / 3600, seconds % 3600 / 60, seconds % 60);
    }

    /// <summary>时长文案：不到一小时 <c>mm:ss</c>，超过写成 <c>h:mm:ss</c>。</summary>
    public static string FormatClock(int totalMs)
    {
        int seconds = Math.Max(0, totalMs) / 1000;
        int hours = seconds / 3600;
        int minutes = seconds % 3600 / 60;
        int secs = seconds % 60;

        return hours > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", hours, minutes, secs)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes, secs);
    }

    /// <summary>
    /// 边录边编码的 MP4：原始 BGRA 帧从 <c>pipe:0</c> 灌进去，不落中间文件。
    /// <c>pad</c> 保证宽高是偶数（H.264 / HEVC 的 yuv420p 要求），否则奇数尺寸的选区会直接编码失败。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 录的过程写**分片 MP4**（<c>frag_keyframe+empty_moov</c>）：进程被杀 / 断电时
    /// 已经写下的那些分片仍然能播，启动时由 <see cref="RecordRecovery"/> 找回。
    /// 正常收尾后再 <see cref="FinalizeArgs"/> 转成普通 faststart MP4（分享 / 上传更省事）。
    /// </para>
    /// <para>
    /// <paramref name="encoder"/> 来自 <see cref="RecordEncoders.Pick"/>（硬件优先，软件兜底）；
    /// 编码器参数表也只有 <see cref="RecordEncoders.Arguments"/> 一份。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Mp4Args(Int32Rect size, int fps, string output, string encoder)
    {
        int width = Math.Max(2, size.Width);
        int height = Math.Max(2, size.Height);
        int rate = NormalizeFps(fps);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{width}x{height}", "-framerate", rate.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-an",
        };

        args.AddRange(RecordEncoders.Arguments(encoder));
        args.AddRange(new[]
        {
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-pix_fmt", "yuv420p", "-movflags", "+frag_keyframe+empty_moov",
            output,
        });

        return args;
    }

    /// <summary>
    /// 收尾：把分片 MP4（+ 若干 WAV 音轨）重新封装成普通 faststart MP4。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 画面**不重编码**（<c>-c:v copy</c>）；声音按 AAC 192k 编一次。
    /// 两路（系统环回 + 麦克风）先各自重采样到 48kHz 立体声再 <c>amix</c>，
    /// 因为设备给的原生格式各不相同（环回常是 48k 立体声，麦克风可能是 16k 单声道），
    /// <c>amix</c> 本身不做格式统一。
    /// </para>
    /// <para>
    /// 增益（−24…+24 dB）按轨套 ffmpeg <c>volume</c>：单轨用输出侧 <c>-af</c>，
    /// 双轨在各路重采样之后套（混音前），0 dB 不挂滤镜。带增益的重载是为了面板上的
    /// 增益弹层——上游 <c>RecordingAudioGainPopover</c> 让两路各调各的。
    /// </para>
    /// <para>
    /// <c>-shortest</c> 收口：音轨比画面长（收尾差几毫秒）时以画面为准。
    /// 没有音轨时就是纯容器搬运，和以前的 <see cref="RemuxArgs"/> 完全一致。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> FinalizeArgs(string input, string output,
        IReadOnlyList<string>? audioTracks) =>
        FinalizeArgs(input, output, audioTracks, null);

    /// <summary>收尾（不带音轨）：等价于传空音轨。</summary>
    public static IReadOnlyList<string> FinalizeArgs(string input, string output) =>
        FinalizeArgs(input, output, null, null);

    /// <summary>收尾 + 按轨增益（<paramref name="gainDb"/> 与音轨一一对应，缺的当 0 dB）。</summary>
    public static IReadOnlyList<string> FinalizeArgs(string input, string output,
        IReadOnlyList<string>? audioTracks, IReadOnlyList<int>? gainDb)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-i", input,
        };

        int GainOf(int index) =>
            gainDb != null && index >= 0 && index < gainDb.Count ? RecordGain.Clamp(gainDb[index]) : 0;

        int tracks = audioTracks?.Count ?? 0;
        if (tracks == 0)
        {
            args.AddRange(new[] { "-c", "copy", "-movflags", "+faststart", output });
            return args;
        }

        foreach (string audio in audioTracks!) args.AddRange(new[] { "-i", audio });

        if (tracks == 1)
        {
            args.AddRange(new[] { "-map", "0:v", "-map", "1:a", "-c:v", "copy" });

            int gain = GainOf(0);
            if (gain != 0) args.AddRange(new[] { "-af", "volume=" + RecordGain.FilterValue(gain) });

            args.AddRange(new[]
            {
                "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2",
                "-shortest", "-movflags", "+faststart",
                output,
            });
            return args;
        }

        // 多路：先统一到 48kHz 立体声浮点，再混（amix 不做格式统一）；增益在混音前按轨套
        var filters = new List<string>();
        var labels = new List<string>();
        for (int i = 0; i < tracks; i++)
        {
            int gain = GainOf(i);
            string volume = gain == 0 ? "" : ",volume=" + RecordGain.FilterValue(gain);
            filters.Add($"[{i + 1}:a]aresample=48000,aformat=sample_fmts=fltp:channel_layouts=stereo{volume}[a{i}]");
            labels.Add($"[a{i}]");
        }
        filters.Add($"{string.Concat(labels)}amix=inputs={tracks}:duration=longest:normalize=0[aout]");

        args.AddRange(new[]
        {
            "-filter_complex", string.Join(";", filters),
            "-map", "0:v", "-map", "[aout]",
            "-c:v", "copy",
            "-c:a", "aac", "-b:a", "192k",
            "-shortest", "-movflags", "+faststart",
            output,
        });
        return args;
    }

    /// <summary>没有音轨时的收尾（等价于 <see cref="FinalizeArgs(string, string)"/>）。</summary>
    public static IReadOnlyList<string> RemuxArgs(string input, string output) =>
        FinalizeArgs(input, output);

    /// <summary>GIF 第一遍：从录好的 mp4 里抽帧做调色板（不缩放就太糊，顺便压到 ≤ <see cref="GifMaxWidth"/>）。</summary>
    public static IReadOnlyList<string> GifPaletteArgs(string input, string palettePath, int fps)
    {
        int rate = Math.Min(NormalizeFps(fps), GifMaxFps);

        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-i", input,
            "-vf", $"fps={rate},{GifScaleFilter},palettegen=stats_mode=diff",
            palettePath,
        };
    }

    /// <summary>GIF 第二遍：拿第一遍的调色板把同一段 mp4 转成 GIF（无限循环）。</summary>
    public static IReadOnlyList<string> GifEncodeArgs(string input, string palettePath, string output, int fps)
    {
        int rate = Math.Min(NormalizeFps(fps), GifMaxFps);

        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-i", input,
            "-i", palettePath,
            "-lavfi", $"fps={rate},{GifScaleFilter}[x];[x][1:v]paletteuse=dither=sierra2_4a:diff_mode=rectangle",
            "-loop", "0",
            output,
        };
    }

    /// <summary>缩到 GIF 宽度上限（小画面不放大）。</summary>
    public static string GifScaleFilter => $"scale='min({GifMaxWidth},iw)':-1:flags=lanczos";

    /// <summary>
    /// 动图转码（APNG / WebP）：单遍、无音轨；帧率与宽度上限和 GIF 一致。
    /// GIF 仍走两遍调色板（<see cref="GifPaletteArgs"/> / <see cref="GifEncodeArgs"/>）。
    /// </summary>
    public static IReadOnlyList<string> AnimatedArgs(string input, string output,
        RecordFormat format, int fps)
    {
        int rate = Math.Min(NormalizeFps(fps), GifMaxFps);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-i", input,
            "-vf", $"fps={rate},{GifScaleFilter}",
            "-an",
        };

        if (format == RecordFormat.Apng)
        {
            args.Add("-plays");
            args.Add("0");
            args.Add("-c:v");
            args.Add("apng");
        }
        else
        {
            args.Add("-c:v");
            args.Add("libwebp_anim");
            args.Add("-lossless");
            args.Add("0");
            args.Add("-q:v");
            args.Add("75");
            args.Add("-loop");
            args.Add("0");
        }

        args.Add(output);
        return args;
    }

    // ============ ffmpeg 定位 ============

    /// <summary>
    /// 找 ffmpeg：先看随包的 <c>runtime\ffmpeg\ffmpeg.exe</c>，再按 <c>PATH</c> 找。
    /// 纯函数（<paramref name="fileExists"/> 注入），自检直接断言。
    /// </summary>
    public static string? Locate(string appDir, string? pathVariable, Func<string, bool> fileExists)
    {
        if (!string.IsNullOrWhiteSpace(appDir))
        {
            string bundled = Path.Combine(appDir, "runtime", "ffmpeg", "ffmpeg.exe");
            if (fileExists(bundled)) return bundled;
        }

        foreach (string raw in (pathVariable ?? "").Split(Path.PathSeparator))
        {
            string dir = raw.Trim().Trim('"');
            if (dir.Length == 0) continue;

            string candidate = Path.Combine(dir, "ffmpeg.exe");
            if (fileExists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>运行时定位（随包优先，其次 PATH）；找不到返回 null。</summary>
    public static string? FindFfmpeg() =>
        Locate(AppPaths.AppDirectory, Environment.GetEnvironmentVariable("PATH"), File.Exists);
}
