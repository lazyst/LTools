using System;
using System.Collections.Generic;
using System.Globalization;

namespace LTools.Features.CapturePro;

/// <summary>渲染进度对话框的阶段（照上游 <c>SNOW_RECORDING_RENDER_STAGE_*</c>）。</summary>
internal enum RecordRenderStage
{
    /// <summary>准备导出：收尾音轨、摆好源文件。</summary>
    Prepare,

    /// <summary>正在渲染：重封装 / 动图转码 / 裁剪重编码。</summary>
    Render,

    /// <summary>收尾：登记进采集历史。</summary>
    Finalize,
}

/// <summary>渲染失败 / 取消之后用户选了什么（上游 <c>retry</c> / <c>keep</c> / <c>discard</c>）。</summary>
internal enum RecordRenderChoice
{
    None,
    Retry,
    Keep,
    Discard,
}

/// <summary>
/// 渲染进度对话框的一份状态（照上游 <c>RecordingRenderDialog</c> 的字段）：
/// 状态文案 / 进度 / 是否已停在「保留源文件」态都由它决定，界面只照着画。
/// </summary>
internal sealed class RecordRenderState
{
    /// <summary>0–100，只增不减（上游取 <c>max(percent, next)</c>）。</summary>
    public double Percent { get; set; }

    public RecordRenderStage Stage { get; set; } = RecordRenderStage.Prepare;

    /// <summary>渲染停了（失败或取消），等用户选重试 / 保留源文件 / 丢弃。</summary>
    public bool Retained { get; set; }

    /// <summary>用户按了「取消」，正在等渲染线程收手。</summary>
    public bool CancelRequested { get; set; }

    /// <summary>收尾 / 清理中：四个按钮都先禁用。</summary>
    public bool Busy { get; set; }

    /// <summary>渲染成功过（进度条转成成功色）。</summary>
    public bool TerminalSucceeded { get; set; }

    /// <summary>源文件所在目录（失败 / 取消时告诉用户东西在哪儿）。</summary>
    public string Path { get; set; } = "";

    /// <summary>失败原因（空 = 用户主动取消）。</summary>
    public string Error { get; set; } = "";

    public RecordRenderState Clone() => (RecordRenderState)MemberwiseClone();
}

/// <summary>
/// 渲染进度对话框的文案与规则（照 Snow Shot 的 <c>presentation/recording/recordingrenderdialog.cpp</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 全部是纯函数：<c>--selftest</c> 直接断言文案 / 按钮显隐 / 进度换算，界面只取值。
/// 上游的对话框锚在录制区域那块屏上、宽 500、没有标准按钮、关窗策略 Manual（关窗 = 保留源文件 /
/// 取消），这些常量也在这里。
/// </para>
/// <para>
/// 进度从 ffmpeg 的 <c>-progress pipe:1</c> 读：每块写 <c>out_time_us=…</c>，用它除以成品总时长
/// 就是百分比。<c>out_time_ms</c> 在 ffmpeg 里其实也是**微秒**（历史包袱），两者一个量纲。
/// </para>
/// </remarks>
internal static class RecordRender
{
    /// <summary>对话框宽度（上游 <c>setPreferredWidth(500)</c>）。</summary>
    public const int ModalWidth = 500;

    /// <summary>单条 ffmpeg 的超时：裁剪重编码可能很慢，给足 30 分钟。</summary>
    public const int StepTimeoutMs = 30 * 60 * 1000;

    public const string Title = "正在渲染录屏";
    public const string CancelText = "取消";
    public const string RetryText = "重试";
    public const string KeepText = "保留源文件";
    public const string DiscardText = "丢弃";

    /// <summary>百分比：夹在 0–100，并保证不倒退（上游 <c>max(percent, next)</c>）。</summary>
    public static double Advance(double current, double next)
    {
        double clamped = Math.Clamp(current, 0, 100);
        if (double.IsNaN(next) || double.IsInfinity(next)) return clamped;
        return Math.Max(clamped, Math.Clamp(next, 0, 100));
    }

    /// <summary>状态行（上游 <c>refresh()</c> 里那串三目）。</summary>
    public static string StatusText(RecordRenderState state)
    {
        if (state.Retained)
            return string.IsNullOrEmpty(state.Error) ? "已取消渲染" : "渲染失败";

        if (state.CancelRequested) return "正在取消渲染…";

        return state.Stage switch
        {
            RecordRenderStage.Prepare => "正在准备导出录屏…",
            RecordRenderStage.Finalize => "正在完成录屏…",
            _ => "正在渲染视频…",
        };
    }

    /// <summary>失败 / 取消时那段可选中的细节：原因（如果有）+ 源文件目录。</summary>
    public static string DetailsText(RecordRenderState state)
    {
        string error = string.IsNullOrEmpty(state.Error) ? "" : state.Error + "\n\n";
        return error + "源文件保留在：\n" + state.Path;
    }

    /// <summary>「取消」只在渲染中显示；失败 / 取消之后换成另外三个。</summary>
    public static bool ShowCancel(RecordRenderState state) => !state.Retained;

    public static bool ShowRetained(RecordRenderState state) => state.Retained;

    public static bool CancelEnabled(RecordRenderState state) =>
        !state.CancelRequested && !state.Busy && !state.TerminalSucceeded;

    public static bool RetainedEnabled(RecordRenderState state) => !state.Busy;

    /// <summary>细节区只在停下来的态显示（上游 <c>details->setVisible(retained)</c>）。</summary>
    public static bool ShowDetails(RecordRenderState state) => state.Retained;

    /// <summary>
    /// 读 ffmpeg <c>-progress</c> 的一行：认 <c>out_time_us</c> / <c>out_time_ms</c>（两者都是微秒）
    /// 和 <c>out_time=HH:MM:SS.ffffff</c>。认不出返回 false（<c>progress=continue</c> /
    /// <c>frame=…</c> 这类行直接跳过）。
    /// </summary>
    public static bool TryReadProgress(string? line, out long microseconds)
    {
        microseconds = 0;
        if (string.IsNullOrWhiteSpace(line)) return false;

        string text = line.Trim();

        if (text.StartsWith("out_time_us=", StringComparison.Ordinal)
            || text.StartsWith("out_time_ms=", StringComparison.Ordinal))
        {
            string number = text[(text.IndexOf('=') + 1)..].Trim();
            return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out microseconds);
        }

        if (text.StartsWith("out_time=", StringComparison.Ordinal))
        {
            string value = text[(text.IndexOf('=') + 1)..].Trim();
            if (value.StartsWith("N/A", StringComparison.OrdinalIgnoreCase)) return false;

            var parts = value.Split(':');
            if (parts.Length != 3) return false;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes)) return false;
            if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)) return false;

            microseconds = (long)Math.Round(((hours * 60 + minutes) * 60 + seconds) * 1_000_000);
            return true;
        }

        return false;
    }

    /// <summary>已渲染的微秒数 → 百分比（总时长未知时返回 0，交给上层的「只增不减」）。</summary>
    public static double PercentOf(long microseconds, double totalMs)
    {
        if (totalMs <= 0 || microseconds <= 0) return 0;

        double doneMs = microseconds / 1000.0;
        return Math.Clamp(doneMs / totalMs * 100.0, 0, 100);
    }

    /// <summary>
    /// 给一条 ffmpeg 命令挂上 <c>-progress pipe:1 -nostats</c>（全局选项，放最前面就行；
    /// 这样每条命令的进度都从 stdout 出来，不和 stderr 的错误混在一起）。
    /// </summary>
    public static IReadOnlyList<string> WithProgress(IReadOnlyList<string> args)
    {
        var list = new List<string>(args.Count + 3) { "-progress", "pipe:1", "-nostats" };
        list.AddRange(args);
        return list;
    }
}
