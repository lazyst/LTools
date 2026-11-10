using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LTools.Config;
using LTools.Core;
using LTools.Features.Builtin;

namespace LTools.Features;

/// <summary>
/// 自动触发模块的运行期总入口：生命周期（Start / Stop）、配置即时生效（Reload）、
/// 统一投递（Fire）与安全策略（总开关 / 单条启用 / 紧急禁用 / 防抖节流延迟 / 并发单飞）。
/// </summary>
/// <remarks>
/// <para>
/// 唯一执行入口是 <see cref="ActionExecutor.RunRef"/>（后台 spawn、不阻塞调用方）；
/// 这里绝不 <c>Task.Wait</c>、不调同步版 <c>Execute</c>。所有投递都发生在后台线程
/// （<see cref="TriggerScheduler"/> 的 Timer 回调 / 二期事件源线程），不碰 WPF。
/// </para>
/// <para>
/// 并发：<see cref="AutomationRunner"/> 的 <c>_running</c> 在已有运行时会静默丢弃，
/// 故自动触发自己先查 <see cref="AutomationRunner.IsRunning"/>：命中时已有自动化在跑就丢弃并记日志
/// （规格 §6「建议一条规则同时只跑一个实例」）。内置功能走 <see cref="FeatureRunner"/> 不受该锁限制。
/// </para>
/// </remarks>
internal static class TriggerService
{
    private static readonly object Gate = new();
    private static TriggerScheduler? _scheduler;
    private static readonly List<ITriggerSource> _sources = new();   // 一期为空；二期在此注册
    private static bool _started;

    // 本次进程的启动状态（进程级常量）：传给 TriggerScheduler 过滤「启动后」规则。
    private static bool _firstStartToday;
    private static bool _launchedByAutoStart;

    // 每条规则的运行期时间戳（毫秒，Environment.TickCount64），按 Id 记。
    private static readonly Dictionary<string, long> _lastHit = new(StringComparer.Ordinal);    // 防抖：最近一次命中
    private static readonly Dictionary<string, long> _lastFired = new(StringComparer.Ordinal);  // 节流：最近一次执行

    /// <summary>紧急禁用空文件（对齐 Quicker 的 <c>c:\qk_disable_auto.txt</c>），放在数据根下。</summary>
    private static string EmergencyFile => Path.Combine(AppPaths.DataRoot, "disable-auto-trigger.txt");

    /// <summary>
    /// 「仅每日第一次启动」的日期标记，放 <c>state/</c>（随备份走、不进 <c>cache/</c>：
    /// 清缓存会让规则当天重复运行）。
    /// </summary>
    private static string FirstStartMarker =>
        Path.Combine(AppPaths.DataRoot, "state", "auto-trigger-last-start.txt");

    /// <summary>
    /// 本次启动是不是今天第一次启动 LTools：读日期标记，不是今天就写今天并返回 true。
    /// 无条件执行（与总开关无关）——「今日首次启动」是进程级事实，不随规则启用状态变化。
    /// </summary>
    private static bool MarkFirstStartToday()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        try
        {
            string? dir = Path.GetDirectoryName(FirstStartMarker);
            if (dir != null) Directory.CreateDirectory(dir);
            if (File.Exists(FirstStartMarker) && File.ReadAllText(FirstStartMarker).Trim() == today)
                return false;
            File.WriteAllText(FirstStartMarker, today);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Triggers", "读写「每日首次启动」标记失败，按首次启动处理：" + ex.Message);
            return true;
        }
    }

    /// <summary>紧急禁用是否生效（空文件存在即禁用，每次 Fire 前检查）。</summary>
    public static bool EmergencyDisabled
    {
        get
        {
            try { return File.Exists(EmergencyFile); }
            catch { return false; }
        }
    }

    /// <summary>托盘菜单「暂停自动触发」勾选项：创建 / 删除紧急禁用空文件。</summary>
    public static void SetEmergencyDisabled(bool disabled)
    {
        try
        {
            if (disabled)
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                File.WriteAllText(EmergencyFile, "");   // 空文件，内容无意义
            }
            else if (File.Exists(EmergencyFile))
            {
                File.Delete(EmergencyFile);
            }
            AppLog.Info("Triggers", disabled ? "已开启紧急禁用（暂停自动触发）" : "已解除紧急禁用");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Triggers", "切换紧急禁用失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 启动模块（App.OnStartup，在 ConfigStore.Initialize 之后）。
    /// <paramref name="launchedByAutoStart"/> = 本次是否由开机自启任务拉起（<c>--autostart</c>）。
    /// </summary>
    public static void Start(string configPath, bool launchedByAutoStart)
    {
        lock (Gate)
        {
            if (_started) return;
            _launchedByAutoStart = launchedByAutoStart;
            _firstStartToday = MarkFirstStartToday();
            // 旧数据根可能只有 6 个域文件（triggers 是后加的第七个）：补齐一次，
            // 免得「读得到规则、却一直没有 configs/triggers.json」。域文件齐全时
            // 这里连磁盘都不读；Modify 的整份兜底只新建缺的域，其它域内容没变就不碰。
            if (!ConfigBundle.IsFullySplit(configPath))
            {
                try { TriggerStore.Modify(configPath, _ => { }); }
                catch (Exception ex) { AppLog.Warn("Triggers", "补齐 configs/triggers.json 失败：" + ex.Message); }
            }

            // 从磁盘读，拿到的是 RepairReferences 清理过引用之后的最新规则。
            TriggerRegistry.Load(AppConfig.Load(configPath));
            _scheduler = new TriggerScheduler(_firstStartToday, _launchedByAutoStart);
            _scheduler.Start(Fire);
            foreach (var source in _sources) source.Start(Fire);
            _started = true;

            int active = TriggerRegistry.Active.Count;
            AppLog.Info("Triggers", TriggerRegistry.Enabled
                ? $"自动触发已启动 · {active} 条规则生效"
                : "自动触发已加载，但总开关关闭（默认），不投递任何规则");
            AppLog.Info("Triggers",
                $"本次启动：每日首次={_firstStartToday} · 开机自启拉起={_launchedByAutoStart}");
            if (EmergencyDisabled)
                AppLog.Info("Triggers", "检测到紧急禁用空文件，自动触发已暂停");
        }
    }

    /// <summary>停止模块（ExitApplication / OnExit）。只 Dispose 后台 Timer，不在 UI 线程等关停。</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (!_started) return;
            _started = false;
            _scheduler?.Stop();
            _scheduler = null;
            foreach (var source in _sources) source.Stop();
            _lastHit.Clear();
            _lastFired.Clear();
        }
    }

    /// <summary>设置页保存 / 导入备份后：让新规则立即生效，不必重启（照 HotkeysView.Save）。</summary>
    public static void Reload(TriggersFile file)
    {
        TriggerRegistry.Load(file);
        lock (Gate) _scheduler?.Rebuild();
    }

    /// <summary>用磁盘配置重新加载（导入备份 / 外部改动 configs/triggers.json 后调用）。</summary>
    public static void ReloadFromDisk(string configPath) => Reload(AppConfig.Load(configPath).Triggers ?? new());

    /// <summary>
    /// 统一投递点：定时 / 启动后 / 二期事件源都经这里。后台线程调用，绝不阻塞。
    /// </summary>
    /// <remarks>
    /// 检查顺序即安全策略：总开关 → 紧急禁用 → 单条启用 → 作用范围 → 防抖 → 节流 → 延迟 → 单飞投递。
    /// </remarks>
    internal static void Fire(TriggerRule rule)
    {
        // 每次 Fire 都重新读总开关与紧急禁用（可能刚被设置页 / 托盘改掉）。
        if (!TriggerRegistry.Enabled) return;
        if (EmergencyDisabled) return;
        if (!rule.Enabled) return;
        if (string.IsNullOrWhiteSpace(rule.ActionRef)) return;

        // 作用范围：名单为空放行；否则看当前前台程序（ForegroundAppCache 后台解析的快照）。
        if (!AppFilterMatch.AllowsScope(rule.Scope, ForegroundAppCache.Current)) return;

        long now = Environment.TickCount64;
        string id = string.IsNullOrEmpty(rule.Id) ? rule.ActionRef : rule.Id;

        // 防抖：DebounceMs 窗口内的重复命中只算一次（保留第一次，丢弃窗口内的后续）。
        // 节流：ThrottleMs 内不重复执行。两者都在锁内判定，避免并发命中一起放行。
        lock (Gate)
        {
            if (!_started) return;
            if (rule.DebounceMs > 0 && _lastHit.TryGetValue(id, out long hit) && now - hit < rule.DebounceMs)
            {
                _lastHit[id] = now;   // 抖动期内刷新命中时间，静默期过后才再触发
                return;
            }
            _lastHit[id] = now;

            if (rule.ThrottleMs > 0 && _lastFired.TryGetValue(id, out long fired) && now - fired < rule.ThrottleMs)
                return;
            _lastFired[id] = now;
        }

        // 命中后延迟执行（后台等待，不阻塞 Timer 回调）。
        if (rule.DelayMs > 0)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(rule.DelayMs).ConfigureAwait(false);
                    Dispatch(rule);
                }
                catch (Exception ex) { AppLog.Warn("Triggers", $"延迟投递失败：{ex.Message}"); }
            });
        }
        else
        {
            Dispatch(rule);
        }
    }

    /// <summary>真正把动作投出去：并发单飞 + 唯一执行入口 RunRef。</summary>
    private static void Dispatch(TriggerRule rule)
    {
        // 引擎的单实例锁在已有运行时会静默丢弃并弹「已有自动化正在运行」；
        // 自动触发是无人值守的，那条提示没人看，故自己先查并记日志（内置功能不受该锁限制）。
        bool isFeature = BindingRef.IsFeature(rule.ActionRef);
        if (!isFeature && AutomationRunner.IsRunning)
        {
            AppLog.Warn("Triggers", $"已有自动化正在运行，跳过规则「{rule.Note}」");
            return;
        }

        try
        {
            if (!ActionExecutor.RunRef(rule.ActionRef, 0, 0))
                AppLog.Warn("Triggers", $"规则「{rule.Note}」的动作引用无效：{rule.ActionRef}");
        }
        catch (Exception ex)
        {
            AppLog.Error("Triggers", $"规则「{rule.Note}」投递失败", ex);
        }
    }
}
