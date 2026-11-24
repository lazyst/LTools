namespace LTools.Core;

/// <summary>
/// 本进程是哪一个角色：**界面宿主**（普通权限的 <c>ShareHost.exe</c>）还是**执行器**（提权的主程序）。
/// </summary>
/// <remarks>
/// <para>
/// 以前到处用 <see cref="Features.ConfigStore.ReadOnly"/> 兼职这件事（「只读 = 我是宿主」），
/// 那是巧合：写权正在往宿主翻（<c>docs/PROCESS_SPLIT.md</c> §15.3），设置 / 编辑器搬过去之后
/// 宿主变成写者、执行器只剩读缓存，<c>ReadOnly</c> 的含义会整个反过来——而「窗口 / 交互归谁」
/// 一点都不变。所以**身份判断一律用它**，<c>ReadOnly</c> 只回答「这个进程能不能写配置」。
/// </para>
/// <para>
/// 只有 <c>ShareHost</c> 的启动路径调 <see cref="MarkUiHost"/>；其它进程（提权主程序、自测、
/// 冒烟）默认就是执行器语义。默认值刻意是 <c>false</c>：漏标也不会让提权进程去开宿主的窗口。
/// </para>
/// </remarks>
internal static class AppRole
{
    /// <summary>本进程是不是界面宿主。</summary>
    public static bool IsUiHost { get; private set; }

    /// <summary>宿主启动时调一次（<c>ShareHost.App.OnStartup</c> 最前面）。</summary>
    public static void MarkUiHost() => IsUiHost = true;
}
