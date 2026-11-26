using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using LTools.Core.Ai;

namespace LTools.Features;

/// <summary>
/// 控件树里的一个控件（屏幕物理像素坐标）。
/// </summary>
/// <remarks>
/// <see cref="Parent"/> / <see cref="Path"/> 指回同一份快照里的父节点与路径（<c>0.2.1</c>）：
/// 选择器靠 <see cref="Parent"/> 往上抬到可交互的父级，控件树对话框靠 <see cref="Depth"/> 缩进。
/// </remarks>
public sealed record ControlTarget(
    Rectangle Bounds,
    string Name,
    string AutomationId,
    string ControlType,
    string ClassName,
    string Path,
    int Depth,
    int Parent,
    bool Enabled,
    bool Offscreen);

/// <summary>一次控件树快照：节点 + 截断情况 + 打不开的原因。</summary>
public sealed class ControlTree
{
    public static readonly ControlTree Empty = new(Array.Empty<ControlTarget>(), 0, 0, false, false, null);

    public ControlTree(IReadOnlyList<ControlTarget> nodes, int maxDepth, int maxElements,
        bool truncated, bool depthCut, string? problem)
    {
        Nodes = nodes;
        MaxDepth = maxDepth;
        MaxElements = maxElements;
        Truncated = truncated;
        DepthCut = depthCut;
        Problem = problem;
    }

    public IReadOnlyList<ControlTarget> Nodes { get; }

    public int MaxDepth { get; }

    public int MaxElements { get; }

    /// <summary>被「最多元素 / 每层上限 / 时间预算」截断：拿到的树不完整，但能用。</summary>
    public bool Truncated { get; }

    /// <summary>到深度上限就停了，但下面确实还有子节点。</summary>
    public bool DepthCut { get; }

    /// <summary>打不开 / 读失败的原因（可读的一句话）；null = 正常拿到（可能为空）。</summary>
    public string? Problem { get; }

    public bool Unavailable => Problem != null;

    public int Count => Nodes.Count;
}

/// <summary>
/// 按窗口取一棵 UI Automation 控件树，并提供「光标下是哪个控件」「该选哪个父级」「写哪几个字段」这些纯判断。
/// </summary>
/// <remarks>
/// <para>
/// 两份消费者：编辑器里的「选控件…」点选器 / 控件树对话框，以及 MCP 的 <c>ltools_uia_tree</c>
/// （<see cref="AiUia"/> 只把这里的结果翻成 JSON）。**遍历只此一份**——限深 / 限量 / 每层上限 /
/// 时间预算 / 单个属性读失败不让整棵树失败，这几条谁用都一样。
/// </para>
/// <para>
/// 定位语义与 <c>ui.uia</c> 一致：名称 + AutomationId + 控件类型按 AND 精确匹配（不区分大小写）。
/// 点选器会写回「名称 + AutomationId」两个（用户选的口径），所以「同名」判断也按同一套 AND 语义算，
/// 不能只比名称——那样会把「两个『确定』但 id 不同」误报成冲突。
/// </para>
/// </remarks>
internal static class ControlTargets
{
    /// <summary>与 <c>ui.uia</c> / <c>ltools_uia_tree</c> 同一个深度上限：Chromium 类应用常要 8–10 层。</summary>
    public const int MaxDepth = 12;

    public const int MaxElements = 1000;

    /// <summary>每个父节点最多看多少个孩子（浏览器 / Electron 可能上千个）。</summary>
    private const int MaxChildren = 200;

    /// <summary>整棵树的墙上时间预算：慢应用（无响应的 UIA）不能把点选器挂死。</summary>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    /// <summary>
    /// 「可交互」的控件类型：光标落在它们里面的文字 / 图片上时，点选器往上抬到这一层
    /// （点文字中心也能点到按钮，但「设值」只有 Edit 能做）。
    /// </summary>
    private static readonly HashSet<string> Interactive = new(StringComparer.OrdinalIgnoreCase)
    {
        "Button", "CheckBox", "ComboBox", "DataItem", "Edit", "Hyperlink", "ListItem",
        "MenuItem", "RadioButton", "Slider", "Spinner", "SplitButton", "TabItem", "TreeItem",
    };

    /// <summary>取一个窗口的控件树。<paramref name="depth"/> / <paramref name="limit"/> ≤ 0 = 用上限。</summary>
    public static ControlTree Snapshot(IntPtr hwnd, int depth = MaxDepth, int limit = MaxElements)
    {
        int maxDepth = Math.Clamp(depth <= 0 ? MaxDepth : depth, 0, MaxDepth);
        int maxElements = Math.Clamp(limit <= 0 ? MaxElements : limit, 1, MaxElements);
        var nodes = new List<ControlTarget>(Math.Min(maxElements, 256));

        if (hwnd == IntPtr.Zero)
            return new ControlTree(nodes, maxDepth, maxElements, false, false, "没有目标窗口");

        AutomationElement? root;
        try
        {
            root = AiUia.Uia.FromHandle(hwnd);
        }
        catch (Exception ex)
        {
            return new ControlTree(nodes, maxDepth, maxElements, false, false,
                $"读控件树失败（{ex.GetType().Name}: {ex.Message}）。目标可能正在忙或无响应。");
        }

        if (root == null)
            return new ControlTree(nodes, maxDepth, maxElements, false, false,
                "这个窗口不暴露 UI Automation：可能是不支持 UIA 的自绘界面，或以更高权限运行" +
                "（提权窗口需要以管理员运行 LTools）。");

        bool truncated = false;
        bool depthCut = false;
        var queue = new Queue<(AutomationElement Element, int Depth, string Path, int Parent)>();
        queue.Enqueue((root, 0, "0", -1));
        var watch = Stopwatch.StartNew();

        // 注：FlaUI 的 CacheRequest 在这条路上救不了场（实测：挂上之后 AutomationId 读出来是空的），
        // 所以照旧逐个读属性。真要做到「每节点一次调用」得自己在 UIA COM 层用
        // FindAllBuildCache 自己建树——这里不做，全量快照已经只在打开对话框时才跑。
        while (queue.Count > 0)
        {
            if (nodes.Count >= maxElements || watch.Elapsed > Budget)
            {
                truncated = true;
                break;
            }

            var (element, level, path, parent) = queue.Dequeue();
            int index = nodes.Count;
            nodes.Add(Describe(element, level, path, parent));

            // 到了深度上限：真的还有孩子才算「被深度截断」，别让每个叶子都报 truncated。
            if (level >= maxDepth)
            {
                if (SafeChildren(element).Length > 0) depthCut = true;
                continue;
            }

            int seen = 0;
            foreach (AutomationElement child in SafeChildren(element))
            {
                if (++seen > MaxChildren)
                {
                    truncated = true;
                    break;
                }

                queue.Enqueue((child, level + 1, $"{path}.{seen - 1}", index));
            }
        }

        return new ControlTree(nodes, maxDepth, maxElements, truncated, depthCut, null);
    }

    // ============ 纯判断（--selftest 直接断言） ============

    /// <summary>
    /// 光标下最深的那个控件（同一个点被父子同时覆盖时取更深、面积更小的那个）；
    /// 没有 / 越界 / 只落在离屏元素上时返回 -1。
    /// </summary>
    public static int AtPoint(IReadOnlyList<ControlTarget> nodes, Point point)
    {
        int best = -1;
        int bestDepth = -1;
        long bestArea = long.MaxValue;

        for (int i = 0; i < nodes.Count; i++)
        {
            ControlTarget node = nodes[i];
            if (node.Offscreen) continue;
            if (node.Bounds.Width <= 0 || node.Bounds.Height <= 0) continue;
            if (!node.Bounds.Contains(point)) continue;

            long area = (long)node.Bounds.Width * node.Bounds.Height;
            if (node.Depth > bestDepth || (node.Depth == bestDepth && area < bestArea))
            {
                best = i;
                bestDepth = node.Depth;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>
    /// 往上抬到最近的可交互控件（自己就是可交互时原样返回）；一路都不是就返回**原来那个**——
    /// 点文字中心同样点得到，别把目标换成整块窗口。
    /// </summary>
    public static int RollUp(IReadOnlyList<ControlTarget> nodes, int index)
    {
        if (index < 0 || index >= nodes.Count) return index;

        int original = index;
        for (int guard = 0; guard < 64 && index >= 0 && index < nodes.Count; guard++)
        {
            if (IsInteractive(nodes[index].ControlType)) return index;
            index = nodes[index].Parent;
        }

        return original;
    }

    /// <summary>这个控件能不能作为 <c>ui.uia</c> 的目标：名称与 AutomationId 至少要有一个。</summary>
    public static bool CanTarget(ControlTarget node) =>
        node.Name.Trim().Length > 0 || node.AutomationId.Trim().Length > 0;

    /// <summary>
    /// 从「光标下那条支路」（深 → 浅，最后一条是窗口本身）里挑目标：跳过窗口，从最深往上找
    /// 第一个可交互控件（点文字中心也点得到按钮，但「设值」只有 Edit 能做）；一路都不是可交互的，
    /// 就退回最深那一层（点文字中心同样点得到）。返回下标，-1 = 没有可用目标。
    /// </summary>
    public static int PickOnPath(IReadOnlyList<ControlTarget> path)
    {
        // 末尾的窗口本身不当目标（点整扇窗口没有意义）；路径没带窗口时按整条算
        int end = path.Count;
        while (end > 0 && path[end - 1].ControlType.Equals("Window", StringComparison.OrdinalIgnoreCase))
            end--;

        if (end <= 0) return -1;

        int fallback = -1;
        for (int i = 0; i < end; i++)
        {
            ControlTarget node = path[i];
            if (node.Bounds.Width <= 0 || node.Bounds.Height <= 0) continue;   // UIA 偶发的 0 尺寸占位元素

            if (IsInteractive(node.ControlType)) return i;
            if (fallback < 0) fallback = i;
        }

        return fallback;
    }

    public static bool IsInteractive(string controlType) => Interactive.Contains(controlType ?? "");

    /// <summary>
    /// 窗口里还有几个控件会被同样一条定位条件命中（含它自己）。只比名称会误报，
    /// 所以按单元的真实匹配规则（非空的名称 / id / 类型全部 AND）算。
    /// </summary>
    public static int CountSame(IReadOnlyList<ControlTarget> nodes, ControlTarget target)
    {
        string name = target.Name.Trim();
        string id = target.AutomationId.Trim();
        string type = target.ControlType.Trim();
        if (name.Length == 0 && id.Length == 0) return 1;

        int count = 0;
        foreach (ControlTarget node in nodes)
        {
            if (name.Length > 0 && !string.Equals(node.Name.Trim(), name, StringComparison.OrdinalIgnoreCase))
                continue;
            if (id.Length > 0 && !string.Equals(node.AutomationId.Trim(), id, StringComparison.OrdinalIgnoreCase))
                continue;
            if (type.Length > 0 && !string.Equals(node.ControlType.Trim(), type, StringComparison.OrdinalIgnoreCase))
                continue;
            count++;
        }

        return Math.Max(1, count);
    }

    /// <summary>控件树对话框的搜索：名称 / AutomationId / 类型 / 类名，包含即命中（不区分大小写）。</summary>
    public static IReadOnlyList<ControlTarget> Filter(IReadOnlyList<ControlTarget> nodes, string? query)
    {
        string text = (query ?? "").Trim();
        if (text.Length == 0) return nodes;

        return nodes.Where(node =>
                node.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                node.AutomationId.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                node.ControlType.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                node.ClassName.Contains(text, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>信息条 / 列表的标题：有名字用名字，没有就写「(无名称)」。</summary>
    public static string TitleOf(ControlTarget node) =>
        node.Name.Trim().Length > 0 ? node.Name.Trim() : "(无名称)";

    /// <summary>副标题：AutomationId · 类名 · 尺寸（都没有时给空串）。</summary>
    public static string DetailOf(ControlTarget node)
    {
        var parts = new List<string>(3);
        if (node.AutomationId.Trim().Length > 0) parts.Add("id: " + node.AutomationId.Trim());
        if (node.ClassName.Trim().Length > 0) parts.Add("class: " + node.ClassName.Trim());
        if (node.Bounds.Width > 0 && node.Bounds.Height > 0)
            parts.Add($"{node.Bounds.Width} × {node.Bounds.Height}");
        return string.Join(" · ", parts);
    }

    // ============ 内部 ============

    /// <summary>
    /// 一个节点 + 它的身份。深度 / 路径 / 父级只有整棵快照知道，悬停那条支路用默认值（0 / "" / -1），
    /// 位置信息由调用方按支路顺序补。
    /// </summary>
    internal static ControlTarget Describe(AutomationElement element, int depth = 0, string path = "", int parent = -1) =>
        new(Safe(() => element.Properties.BoundingRectangle.ValueOrDefault, Rectangle.Empty),
            ReadName(element),
            ReadAutomationId(element),
            ReadControlType(element, "Custom"),
            ReadClassName(element),
            path,
            depth,
            parent,
            Safe(() => element.Properties.IsEnabled.ValueOrDefault, false),
            Safe(() => element.Properties.IsOffscreen.ValueOrDefault, false));

    internal static Rectangle ReadBounds(AutomationElement element) =>
        Safe(() => element.Properties.BoundingRectangle.ValueOrDefault, Rectangle.Empty);

    internal static bool ReadOffscreen(AutomationElement element) =>
        Safe(() => element.Properties.IsOffscreen.ValueOrDefault, false);

    internal static string ReadName(AutomationElement element) =>
        Text(() => element.Properties.Name.ValueOrDefault);

    internal static string ReadAutomationId(AutomationElement element) =>
        Text(() => element.Properties.AutomationId.ValueOrDefault);

    internal static string ReadControlType(AutomationElement element, string fallback = "") =>
        Text(() => element.Properties.ControlType.ValueOrDefault.ToString(), fallback);

    internal static string ReadClassName(AutomationElement element) =>
        Text(() => element.Properties.ClassName.ValueOrDefault);

    /// <summary>缓存里读到的一次身份（<c>UiaSnapSource</c> 用它，不再逐个属性跨进程读）。</summary>
    internal static ControlTarget DescribeCached(Rectangle bounds, string name, string automationId,
        string controlType, string className, bool enabled, bool offscreen) =>
        new(bounds, Tidy(name), Tidy(automationId), Text(() => controlType, "Custom"), Tidy(className),
            "", 0, -1, enabled, offscreen);

    /// <summary>
    /// UIA 的控件类型号 → 英文名（<c>50000</c> = Button …）。缓存请求拿到的是号，
    /// 而参数 / 选择器 / <see cref="IsInteractive"/> 用的都是 FlaUI 那套名字（<c>Button</c> / <c>TabItem</c>）。
    /// </summary>
    internal static string TypeName(int uiaControlType) => uiaControlType switch
    {
        50000 => "Button",
        50001 => "Calendar",
        50002 => "CheckBox",
        50003 => "ComboBox",
        50004 => "Edit",
        50005 => "Hyperlink",
        50006 => "Image",
        50007 => "ListItem",
        50008 => "List",
        50009 => "Menu",
        50010 => "MenuBar",
        50011 => "MenuItem",
        50012 => "ProgressBar",
        50013 => "RadioButton",
        50014 => "ScrollBar",
        50015 => "Slider",
        50016 => "Spinner",
        50017 => "StatusBar",
        50018 => "Tab",
        50019 => "TabItem",
        50020 => "Text",
        50021 => "ToolBar",
        50022 => "ToolTip",
        50023 => "Tree",
        50024 => "TreeItem",
        50025 => "Custom",
        50026 => "Group",
        50027 => "Thumb",
        50028 => "DataGrid",
        50029 => "DataItem",
        50030 => "Document",
        50031 => "SplitButton",
        50032 => "Window",
        50033 => "Pane",
        50034 => "Header",
        50035 => "HeaderItem",
        50036 => "Table",
        50037 => "TitleBar",
        50038 => "Separator",
        50039 => "SemanticZoom",
        50040 => "AppBar",
        _ => "Custom",
    };

    /// <summary>控件类型的中文名（悬停标签用；英文名走 <see cref="ReadControlType"/>）。</summary>
    internal static string KindLabel(string controlType) => controlType switch
    {
        "Button" => "按钮",
        "Edit" => "输入框",
        "Text" => "文本",
        "Document" => "文档",
        "List" => "列表",
        "ListItem" => "列表项",
        "Menu" => "菜单",
        "MenuItem" => "菜单项",
        "Tab" => "选项卡",
        "TabItem" => "选项卡",
        "Tree" => "树",
        "TreeItem" => "树节点",
        "ComboBox" => "下拉框",
        "CheckBox" => "复选框",
        "RadioButton" => "单选框",
        "Slider" => "滑块",
        "ToolBar" => "工具栏",
        "StatusBar" => "状态栏",
        "Pane" => "面板",
        "Group" => "分组",
        "Window" => "窗口",
        "Image" => "图片",
        "Hyperlink" => "链接",
        "Table" => "表格",
        "DataGrid" => "表格",
        "DataItem" => "表格行",
        "Custom" => "控件",
        _ => "元素",
    };

    /// <summary>
    /// 「类型 · 名称」这种一行的标签（支路去重 / 日志用；卡片自己拼更细的文案）。
    /// </summary>
    internal static string SnapLabel(string controlType, string name)
    {
        string kind = KindLabel(controlType);
        string trimmed = Tidy(name);
        return trimmed.Length == 0 ? kind : $"{kind} · {trimmed}";
    }

    private static string Tidy(string? value) =>
        (value ?? "").Trim().Replace("\r", " ").Replace("\n", " ");

    private static AutomationElement[] SafeChildren(AutomationElement element)
    {
        try { return element.FindAllChildren(); }
        catch { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>单个属性读失败不该毁掉整棵树；名称 / id 里的换行压成空格（写进单行框）。</summary>
    private static string Text(Func<string?> read, string fallback = "")
    {
        try
        {
            string value = (read() ?? "").Trim().Replace("\r", " ").Replace("\n", " ");
            return value.Length > 0 ? value : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}

/// <summary>点选 / 控件树选完之后交给编辑器的结果：写进哪几个字段由调用方决定。</summary>
public sealed record PickedControl(
    string Name,
    string AutomationId,
    string ControlType,
    string WindowClass,
    string WindowTitle);
