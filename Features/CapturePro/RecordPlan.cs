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

/// <summary>录成什么：MP4（H.264）还是 GIF（自动调色板）。</summary>
internal enum RecordFormat
{
    Mp4,
    Gif,
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

    public RecordMode Mode { get; init; } = RecordMode.Region;

    public RecordFormat Format { get; init; } = RecordFormat.Mp4;

    public int Fps { get; init; } = DefaultFps;

    public int Countdown { get; init; } = DefaultCountdown;

    public bool IsGif => Format == RecordFormat.Gif;

    /// <summary>实际录制帧率（夹到 5–60）。</summary>
    public int EffectiveFps => NormalizeFps(Fps);

    /// <summary>GIF 转码时用的帧率。</summary>
    public int GifFps => Math.Min(EffectiveFps, GifMaxFps);

    /// <summary>成品扩展名（不含点）。</summary>
    public string Extension => IsGif ? "gif" : "mp4";

    public static RecordPlan FromValues(string? mode, string? format, int fps, int countdown) => new()
    {
        Mode = ParseMode(mode),
        Format = ParseFormat(format),
        Fps = NormalizeFps(fps),
        Countdown = NormalizeCountdown(countdown),
    };

    public static RecordMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "primary" => RecordMode.Primary,
        "all" => RecordMode.All,
        "window" => RecordMode.Window,
        _ => RecordMode.Region,
    };

    public static RecordFormat ParseFormat(string? value) =>
        string.Equals(value?.Trim(), "gif", StringComparison.OrdinalIgnoreCase)
            ? RecordFormat.Gif
            : RecordFormat.Mp4;

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
        string.Equals(extension, "gif", StringComparison.OrdinalIgnoreCase) ? "GIF" : "MP4";

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
    /// <c>pad</c> 保证宽高是偶数（H.264 的 yuv420p 要求），否则奇数尺寸的选区会直接编码失败。
    /// </summary>
    public static IReadOnlyList<string> Mp4Args(Int32Rect size, int fps, string output)
    {
        int width = Math.Max(2, size.Width);
        int height = Math.Max(2, size.Height);
        int rate = NormalizeFps(fps);

        return new[]
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-f", "rawvideo", "-pixel_format", "bgra",
            "-video_size", $"{width}x{height}", "-framerate", rate.ToString(CultureInfo.InvariantCulture),
            "-i", "pipe:0",
            "-an",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "20",
            "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            output,
        };
    }

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
