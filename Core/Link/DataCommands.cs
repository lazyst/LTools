namespace LTools.Core.Link;

/// <summary>
/// 配置文件写入的方法名（**执行器 → 宿主**）：<c>configs/*.json</c> 的唯一物理写者是
/// 普通权限宿主（<c>docs/PROCESS_SPLIT.md</c> §7 / §15.3）。
/// </summary>
/// <remarks>
/// 翻转的是「文件由谁写」，不是「谁决定内容」：执行器照旧读-改-写自己的内存配置，
/// 只是最后落盘这一步转给宿主。这样设置 / 编辑器将来搬进宿主时不必为每个控件造一条 RPC。
/// </remarks>
internal static class DataMethods
{
    /// <summary>把一份域配置的 JSON 交给宿主落盘。</summary>
    public const string Write = "data.write";
}

/// <summary>写一份域配置：<see cref="Name"/> 只能是 <c>ConfigBundle.DomainFiles</c> 里的文件名。</summary>
internal sealed record DataWriteRequest(string Name, string Json);

/// <summary><see cref="Written"/> = 宿主真的写了（false = 名字不在白名单 / 写失败）。</summary>
internal sealed record DataWriteReply(bool Written);
