using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using LTools.Core;
using LTools.Native;
using LTools.Views.CapturePro;

namespace LTools.Features.CapturePro;

/// <summary>
/// 裁剪 / 倍速的会话：开窗口、探测音轨、导出、复制。
/// </summary>
/// <remarks>
/// <para>
/// 窗口只管界面（时间轴 / 预览 / 倍速），导出与文件操作都在这里，
/// 这样录制结束时的「录完直接裁剪」与采集历史里的「裁剪…」走的是同一条路。
/// </para>
/// <para>
/// 导出**永远写新文件**（<c>&lt;名字&gt;-裁剪.mp4</c>，重名加序号），原片不动——
/// 与上游「Publication uses temporary files and never replaces a source」一致。
/// </para>
/// </remarks>
internal static class RecordTrimSession
{
    /// <summary>打开裁剪窗口（**必须在 UI 线程**调用）。</summary>
    public static void Open(CaptureEntry entry)
    {
        var window = new RecordTrimWindow(entry);
        window.Show();
    }

    /// <summary>
    /// 导出用的临时路径（<c>cache/record</c>）：成功后再由 <see cref="Remember"/> 搬进采集历史，
    /// 或由「另存为」直接写到用户选的位置。
    /// </summary>
    public static string TempOutput()
    {
        Directory.CreateDirectory(RecordRecovery.TempDir);
        return Path.Combine(RecordRecovery.TempDir, "trim-" + IdFactory.ShortId() + ".mp4");
    }

    public static string BaseName(CaptureEntry entry)
    {
        string file = CaptureCatalog.FilePath(entry);
        string name = Path.GetFileNameWithoutExtension(file);
        return string.IsNullOrWhiteSpace(name) ? "录屏" : name;
    }

    /// <summary>
    /// 片长（毫秒）：优先用采集历史里记的时长，没有再用 ffmpeg 读（老记录 / 外部文件）。
    /// </summary>
    public static int DurationOf(CaptureEntry entry)
    {
        if (entry.DurationMs > 0) return entry.DurationMs;

        string? ffmpeg = RecordPlan.FindFfmpeg();
        if (ffmpeg == null) return 0;

        string info = RunInfo(ffmpeg, CaptureCatalog.FilePath(entry));
        return ParseDurationMs(info);
    }

    /// <summary>有没有音轨（导出时决定 <c>-c:a copy</c> 还是 <c>-an</c>）。</summary>
    public static bool HasAudio(CaptureEntry entry)
    {
        string? ffmpeg = RecordPlan.FindFfmpeg();
        if (ffmpeg == null) return false;

        return HasAudioStream(RunInfo(ffmpeg, CaptureCatalog.FilePath(entry)));
    }

    /// <summary>从 ffmpeg 的流信息里判有没有音频（<c>Stream #0:1: Audio: aac…</c>）。</summary>
    internal static bool HasAudioStream(string info)
    {
        foreach (string line in info.Split('\n'))
            if (line.Contains("Stream #", StringComparison.Ordinal)
                && line.Contains(": Audio:", StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>从 <c>Duration: 00:01:23.45</c> 解析毫秒；解析不出返回 0。</summary>
    internal static int ParseDurationMs(string info)
    {
        int at = info.IndexOf("Duration:", StringComparison.Ordinal);
        if (at < 0) return 0;

        string text = info.Substring(at + "Duration:".Length, Math.Min(24, info.Length - at - 9)).Trim();
        int comma = text.IndexOf(',');
        if (comma > 0) text = text[..comma];

        var parts = text.Split(':');
        if (parts.Length != 3) return 0;

        if (!int.TryParse(parts[0], out int hours)) return 0;
        if (!int.TryParse(parts[1], out int minutes)) return 0;
        if (!double.TryParse(parts[2], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds)) return 0;

        return (int)Math.Round(((hours * 60 + minutes) * 60 + seconds) * 1000);
    }

    /// <summary>
    /// 导出裁剪结果。全片 + 1.0x 直接复制字节（上游同样的「复用原文件」），
    /// 其余重编码。返回 false 时 <paramref name="error"/> 是给用户看的说明。
    /// </summary>
    public static bool Export(CaptureEntry entry, string output, TrimRange range, bool hasAudio, out string error)
    {
        error = "";

        string input = CaptureCatalog.FilePath(entry);
        if (!File.Exists(input))
        {
            error = "原片不在了：" + input;
            return false;
        }

        string? ffmpeg = RecordPlan.FindFfmpeg();
        if (ffmpeg == null)
        {
            error = "没找到 ffmpeg";
            return false;
        }

        try
        {
            if (range.IsFullRange(Math.Max(range.EndMs, entry.DurationMs)))
            {
                File.Copy(input, output, true);
                return true;
            }

            // 先写 .part 再改名：导出中途失败不会留下一个看起来正常的半成品
            string part = output + ".part.mp4";
            File.Delete(part);

            if (!RecordSession.RunFfmpeg(ffmpeg, RecordTrim.ExportArgs(input, part, range, hasAudio), out error))
            {
                File.Delete(part);
                return false;
            }

            File.Move(part, output, true);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>把成品放进剪贴板（文件形式，资源管理器里能直接粘贴）。</summary>
    public static bool CopyToClipboard(string path)
    {
        try
        {
            return NativeClipboard.SetFileList(new[] { path }, cut: false);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Trim", "复制裁剪结果失败：" + ex.Message);
            return false;
        }
    }

    private static string RunInfo(string ffmpeg, string input)
    {
        if (!File.Exists(input)) return "";

        try
        {
            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            foreach (string arg in new[] { "-hide_banner", "-i", input })
                info.ArgumentList.Add(arg);

            using var process = Process.Start(info);
            if (process == null) return "";

            var text = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) text.AppendLine(e.Data); };
            process.BeginErrorReadLine();

            if (!process.WaitForExit(15000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
            }

            return text.ToString();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Trim", "读取视频信息失败：" + ex.Message);
            return "";
        }
    }

    /// <summary>采集历史里的一条记录（导出成功后登记，让裁剪结果也能在历史里找到）。</summary>
    /// <returns>成品在采集历史里的路径；登记失败返回 null（临时文件保持原样）。</returns>
    public static string? Remember(CaptureEntry source, string tempFile, TrimRange range)
    {
        try
        {
            var entry = CaptureCatalog.CreateRecord(source.Width, source.Height, "录屏 · 裁剪", "mp4",
                range.OutputMs, tempFile, null);

            return entry == null ? null : CaptureCatalog.FilePath(entry);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Trim", "登记裁剪结果失败：" + ex.Message);
            return null;
        }
    }
}
