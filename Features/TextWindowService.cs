using System;
using System.Collections.Generic;
using System.Windows;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 文本窗口的登记处：按 <c>key</c> 复用窗口、记住上次的位置尺寸、统一关窗。
/// </summary>
/// <remarks>
/// <para>
/// 与 <see cref="Core.ToastService"/> 同一套路：任意线程可调（内部投递到 UI 线程），
/// 窗口本身不是模态的——流程在后台线程轮询结果，停止热键照常有效。
/// </para>
/// <para>
/// 一个 key 只留一个窗口：循环里反复「显示」不会刷出一屏窗口，内容就地替换或追加。
/// key 留空 = 每次新开一个（各管各的）。
/// </para>
/// </remarks>
internal static class TextWindowService
{
    private static readonly Dictionary<string, TextViewWindow> ByKey = new(StringComparer.Ordinal);

    /// <summary>上次关闭时的位置尺寸（只在本次运行内记住）。</summary>
    private static Rect? _lastBounds;

    /// <summary>
    /// 打开（或复用）一个文本窗口。**必须在 UI 线程调用**（内部会再兜一次 Dispatcher）。
    /// </summary>
    /// <param name="key">复用键；空 = 每次新开。</param>
    /// <param name="editable">true = 读取模式（可编辑、有确定 / 取消）。</param>
    /// <param name="append">复用已有窗口时把内容接在末尾，而不是替换。</param>
    /// <param name="activate">是否抢焦点（读取模式要，显示模式不要）。</param>
    public static TextViewWindow Open(string? key, string title, string text,
        bool editable, bool monospace, bool append, bool activate)
    {
        var app = Application.Current
            ?? throw new InvalidOperationException("界面未初始化，无法打开文本窗口");
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Open(key, title, text, editable, monospace, append, activate));

        key = key?.Trim() ?? "";

        if (key.Length > 0 && ByKey.TryGetValue(key, out var existing) && !existing.IsClosed)
        {
            if (existing.IsEditable != editable)
            {
                // 显示窗口被当成读取窗口复用（或反过来）：行为差太多，重开一个更清楚。
                existing.Close();
            }
            else
            {
                if (append) existing.AppendContent(text);
                else existing.SetContent(text);
                if (!string.IsNullOrWhiteSpace(title)) existing.Title = title.Trim();
                if (activate) existing.Activate();
                return existing;
            }
        }

        var window = new TextViewWindow(title, text, editable, monospace, wrap: true, activate);
        ApplyBounds(window);

        window.Closing += (_, _) => RememberBounds(window);
        window.Closed += (_, _) =>
        {
            if (key.Length > 0 && ByKey.TryGetValue(key, out var mapped) && ReferenceEquals(mapped, window))
                ByKey.Remove(key);
        };

        if (key.Length > 0) ByKey[key] = window;
        window.Show();
        if (activate)
        {
            window.Activate();
            window.Focus();
        }
        return window;
    }

    /// <summary>按 key 关窗；key 为空时关掉全部文本窗口。</summary>
    public static void Close(string? key)
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            // 同步等它关完：下一步可能马上用同一个 key 重新打开，异步关会撞上还没关的旧窗口。
            app.Dispatcher.Invoke(() => Close(key));
            return;
        }

        string trimmed = key?.Trim() ?? "";
        if (trimmed.Length == 0) { CloseAll(); return; }

        if (ByKey.TryGetValue(trimmed, out var window))
        {
            ByKey.Remove(trimmed);
            if (!window.IsClosed) window.Close();
        }
    }

    /// <summary>关掉所有文本窗口（程序退出 / 停止动作时兜底）。</summary>
    public static void CloseAll()
    {
        var app = Application.Current;
        if (app == null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(CloseAll);
            return;
        }

        var windows = new List<TextViewWindow>(ByKey.Values);
        ByKey.Clear();
        foreach (var window in windows)
            if (!window.IsClosed) window.Close();
    }

    /// <summary>这个 key 现在有没有开着的窗口（冒烟 / 排查用）。</summary>
    public static bool IsOpen(string key)
    {
        var app = Application.Current;
        if (app == null || !app.Dispatcher.CheckAccess()) return false;
        return ByKey.TryGetValue(key?.Trim() ?? "", out var window) && !window.IsClosed;
    }

    private static void ApplyBounds(TextViewWindow window)
    {
        if (_lastBounds is not { } bounds || bounds.Width < 200 || bounds.Height < 150) return;

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = bounds.Width;
        window.Height = bounds.Height;
        window.Left = Clamp(bounds.Left, SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenWidth, bounds.Width);
        window.Top = Clamp(bounds.Top, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenHeight, bounds.Height);
    }

    /// <summary>把窗口夹回可见区：拔掉显示器 / 改了分辨率后别把窗口丢到屏幕外。</summary>
    private static double Clamp(double value, double origin, double size, double extent)
    {
        double max = origin + size - Math.Min(extent, 120);   // 至少留一截在屏幕里
        return Math.Min(Math.Max(value, origin - extent + 120), max);
    }

    private static void RememberBounds(TextViewWindow window)
    {
        Rect bounds = window.WindowState == WindowState.Normal
            ? new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight)
            : window.RestoreBounds;
        if (bounds.Width < 200 || bounds.Height < 150) return;
        _lastBounds = bounds;
    }
}
