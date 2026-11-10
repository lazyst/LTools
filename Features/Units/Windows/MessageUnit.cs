using System.Globalization;
using LTools.Core.Actions;
using LTools.Features;
using LTools.Native;

namespace LTools.Features.Units.Windows;

/// <summary>
/// 向窗口发 Windows 消息（<c>window.message</c>）：关窗口、通知重绘、发自定义消息。
/// </summary>
/// <remarks>
/// <para>
/// 「窗口操作」只会切前台 / 最大化 / 改尺寸这类标准动作；有些程序只认消息（例如
/// <c>WM_CLOSE</c> 才算正常退出，<c>WM_COMMAND</c> 才能触发菜单）。这里补上这一层。
/// </para>
/// <para>
/// 默认走 <c>SendMessageTimeout</c>（带超时、不卡死），目标无响应时这一步失败；
/// 选「投递」则 <c>PostMessage</c> 发完就返回，目标卡死也不影响流程。
/// </para>
/// </remarks>
public sealed class MessageUnit : IStepUnit
{
    private static readonly string[] Operations = { "send", "post", "close" };

    private static readonly Dictionary<string, string> OperationLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["send"] = "发消息并等返回",
        ["post"] = "投递消息（不等）",
        ["close"] = "关闭窗口（WM_CLOSE）",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "window.message",
        Name = "窗口消息",
        Category = "窗口与程序",
        Icon = "send",
        Description = "向指定窗口发 Windows 消息（关窗口 / 自定义消息）；超时不会卡住流程",
        Inputs = new[]
        {
            new InputSpec("operation", "方式", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("send"), Options: Operations, Labels: OperationLabels),
            new InputSpec("title", "窗口标题", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.window | PickerKind.inline | PickerKind.clear,
                Description: "按标题包含匹配，取最上面那个；留空则只看类名"),
            new InputSpec("class", "窗口类名", ValueKind.text,
                Pickers: PickerKind.variable | PickerKind.windowClass | PickerKind.inline | PickerKind.clear,
                Description: "精确匹配；标题和类名都填时两个都要满足"),
            new InputSpec("message", "消息号", ValueKind.text,
                VisibleWhen: "operation == 'send' || operation == 'post'",
                Default: Value.FromText("0x0010"),
                Description: "十进制或 0x 开头的十六进制；0x0010 = WM_CLOSE，0x000F = WM_PAINT"),
            new InputSpec("wParam", "wParam", ValueKind.text,
                VisibleWhen: "operation == 'send' || operation == 'post'",
                Default: Value.FromText("0"), Description: "同样支持十进制 / 0x 十六进制"),
            new InputSpec("lParam", "lParam", ValueKind.text,
                VisibleWhen: "operation == 'send' || operation == 'post'",
                Default: Value.FromText("0"), Description: "同样支持十进制 / 0x 十六进制"),
            new InputSpec("timeoutMs", "超时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(1000), Advanced: true,
                VisibleWhen: "operation == 'send'",
                Description: "目标没响应就放弃并让这一步失败（投递方式不需要）"),
        },
        Outputs = new[]
        {
            new OutputSpec("sent", "已发出", ValueKind.@bool, Description: "消息是否成功交给目标窗口"),
            new OutputSpec("result", "返回值", ValueKind.number,
                Description: "SendMessage 的返回值（窗口自己决定含义）；投递 / 关闭为 0"),
            new OutputSpec("title", "窗口标题", ValueKind.text,
                Description: "实际命中的窗口标题"),
            new OutputSpec("hwnd", "窗口句柄", ValueKind.number,
                Description: "命中的窗口句柄（排查时有用）"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string title = args.Text("title").Trim();
        string className = args.Text("class").Trim();
        if (title.Length == 0 && className.Length == 0)
            throw new ActionException("窗口标题和类名至少要填一个");

        IntPtr hwnd = WindowFinder.Find(title, className, out string matched);
        if (hwnd == IntPtr.Zero)
            throw new ActionException($"没找到窗口：{WindowFinder.Describe(title, className)}");

        string op = args.Text("operation").Trim().ToLowerInvariant();
        bool sent;
        long result = 0;

        switch (op)
        {
            case "close":
                sent = Win32.PostMessage(hwnd, Win32.WmClose, IntPtr.Zero, IntPtr.Zero);
                break;

            case "post":
                sent = Win32.PostMessage(hwnd, Message(args, "message"),
                    new IntPtr(Number(args, "wParam")), new IntPtr(Number(args, "lParam")));
                break;

            case "send":
            {
                uint timeout = (uint)Math.Clamp(args.Number("timeoutMs"), 100, 60_000);
                IntPtr ok = Win32.SendMessageTimeout(hwnd, Message(args, "message"),
                    new IntPtr(Number(args, "wParam")), new IntPtr(Number(args, "lParam")),
                    Win32.SmtoAbortIfHung, timeout, out IntPtr returned);

                if (ok == IntPtr.Zero)
                    throw new ActionException($"窗口「{matched}」{timeout} ms 内没有响应");

                sent = true;
                result = returned.ToInt64();
                break;
            }

            default:
                throw new ActionException($"未知方式：{op}");
        }

        if (!sent) throw new ActionException($"消息没能发给窗口「{matched}」");

        ctx.SetOutput("sent", Value.FromBool(true));
        ctx.SetOutput("result", Value.FromNumber(result));
        ctx.SetOutput("title", Value.FromText(matched));
        ctx.SetOutput("hwnd", Value.FromNumber(hwnd.ToInt64()));
    }

    private static uint Message(StepArgs args, string key) => (uint)Number(args, key);

    /// <summary>解析消息号 / 参数：允许 <c>0x</c> 十六进制与十进制，负数按无符号回绕（-1 → 0xFFFFFFFF）。</summary>
    private static long Number(StepArgs args, string key)
    {
        string text = args.Text(key).Trim();
        if (text.Length == 0) return 0;

        bool negative = text.StartsWith('-');
        if (negative) text = text[1..];

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
                throw new ActionException($"{key} 不是合法的十六进制：{text}");
            return negative ? -hex : hex;
        }

        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            throw new ActionException($"{key} 不是数字：{text}");
        return value;
    }
}
