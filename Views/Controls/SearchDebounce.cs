using System.Windows.Threading;

namespace LTools.Views.Controls;

/// <summary>
/// 搜索 / 筛选输入框的统一防抖：输入只重启计时，停手后跑一次重活。
/// 清除 × / Esc / 回车这类「要立刻生效」的入口调 <see cref="Now"/>。
/// </summary>
/// <remarks>低频重活（整表重建 / 读日志 / 重建视觉树）别再每敲一键跑一次；
/// <c>DispatcherTimer</c> 跑在 UI 线程，回调里仍是同步工作，所以只负责「少跑几次」，
/// 真正的减负还要靠缓存 / 局部过滤（见各调用点）。</remarks>
internal sealed class SearchDebounce
{
    private readonly DispatcherTimer _timer;
    private readonly Action _run;

    public SearchDebounce(int delayMs, Action run)
    {
        _run = run;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        _timer.Tick += (_, _) => { _timer.Stop(); _run(); };
    }

    /// <summary>文本又变了：重新计时。</summary>
    public void Signal()
    {
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>取消还没跑的那次（窗口关闭 / 数据重载时用）。</summary>
    public void Cancel() => _timer.Stop();

    /// <summary>立刻执行，并取消排队中的那次。</summary>
    public void Now()
    {
        _timer.Stop();
        _run();
    }

    /// <summary>
    /// 只把「待执行的那次」立刻落下，没有排队就什么都不做。
    /// 搜索框带 ↑↓ / Enter 导航时用：防抖还没到点，但方向键必须拿到最新筛选结果。
    /// </summary>
    public void Flush()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _run();
    }
}
