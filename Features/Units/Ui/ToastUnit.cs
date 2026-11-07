using System;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Features;

namespace LTools.Features.Units.Ui;

/// <summary>提示弹窗：屏幕某个角显示一段文字，投递到 UI 线程即返回，不阻塞后续步骤。</summary>
public sealed class ToastUnit : IStepUnit
{
    private static readonly string[] Positions =
    {
        "default", "bottomRight", "bottomLeft", "bottomCenter", "topRight", "topLeft", "topCenter",
    };

    public UnitSpec Spec { get; } = new()
    {
        Key = "ui.toast",
        Name = "提示弹窗",
        Category = "界面",
        Icon = "toast",
        Description = "显示一条提示，不阻塞后续步骤、不抢焦点；同一「去重键」的提示会就地更新而不是堆叠",
        Inputs = new[]
        {
            new InputSpec("text", "内容", ValueKind.text, InputEditor.multiline, Required: true),
            new InputSpec("title", "标题", ValueKind.text),
            new InputSpec("durationMs", "停留毫秒", ValueKind.number, InputEditor.number,
                Default: Value.FromNumber(ToastService.DefaultDurationMs)),
            new InputSpec("position", "位置", ValueKind.text, InputEditor.enumList,
                Default: Value.FromText("default"), Options: Positions),
            new InputSpec("key", "去重键", ValueKind.text),
        },
    };

    public void Execute(StepContext ctx, StepArgs args)
    {
        string text = args.Text("text");
        if (text.Length == 0) return;   // 空内容没什么可看的，和旧实现一致

        string title = args.Text("title").Trim();
        int duration = (int)Math.Clamp(args.Number("durationMs"), 0, 600_000);
        string key = args.Text("key").Trim();

        ToastService.Show(text, title.Length == 0 ? null : title, duration, ToastKind.info,
            key.Length == 0 ? null : key, onClick: null, ResolvePosition(args.Text("position")));
    }

    /// <summary>位置键 → 枚举；「跟随设置」读配置里的默认落点（带 10 秒缓存，循环里不会反复读盘）。</summary>
    private static ToastPosition ResolvePosition(string key)
    {
        key = (key ?? "").Trim();
        if (key.Length == 0 || key.Equals("default", StringComparison.OrdinalIgnoreCase))
            key = DefaultPositionKey();

        return Enum.TryParse<ToastPosition>(key, ignoreCase: true, out var position)
            ? position
            : ToastPosition.bottomRight;
    }

    private static (DateTime At, string Key) _defaultCache;

    private static string DefaultPositionKey()
    {
        if (DateTime.UtcNow - _defaultCache.At < TimeSpan.FromSeconds(10)) return _defaultCache.Key;

        string key = nameof(ToastPosition.bottomRight);
        try { key = ConfigStore.LoadConfig().ToastPosition.ToString(); }
        catch { /* 没有配置文件（例如冒烟测试）：用右下角 */ }

        _defaultCache = (DateTime.UtcNow, key);
        return key;
    }
}
