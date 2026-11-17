using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录屏的「抢救」：录的过程写成**分片 MP4**，进程被杀 / 断电时已经落下的分片还能播；
/// 下次启动把 <c>cache/record/</c> 里这些残片重新封装、收进采集历史。
/// </summary>
/// <remarks>
/// <para>
/// 照 Snow Shot 的 recording recovery：录制中断不等于白录。这里不做「弹框问用户」，
/// 直接收进采集历史并用一条常驻 toast 说明去处——被中断的那次用户本来就在等结果。
/// </para>
/// <para>
/// 只有**真的打不开**（ffmpeg 连尺寸 / 时长都读不出来）的残片才删；能读出来的一律留下，
/// 时长取容器里记的（分片文件可能比实际短一点，但不会凭空多出来）。
/// </para>
/// </remarks>
internal static class RecordRecovery
{
    private static readonly Regex DurationRe = new(
        @"Duration:\s*(\d+):(\d{2}):(\d{2}(?:\.\d+)?)", RegexOptions.CultureInvariant);

    private static readonly Regex VideoSizeRe = new(
        @"Video:.*?(\d{2,5})x(\d{2,5})", RegexOptions.CultureInvariant);

    /// <summary>录制缓存目录（与 <see cref="RecordSession"/> 同一个）。</summary>
    public static string TempDir => Path.Combine(AppPaths.DataRoot, "cache", "record");

    /// <summary>从 <c>ffmpeg -i</c> 的输出里读尺寸与时长（纯函数，自检直接断言）。</summary>
    public static bool TryParseProbe(string output, out int width, out int height, out int durationMs)
    {
        width = 0;
        height = 0;
        durationMs = 0;

        var size = VideoSizeRe.Match(output ?? "");
        if (!size.Success) return false;
        if (!int.TryParse(size.Groups[1].Value, out width)) return false;
        if (!int.TryParse(size.Groups[2].Value, out height)) return false;
        if (width < 2 || height < 2) return false;

        var duration = DurationRe.Match(output ?? "");
        if (duration.Success)
        {
            int hours = int.Parse(duration.Groups[1].Value, CultureInfo.InvariantCulture);
            int minutes = int.Parse(duration.Groups[2].Value, CultureInfo.InvariantCulture);
            double seconds = double.Parse(duration.Groups[3].Value, CultureInfo.InvariantCulture);
            durationMs = (int)Math.Max(0, ((hours * 60 + minutes) * 60 + seconds) * 1000);
        }

        return true;
    }

    /// <summary>
    /// 把上次中断留下的残片收进采集历史。返回恢复条数（0 = 没什么可恢复的）。
    /// </summary>
    /// <remarks>只该在启动时调一次（正常流程里 <c>App.OnStartup</c>）。</remarks>
    public static int Sweep()
    {
        try
        {
            return SweepCore();
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "抢救上次录屏失败：" + ex.Message);
            return 0;
        }
    }

    private static int SweepCore()
    {
        if (!Directory.Exists(TempDir)) return 0;

        var files = Directory.EnumerateFiles(TempDir, "*.mp4")
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0) return 0;

        string? ffmpeg = RecordPlan.FindFfmpeg();
        if (ffmpeg == null) return 0;   // 没有 ffmpeg 就读不出尺寸、也重封装不了，留给下次

        int recovered = 0;
        foreach (string file in files)
        {
            if (!TryRecover(ffmpeg, file)) Delete(file);
            else recovered++;
        }

        if (recovered > 0)
        {
            ToastService.Show(
                $"上次录屏被中断，已恢复 {recovered} 个文件到采集历史",
                "录屏", 5000, ToastKind.warning, key: "record-recovery",
                onClick: () => OpenRecordsFolder(),
                actionText: "打开文件夹");
        }

        return recovered;
    }

    private static bool TryRecover(string ffmpeg, string file)
    {
        long length;
        try { length = new FileInfo(file).Length; }
        catch { return false; }

        // 太小 = 只有文件头，救不回来
        if (length < 4096) return false;

        // 分片 mp4 本来就是能播的；重封装一次拿到正常的时长 / faststart
        string fixedFile = Path.ChangeExtension(file, ".fixed.mp4");
        bool remuxed = RecordSession.RunFfmpeg(ffmpeg, RecordPlan.RemuxArgs(file, fixedFile), out _)
            && File.Exists(fixedFile) && new FileInfo(fixedFile).Length > 0;

        string candidate = remuxed ? fixedFile : file;
        try
        {
            if (!TryProbe(ffmpeg, candidate, out int width, out int height, out int durationMs))
            {
                Delete(fixedFile);
                return false;
            }

            var entry = CaptureCatalog.CreateRecord(width, height, "录屏（中断后恢复）", "mp4",
                durationMs, candidate, poster: null);
            if (entry == null)
            {
                Delete(fixedFile);
                return false;
            }

            if (!string.Equals(candidate, file, StringComparison.OrdinalIgnoreCase)) Delete(file);
            AppLog.Info("CapturePro.Record",
                $"恢复上次中断的录屏：{width} × {height} · {durationMs} ms → {entry.File}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "恢复录屏失败：" + ex.Message);
            Delete(fixedFile);
            return false;
        }
    }

    private static bool TryProbe(string ffmpeg, string file, out int width, out int height, out int durationMs)
    {
        width = height = durationMs = 0;

        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-i");
        info.ArgumentList.Add(file);

        using var process = Process.Start(info);
        if (process == null) return false;

        string output = process.StandardError.ReadToEnd();
        process.WaitForExit(15000);

        return TryParseProbe(output, out width, out height, out durationMs);
    }

    private static void Delete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 下次再试 */ }
    }

    private static void OpenRecordsFolder()
    {
        try
        {
            Directory.CreateDirectory(CaptureCatalog.RecordsDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{CaptureCatalog.RecordsDir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro.Record", "打开录屏目录失败：" + ex.Message);
        }
    }
}
