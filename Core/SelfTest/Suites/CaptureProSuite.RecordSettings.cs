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

/// <summary>CaptureProSuite 的「录屏：设置 / 裁剪 / 特效」部分。</summary>
internal static partial class CaptureProSuite
{
    // ============ 录屏：裁剪 / 倍速 ============

    /// <summary>
    /// 裁剪区间夹取、倍速档位、<c>atempo</c> 串联（单级只接受 0.5–2.0）、
    /// 导出参数与文件名；以及从 ffmpeg 输出里读时长 / 音轨。
    /// </summary>
    private static void RecordTrimRules()
    {
        // ---- 区间夹取 ----
        var all = RecordTrim.Normalize(0, 10_000, 1.0, 10_000);
        Check.Equal(0, all.StartMs, "起点");
        Check.Equal(10_000, all.EndMs, "终点");
        Check.Equal(10_000, all.SourceMs, "源时长");
        Check.Equal(10_000, all.OutputMs, "1.0x 成品时长 = 源时长");
        Check.True(all.IsFullRange(10_000), "整片 + 1.0x 是全片");

        var flipped = RecordTrim.Normalize(8_000, 2_000, 1.0, 10_000);
        Check.True(flipped.EndMs > flipped.StartMs, "终点在起点前面时被推开（不会出现负区间）");
        Check.Equal(RecordTrim.MinDurationMs, flipped.SourceMs, "至少留一帧");

        var past = RecordTrim.Normalize(0, 99_999, 1.0, 10_000);
        Check.Equal(10_000, past.EndMs, "终点不越过片长");

        var negative = RecordTrim.Normalize(-5_000, 5_000, 1.0, 10_000);
        Check.Equal(0, negative.StartMs, "起点负数收到 0");

        var fast = RecordTrim.Normalize(0, 6_000, 2.0, 10_000);
        Check.Equal(3_000, fast.OutputMs, "2.0x 成品时长减半");
        Check.False(fast.IsFullRange(10_000), "剪过就不是全片");

        var slow = RecordTrim.Normalize(0, 6_000, 0.5, 10_000);
        Check.Equal(12_000, slow.OutputMs, "0.5x 成品时长翻倍");

        // 倍速夹取 + 吸附到标准档
        Check.Equal(RecordTrim.MaxSpeed, RecordTrim.Normalize(0, 1000, 99, 1000).Speed, "倍速夹到 3.0");
        Check.Equal(RecordTrim.MinSpeed, RecordTrim.Normalize(0, 1000, 0.01, 1000).Speed, "倍速夹到 0.25");
        Check.Equal(1.5, RecordTrim.Normalize(0, 1000, 1.4, 1000).Speed, "1.4 吸附到 1.5");
        Check.Equal(1.0, RecordTrim.Normalize(0, 1000, double.NaN, 1000).Speed, "NaN 当 1.0");

        // 零长度片（读不出时长的坏文件）也不炸
        var empty = RecordTrim.Normalize(0, 0, 1.0, 0);
        Check.True(empty.EndMs >= empty.StartMs, "零长度片不会算出倒置区间");

        // ---- 文案 ----
        Check.Equal("1.0x", RecordTrim.SpeedText(1.0), "1.0 保留一位");
        Check.Equal("0.25x", RecordTrim.SpeedText(0.25), "0.25 不补零");
        Check.Equal("2.0x", RecordTrim.SpeedText(2.0), "2.0 保留一位");
        Check.Equal("00:00.000", RecordTrim.FormatSeconds(0), "零时码");
        Check.Equal("01:02.345", RecordTrim.FormatSeconds(62_345), "带毫秒的时间码");
        Check.Equal("00:16", RecordPlan.FormatClock(16_000), "摘要用整秒时码");

        // ---- atempo 串联：单级只接受 0.5–2.0 ----
        Check.Equal(0, RecordTrim.AtempoChain(1.0).Count, "1.0x 不挂音频滤镜");
        Check.Equal("2,1.5", string.Join(",", RecordTrim.AtempoChain(3.0)), "3.0x = 2.0 + 1.5");
        Check.Equal("0.5,0.5", string.Join(",", RecordTrim.AtempoChain(0.25)), "0.25x = 0.5 + 0.5");
        Check.Equal("2", string.Join(",", RecordTrim.AtempoChain(2.0)), "2.0x 一级就够");
        Check.Equal("0.5", string.Join(",", RecordTrim.AtempoChain(0.5)), "0.5x 一级就够");
        Check.Equal("1.25", string.Join(",", RecordTrim.AtempoChain(1.25)), "1.25x 一级");
        Check.Equal(0, RecordTrim.AtempoChain(0).Count, "0 不产出（调用方自己判）");

        foreach (double speed in RecordTrim.Speeds)
        {
            double product = 1;
            foreach (double factor in RecordTrim.AtempoChain(speed))
            {
                Check.True(factor is >= 0.5 and <= 2.0, $"每级 atempo 都在 0.5–2.0（{speed}x → {factor}）");
                product *= factor;
            }

            if (Math.Abs(speed - 1.0) > 0.0001)
                Check.True(Math.Abs(product - speed) < 0.001, $"{speed}x 的因子乘积等于倍速（{product}）");
        }

        // ---- 导出参数 ----
        var cut = RecordTrim.Normalize(2_000, 8_000, 1.0, 10_000);
        var cutArgs = RecordTrim.ExportArgs("in.mp4", "out.mp4", cut, hasAudio: true);
        string cutLine = string.Join(" ", cutArgs);
        Check.True(cutLine.Contains("-ss 2") && cutLine.Contains("-to 8"), "按源时间切（秒）");
        Check.True(cutLine.IndexOf("-ss", StringComparison.Ordinal)
            < cutLine.IndexOf("-i in.mp4", StringComparison.Ordinal),
            "-ss / -to 必须放在 -i 之前（否则时间基准变成输出时间轴，倍速会剪错）");
        Check.True(cutLine.Contains("-t 6"), "输出侧 -t 按成品时长收口（1.0x 剪 6 秒 = 6 秒）");
        Check.True(cutLine.IndexOf("-t 6", StringComparison.Ordinal)
            > cutLine.IndexOf("-i in.mp4", StringComparison.Ordinal),
            "-t 是输出选项，要放在 -i 之后");
        Check.True(cutLine.Contains("-c:a aac"), "声音一律重编码（切点不在音频包边界上）");
        Check.False(cutLine.Contains("setpts"), "1.0x 不改画面时间戳");
        Check.False(cutLine.Contains("atempo"), "1.0x 不挂 atempo");
        Check.True(cutLine.Contains("libx264"), "画面重编码（裁剪必须重编）");
        Check.True(cutLine.Contains("+faststart"), "导出也转 faststart");
        Check.Equal("out.mp4", cutArgs[^1], "输出在最后一个参数");

        var sped = RecordTrim.Normalize(1_000, 4_000, 2.0, 10_000);
        var spedArgs = RecordTrim.ExportArgs("in.mp4", "out.mp4", sped, hasAudio: true);
        string spedLine = string.Join(" ", spedArgs);
        Check.True(spedLine.Contains("setpts=(PTS-STARTPTS)/2"), "画面按倍速改时间戳");
        Check.True(spedLine.Contains("atempo=2"), "声音用 atempo 跟速");
        Check.True(spedLine.Contains("-t 1.5"), "成品时长按倍速换算（3 秒剪成 2 倍 = 1.5 秒）");

        var silent = RecordTrim.ExportArgs("in.mp4", "out.mp4", cut, hasAudio: false);
        Check.True(string.Join(" ", silent).Contains("-an"), "没有音轨就明确 -an");

        // ---- 导出文件名：永远不覆盖原片 ----
        string dir = Path.Combine("C:\\v", "records");
        Check.Equal(Path.Combine(dir, "rec-裁剪.mp4"), RecordTrim.OutputPath(dir, "rec", _ => false),
            "默认是「原名字-裁剪」");
        Check.Equal(Path.Combine(dir, "rec-裁剪-2.mp4"),
            RecordTrim.OutputPath(dir, "rec", p => p.EndsWith("rec-裁剪.mp4", StringComparison.Ordinal)),
            "已经存在就加序号");

        // ---- ffmpeg 输出解析（老记录 / 外部文件才需要问）----
        const string info =
            "Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'x.mp4':\n" +
            "  Duration: 00:01:02.34, start: 0.000000, bitrate: 1200 kb/s\n" +
            "  Stream #0:0[0x1](und): Video: h264 (High), yuv420p, 1920x1080, 30 fps\n" +
            "  Stream #0:1[0x2](und): Audio: aac (LC), 48000 Hz, stereo, fltp, 192 kb/s";

        Check.Equal(62_340, RecordTrimSession.ParseDurationMs(info), "从 Duration 行读毫秒");
        Check.True(RecordTrimSession.HasAudioStream(info), "有 Audio 流");
        Check.False(RecordTrimSession.HasAudioStream(
            "  Stream #0:0[0x1](und): Video: h264 (High), yuv420p, 1920x1080, 30 fps"), "只有视频流");
        Check.Equal(0, RecordTrimSession.ParseDurationMs("garbage"), "读不出时长返回 0");
        Check.Equal(0, RecordTrimSession.ParseDurationMs("Duration: N/A, start: 0"), "N/A 也返回 0");
    }

    /// <summary>
    /// P1：与 Snow Shot 对齐的新标注（直线 / 测距 / 测角 / 括号 / 气泡 / 放大镜 / 水印 / 矩形荧光 / 画笔滤镜）
    /// 的几何与命中。**不碰窗口、不碰位图**，纯数据断言。
    /// </summary>
}
