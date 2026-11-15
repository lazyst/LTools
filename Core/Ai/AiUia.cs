using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using LTools.Features;
using LTools.Native;

namespace LTools.Core.Ai;

/// <summary>
/// MCP「控件树」工具的底层快照：把一个窗口的 UI Automation 子树摊平成一份带路径的清单。
/// </summary>
/// <remarks>
/// 为什么 agent 需要它（A4）：「文字点击」「鼠标操作」要知道「那个按钮叫什么 / 在哪儿」，
/// 而它看不见用户的屏幕，也不能模拟点一下试试。截图能看形状，控件树给的是**准确的名字与矩形**——
/// 把元素的 <c>name</c> 填进「文字点击」的文本，或把 <c>bounds</c> 中心当坐标，两条路都能走。
/// 这里不引入「按控件点击」这种新动作单元：动作单元是用户的东西，工具只提供观察能力。
/// </remarks>
internal static class AiUia
{
    private const int MaxDepth = 12;

    /// <summary>默认就走到上限：Chromium 类应用要 8–10 层才到真正的内容，浅了只会拿到一串空 Pane。真正的地板是 limit。</summary>
    private const int DefaultDepth = MaxDepth;
    private const int MaxElements = 1000;
    private const int DefaultElements = 300;

    /// <summary>每个父节点最多看多少个孩子（浏览器 / Electron 可能上千个）。</summary>
    private const int MaxChildren = 200;

    /// <summary>整棵树的墙上时间预算：慢应用（无响应的 UIA）不能把工具挂死。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private const int NameLimit = 120;
    private const int IdLimit = 80;

    private static readonly Lazy<UIA3Automation> Automation = new(() => new UIA3Automation
    {
        // 目标程序无响应时不要让这次调用挂住（与框选探测同一套超时）
        ConnectionTimeout = TimeSpan.FromMilliseconds(300),
        TransactionTimeout = TimeSpan.FromMilliseconds(400),
    });

    /// <summary>
    /// 共享的 UIA3 实例：<c>ui.uia</c> 动作单元复用，避免各建一条 COM 连接。
    /// <c>ltools_uia_tree</c> 本身只读，多一个消费者不改变它的语义。
    /// </summary>
    internal static UIA3Automation Uia => Automation.Value;

    /// <summary>
    /// 取一个窗口的控件树快照。不传窗口名就用前台窗口；找不到匹配的窗口也退回前台窗口（回执里写明）。
    /// </summary>
    /// <param name="title">窗口标题包含（空 = 不限）。</param>
    /// <param name="className">窗口类名（精确，空 = 不限）。</param>
    /// <param name="depth">要往下几层（≤ 0 = 用默认值 12，也就是上限）。</param>
    /// <param name="limit">最多返回多少个元素（≤ 0 = 用默认值 300）。</param>
    public static Dictionary<string, object?> Snapshot(string? title, string? className, int depth, int limit)
    {
        int maxDepth = Math.Clamp(depth <= 0 ? DefaultDepth : depth, 0, MaxDepth);
        int maxElements = Math.Clamp(limit <= 0 ? DefaultElements : limit, 1, MaxElements);

        var payload = new Dictionary<string, object?>();
        string? fallbackNote = null;

        IntPtr hwnd = ResolveWindow(title, className, out string matchedTitle, out bool fallback);
        if (hwnd == IntPtr.Zero)
            throw new AiToolException("not_found",
                "取不到窗口：没有一个可见窗口（传 title / className 指定，或不传用前台窗口）");

        if (fallback)
            fallbackNote = "按 title / className 没找到窗口，已改用前台窗口";

        payload["window"] = Describe(hwnd, matchedTitle);
        payload["maxDepth"] = maxDepth;
        payload["maxElements"] = maxElements;

        var elements = new List<Dictionary<string, object?>>(Math.Min(maxElements, 128));
        bool truncated = false;
        bool depthCut = false;

        try
        {
            AutomationElement? root = Automation.Value.FromHandle(hwnd);
            if (root == null)
                throw new AiToolException("uia_unavailable",
                    "这个窗口不暴露 UI Automation：可能是不支持 UIA 的自绘界面，或以更高权限运行" +
                    "（提权窗口需要以管理员运行 LTools）。可以改用 ltools_screenshot 看形状 + 「文字点击」。");

            var queue = new Queue<(AutomationElement Element, int Depth, string Path)>();
            queue.Enqueue((root, 0, "0"));
            var watch = Stopwatch.StartNew();

            while (queue.Count > 0)
            {
                if (elements.Count >= maxElements || watch.Elapsed > Budget)
                {
                    truncated = true;
                    break;
                }

                var (element, level, path) = queue.Dequeue();
                elements.Add(Describe(element, level, path));

                // 到了深度上限：真的还有孩子才算「被深度截断」，别让每个叶子都报 truncated。
                if (level >= maxDepth)
                {
                    if (SafeChildren(element).Length > 0) depthCut = true;
                    continue;
                }

                int seen = 0;
                foreach (var child in SafeChildren(element))
                {
                    if (++seen > MaxChildren)
                    {
                        truncated = true;
                        break;
                    }
                    queue.Enqueue((child, level + 1, $"{path}.{seen - 1}"));
                }
            }
        }
        catch (AiToolException) { throw; }
        catch (Exception ex)
        {
            throw new AiToolException("uia_unavailable",
                $"读控件树失败（{ex.GetType().Name}: {ex.Message}）。目标可能正在忙或无响应，" +
                "可以调小 depth，或改用 ltools_screenshot 看形状。");
        }

        payload["count"] = elements.Count;
        payload["truncated"] = truncated || depthCut;
        payload["elements"] = elements;

        var notes = new List<string>();
        if (fallbackNote != null) notes.Add(fallbackNote);
        if (depthCut) notes.Add($"到 depth = {maxDepth} 就停了；要更深就调大 depth（上限 {MaxDepth}）");
        if (truncated) notes.Add($"结果被 limit = {maxElements} 或时间预算截断；先调小 depth 或用 title 缩小范围");
        if (notes.Count > 0) payload["note"] = string.Join("；", notes);
        return payload;
    }

    /// <summary>解析目标窗口：给了筛选条件就精确找，找不到退回前台（并告诉调用方）。</summary>
    private static IntPtr ResolveWindow(string? title, string? className, out string matchedTitle,
        out bool fallback)
    {
        fallback = false;
        matchedTitle = "";

        bool filter = !string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(className);
        if (filter)
        {
            IntPtr found = WindowFinder.Find(title, className, out string matched);
            if (found != IntPtr.Zero)
            {
                matchedTitle = matched;
                return found;
            }
            fallback = true;
        }

        IntPtr foreground = Win32.GetForegroundWindow();
        if (foreground != IntPtr.Zero) matchedTitle = WindowFinder.GetText(foreground);
        return foreground;
    }

    /// <summary>窗口本身的信息：标题 / 类名 / 进程 / 屏幕矩形（DWM 可见范围）。</summary>
    private static Dictionary<string, object?> Describe(IntPtr hwnd, string title)
    {
        var (x, y, w, h) = WindowFinder.GetVisibleBounds(hwnd);

        return new Dictionary<string, object?>
        {
            ["title"] = title.Length > 0 ? title : WindowFinder.GetText(hwnd),
            ["class"] = WindowFinder.GetClass(hwnd),
            ["process"] = ProcessName(hwnd),
            ["handle"] = hwnd.ToInt64(),
            ["bounds"] = new Dictionary<string, object?>
            {
                ["left"] = x,
                ["top"] = y,
                ["width"] = w,
                ["height"] = h,
            },
        };
    }

    /// <summary>一个控件：路径 / 层级 / 控件类型 / 名字 / automationId / 类名 / 屏幕矩形 / 是否可用。</summary>
    private static Dictionary<string, object?> Describe(AutomationElement element, int depth, string path)
    {
        Rectangle bounds = Safe(() => element.Properties.BoundingRectangle.ValueOrDefault, Rectangle.Empty);
        string name = Shorten(Safe(() => element.Properties.Name.ValueOrDefault ?? "", ""), NameLimit);

        return new Dictionary<string, object?>
        {
            ["path"] = path,
            ["depth"] = depth,
            ["controlType"] = Safe(() => element.Properties.ControlType.ValueOrDefault.ToString(), "Custom"),
            ["name"] = name.Length > 0 ? name : null,
            ["automationId"] = Nullable(Safe(() => element.Properties.AutomationId.ValueOrDefault ?? "", ""), IdLimit),
            ["className"] = Nullable(Safe(() => element.Properties.ClassName.ValueOrDefault ?? "", ""), IdLimit),
            ["bounds"] = new Dictionary<string, object?>
            {
                ["left"] = bounds.X,
                ["top"] = bounds.Y,
                ["width"] = bounds.Width,
                ["height"] = bounds.Height,
            },
            ["enabled"] = Safe(() => element.Properties.IsEnabled.ValueOrDefault, false),
            ["offscreen"] = Safe(() => element.Properties.IsOffscreen.ValueOrDefault, false),
        };
    }

    private static AutomationElement[] SafeChildren(AutomationElement element)
    {
        try { return element.FindAllChildren(); }
        catch { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>单个属性的读取都可能超时 / 抛 COM 异常：坏一个属性不该让整棵树失败。</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }

    private static string ProcessName(IntPtr hwnd)
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return "";
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return ""; }
    }

    private static string? Nullable(string text, int max) =>
        text.Trim().Length == 0 ? null : Shorten(text.Trim(), max);

    private static string Shorten(string text, int max)
    {
        text = text.Trim().Replace("\r", " ").Replace("\n", " ");
        return text.Length <= max ? text : text[..max] + "…";
    }
}
