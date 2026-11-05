using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Views;

namespace LTools.Core;

/// <summary>
/// 提示弹窗（toast）的显示服务：右下角堆叠、自动消失、**不阻塞调用方**。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="MouseTip"/>（跟着鼠标、~1.8 秒的临时提示）分工不同：这里给用户看的是
/// 一段需要读的正文（可以带 <c>${变量}</c>），默认停留 4 秒、点一下就关，最多同时 4 条（多的挤掉最旧的）。
/// </para>
/// <para>
/// 可从任意线程调用（流程在后台线程）：内部统一投递到 UI 线程并立即返回，
/// 所以「提示弹窗」这一步不会拖住后面的步骤。
/// </para>
/// <para>
/// 位置取工作区右下角（主显示器，DIP）——与系统通知一致；自动化可能跑在任何窗口上，
/// 固定角落比跟随光标更不容易挡住正在操作的地方。
/// </para>
/// </remarks>
internal static class ToastService
{
    /// <summary>默认停留时长（ms）。</summary>
    public const int DefaultDurationMs = 4000;

    /// <summary>最多同时显示几条；超出时挤掉最旧的那条。</summary>
    private const int MaxVisible = 4;

    private const double MarginPx = 12;
    private const double GapPx = 8;

    private static readonly List<ToastWindow> Visible = new();

    /// <summary>显示一条提示（不阻塞；可从任意线程调用）。<paramref name="durationMs"/> ≤ 0 = 不自动关闭。</summary>
    public static void Show(string text, string? title = null, int durationMs = DefaultDurationMs)
    {
        if (string.IsNullOrEmpty(text)) return;

        var app = Application.Current;
        if (app == null) return;

        try
        {
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() => SafeShowCore(text, title, durationMs));
                return;
            }
            SafeShowCore(text, title, durationMs);
        }
        catch (Exception ex)
        {
            // 提示是辅助信息：显示失败（例如程序正在退出）不应该让流程那一步失败。
            AppLog.Warn("Toast", $"提示弹窗显示失败：{ex.Message}");
        }
    }

    private static void SafeShowCore(string text, string? title, int durationMs)
    {
        try { ShowCore(text, title, durationMs); }
        catch (Exception ex) { AppLog.Warn("Toast", $"提示弹窗显示失败：{ex.Message}"); }
    }

    private static void ShowCore(string text, string? title, int durationMs)
    {
        // 先把最旧的移出列表再让它淡出：Dismiss 是异步的，等它关闭会导致这里空转。
        while (Visible.Count >= MaxVisible)
        {
            var oldest = Visible[0];
            Visible.RemoveAt(0);
            oldest.Dismiss();
        }

        var toast = new ToastWindow(title, text);
        toast.Closed += (_, _) =>
        {
            Visible.Remove(toast);
            Reflow();
        };

        Visible.Add(toast);
        toast.Show();
        toast.UpdateLayout();   // SizeToContent=Height：先量出高度才能往上堆
        Reflow();
        toast.StartCountdown(durationMs);
    }

    /// <summary>从右下角往上重新排（最新的贴右下角）。</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Bottom - MarginPx;

        for (int i = Visible.Count - 1; i >= 0; i--)
        {
            var toast = Visible[i];
            double width = toast.ActualWidth > 0 ? toast.ActualWidth : toast.Width;
            double height = toast.ActualHeight > 0 ? toast.ActualHeight : 60;

            toast.Left = Math.Round(area.Right - MarginPx - width);
            bottom -= height;
            toast.Top = Math.Round(bottom);
            bottom -= GapPx;
        }
    }
}
