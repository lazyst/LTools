using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using LTools.Views;

namespace LTools.Core;

/// <summary>提示的语义等级，决定左侧色条的颜色（失败要用红，别和普通提示长一样）。</summary>
public enum ToastKind
{
    info,
    success,
    warning,
    error,
}

/// <summary>提示出现的位置（工作区坐标，多屏时用主显示器的工作区）。</summary>
public enum ToastPosition
{
    bottomRight,
    bottomLeft,
    bottomCenter,
    topRight,
    topLeft,
    topCenter,
}

/// <summary>
/// 提示弹窗（toast）的显示服务：堆叠、自动消失、**不阻塞调用方**。
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
/// 运行结果提示带 <see cref="ToastKind"/> 与可选的 <c>key</c>：同一个 key 的提示会**就地更新**
/// 并累加次数（循环里反复提示同一句话不会刷屏）；需要「打开日志 / 设置」这类后续动作时，
/// 用 <c>onClick</c> 给底部一个写明动作的按钮（<c>actionText</c>），**正文点击只关闭、不执行回调**——
/// 想关掉就一定能干净关掉。
/// </para>
/// </remarks>
internal static class ToastService
{
    /// <summary>默认停留时长（ms）。</summary>
    public const int DefaultDurationMs = 4000;

    /// <summary>失败提示默认常驻（等用户看完 / 点掉，下一次运行会替换它）。</summary>
    public const int StickyMs = 0;

    /// <summary>最多同时显示几条；超出时挤掉最旧的那条。</summary>
    private const int MaxVisible = 4;

    private const double MarginPx = 12;
    private const double GapPx = 8;

    private static readonly List<ToastWindow> Visible = new();

    /// <summary>按 key 去重：同一个 key 只留一条，内容就地换成最新的。</summary>
    private static readonly Dictionary<string, ToastWindow> ByKey = new(StringComparer.Ordinal);

    /// <summary>显示一条普通提示（不阻塞；可从任意线程调用）。<paramref name="durationMs"/> ≤ 0 = 不自动关闭。</summary>
    public static void Show(string text, string? title = null, int durationMs = DefaultDurationMs) =>
        Show(text, title, durationMs, ToastKind.info);

    /// <summary>显示一条带等级 / 位置 / 去重键 / 操作按钮的提示。</summary>
    /// <param name="durationMs">≤ 0 = 不自动关闭（失败提示用这个）。</param>
    /// <param name="key">去重键：同一个键的提示就地更新并累计次数。</param>
    /// <param name="onClick">底部主操作按钮执行什么（例如打开运行记录）。<b>正文点击不会执行它</b>。</param>
    /// <param name="actionText">主操作按钮上的文字（「查看日志」「打开键鼠宏设置」…）；为空用「查看详情」。</param>
    public static void Show(string text, string? title, int durationMs, ToastKind kind,
        string? key = null, Action? onClick = null, string? actionText = null,
        ToastPosition position = ToastPosition.bottomRight)
    {
        if (string.IsNullOrEmpty(text)) return;

        var app = Application.Current;
        if (app == null) return;

        try
        {
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(
                    () => SafeShowCore(text, title, durationMs, kind, key, onClick, actionText, position));
                return;
            }
            SafeShowCore(text, title, durationMs, kind, key, onClick, actionText, position);
        }
        catch (Exception ex)
        {
            // 提示是辅助信息：显示失败（例如程序正在退出）不应该让流程那一步失败。
            AppLog.Warn("Toast", $"提示弹窗显示失败：{ex.Message}");
        }
    }

    /// <summary>按 key 关掉一条提示（例如回放结束收起「按 X 停止」）。不存在的 key 什么也不做。</summary>
    public static void Close(string? key)
    {
        if (string.IsNullOrEmpty(key)) return;

        var app = Application.Current;
        if (app == null) return;

        try
        {
            if (!app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.BeginInvoke(() => CloseCore(key));
                return;
            }
            CloseCore(key);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Toast", $"关闭提示失败：{ex.Message}");
        }
    }

    private static void CloseCore(string key)
    {
        if (!ByKey.TryGetValue(key, out var toast)) return;
        ByKey.Remove(key);
        Visible.Remove(toast);
        toast.Dismiss();
        Reflow();
    }

    private static void SafeShowCore(string text, string? title, int durationMs, ToastKind kind,
        string? key, Action? onClick, string? actionText, ToastPosition position)
    {
        try { ShowCore(text, title, durationMs, kind, key, onClick, actionText, position); }
        catch (Exception ex) { AppLog.Warn("Toast", $"提示弹窗显示失败：{ex.Message}"); }
    }

    private static void ShowCore(string text, string? title, int durationMs, ToastKind kind,
        string? key, Action? onClick, string? actionText, ToastPosition position)
    {
        // 同一个 key：就地更新内容 + 次数，不新开一条（循环里的提示不会刷满屏幕）。
        if (!string.IsNullOrEmpty(key) && ByKey.TryGetValue(key, out var existing) && Visible.Contains(existing))
        {
            existing.SetContent(title, text, kind, onClick, actionText);
            existing.Bump();
            existing.StartCountdown(durationMs);
            Reflow();
            return;
        }

        // 先把最旧的移出列表再让它淡出：Dismiss 是异步的，等它关闭会导致这里空转。
        while (Visible.Count >= MaxVisible)
        {
            var oldest = Visible[0];
            Visible.RemoveAt(0);
            oldest.Dismiss();
        }

        var toast = new ToastWindow(title, text, kind, position)
        {
            ActionText = actionText ?? "",
            ClickAction = onClick,
        };
        toast.Closed += (_, _) =>
        {
            Visible.Remove(toast);
            if (!string.IsNullOrEmpty(key)
                && ByKey.TryGetValue(key, out var mapped)
                && ReferenceEquals(mapped, toast))
                ByKey.Remove(key);
            Reflow();
        };

        Visible.Add(toast);
        if (!string.IsNullOrEmpty(key)) ByKey[key] = toast;

        toast.Show();
        toast.UpdateLayout();   // SizeToContent=Height：先量出高度才能往上堆
        Reflow();
        toast.StartCountdown(durationMs);
    }

    /// <summary>按位置分组重新排版：右下 / 左下 / 底部居中往上堆，右上 / 左上 / 顶部居中往下堆。</summary>
    private static void Reflow()
    {
        var area = SystemParameters.WorkArea;

        foreach (var group in Visible.GroupBy(t => t.Position))
        {
            bool top = group.Key is ToastPosition.topLeft or ToastPosition.topCenter or ToastPosition.topRight;
            double edge = top ? area.Top + MarginPx : area.Bottom - MarginPx;
            var toasts = group.ToList();

            // 最新的贴边，旧的往外推（与原来的堆叠顺序一致）。
            for (int i = toasts.Count - 1; i >= 0; i--)
            {
                var toast = toasts[i];
                double width = toast.ActualWidth > 0 ? toast.ActualWidth : toast.Width;
                double height = toast.ActualHeight > 0 ? toast.ActualHeight : 60;

                toast.Left = Math.Round(group.Key switch
                {
                    ToastPosition.bottomLeft or ToastPosition.topLeft => area.Left + MarginPx,
                    ToastPosition.bottomCenter or ToastPosition.topCenter => area.Left + (area.Width - width) / 2,
                    _ => area.Right - MarginPx - width,
                });

                if (top)
                {
                    toast.Top = Math.Round(edge);
                    edge += height + GapPx;
                }
                else
                {
                    edge -= height;
                    toast.Top = Math.Round(edge);
                    edge -= GapPx;
                }
            }
        }
    }
}
