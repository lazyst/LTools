using System.Collections.Generic;
using System.Linq;

namespace LTools.Core.Running;

/// <summary>
/// 一条排队中的运行：**只存 Id**，不存动作本体 / 参数值——排队期间动作可能被改、被删，
/// 参数框要等轮到它时才弹（见 <c>docs/RUN_INTEROP.md</c> §3.5）。
/// </summary>
internal sealed record RunRequest(
    string ActionId,
    string DisplayName,
    int CursorX,
    int CursorY,
    RunSource Source,
    bool NeedsAsk);

/// <summary>
/// 运行队列：先进先出、同名只留一条、有上限。纯数据（锁内操作，不碰线程 / 窗口）。
/// </summary>
internal static class RunQueue
{
    private static readonly object Gate = new();
    private static readonly List<RunRequest> Items = new();

    public static int Count
    {
        get { lock (Gate) return Items.Count; }
    }

    /// <summary>队列里是否已有这个动作（准入用它去重）。</summary>
    public static bool Contains(string actionId)
    {
        if (string.IsNullOrEmpty(actionId)) return false;
        lock (Gate) return Items.Any(x => string.Equals(x.ActionId, actionId, System.StringComparison.Ordinal));
    }

    /// <summary>入队；已有同名时不重复加，返回 false。</summary>
    public static bool TryEnqueue(RunRequest request)
    {
        lock (Gate)
        {
            if (Items.Any(x => string.Equals(x.ActionId, request.ActionId, System.StringComparison.Ordinal)))
                return false;
            Items.Add(request);
            return true;
        }
    }

    /// <summary>取队首（不删）。</summary>
    public static RunRequest? Peek()
    {
        lock (Gate) return Items.Count == 0 ? null : Items[0];
    }

    /// <summary>取出队首。</summary>
    public static RunRequest? TryDequeue()
    {
        lock (Gate)
        {
            if (Items.Count == 0) return null;
            RunRequest request = Items[0];
            Items.RemoveAt(0);
            return request;
        }
    }

    /// <summary>清空（停止热键 / 退出 / 切回不支持排队的档）。返回清掉的条数。</summary>
    public static int Clear()
    {
        lock (Gate)
        {
            int count = Items.Count;
            Items.Clear();
            return count;
        }
    }

    /// <summary>只读快照（给指示器 / 运行列表 / 自测用）。</summary>
    public static IReadOnlyList<RunRequest> Snapshot()
    {
        lock (Gate) return Items.ToArray();
    }
}
