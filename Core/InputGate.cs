using System;
using System.Threading;
using LTools.Core.Actions;

namespace LTools.Core;

/// <summary>注入的类别（只用于诊断 / 自测；互斥本身是一把锁）。</summary>
internal enum InputKind
{
    Other,
    Keyboard,
    Mouse,
    Text,
    Clipboard,
    Macro,
}

/// <summary>
/// 注入闸：**一次按键 / 一次点击 / 一次拖动 / 一次文本 / 一次剪贴板操作** 之间互斥；
/// 试运行可以整段独占（见 <c>docs/RUN_INTEROP.md</c> §5）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不是「整段运行互斥」：那样并发就没有意义了。真正会互相踩的只有「同时往系统里发键鼠 /
/// 改剪贴板」这一小段，其余（OCR / UIA / 等待 / HTTP）本来就可以并行。
/// </para>
/// <para>
/// **同线程可重入**：动作单元可以先占住「这次拖动」，内部再调 <c>MouseOps</c> 也不会自己把自己卡死。
/// 等待时读 <see cref="TokenProvider"/>（由运行线程设成自己的取消令牌），按停止热键能立刻打断。
/// </para>
/// <para>
/// 钩子线程**绝不**走这里：CapsLock 轻按发 Esc 之类的注入不经过本闸，否则会被一个长拖动挡住输入。
/// </para>
/// </remarks>
internal static class InputGate
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    [ThreadStatic] private static int _depth;

    /// <summary>运行线程设置：等待锁时用哪一份取消令牌（别的线程返回 None）。</summary>
    public static Func<CancellationToken>? TokenProvider { get; set; }

    /// <summary>当前线程是否正持有注入闸（诊断 / 自测）。</summary>
    public static bool IsHeld => _depth > 0;

    /// <summary>进入一段注入；释放返回值即退出。</summary>
    public static IDisposable Enter(InputKind kind = InputKind.Other, CancellationToken token = default)
    {
        if (_depth > 0)
        {
            _depth++;
            return new Release();
        }

        CancellationToken effective = token.CanBeCanceled
            ? token
            : TokenProvider?.Invoke() ?? CancellationToken.None;

        try
        {
            if (!Gate.Wait(Timeout.Infinite, effective))
                throw new ActionAbortedException("已停止", userCancelled: true);
        }
        catch (OperationCanceledException)
        {
            throw new ActionAbortedException("已停止", userCancelled: true);
        }

        _depth = 1;
        return new Release();
    }

    /// <summary>试运行整段独占输入（由 <c>AutomationRunner.RunCore</c> 包住整个过程）。</summary>
    public static IDisposable EnterRunExclusive(CancellationToken token) =>
        Enter(InputKind.Other, token);

    private sealed class Release : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;

            if (_depth > 1)
            {
                _depth--;
                return;
            }

            _depth = 0;
            Gate.Release();
        }
    }
}
