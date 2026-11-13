using System;
using System.Collections.Generic;
using System.Linq;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features.Units.Text;

/// <summary>
/// 剪贴板历史（Win+V）：<c>operation = list | delete</c>。
/// 只能读和删——历史条目是系统快照，没有「改」的接口。
/// </summary>
public sealed class ClipboardHistoryUnit : IStepUnit
{
    private static readonly string[] Operations = { "list", "delete" };

    public UnitSpec Spec { get; } = new()
    {
        Key = "clipboard.history",
        Name = "剪贴板历史",
        Category = "剪贴板",
        Icon = "clipboard",
        Description = "读取 Windows 剪贴板历史（Win+V）的最近记录，或删掉其中一条；需要系统开启剪贴板历史",
        Inputs = new[]
        {
            new InputSpec("operation", "操作", ValueKind.text, InputEditor.enumList,
                Required: true, Default: Value.FromText("list"), Options: Operations,
                OptionParams: OptionMap.Of(
                    ("list", ["limit"]),
                    ("delete", ["id"]))),
            new InputSpec("limit", "最多条数", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(25),
                Description: "系统默认只留 25 条；填 0 = 能读多少读多少"),
            new InputSpec("id", "记录 ID", ValueKind.text,
                Description: "从「记录 ID」输出里挑一条；留空 = 删最新一条"),
        },
        Outputs = new[]
        {
            new OutputSpec("items", "记录列表", ValueKind.list,
                Description: "每条一行：「[文本] 开头几个字」/「[图片]」/「[文件]」，最新在前"),
            new OutputSpec("texts", "全文列表", ValueKind.list,
                Description: "与「记录列表」同序的完整文本；图片 / 文件条目是空串"),
            new OutputSpec("ids", "记录 ID", ValueKind.list,
                Description: "与「记录列表」同序的系统 id，删除时填它"),
            new OutputSpec("count", "条数", ValueKind.number, Description: "读到 / 删完后剩下的条数"),
            new OutputSpec("first", "最新一条", ValueKind.text,
                Description: "最新一条的全文（删除后是删完之后的）"),
            new OutputSpec("deleted", "已删除", ValueKind.@bool,
                Description: "这次是否真的删掉了一条（找不到 / 历史为空 = 假，不算错误）"),
            new OutputSpec("found", "有记录", ValueKind.@bool, Description: "历史里是否有记录"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string operation = args.Text("operation").Trim().ToLowerInvariant();

        if (operation == "delete")
        {
            bool deleted = ClipboardHistory.Delete(args.Text("id"), out string? deleteError);
            if (deleteError != null) throw new ActionException(deleteError);

            // 删完再读一遍：输出始终是「操作之后的历史状态」，用户能直接用新的一条。
            var remaining = ClipboardHistory.List(0, out string? readError);
            if (readError != null) throw new ActionException(readError);

            WriteOutputs(ctx, remaining, deleted);
            return;
        }

        if (operation != "list")
            throw new ActionException($"剪贴板历史操作只能是 list / delete（当前 {operation}）");

        int limit = (int)Math.Round(args.Number("limit"));
        if (limit < 0) throw new ActionException($"「最多条数」不能是负数（当前 {limit}）");

        var entries = ClipboardHistory.List(limit, out string? error);
        if (error != null) throw new ActionException(error);

        WriteOutputs(ctx, entries, deleted: false);
    }

    private static void WriteOutputs(
        StepContext ctx, IReadOnlyList<ClipboardHistoryEntry> entries, bool deleted)
    {
        ctx.SetOutput("items", Value.FromList(entries.Select(e => Value.FromText(Preview(e)))));
        ctx.SetOutput("texts", Value.FromList(entries.Select(e => Value.FromText(e.Text))));
        ctx.SetOutput("ids", Value.FromList(entries.Select(e => Value.FromText(e.Id))));
        ctx.SetOutput("count", Value.FromNumber(entries.Count));
        ctx.SetOutput("first", Value.FromText(entries.Count > 0 ? entries[0].Text : ""));
        ctx.SetOutput("deleted", Value.FromBool(deleted));
        ctx.SetOutput("found", Value.FromBool(entries.Count > 0));
    }

    /// <summary>一条记录的预览：类型标记 + 开头几十个字（图片 / 文件没有文本）。</summary>
    private static string Preview(ClipboardHistoryEntry entry)
    {
        string kind = entry.Kind switch
        {
            "text" => "文本",
            "image" => "图片",
            "files" => "文件",
            "link" => "链接",
            _ => "其它",
        };

        string text = entry.Text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length > 40) text = text[..40] + "…";
        return text.Length > 0 ? $"[{kind}] {text}" : $"[{kind}]";
    }
}

/// <summary>清空当前剪贴板；可一并清掉 Windows 剪贴板历史（Win+V）。</summary>
public sealed class ClipboardClearUnit : IStepUnit
{
    public UnitSpec Spec { get; } = new()
    {
        Key = "clipboard.clear",
        Name = "清空剪贴板",
        Category = "剪贴板",
        Icon = "delete",
        Description = "清空当前剪贴板内容；可一并清掉 Windows 剪贴板历史（Win+V）",
        Inputs = new[]
        {
            new InputSpec("includeHistory", "连历史一起清", ValueKind.@bool, InputEditor.boolean,
                Default: Value.FromBool(false),
                Description: "关 = 只清当前剪贴板；开 = 连 Win+V 历史一起清（不可恢复）"),
        },
        Outputs = new[]
        {
            new OutputSpec("cleared", "已清空", ValueKind.@bool,
                Description: "当前剪贴板是否清空成功（被别的程序占用时可能失败）"),
            new OutputSpec("count", "清掉的历史条数", ValueKind.number,
                Description: "开了「连历史一起清」时，清掉前历史里有多少条；否则为 0"),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        bool cleared = NativeClipboard.Clear();
        int count = 0;

        if (args.Bool("includeHistory"))
        {
            var before = ClipboardHistory.List(0, out string? readError);
            if (readError != null) throw new ActionException(readError);
            count = before.Count;

            if (!ClipboardHistory.Clear(out string? clearError))
                throw new ActionException(clearError ?? "清空剪贴板历史失败");
        }

        ctx.SetOutput("cleared", Value.FromBool(cleared));
        ctx.SetOutput("count", Value.FromNumber(count));
    }
}
