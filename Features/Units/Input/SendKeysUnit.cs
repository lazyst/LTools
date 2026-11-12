using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Input;

/// <summary>
/// 模拟按键：选一个键（分组下拉）+ 一个动作（短按 / 按下 / 抬起），可带修饰键。
/// </summary>
/// <remarks>
/// <para>
/// 只做「按键」这一件事：文本归「发送文本」，等待归「延迟」——旧版的多行条目
/// （<c>text:…</c> / <c>sleep:…</c>）与「发送文本」重叠，已删除。
/// </para>
/// <para>
/// 组合键一步就能表达：修饰键多选 + 短按主键 = <c>Ctrl+S</c>；需要「按住不放」时
/// 用「按下 / 抬起」两个步骤（先按下 Ctrl，再短按别的键，最后抬起 Ctrl）。
/// </para>
/// </remarks>
public sealed class SendKeysUnit : IStepUnit
{
    private static readonly string[] Actions = { "tap", "down", "up" };
    private static readonly string[] ModifierOptions = { "ctrl", "shift", "alt", "win" };

    private static readonly IReadOnlyDictionary<string, string> ActionLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["tap"] = "短按",
            ["down"] = "按下",
            ["up"] = "抬起",
        };

    private static readonly IReadOnlyDictionary<string, string> ModifierLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ctrl"] = "Ctrl",
            ["shift"] = "Shift",
            ["alt"] = "Alt",
            ["win"] = "Win",
        };

    public UnitSpec Spec { get; } = new()
    {
        Key = "input.sendKeys",
        Name = "模拟按键",
        Category = "键鼠与输入",
        Icon = "keyboard",
        Description = "发送一个按键：短按 / 按下 / 抬起，可带修饰键（文本用「发送文本」，等待用「延迟」）",
        Inputs = new[]
        {
            new InputSpec("key", "按键", ValueKind.text, InputEditor.key, Required: true,
                Description: "点开下拉，按分类选一个键：字母 / 数字 / 符号 / 功能键 / 编辑与导航 / 小键盘 / 修饰键 / 系统键"),
            new InputSpec("action", "动作", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("tap"), Options: Actions, Labels: ActionLabels,
                Description: "短按 = 按下再抬起；按下 / 抬起 用于按住不放（如先按下 Ctrl，再短按别的键）"),
            new InputSpec("mods", "修饰键", ValueKind.text, InputEditor.flags,
                Options: ModifierOptions, Labels: ModifierLabels,
                Description: "可多选；短按时整组包住主键，按下 / 抬起时与主键一起按下 / 抬起"),
            new InputSpec("holdMs", "按住时长毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(0), Advanced: true,
                Description: "短按时按下与抬起之间的间隔；0 = 单批发送（最快最稳）"),
            new InputSpec("repeat", "重复次数", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(1), Advanced: true,
                Description: "只对短按有效；连按方向键这类场景用（更复杂的需求用「重复」步骤）"),
            new InputSpec("gapMs", "重复间隔毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(30), Advanced: true,
                Description: "重复短按之间的间隔；太短有些程序会漏键"),
            new InputSpec("focusDelayMs", "焦点延时毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(50), Advanced: true,
                Description: "发送前等多久，给目标窗口切到前台的时间"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string keyName = args.Text("key").Trim();
        if (keyName.Length == 0) throw new ActionException("模拟按键：没有选择按键");
        if (!KeyStroke.TryResolveKey(keyName, out ushort vk))
            throw new ActionException($"模拟按键：未知按键「{keyName}」");

        string action = args.Text("action").Trim().ToLowerInvariant();
        if (action.Length == 0) action = "tap";
        if (action is not ("tap" or "down" or "up"))
            throw new ActionException($"模拟按键：动作只能是 tap / down / up（当前 {action}）");

        var mods = ResolveModifiers(args.Text("mods"));

        int delay = (int)Math.Clamp(args.Number("focusDelayMs"), 0, 10_000);
        if (delay > 0 && !ctx.Wait(delay))
            throw new ActionAbortedException("已停止", userCancelled: true);

        if (action == "down")
        {
            foreach (ushort mod in mods) { InputHelper.Down(mod); InputHoldState.KeyDown(mod); }
            InputHelper.Down(vk);
            InputHoldState.KeyDown(vk);
            return;
        }

        if (action == "up")
        {
            InputHelper.Up(vk);
            InputHoldState.KeyUp(vk);
            for (int i = mods.Count - 1; i >= 0; i--)
            {
                InputHelper.Up(mods[i]);
                InputHoldState.KeyUp(mods[i]);
            }
            return;
        }

        int repeat = Math.Clamp((int)args.Number("repeat"), 1, 100);
        int hold = Math.Clamp((int)args.Number("holdMs"), 0, 10_000);
        int gap = Math.Clamp((int)args.Number("gapMs"), 0, 10_000);

        for (int n = 0; n < repeat; n++)
        {
            if (hold <= 0)
            {
                // 单批 SendInput：修饰键与主键一起按下、逆序抬起，最稳。
                InputHelper.Combo(mods.Append(vk).ToArray());
            }
            else
            {
                foreach (ushort mod in mods) InputHelper.Down(mod);
                InputHelper.Down(vk);
                if (!ctx.Wait(hold))
                    throw new ActionAbortedException("已停止", userCancelled: true);
                InputHelper.Up(vk);
                for (int i = mods.Count - 1; i >= 0; i--) InputHelper.Up(mods[i]);
            }

            if (n + 1 < repeat && gap > 0 && !ctx.Wait(gap))
                throw new ActionAbortedException("已停止", userCancelled: true);
        }
    }

    /// <summary>修饰键多选值（<c>ctrl+shift</c>）→ VK 列表，顺序固定为 ctrl / shift / alt / win。</summary>
    private static List<ushort> ResolveModifiers(string text)
    {
        var wanted = new HashSet<string>(
            (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        var mods = new List<ushort>();
        foreach (string name in ModifierOptions)
        {
            if (!wanted.Contains(name)) continue;
            if (!KeyStroke.TryResolveKey(name, out ushort modVk))
                throw new ActionException($"模拟按键：未知修饰键「{name}」");
            mods.Add(modVk);
        }

        if (mods.Count != wanted.Count)
        {
            string unknown = string.Join(" / ", wanted.Where(w =>
                !ModifierOptions.Contains(w, StringComparer.OrdinalIgnoreCase)));
            throw new ActionException($"模拟按键：未知修饰键「{unknown}」（可用：ctrl / shift / alt / win）");
        }

        return mods;
    }
}
