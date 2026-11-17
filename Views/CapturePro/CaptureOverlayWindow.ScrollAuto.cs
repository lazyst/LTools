using System;
using System.Windows;
using System.Windows.Threading;
using LTools.Features.CapturePro;

namespace LTools.Views.CapturePro;

/// <summary>
/// 长截图的自动滚动：投一格 → 等落定 → 抓帧 → 拼接，节奏按实测成本 + 退避算出来；
/// 连同面板上的自动滚动 / 方向按钮与状态行文案。
/// </summary>
/// <remarks>
/// 用户自己滚的路线在 <c>CaptureOverlayWindow.Scroll.cs</c>；这里只负责「替用户滚」那一半，
/// 以及两种路线共用的状态行。节奏算法见 <see cref="ScrollCadence"/>。
/// </remarks>
public partial class CaptureOverlayWindow
{
    // ============ 自动滚动 ============

    private void ScrollAuto_Click(object sender, RoutedEventArgs e)
    {
        ToggleAutoScroll();
        FocusScrollTarget();        // 点面板会把前台抢过来，投滚轮之前顶回去
    }

    private void ScrollAxis_Click(object sender, RoutedEventArgs e)
    {
        if (!_scrollMode) return;
        _scrollAxis = _scrollAxis == StitchAxis.Vertical ? StitchAxis.Horizontal : StitchAxis.Vertical;
        FocusScrollTarget();
        RestartScroll();
    }

    /// <summary>开关自动滚动（面板按钮 / 空格）。</summary>
    private void ToggleAutoScroll()
    {
        if (!_scrollMode) return;

        if (_scrollAuto)
        {
            StopAutoScroll("自动滚动已停");
            return;
        }

        if (!_scrollReady)
        {
            UpdateScrollStatus("还没读到页面画面，稍等一下再自动滚动");
            return;
        }

        _scrollAuto = true;
        _scrollCadence.Reset();
        _scrollAutoInterval = ScrollAutoIntervalMs;
        _scrollStill = 0;                  // 上一轮停在「到底」的计数别带过来，不然一开就停
        _scrollMiss = 0;
        _scrollAutoClock.Reset();
        UpdateScrollAutoChrome();
        UpdateScrollStatus();
        AutoScrollKick();
    }

    /// <summary>停掉自动滚动（<paramref name="note"/> 会写进状态行，null = 只改状态）。</summary>
    private void StopAutoScroll(string? note = null)
    {
        bool was = _scrollAuto;
        _scrollAuto = false;
        StopAutoTimer();
        RestoreScrollCursor();
        UpdateScrollAutoChrome();
        if (note != null) UpdateScrollStatus(note);
        else if (was) UpdateScrollStatus();
    }

    /// <summary>投一格 + 安排抓帧；正在抓就等这一轮结束（<see cref="ScrollStep"/> 收尾会再排）。</summary>
    private void AutoScrollKick()
    {
        if (!_scrollAuto || !_scrollMode || !_scrollReady) return;
        if (_scrollFull)
        {
            StopAutoScroll("已到长度上限，自动滚动已停");
            return;
        }

        if (_scrollBusy)
        {
            // 上一轮还没回来：等一会儿再投（别让节奏链在这里断掉）
            ArmAutoScroll();
            return;
        }

        FocusScrollTarget();            // 真滚轮兜底路径要靠前台 / 光标路由到页面
        if (!PostScrollStep(-ScrollAutoDelta))
        {
            StopAutoScroll("自动滚动投不出滚轮，已停");
            return;
        }

        _scrollAutoClock.Restart();
        _scrollPending++;
        _scrollDirty = true;
        ScheduleScroll();
    }

    /// <summary>按当前节奏安排下一格：间隔由 <see cref="ScrollCadence"/> 按实测成本 + 退避算出来。</summary>
    private void ArmAutoScroll()
    {
        if (!_scrollAuto || !_scrollMode) return;

        _scrollAutoInterval = _scrollCadence.NextInterval(ScrollAutoIntervalMs, ScrollSettleMs);

        if (_scrollAutoTimer == null)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Input);
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                AutoScrollKick();
            };
            _scrollAutoTimer = timer;
        }

        // 节奏算的是「两次投滚轮之间」的间隔：抓帧 + 拼接已经花掉的时间要扣掉，
        // 否则每格的实际周期 = 间隔 + 成本，节奏会越滚越慢（snow-shot 那边是定时器固定节拍，不存在这笔账）。
        // 下限 25ms：上一轮还在抓帧（`_scrollBusy`）时别把定时器排成 1ms 空转。
        double spent = _scrollAutoClock.IsRunning ? _scrollAutoClock.Elapsed.TotalMilliseconds : 0;
        int wait = (int)Math.Round(Math.Max(0, _scrollAutoInterval - spent));

        _scrollAutoTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(25, wait));
        _scrollAutoTimer.Start();
    }

    private void StopAutoTimer() => _scrollAutoTimer?.Stop();

    /// <summary>面板上的自动滚动按钮：亮起来 = 正在自动滚。</summary>
    private void UpdateScrollAutoChrome()
    {
        if (ScrollAutoBtn == null) return;

        ScrollAutoBtn.IsChecked = _scrollAuto;
        ScrollAutoBtn.ToolTip = _scrollAuto
            ? "自动滚动中：点一下停下（空格）"
            : "自动滚动：让它自己往下滚着拼（空格）";

        if (ScrollAxisBtn != null)
        {
            ScrollAxisBtn.IsChecked = _scrollAxis == StitchAxis.Horizontal;
            ScrollAxisBtn.ToolTip = _scrollAxis == StitchAxis.Vertical
                ? "当前：纵向拼接（往下滚）。点一下改成横向（往右拼），会重新开始"
                : "当前：横向拼接（往右拼）。点一下改回纵向，会重新开始";
        }
    }

    // ============ 状态行 ============

    /// <summary>状态行：拼到多大 + 怎么操作；<paramref name="note"/> 是这一拍要额外说的一句。</summary>
    private void UpdateScrollStatus(string? note = null)
    {
        string text = _scrollHeight > 0
            ? $"{_scrollWidth} × {_scrollHeight} px · {_scrollShown / (double)Math.Max(1, _scrollView):0.#} 屏"
            : "正在读取页面…";

        text += _scrollAxis == StitchAxis.Vertical
            ? "\n滚轮往下滚（页面自己滚）· Enter 完成 · Esc 放弃"
            : "\n滚轮往下滚 = 往右拼 · Enter 完成 · Esc 放弃";

        if (_scrollAuto)
        {
            int fps = (int)Math.Round(1000.0 / Math.Max(1, _scrollAutoInterval));
            text += $"\n自动滚动中 · 约 {fps} 帧/秒 · 空格停下";
        }
        else
        {
            text += "\n空格 = 自动滚动";
        }

        if (_scrollFull) text += "\n已到长度上限";
        if (!string.IsNullOrEmpty(note)) text += "\n" + note;
        ScrollStatus.Text = text;
    }
}
