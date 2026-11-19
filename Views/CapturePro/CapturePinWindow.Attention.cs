using System;
using System.Diagnostics;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 抖动提醒：贴到重复内容时把已经贴着的那张抖一下（照上游 <c>shakeForAttention</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 顺序照上游：先让窗口看得见（被分组藏起来就显示 / 从缩略图还原 / 退出收顶部），
/// 锁定的贴图不抖（它本来就不该动）；然后 300ms 水平抖动，幅度 6 DIP，
/// 关键帧见 <see cref="PinMath.ShakeOffset"/>。
/// </para>
/// <para>
/// 抖的是 <c>Left</c>（WPF 窗口几何），只有 UI 线程能改，所以用
/// <see cref="DispatcherTimer"/> 推；窗口关掉 / 开始拖动 / 再次抖动之前必须停掉，
/// 否则会一直拽着 <c>Left</c>。
/// </para>
/// </remarks>
public partial class CapturePinWindow
{
    private DispatcherTimer? _attentionTimer;
    private double? _attentionOrigin;
    private readonly Stopwatch _attentionClock = new();

    /// <summary>这张贴图正在抖动提醒。</summary>
    internal bool AttentionShaking => _attentionTimer != null;

    /// <summary>抖一下（贴到重复内容时由 <see cref="CapturePinManager.HandleDuplicate"/> 调）。</summary>
    internal void ShakeForAttention()
    {
        if (_attentionTimer != null) return;      // 已经在抖：别叠第二条动画

        if (!IsVisible) Show();                    // 被分组藏起来 / 从管理页来
        if (_locked) return;                       // 锁定的贴图不动（照上游 m_lockedMode 直接 return）

        ExitHideToTop();
        if (_thumb) SetThumbnail(false);

        _attentionOrigin = Left;
        _attentionClock.Restart();

        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        timer.Tick += (_, _) =>
        {
            if (_attentionOrigin is not { } origin)
            {
                StopAttentionShake();
                return;
            }

            double progress = _attentionClock.Elapsed.TotalMilliseconds / PinMath.ShakeDurationMs;
            if (progress >= 1 || !IsVisible)
            {
                StopAttentionShake();
                return;
            }

            Left = origin + Math.Round(PinMath.ShakeOffset(progress));
        };

        _attentionTimer = timer;
        timer.Start();
    }

    /// <summary>停掉抖动并回到原位（关窗 / 开始拖动 / 再次抖之前都要调）。</summary>
    internal void StopAttentionShake()
    {
        _attentionTimer?.Stop();
        _attentionTimer = null;

        if (_attentionOrigin is not { } origin) return;
        _attentionOrigin = null;
        Left = origin;
    }
}
