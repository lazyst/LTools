using System;
using System.Globalization;

namespace LTools.Features.CapturePro;

/// <summary>
/// 录屏的声音增益（照 Snow Shot 的 <c>RecordingAudioGainPopover</c>）：−24…+24 dB、默认 0。
/// </summary>
/// <remarks>
/// <para>
/// 增益在收尾的 ffmpeg 里按轨套 <c>volume=NdB</c>（录制中拖动滑块改的是**整条音轨**，
/// 不像上游逐缓冲实时乘）；电平表要显示「处理后」的峰值，所以 <see cref="Factor"/> 把 dB 换成
/// 线性倍数，采集那边只留原始峰值（0–1 满刻度）。
/// </para>
/// <para>
/// 全部是纯函数：<c>--selftest</c> 直接断言夹取 / 文案 / 倍数 / 峰值换算。
/// </para>
/// </remarks>
internal static class RecordGain
{
    public const int MinDb = -24;
    public const int MaxDb = 24;
    public const int DefaultDb = 0;

    /// <summary>滑块 / 配置里的增益夹到 −24…+24。</summary>
    public static int Clamp(int db) => db < MinDb ? MinDb : db > MaxDb ? MaxDb : db;

    /// <summary>界面文案：<c>+3 dB</c> / <c>0 dB</c> / <c>-6 dB</c>。</summary>
    public static string Format(int db)
    {
        int value = Clamp(db);
        return value > 0
            ? "+" + value.ToString(CultureInfo.InvariantCulture) + " dB"
            : value.ToString(CultureInfo.InvariantCulture) + " dB";
    }

    /// <summary>dB → 线性倍数（0 dB = 1，+6 dB ≈ 2，−6 dB ≈ 0.5）。</summary>
    public static double Factor(int db) => Math.Pow(10, Clamp(db) / 20.0);

    /// <summary>ffmpeg <c>volume</c> 滤镜的增益写法（<c>3dB</c> / <c>-6dB</c>）。</summary>
    public static string FilterValue(int db) =>
        Clamp(db).ToString("0.####", CultureInfo.InvariantCulture) + "dB";

    /// <summary>
    /// 一段采集缓冲的峰值（0–1，满刻度 = 1）：WASAPI 环回常给 32 位浮点，麦克风多是 16 位整数，
    /// 8 / 24 / 32 位整数也一并认（设备格式不统一，别按一种写死）。
    /// </summary>
    public static float PeakOf(byte[] buffer, int count, int bitsPerSample, bool isFloat)
    {
        if (buffer == null || count <= 0) return 0f;

        int size = bitsPerSample / 8;
        if (size <= 0) return 0f;

        int limit = Math.Min(count, buffer.Length);
        float peak = 0f;

        for (int i = 0; i + size <= limit; i += size)
        {
            float value;
            if (isFloat && bitsPerSample == 32)
            {
                value = BitConverter.ToSingle(buffer, i);
            }
            else if (bitsPerSample == 16)
            {
                value = BitConverter.ToInt16(buffer, i) / 32768f;
            }
            else if (bitsPerSample == 24)
            {
                int sample = buffer[i] | (buffer[i + 1] << 8) | ((sbyte)buffer[i + 2] << 16);
                value = sample / 8388608f;
            }
            else if (bitsPerSample == 32)
            {
                value = BitConverter.ToInt32(buffer, i) / 2147483648f;
            }
            else if (bitsPerSample == 8)
            {
                value = (buffer[i] - 128) / 128f;
            }
            else
            {
                continue;
            }

            if (float.IsNaN(value) || float.IsInfinity(value)) continue;

            float magnitude = Math.Abs(value);
            if (magnitude > peak) peak = magnitude;
        }

        return Math.Min(1f, peak);
    }

    /// <summary>套上增益后的电平（用于电平表）：返回 0–1 的显示值和「削波」标记。</summary>
    public static (double Level, bool Clipping) Process(float rawPeak, int db)
    {
        if (rawPeak <= 0f || float.IsNaN(rawPeak)) return (0, false);

        double processed = rawPeak * Factor(db);
        return (Math.Min(1.0, processed), processed > 1.0);
    }
}
