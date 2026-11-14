namespace LTools.Core.Actions;

/// <summary>
/// 失败的性质（P5）：只用于「重试哪些失败」与提示文案，**不改变控制流**——
/// 失败照旧交给失败策略（停止 / 失败继续），重试次数照旧由 <see cref="Step.Retry"/> 决定。
/// </summary>
/// <remarks>
/// 以前机器分不出「等超时了」和「参数写错了」，一律按「任何失败都重试」处理：
/// 找窗口 3 次、等图 3 次还说得过去，可参数写错重试 3 次只是白白拖慢失败。
/// 现在动作单元在语义明确的地方带上性质，步骤可以只对某几类失败重试。
/// </remarks>
public enum ActionErrorKind
{
    /// <summary>没分类（默认）：重试条件里写 <c>unknown</c> 才匹配它。</summary>
    unknown = 0,

    /// <summary>等到了上限还没等到（等待 / 轮询超时、HTTP 超时）。</summary>
    timeout,

    /// <summary>要找的目标不存在（窗口 / 文字 / 模板图 / 文件 / 宏）。</summary>
    notFound,

    /// <summary>前置条件不满足（缺参数、缺依赖、状态不对、系统不支持）。</summary>
    precondition,

    /// <summary>用户停止 / 预算耗尽。只在引擎内部用，配置里写不出来，也永远不会被重试。</summary>
    aborted,
}

/// <summary>
/// 失败分类的唯一实现：构造带性质的异常、判定异常的性质、解析 / 规范化
/// <see cref="Step.RetryOn"/>（「只重试这几类失败」）。
/// </summary>
/// <remarks>
/// 动作单元在语义明确的地方调 <see cref="NotFound"/> / <see cref="Timeout"/> / <see cref="Precondition"/>，
/// 其余照旧 <c>throw new ActionException(…)</c>（性质 = <see cref="ActionErrorKind.unknown"/>）。
/// **别在引擎里按错误文本猜性质**：文字一改分类就错，而且本地化后更没戏。
/// </remarks>
public static class ActionErrors
{
    /// <summary>能写进 <see cref="Step.RetryOn"/> 的键（<see cref="ActionErrorKind.aborted"/> 刻意不在里面）。</summary>
    public static IReadOnlyList<string> Keys { get; } = new[] { "notFound", "timeout", "precondition", "unknown" };

    public static ActionException NotFound(string message) =>
        new(message, ActionErrorKind.notFound);

    public static ActionException NotFound(string message, Exception inner) =>
        new(message, inner, ActionErrorKind.notFound);

    public static ActionException Timeout(string message) =>
        new(message, ActionErrorKind.timeout);

    public static ActionException Timeout(string message, Exception inner) =>
        new(message, inner, ActionErrorKind.timeout);

    public static ActionException Precondition(string message) =>
        new(message, ActionErrorKind.precondition);

    public static ActionException Precondition(string message, Exception inner) =>
        new(message, inner, ActionErrorKind.precondition);

    /// <summary>这个异常属于哪一类（已经包了一层的步骤失败会往下看内层）。</summary>
    public static ActionErrorKind KindOf(Exception? ex) => ex switch
    {
        null => ActionErrorKind.unknown,
        StepFailureException failure => failure.Kind,
        ActionException action => action.Kind,
        ActionAbortedException => ActionErrorKind.aborted,
        _ => ActionErrorKind.unknown,
    };

    /// <summary>给人看的名字（提示文案 / 运行历史 / 编辑器 chip）。</summary>
    public static string Label(ActionErrorKind kind) => kind switch
    {
        ActionErrorKind.timeout => "超时",
        ActionErrorKind.notFound => "未找到",
        ActionErrorKind.precondition => "前置条件",
        ActionErrorKind.aborted => "已停止",
        _ => "未知错误",
    };

    /// <summary>配置里的键（<c>notFound</c>）；认不出来时返回 null。</summary>
    public static string? KeyOf(ActionErrorKind kind) => kind switch
    {
        ActionErrorKind.timeout => "timeout",
        ActionErrorKind.notFound => "notFound",
        ActionErrorKind.precondition => "precondition",
        ActionErrorKind.unknown => "unknown",
        _ => null,
    };

    /// <summary>
    /// 解析 <see cref="Step.RetryOn"/>：逗号分隔的键，空串 = 任何失败都重试。
    /// 返回 false 表示里面有认不出的键（校验器据此报 error）。
    /// </summary>
    public static bool TryParse(string? text, out HashSet<ActionErrorKind> kinds)
    {
        kinds = new HashSet<ActionErrorKind>();
        if (string.IsNullOrWhiteSpace(text)) return true;

        foreach (string raw in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string key = raw.Trim();
            if (key.Length == 0) continue;

            ActionErrorKind? kind = key.ToLowerInvariant() switch
            {
                "notfound" => ActionErrorKind.notFound,
                "timeout" => ActionErrorKind.timeout,
                "precondition" => ActionErrorKind.precondition,
                "unknown" => ActionErrorKind.unknown,
                _ => null,
            };
            if (kind == null) return false;
            kinds.Add(kind.Value);
        }

        return true;
    }

    /// <summary>规范化成存盘形状（固定顺序、去重、去空白）；空 = 任何失败。</summary>
    public static string Normalize(string? text)
    {
        if (!TryParse(text, out var kinds) || kinds.Count == 0) return "";
        return string.Join(",", Keys.Where(key => kinds.Contains(KindOfKey(key)!.Value)));
    }

    /// <summary>重试条件里的一份可读文案（编辑器 chip 用）：<c>仅未找到</c> / <c>仅未找到·超时</c>。</summary>
    public static string Describe(string? retryOn)
    {
        if (!TryParse(retryOn, out var kinds) || kinds.Count == 0) return "";

        var labels = Keys
            .Where(key => kinds.Contains(KindOfKey(key)!.Value))
            .Select(key => Label(KindOfKey(key)!.Value));
        return "仅" + string.Join("·", labels);
    }

    private static ActionErrorKind? KindOfKey(string key) => key.ToLowerInvariant() switch
    {
        "notfound" => ActionErrorKind.notFound,
        "timeout" => ActionErrorKind.timeout,
        "precondition" => ActionErrorKind.precondition,
        "unknown" => ActionErrorKind.unknown,
        _ => null,
    };

    /// <summary>
    /// 这一次失败该不该重试：<paramref name="retryOn"/> 为空 = 任何失败都重试（老行为）；
    /// 否则只有匹配的那几类才重试。**停止 / 预算耗尽永不重试**（引擎里更在控制信号分支直接抛出）。
    /// </summary>
    public static bool ShouldRetry(string? retryOn, Exception ex)
    {
        ActionErrorKind kind = KindOf(ex);
        if (kind == ActionErrorKind.aborted) return false;
        if (!TryParse(retryOn, out var kinds) || kinds.Count == 0) return true;
        return kinds.Contains(kind);
    }
}
