using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 编码器选择：<c>libx264</c> / <c>libx265</c> 与各家硬件编码（NVENC / AMF / QSV）。
/// </summary>
/// <remarks>
/// <para>
/// ffmpeg 的 essentials 包里 <b>八个编码器都编进去了</b>，但「编进去了」不等于「这台机器能用」：
/// 没有对应显卡时 NVENC / AMF / QSV 会在初始化那一刻失败（实测 QSV 在这种机器上就是
/// <c>Error creating a MFX session</c>）。所以分两层：
/// </para>
/// <list type="number">
/// <item>启动录制前 <see cref="Probe"/> 逐个试一段 0.1 秒的黑场（约 100ms/个，一次进程只跑一次并缓存）；</item>
/// <item>真录的时候若硬件编码器仍然开不起来，<see cref="RecordEngine"/> 用 <see cref="Fallback"/> 重开一次——</item>
/// </list>
/// <para>任何一层都不该让用户丢录像：硬件路径只是「更快」，软件路径永远兜底。</para>
/// </remarks>
internal static class RecordEncoders
{
    public const string X264 = "libx264";
    public const string X265 = "libx265";

    /// <summary>探测顺序也是优选顺序（NVENC → AMF → QSV；QSV 在各家核显驱动上最容易开不起来）。</summary>
    public static readonly IReadOnlyList<string> Candidates = new[]
    {
        "h264_nvenc", "hevc_nvenc",
        "h264_amf", "hevc_amf",
        "h264_qsv", "hevc_qsv",
    };

    private static readonly object Gate = new();
    private static string? _probedFor;
    private static IReadOnlyCollection<string> _available = Array.Empty<string>();

    /// <summary>硬件编码器（按 <see cref="Candidates"/> 顺序）——只有真的能开起来才在里面。</summary>
    public static IReadOnlyCollection<string> Probe(string ffmpeg)
    {
        lock (Gate)
        {
            if (_probedFor == ffmpeg) return _available;

            var ok = new List<string>();
            foreach (string encoder in Candidates)
                if (CanEncode(ffmpeg, encoder)) ok.Add(encoder);

            _probedFor = ffmpeg;
            _available = ok;

            if (ok.Count > 0) AppLog.Info("CapturePro.Record", "可用硬件编码器：" + string.Join(", ", ok));
            return ok;
        }
    }

    /// <summary>某个硬件编码器能不能开起来（128×128 两帧黑场，<c>-f null</c> 不落盘）。</summary>
    public static bool CanEncode(string ffmpeg, string encoder)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string arg in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-f", "lavfi", "-i", "color=c=black:s=256x256:r=30:d=0.1",
                         "-frames:v", "2", "-c:v", encoder, "-f", "null", "-",
                     })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process == null) return false;

            process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", $"探测 {encoder} 失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 按意图挑一个编码器：<c>auto</c> / <c>h264</c> 有硬件就用硬件，<c>hevc</c> 优先硬件 HEVC；
    /// 没有可用硬件时退到 <c>libx264</c> / <c>libx265</c>。纯函数（硬件清单注入），自检直接断言。
    /// </summary>
    public static string Pick(RecordCodec codec, IReadOnlyCollection<string> hardware)
    {
        // 编码器名是「格式_厂商」（h264_nvenc / hevc_amf），所以要按前缀认格式
        string prefix = codec == RecordCodec.hevc ? "hevc" : "h264";

        foreach (string encoder in Candidates)
        {
            if (!encoder.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (Contains(hardware, encoder)) return encoder;
        }

        // auto 时「只有 HEVC 硬件、没 H.264 硬件」也要用上（少见，但别平白掉回软件）
        if (codec == RecordCodec.auto)
        {
            foreach (string encoder in Candidates)
                if (Contains(hardware, encoder)) return encoder;
        }

        return codec == RecordCodec.hevc ? X265 : X264;
    }

    /// <summary>硬件编码器开不起来时的软件兜底。</summary>
    public static string Fallback(string encoder) =>
        encoder is X265 or "hevc_nvenc" or "hevc_amf" or "hevc_qsv" ? X265 : X264;

    public static bool IsHardware(string encoder) =>
        encoder is not (X264 or X265) && Candidates.Contains(encoder);

    public static bool IsHevc(string encoder) =>
        encoder is X265 or "hevc_nvenc" or "hevc_amf" or "hevc_qsv";

    /// <summary>编码器参数（画面质量 / 速度档）。</summary>
    /// <param name="encoder">编码器名（来自 <see cref="Pick"/>）。</param>
    /// <param name="quality">画面质量 0–100（上游 <c>video_quality</c>，默认 80）。</param>
    /// <param name="preset">x264 / x265 的速度档（上游 <c>encoding_preset</c>，默认 <c>veryfast</c>）。</param>
    public static IReadOnlyList<string> Arguments(string encoder,
        int quality = RecordOptions.DefaultVideoQuality, string preset = "veryfast")
    {
        int crf = RecordOptions.CrfOf(quality);
        string speed = RecordOptions.NormalizePreset(preset);
        string nvencPreset = speed is "ultrafast" or "superfast" or "veryfast" or "faster" or "fast" ? "p1"
            : speed is "medium" ? "p4"
            : "p6";

        return encoder switch
        {
            X265 => new[] { "-c:v", X265, "-preset", speed, "-crf", crf.ToString(), "-tag:v", "hvc1" },
            "h264_nvenc" => new[]
            {
                "-c:v", "h264_nvenc", "-preset", nvencPreset, "-tune", "ll", "-rc", "vbr",
                "-cq", crf.ToString(), "-b:v", "0",
            },
            "hevc_nvenc" => new[]
            {
                "-c:v", "hevc_nvenc", "-preset", nvencPreset, "-tune", "ll", "-rc", "vbr",
                "-cq", (crf + 3).ToString(), "-b:v", "0", "-tag:v", "hvc1",
            },
            "h264_amf" => new[]
            {
                "-c:v", "h264_amf", "-quality", "balanced", "-rc", "cqp",
                "-qp_i", crf.ToString(), "-qp_p", crf.ToString(), "-qp_b", (crf + 2).ToString(),
            },
            "hevc_amf" => new[]
            {
                "-c:v", "hevc_amf", "-quality", "balanced", "-rc", "cqp",
                "-qp_i", (crf + 3).ToString(), "-qp_p", (crf + 3).ToString(), "-qp_b", (crf + 5).ToString(),
                "-tag:v", "hvc1",
            },
            "h264_qsv" => new[] { "-c:v", "h264_qsv", "-preset", speed, "-global_quality", crf.ToString() },
            "hevc_qsv" => new[]
            {
                "-c:v", "hevc_qsv", "-preset", speed, "-global_quality", (crf + 3).ToString(), "-tag:v", "hvc1",
            },
            _ => new[] { "-c:v", X264, "-preset", speed, "-crf", crf.ToString() },
        };
    }

    /// <summary>成品扩展名之外的一句话名字（日志 / 控制条用）。</summary>
    public static string Label(string encoder) => encoder switch
    {
        X264 => "H.264",
        X265 => "HEVC",
        "h264_nvenc" => "H.264 · NVENC",
        "hevc_nvenc" => "HEVC · NVENC",
        "h264_amf" => "H.264 · AMF",
        "hevc_amf" => "HEVC · AMF",
        "h264_qsv" => "H.264 · QSV",
        "hevc_qsv" => "HEVC · QSV",
        _ => encoder,
    };

    private static bool Contains(IReadOnlyCollection<string> set, string value)
    {
        foreach (string item in set)
            if (string.Equals(item, value, StringComparison.Ordinal)) return true;
        return false;
    }
}
