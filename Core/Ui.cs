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
}
