using System.Collections.Generic;
using System.Windows;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Views;

namespace LTools.Features.Units.Ui;

/// <summary>
/// 记事本式的文本窗口：显示文本 / 数据，或让用户改完读回（<c>ui.textWindow</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 与「提示弹窗」（小卡片、几秒就消失）和「输入框」（一行 / 一小块）分工不同：
/// 这里给的是**大窗口**——长文本、JSON、多行结果，可以拖动、最大化、全选复制。
/// </para>
/// <para>
/// 「显示」默认不阻塞（流程继续跑，窗口留给用户看）；勾「等窗口关闭」才等。
/// 「读取」是可编辑窗口，用户点「确定」后返回内容。两种模式都**不用模态对话框**：
/// 流程在后台线程轮询，停止热键照样能中止（<c>ui.input</c> 用的是 ShowDialog，停不下来）。
/// </para>
/// </remarks>
public sealed class TextWindowUnit : IStepUnit
{
    private static readonly IReadOnlyDictionary<string, string> ModeLabels =
        new Dictionary<string, string>
        {
            ["show"] = "显示",
            ["read"] = "读取",
            ["close"] = "关闭窗口",
        };

    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.textWindow",
        Name = "文本窗口",
        Category = "界面",
        Icon = "window",
        Description = "记事本式窗口：显示一段文本 / 数据，或让用户改完读回；同一「复用键」只开一个窗口",
        Inputs = new[]
        {
            new InputSpec("mode", "方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("show"),
                Options: new[] { "show", "read", "close" },
                Labels: ModeLabels,
                OptionParams: OptionMap.Of(
                    ("show", ["text", "append", "wait", "monospace", "title"]),
                    ("read", ["text", "monospace", "title"]),
                    ("close", [])),
                Description: "显示 = 只读窗口（默认不阻塞）；读取 = 可编辑，点「确定」后返回内容；关闭窗口 = 按复用键关掉"),
            new InputSpec("title", "标题", ValueKind.text,
                Description: "窗口标题；留空时显示方式用「文本窗口」、读取方式用「输入文本」"),
            new InputSpec("text", "内容", null, InputEditor.multiline,
                Description: "要显示的文本；列表按每行一项、对象按 JSON 展开。读取方式下是预填内容"),
            new InputSpec("key", "复用键", ValueKind.text,
                Description: "同一个键只开一个窗口，内容就地替换（循环里不会刷出一屏）；留空 = 每次新开。关闭窗口方式留空 = 关掉全部"),
            new InputSpec("append", "追加到末尾", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "复用已有窗口时把新内容接在后面（当运行日志 / 收集结果用）"),
            new InputSpec("wait", "等窗口关闭", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "勾上就等用户关掉窗口再继续（像 MessageBox）；不勾流程直接往下走"),
            new InputSpec("monospace", "等宽字体", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "JSON、对齐的数据用等宽字体更清楚"),
        },
        Outputs = new[]
        {
            new OutputSpec("text", "窗口内容", ValueKind.text, "text",
                Description: "显示：窗口里的完整内容；读取：用户确定的内容（取消为空）；关闭：空"),
            new OutputSpec("found", "已确定", ValueKind.@bool,
                Description: "读取：用户是否点了确定；显示 / 关闭：恒为真"),
            new OutputSpec("changed", "内容有改动", ValueKind.@bool,
                Description: "读取：用户是否改过预填内容"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var app = Application.Current
            ?? throw new ActionException("界面未初始化，无法打开文本窗口");

        string mode = args.Text("mode").Trim().ToLowerInvariant();
        string key = args.Text("key").Trim();
        string title = args.Text("title");

        if (mode == "close")
        {
            TextWindowService.Close(key);
            ctx.SetOutput("text", Value.FromText(""));
            ctx.SetOutput("found", Value.FromBool(true));
            ctx.SetOutput("changed", Value.FromBool(false));
            return;
        }

        bool read = mode == "read";
        bool monospace = args.Bool("monospace");
        bool append = !read && args.Bool("append");
        bool wait = !read && args.Bool("wait");
        string text = ListText.Display(args.TryGet("text"));

        // 显示模式不抢焦点（用户可能在别的窗口里继续操作），读取模式要抢（要打字）。
        TextViewWindow window = TextWindowService.Open(key, title, text, read, monospace, append, activate: read);

        // 显示且不等：把当前内容带回去就继续（读窗口内容必须回 UI 线程）。
        if (!read && !wait)
        {
            var current = app.Dispatcher.Invoke(() => window.Text);
            ctx.SetOutput("text", Value.FromText(current));
            ctx.SetOutput("found", Value.FromBool(true));
            ctx.SetOutput("changed", Value.FromBool(false));
            return;
        }

        // 等用户操作：轮询而不是 ShowDialog —— 模态对话框会卡住 UI 线程，停止热键就没法中止了。
        while (true)
        {
            var state = app.Dispatcher.Invoke(() =>
                (Closed: window.IsClosed, window.Confirmed, window.Text, window.Changed));

            if (state.Closed)
            {
                bool confirmed = read ? state.Confirmed : true;
                ctx.SetOutput("text", Value.FromText(confirmed ? state.Text : ""));
                ctx.SetOutput("found", Value.FromBool(confirmed));
                ctx.SetOutput("changed", Value.FromBool(read && state.Changed));
                return;
            }

            if (!ctx.Wait(50))
            {
                app.Dispatcher.Invoke(() => { if (!window.IsClosed) window.Close(); });
                throw new ActionAbortedException("已停止", userCancelled: true);
            }
        }
    }
}
