namespace LTools.Features.CapturePro;

/// <summary>
/// 「延时截图」的规则：等待秒数的取值范围与文案。
/// </summary>
/// <remarks>
/// 照 Snow Shot 的 <c>screenshotdelayedcapture</c> + <c>screenshot/delay_seconds</c>：
/// 秒数 clamp 1–10（默认 3），到点再弹框选；**没有倒计时窗口**，也不在等待期间占屏幕。
/// 等待本体走 <see cref="Wait"/>：分片睡 + 可取消，UI 线程与后台线程都能调。
/// </remarks>
internal static class CaptureDelay
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 10;
    public const int DefaultSeconds = 3;

    /// <summary>0 / 负数 / 空 = 默认 3 秒；其余 clamp 到 1–10。</summary>
    public static int Normalize(int? seconds)
    {
        int value = seconds ?? DefaultSeconds;
        if (value <= 0) value = DefaultSeconds;
        return System.Math.Clamp(value, MinSeconds, MaxSeconds);
    }

    /// <summary>「3 秒后截图」——托盘 / 快捷键条目与提示共用这一句。</summary>
    public static string Describe(int? seconds) => $"{Normalize(seconds)} 秒后截图";

    /// <summary>
    /// 等 <paramref name="seconds"/> 秒。返回 false = 期间被取消（调用方直接收摊，别再去截图）。
    /// </summary>
    /// <remarks>
    /// 100ms 一片：既能及时响应取消，也不会把线程睡成一整段不可打断的等待
    /// （停止热键 / 退出程序都靠这个粒度）。
    /// </remarks>
    public static bool Wait(int? seconds, System.Threading.CancellationToken token = default)
    {
        int remaining = Normalize(seconds) * 1000;
        while (remaining > 0)
        {
            if (token.IsCancellationRequested) return false;
            int slice = System.Math.Min(100, remaining);
            if (token.WaitHandle.WaitOne(slice)) return false;
            remaining -= slice;
        }

        return !token.IsCancellationRequested;
    }
}
