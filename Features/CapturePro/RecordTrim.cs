using System;
using System.Collections.Generic;
using System.Globalization;

namespace LTools.Features.CapturePro;

/// <summary>裁剪区间 + 倍速：源时间（毫秒）上的半开区间，<c>[StartMs, EndMs)</c>。</summary>
internal readonly record struct TrimRange(int StartMs, int EndMs, double Speed)
{
    public int SourceMs => Math.Max(0, EndMs - StartMs);

    /// <summary>成品时长（按倍速换算）。</summary>
    public int OutputMs => (int)Math.Round(SourceMs / Math.Max(0.05, Speed));

    public bool IsFullRange(int durationMs) =>
        StartMs <= 0 && EndMs >= durationMs && Math.Abs(Speed - 1.0) < 0.0001;

    public string Label =>
        $"{RecordTrim.FormatSeconds(StartMs)} → {RecordTrim.FormatSeconds(EndMs)} · {RecordTrim.SpeedText(Speed)}";
}

/// <summary>
/// 录完之后的裁剪 / 倍速：区间夹取、倍速文案、ffmpeg 导出参数。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的裁剪规格（<c>docs/recording-trimming.md</c>）：源时间轴上的半开区间、
/// 倍速档位 0.25–3.0、<c>1.0x</c> 全片直接复用原文件、声音用**有界的 <c>atempo</c> 串联**
/// （单级只接受 0.5–2.0，超出就拆成两级乘起来），画面用 <c>setpts</c>。
/// </para>
/// <para>
/// 全部是纯函数：<c>--selftest</c> 直接断言夹取、档位、atempo 分解与参数拼装；
/// 真正跑 ffmpeg 的是 <see cref="RecordTrimSession"/>。
/// </para>
/// </remarks>
internal static class RecordTrim
{
    /// <summary>倍速档位（上游同一套：0.25 / 0.5 / 0.75 / 1 / 1.25 / 1.5 / 1.75 / 2 / 3）。</summary>
    public static readonly double[] Speeds = new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 3.0 };

    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 3.0;

    /// <summary>最短可选区间（一帧 60fps 也放得下）。</summary>
    public const int MinDurationMs = 16;

    /// <summary>夹取成一个合法区间：至少 <see cref="MinDurationMs"/>、不越过片长、倍速落在档位范围内。</summary>
    public static TrimRange Normalize(int startMs, int endMs, double speed, int durationMs)
    {
        int duration = Math.Max(0, durationMs);
        int start = Math.Clamp(startMs, 0, Math.Max(0, duration - MinDurationMs));
        int end = Math.Clamp(endMs, start + MinDurationMs, Math.Max(start + MinDurationMs, duration));

        double rate = double.IsFinite(speed) ? Math.Clamp(speed, MinSpeed, MaxSpeed) : 1.0;

        // 吸附到最近的标准档（手拖时间轴不会把倍速改掉，但手填 1.4 会落到 1.5 附近）
        foreach (double candidate in Speeds)
            if (Math.Abs(candidate - rate) < 0.125) rate = candidate;

        return new TrimRange(start, end, rate);
    }

    /// <summary>倍速文案：<c>1.0x</c> / <c>0.25x</c>。</summary>
    public static string SpeedText(double speed) =>
        speed.ToString("0.0#", CultureInfo.InvariantCulture) + "x";

    /// <summary>秒数文案：<c>mm:ss.mmm</c>（裁剪时间码要看到毫秒）。</summary>
    public static string FormatSeconds(int ms)
    {
        int total = Math.Max(0, ms);
        int minutes = total / 60000;
        int seconds = total % 60000 / 1000;
        int millis = total % 1000;

        return string.Create(CultureInfo.InvariantCulture, $"{minutes:00}:{seconds:00}.{millis:000}");
    }

    /// <summary>
    /// 把倍速拆成一串 <c>atempo</c> 因子：每级只能 0.5–2.0，所以 3x 是 <c>2.0,1.5</c>、
    /// 0.25x 是 <c>0.5,0.5</c>。1.0x 返回空（不挂音频滤镜）。
    /// </summary>
    public static IReadOnlyList<double> AtempoChain(double speed)
    {
        var chain = new List<double>();
        if (!double.IsFinite(speed) || speed <= 0) return chain;

        double left = Math.Clamp(speed, MinSpeed, MaxSpeed);
        while (left > 2.0)
        {
            chain.Add(2.0);
            left /= 2.0;
        }

        while (left < 0.5)
        {
            chain.Add(0.5);
            left /= 0.5;
        }

        if (Math.Abs(left - 1.0) > 0.0001) chain.Add(Math.Round(left, 4));
        return chain;
    }

    /// <summary>
    /// 导出参数：<c>-ss</c> / <c>-to</c> 是**输入**选项（放在 <c>-i</c> 之前），按源时间读，
    /// 画面 <c>setpts</c>、声音 <c>atempo</c> 只作用在选中的那一段上，重编码成 H.264 + AAC 的 faststart MP4。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-ss</c> / <c>-to</c> 必须在 <c>-i</c> 之前：放到后面它们是「输出」选项，时间基准变成输出时间轴，
    /// 倍速一上来就剪错（实测剪 800ms + 2 倍速会得到 800ms）。
    /// </para>
    /// <para>
    /// 光有输入侧区间还不够：<c>setpts</c> 改了时间戳之后，ffmpeg 的 muxer 仍按输入侧区间的绝对值
    /// 推断成品长度，2 倍速的 800ms 剪出 500ms（该 400ms）、1.5 倍速多一帧。所以再补一个**输出侧**
    /// <c>-t</c> = 成品时长收口，视频 / 音频都在这个长度上停（实测 0.25x–3x 全档位误差 ≤ 1 帧）。
    /// </para>
    /// <para>
    /// 声音一律重编码（不 <c>-c:a copy</c>）：切点常常不在音频包边界上，
    /// 直接搬会把前一段的尾巴带进来、音画对不齐。
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> ExportArgs(string input, string output, TrimRange range, bool hasAudio)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error", "-nostats",
            "-ss", Seconds(range.StartMs / 1000.0),
            "-to", Seconds(range.EndMs / 1000.0),
            "-i", input,
            "-t", Seconds(range.OutputMs / 1000.0),
        };

        bool retime = Math.Abs(range.Speed - 1.0) > 0.0001;

        if (retime)
        {
            args.AddRange(new[]
            {
                "-vf", string.Create(CultureInfo.InvariantCulture, $"setpts=(PTS-STARTPTS)/{range.Speed.ToString("0.####", CultureInfo.InvariantCulture)}"),
            });
        }

        args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "18", "-pix_fmt", "yuv420p" });

        if (hasAudio)
        {
            var chain = retime ? AtempoChain(range.Speed) : Array.Empty<double>();
            if (chain.Count > 0)
                args.AddRange(new[] { "-af", string.Join(",", Filters(chain)) });

            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        }
        else
        {
            args.Add("-an");
        }

        args.AddRange(new[] { "-movflags", "+faststart", output });
        return args;
    }

    private static IEnumerable<string> Filters(IReadOnlyList<double> chain)
    {
        foreach (double factor in chain)
            yield return "atempo=" + factor.ToString("0.####", CultureInfo.InvariantCulture);
    }

    private static string Seconds(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>导出文件名：原文件名 + <c>-裁剪</c>，重名就加序号（不覆盖原片）。</summary>
    public static string OutputPath(string directory, string baseName, Func<string, bool> exists)
    {
        string suffix = "-裁剪";
        string candidate = System.IO.Path.Combine(directory, baseName + suffix + ".mp4");

        for (int i = 2; exists(candidate) && i < 1000; i++)
            candidate = System.IO.Path.Combine(directory, $"{baseName}{suffix}-{i}.mp4");

        return candidate;
    }
}
