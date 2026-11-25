using System.Windows.Threading;

namespace LTools.Core;

/// <summary>
/// 主 UI 线程投递入口。低级键鼠钩子跑在专用线程（见 <c>Hooks/HookDispatcher</c>），
/// 回调里**绝不能**同步等待 UI 线程（否则输入延迟又回到 UI 忙不忙上），所以一切 WPF
/// 副作用都走 <see cref="Post"/>：只排队、不阻塞。
/// </summary>
/// <remarks>
/// <c>Initialize</c> 在 <c>App.OnStartup</c> 最前面调一次（那时还在主 UI 线程）。
/// 已经在 UI 线程时同步执行，保持「UI 代码自己调 Post」的直觉语义；从钩子线程调则 BeginInvoke。
/// </remarks>
internal static class Ui
{
    private static Dispatcher? _dispatcher;

    /// <summary>在 App.OnStartup（主 UI 线程）初始化一次。</summary>
    public static void Initialize(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>当前线程是不是主 UI 线程。</summary>
    public static bool IsUiThread => _dispatcher?.CheckAccess() == true;

    /// <summary>
    /// 把要在主 UI 线程执行的工作排进去，立刻返回。钩子回调里只能调这个，不能 Invoke。
    /// 未初始化（启动早期）时静默丢弃，避免钩子线程在日志里刷屏。
    /// </summary>
    public static void Post(Action action)
    {
        var d = _dispatcher;
        if (d == null) return;
        if (d.CheckAccess())
        {
            action();
            return;
        }
        d.BeginInvoke(action);
    }

    /// <summary>
    /// 在 UI 线程上**同步**执行（等它做完再返回）。
    /// </summary>
    /// <remarks>
    /// **只能从后台线程 / 动作线程调**，钩子回调里绝不能用（钩子线程等 UI 会把输入卡住，见上面的说明）。
    /// 用于「先收起自己的窗口，再请求截图」这种有先后顺序的两步。
    /// </remarks>
    public static void Run(Action action)
    {
        var d = _dispatcher;
        if (d == null || d.CheckAccess())
        {
            action();
            return;
        }
        d.Invoke(action);
    }

    /// <summary>
    /// 排到主 UI 线程执行、**不等**；没有 dispatcher（自测 / 控制台冒烟）时就地执行。
    /// </summary>
    /// <remarks>
    /// 链路读循环用它接「必须回 UI 线程做、但答复要等做完」的请求：读循环只交出去就返回，
    /// 才能继续收对端发回来的其它答复（自己阻塞等 UI 会把它自己卡死，见 <c>ActionEditHost</c>）。
    /// 和 <see cref="Post"/> 的区别只有一个：这里**绝不丢**（没有 UI 线程就当场做）。
    /// </remarks>
    public static void Enqueue(Action action)
    {
        var d = _dispatcher;
        if (d == null || d.CheckAccess())
        {
            action();
            return;
        }
        d.BeginInvoke(action);
    }
}
