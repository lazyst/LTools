using System.Runtime.ExceptionServices;
using System.Threading;

namespace LTools.Native;

/// <summary>
/// 在一条临时 STA 线程上跑一段代码。
/// </summary>
/// <remarks>
/// 动作在后台 MTA 线程执行，而 <c>Shell.Application</c> 这类 COM 对象必须在 STA 上创建和使用；
/// 直接在当前线程 <c>SetApartmentState</c> 是做不到的（线程启动后不能再改套间）。
/// 这里每次新建一条后台线程、跑完就结束——调用频率很低（一次动作一步），不值得维护线程池。
/// </remarks>
internal static class StaRunner
{
    /// <summary>在 STA 线程上执行 <paramref name="work"/> 并等它结束；超时抛 <see cref="TimeoutException"/>。</summary>
    public static T? Run<T>(Func<T> work, int timeoutMs = 3000, string name = "ltools-sta")
    {
        T? result = default;
        ExceptionDispatchInfo? failure = null;

        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(Math.Clamp(timeoutMs, 100, 15_000)))
            throw new TimeoutException($"等待「{name}」超时（{timeoutMs} ms）");

        failure?.Throw();
        return result;
    }
}
