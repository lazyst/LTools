using System.Globalization;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>模拟按键：条目为 "ctrl+s" / "text:你好" / "sleep:200"。</summary>
public sealed class SendKeysUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "input.sendKeys",
        Name = "模拟按键",
        Category = "键鼠与输入",
        Icon = "keyboard",
        Description = "依次发送按键组合、短文本或等待；条目写法 ctrl+s / text:你好 / sleep:200",
        Inputs = new[]
        {
            new InputSpec("keys", "按键", Editor: InputEditor.multiline, Required: true,
                Description: "ctrl+s / text:文字 / sleep:200，每行一项"),
            new InputSpec("focusDelayMs", "焦点延时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(50), Advanced: true,
                Description: "发送前等多久，给目标窗口切到前台的时间"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        var items = KeyItems(args);
        if (items.Count == 0) throw new ActionException("模拟按键的条目为空");

        int delay = (int)Math.Clamp(args.Number("focusDelayMs"), 0, 10_000);
        if (delay > 0 && !ctx.Wait(delay))
            throw new ActionAbortedException("已停止", userCancelled: true);

        for (int i = 0; i < items.Count; i++)
        {
            string item = items[i].AsText().Trim();
            if (item.Length == 0) continue;

            if (item.StartsWith("sleep:", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(item[6..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out int ms) && ms > 0 && !ctx.Wait(ms))
                    throw new ActionAbortedException("已停止", userCancelled: true);
                continue;
            }

            if (item.StartsWith("text:", StringComparison.OrdinalIgnoreCase))
            {
                string text = item[5..];
                if (text.Length > 0) TextSender.Dispatch(text, SendTextMode.auto, "模拟按键");
                continue;
            }

            if (!KeyStroke.TryParse(item, out var vks, out string error))
                throw new ActionException($"第 {i + 1} 个按键无法解析「{item}」：{error}");

            InputHelper.Combo(vks.ToArray());
            if (i + 1 < items.Count) ctx.Wait(15);
        }
    }

    /// <summary>
    /// 按键条目：值本身是列表时直接取项；编辑器里填的多行文本按行拆分（去掉空行）。
    /// </summary>
    private static IReadOnlyList<Value> KeyItems(StepArgs args)
    {
        Value value = args.Get("keys");
        if (value.Kind == ValueKind.list) return value.Items;

        string text = value.AsText().Replace("\r\n", "\n").Replace('\r', '\n');
        return text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select(Value.FromText)
            .ToList();
    }
}

/// <summary>鼠标操作：移动 / 单击 / 双击 / 右键 / 中键 / 滚轮。</summary>
