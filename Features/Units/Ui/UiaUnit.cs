using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Patterns;
using FlaUI.UIA3;
using LTools.Core.Actions;
using LTools.Core.Ai;
using LTools.Features;
using LTools.Native;

namespace LTools.Features.Units.Ui;

/// <summary>
/// 控件操作（<c>ui.uia</c>）：按 Name / AutomationId 找 UIA 控件，再对它做一个操作。
/// </summary>
/// <remarks>
/// <para>
/// 「文字点击」靠 OCR 看形状、「鼠标操作」靠坐标，两者都看不见控件本身。这里补的是
/// **准确的名字与 AutomationId**：树形遍历（限深 / 限量 / 每层上限 / 4 秒预算）按精确匹配找元素，
/// 找到后走项目统一的 <see cref="MouseOps"/> 注入鼠标，或用 UIA 自己的 ValuePattern 读 / 设值。
/// 没有「XPath 定位」，也没有在线服务。
/// </para>
/// <para>
/// 命中类而非等待类：等不到按 <see cref="ActionErrors.NotFound"/> 抛错（能配
/// <c>RetryOn = notFound</c> 重试），每轮 <c>ctx.EnterIteration()</c> + <c>ctx.Wait</c>，
/// 按停止热键立刻返回。UIA 在动作线程（MTA）直接调，**不包 <c>StaRunner</c>**（与
/// <c>ltools_uia_tree</c> 同一条已验证路径），并复用 <see cref="AiUia.Uia"/> 的共享
/// <c>UIA3Automation</c> 实例，不另建 COM 连接。
/// </para>
/// <para>
/// <c>action</c> 是「对同一个控件做的不同操作」——目标一致、失败语义一致（找不到即失败），
/// 用 <c>OptionParams</c> 收起只有「设值」才用到的 <c>value</c>；与 <c>file.open</c> 那种
/// 把三种打开目标混进一个 <c>operation</c> 的退役单元不是一回事。XPath 与 expand / toggle /
/// invoke 留到有真实需求再加。
/// </para>
/// </remarks>
public sealed class UiaUnit : IStepUnit
{
    private const int MaxDepth = 12;
    private const int MaxElements = 1000;
    private const int MaxChildren = 200;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private static readonly string[] Actions =
        { "click", "doubleClick", "rightClick", "getText", "setValue", "focus" };

    private static readonly Dictionary<string, string> ActionLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["click"] = "单击",
        ["doubleClick"] = "双击",
        ["rightClick"] = "右键",
        ["getText"] = "读文本",
        ["setValue"] = "设值",
        ["focus"] = "设焦点",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.uia",
        Name = "控件操作",
        Category = "界面",
        Icon = "window",
        Description = "按名称 / AutomationId 找控件并点击 / 读值 / 设值；找不到按「未找到」标性质",
        Inputs = new[]
        {
            new InputSpec("title", "窗口标题", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.inline | PickerKind.clear | PickerKind.window,
                Description: "标题包含；留空 = 前台窗口"),
            new InputSpec("className", "窗口类名", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.inline | PickerKind.clear | PickerKind.windowClass,
                Description: "类名精确；留空 = 不限"),
            new InputSpec("name", "控件名称", ValueKind.text,
                Description: "UIA Name，精确匹配（不区分大小写）；与 AutomationId 至少填一个"),
            new InputSpec("automationId", "AutomationId", ValueKind.text,
                Description: "UIA AutomationId，精确匹配；可用「控件树」工具查到"),
            new InputSpec("controlType", "控件类型", ValueKind.text,
                Description: "可选过滤，如 Button / Edit / Text；不区分大小写"),
            new InputSpec("action", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("click"),
                Options: Actions, Labels: ActionLabels,
                OptionParams: OptionMap.Of(
                    ("click", []),
                    ("doubleClick", []),
                    ("rightClick", []),
                    ("getText", []),
                    ("setValue", ["value"]),
                    ("focus", [])),
                Description: "对同一个控件做的不同操作；找不到一律按「未找到」失败"),
            new InputSpec("value", "要设的值", ValueKind.text,
                Description: "只在「设值」下生效"),
            new InputSpec("timeoutMs", "最多等待毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0),
                Description: "0 = 只找一次；-1 = 一直等到找到（按停止热键结束）；最多 10 分钟"),
            new InputSpec("intervalMs", "轮询间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(250), Advanced: true,
                Description: "两次查找之间隔多久（50–5000）；UIA 遍历本身可能要几百毫秒"),
            new InputSpec("depth", "最大深度", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(12), Advanced: true,
                Description: "控件树往下几层（1–12）；Chromium 类应用常要 8–10 层"),
            new InputSpec("limit", "最多元素", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(300), Advanced: true,
                Description: "单次查找最多看多少个控件（1–1000）"),
        },
        Outputs = new[]
        {
            new OutputSpec("found", "找到了", ValueKind.@bool, "uiaFound",
                Description: "是否找到控件；没找到整步按「未找到」失败，不会给假"),
            new OutputSpec("text", "控件文本", ValueKind.text, "uiaText",
                Description: "控件名称；「读文本」时优先给 ValuePattern 的值"),
            new OutputSpec("name", "控件名称", ValueKind.text,
                Description: "UIA Name"),
            new OutputSpec("automationId", "AutomationId", ValueKind.text,
                Description: "UIA AutomationId"),
            new OutputSpec("controlType", "控件类型", ValueKind.text,
                Description: "如 Button / Edit / Text"),
            new OutputSpec("x", "中心 X", ValueKind.number, "uiaX",
                Description: "控件矩形中心 X（屏幕物理像素，与截图 / 鼠标操作同源）"),
            new OutputSpec("y", "中心 Y", ValueKind.number, "uiaY",
                Description: "控件矩形中心 Y"),
            new OutputSpec("bounds", "矩形", ValueKind.@object,
                Description: "left / top / width / height（对象）"),
            new OutputSpec("count", "看过几个控件", ValueKind.number,
                Description: "这次查找遍历过的控件数，便于调 depth / limit"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string title = args.Text("title").Trim();
        string className = args.Text("className").Trim();
        string name = args.Text("name").Trim();
        string id = args.Text("automationId").Trim();
        string controlType = args.Text("controlType").Trim();
        if (name.Length == 0 && id.Length == 0)
            throw ActionErrors.Precondition("「控件名称」和「AutomationId」至少填一个");

        string action = args.Text("action").Trim().ToLowerInvariant();
        int timeout = Timeouts.Normalize(args.Number("timeoutMs"));
        long limit = Timeouts.Loop(timeout);
        int interval = (int)Math.Clamp(args.Number("intervalMs"), 50, 5_000);
        int depth = (int)Math.Clamp(args.Number("depth"), 1, MaxDepth);
        int maxElements = (int)Math.Clamp(args.Number("limit"), 1, MaxElements);

        long started = Environment.TickCount64;
        AutomationElement? element = null;
        int visited = 0;

        while (true)
        {
            ctx.EnterIteration();
            IntPtr hwnd = ResolveWindow(title, className);
            element = Find(AiUia.Uia, hwnd, name, id, controlType, depth, maxElements, out visited);
            if (element != null) break;

            long elapsed = Environment.TickCount64 - started;
            if (elapsed >= limit)
                throw ActionErrors.NotFound(
                    $"没找到控件（名称「{name}」/ AutomationId「{id}」）{Timeouts.DescribeWait(timeout)}");

            int slice = (int)Math.Min(interval, Math.Max(1, limit - elapsed));
            if (!ctx.Wait(slice)) throw new ActionAbortedException("已停止", userCancelled: true);
        }

        Rectangle bounds = Safe(() => element.Properties.BoundingRectangle.ValueOrDefault, Rectangle.Empty);
        string foundName = Safe(() => element.Properties.Name.ValueOrDefault ?? "", "");
        string foundId = Safe(() => element.Properties.AutomationId.ValueOrDefault ?? "", "");
        string foundType = Safe(() => element.Properties.ControlType.ValueOrDefault.ToString(), "");
        int cx = bounds.X + bounds.Width / 2;
        int cy = bounds.Y + bounds.Height / 2;
        string text = foundName;

        switch (action)
        {
            case "click":
            case "doubleclick":
            case "rightclick":
                MouseOps.Move(cx, cy);
                MouseOps.Perform(action switch
                {
                    "doubleclick" => MouseActionKind.doubleClick,
                    "rightclick" => MouseActionKind.rightClick,
                    _ => MouseActionKind.click,
                });
                break;

            case "focus":
                element.Focus();
                break;

            case "gettext":
            {
                IValuePattern? pattern = Safe(
                    () => element.Patterns.Value.PatternOrDefault, (IValuePattern?)null);
                if (pattern != null)
                    text = Safe(() => pattern.Value.ValueOrDefault ?? foundName, foundName);
                break;
            }

            case "setvalue":
            {
                IValuePattern? pattern = Safe(
                    () => element.Patterns.Value.PatternOrDefault, (IValuePattern?)null);
                if (pattern == null)
                    throw ActionErrors.Precondition(
                        "这个控件不支持「设值」（没有 ValuePattern）；改用「单击」+「发送文本」");
                if (Safe(() => pattern.IsReadOnly.ValueOrDefault, false))
                    throw ActionErrors.Precondition("这个控件是只读的，设不了值");
                pattern.SetValue(args.Text("value"));
                break;
            }

            default:
                throw ActionErrors.Precondition($"未知操作：{action}");
        }

        ctx.SetOutput("found", Value.FromBool(true));
        ctx.SetOutput("text", Value.FromText(text));
        ctx.SetOutput("name", Value.FromText(foundName));
        ctx.SetOutput("automationId", Value.FromText(foundId));
        ctx.SetOutput("controlType", Value.FromText(foundType));
        ctx.SetOutput("x", Value.FromNumber(cx));
        ctx.SetOutput("y", Value.FromNumber(cy));
        ctx.SetOutput("bounds", Value.Object(
            ("left", Value.FromNumber(bounds.X)),
            ("top", Value.FromNumber(bounds.Y)),
            ("width", Value.FromNumber(bounds.Width)),
            ("height", Value.FromNumber(bounds.Height))));
        ctx.SetOutput("count", Value.FromNumber(visited));
    }

    /// <summary>找窗口：给了筛选条件就精确找，找不到 / 没给就用前台窗口（照 <c>ltools_uia_tree</c>）。</summary>
    private static IntPtr ResolveWindow(string title, string className)
    {
        if (title.Length > 0 || className.Length > 0)
        {
            IntPtr found = WindowFinder.Find(title, className);
            if (found != IntPtr.Zero) return found;
        }
        return Win32.GetForegroundWindow();
    }

    /// <summary>BFS 遍历 UIA 子树，按 Name / AutomationId（+ 可选控件类型）精确匹配。</summary>
    private static AutomationElement? Find(UIA3Automation uia, IntPtr hwnd, string name, string id,
        string controlType, int depth, int limit, out int visited)
    {
        visited = 0;
        AutomationElement? root = Safe(() => uia.FromHandle(hwnd), (AutomationElement?)null);
        if (root == null) return null;

        var queue = new Queue<(AutomationElement Element, int Level)>();
        queue.Enqueue((root, 0));
        var watch = Stopwatch.StartNew();

        while (queue.Count > 0 && visited < limit && watch.Elapsed < Budget)
        {
            var (element, level) = queue.Dequeue();
            visited++;
            if (Matches(element, name, id, controlType)) return element;
            if (level >= depth) continue;

            int seen = 0;
            foreach (AutomationElement child in Children(element))
            {
                if (++seen > MaxChildren) break;
                queue.Enqueue((child, level + 1));
            }
        }
        return null;
    }

    /// <summary>至少要填了 name 或 id 才算「有定位条件」；两者都填时按 AND 匹配。</summary>
    private static bool Matches(AutomationElement element, string name, string id, string controlType)
    {
        bool keyed = false;
        if (name.Length > 0)
        {
            keyed = true;
            if (!string.Equals(Safe(() => element.Properties.Name.ValueOrDefault ?? "", ""), name,
                    StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (id.Length > 0)
        {
            keyed = true;
            if (!string.Equals(Safe(() => element.Properties.AutomationId.ValueOrDefault ?? "", ""), id,
                    StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (controlType.Length > 0 &&
            !string.Equals(Safe(() => element.Properties.ControlType.ValueOrDefault.ToString(), ""),
                controlType, StringComparison.OrdinalIgnoreCase)) return false;
        return keyed;
    }

    private static AutomationElement[] Children(AutomationElement element)
    {
        try { return element.FindAllChildren(); }
        catch { return Array.Empty<AutomationElement>(); }
    }

    /// <summary>单个 UIA 属性的读取都可能超时 / 抛 COM 异常：坏一个属性不该让整次查找失败。</summary>
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); } catch { return fallback; }
    }
}
