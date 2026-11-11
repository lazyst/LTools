using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using LTools.Config;
using LTools.Core;
using LTools.Core.Actions;
using LTools.Native;

namespace LTools.Features;

/// <summary>
/// 前台进程切换事件源（<c>window.foreground</c>，见 <c>docs/AUTO_TRIGGER.md</c> §13.4/13.5/13.7）。
/// 订阅 <see cref="ForegroundAppCache.ForegroundChanged"/>（后台解析完成后才触发，回调里不查
/// <c>Process.MainModule</c>），命中时构造一条 <see cref="TriggerEvent"/> 交给统一投递，
/// **不自己筛规则、不自己调动作执行链、不自己判安全策略**。
/// </summary>
/// <remarks>
/// <para>
/// 「切换」按**进程**判定：<c>now.ExeName != _last.ExeName</c> 才投递，同一进程的多个窗口、
/// 同一窗口的重复通知都不产生事件。这样 <c>prevProcess</c> 永远是「上一个真实前台应用」，
/// 而不是「上一次收到 WinEvent 的窗口」——桌面 / 任务栏（<c>explorer.exe</c> 的 shell 窗口）、
/// LTools 自己的窗口都不会把它污染掉（用户报过「没换程序却反复命中 <c>prevProcess: explorer</c>」）。
/// <c>_last</c> 是本事件源自己的权威快照，只在缓存冷启动时用 <c>previous</c> 初始化一次；
/// <c>prevProcess</c> / <c>triggerPrevWindow*</c> 一律取自它，别改用缓存传来的旧快照。
/// </para>
/// <para>
/// 三类透明处理：① shell 窗口（<see cref="ForegroundApp.IsShellClassName"/>）不投递、也不推进
/// <c>_last</c>；② 解析失败 <c>now == null</c> 不投递；③ LTools 自己的窗口照常投递（保留
/// <c>OwnEvent</c> / <c>excludeOwn</c> 语义），但**不推进** <c>_last</c>，于是「Chrome → 面板
/// → Chrome」不会退化成两次假切换。
/// </para>
/// <para>
/// 订阅是**全局一次**的，<see cref="Reload"/> 只刷新「有没有这类规则」的标志，不重挂钩子
/// （钩子生命周期归 <see cref="ForegroundAppCache"/>）。
/// </para>
/// </remarks>
internal sealed class WindowForegroundSource : ITriggerSource
{
    private readonly object _gate = new();
    private Action<TriggerEvent>? _fire;
    private bool _subscribed;
    private bool _hasRules;
    private ForegroundApp? _last;   // 上一个「真实前台应用」（非 shell / 非自己 / 非 null）

    public void Start(Action<TriggerEvent> fire)
    {
        lock (_gate)
        {
            _fire = fire;
            if (!_subscribed)
            {
                ForegroundAppCache.ForegroundChanged += OnForeground;
                _subscribed = true;
            }
        }
        Reload();
    }

    /// <summary>全局订阅不变，只刷新「有没有这类规则」的标志（没有就别白构造事件）。</summary>
    public void Reload()
    {
        bool has = TriggerRegistry.Active.Any(r =>
            r.Kind == TriggerKind.@event && r.Enabled
            && string.Equals(r.EventType, "window.foreground", StringComparison.OrdinalIgnoreCase));
        lock (_gate) _hasRules = has;
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_subscribed)
            {
                ForegroundAppCache.ForegroundChanged -= OnForeground;
                _subscribed = false;
            }
            _fire = null;
            _hasRules = false;
            _last = null;
        }
    }

    private void OnForeground(ForegroundApp? now, ForegroundApp? previous)
    {
        Action<TriggerEvent> fire;
        ForegroundApp cur;
        ForegroundApp? prev;
        bool own;
        lock (_gate)
        {
            if (!_hasRules || _fire == null) return;
            fire = _fire;

            // 缓存冷启动时用旧快照兜一次底；shell / 自己的窗口不算「上一个真实应用」。
            if (_last == null && IsRealApp(previous)) _last = previous;

            if (now == null) return;                                     // 解析失败
            cur = now;
            if (ForegroundApp.IsShellClassName(cur.ClassName)) return;   // 桌面 / 任务栏透明
            own = ForegroundApp.IsOwnWindow(cur.Hwnd);

            // 进程真的换了才投递；prevProcess 取自 _last（权威），不是缓存给的 previous。
            if (string.Equals(cur.ExeName, _last?.ExeName ?? "", StringComparison.OrdinalIgnoreCase))
                return;
            prev = _last;

            // 自己的窗口不推进 _last，避免把工具窗口记成「上一个进程」。
            if (!own) _last = cur;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = cur.Title,
            ["process"] = cur.ExeName,
            ["class"] = cur.ClassName,
            ["prevTitle"] = prev?.Title ?? "",
            ["prevProcess"] = prev?.ExeName ?? "",
        };
        var values = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
        {
            ["triggerWindowTitle"] = Value.FromText(cur.Title),
            ["triggerWindowProcess"] = Value.FromText(cur.ExeName),
            ["triggerWindowClass"] = Value.FromText(cur.ClassName),
            ["triggerWindowHandle"] = Value.FromText(HandleText(cur)),
            ["triggerPrevWindowTitle"] = Value.FromText(prev?.Title ?? ""),
            ["triggerPrevWindowProcess"] = Value.FromText(prev?.ExeName ?? ""),
        };

        fire(new TriggerEvent
        {
            EventType = "window.foreground",
            Fields = fields,
            Values = values,
            Foreground = cur,     // §13.3：作用范围用切换后的窗口快照
            OwnEvent = own,
        });
    }

    /// <summary>能当「上一个真实前台应用」的快照：非 shell、非 LTools 自己。</summary>
    private static bool IsRealApp(ForegroundApp? app) =>
        app != null
        && !ForegroundApp.IsShellClassName(app.ClassName)
        && !ForegroundApp.IsOwnWindow(app.Hwnd);

    private static string HandleText(ForegroundApp? app) => app == null
        ? ""
        : "0x" + app.Hwnd.ToInt64().ToString("X", CultureInfo.InvariantCulture);
}
