using System;
using System.Threading;
using LTools.Config;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 截图样式记忆的读写：进程内缓存一份 <see cref="ToolStyleSet"/>，改动**防抖 300ms 后**只写
/// <c>configs/ui.json</c> 的 <c>CaptureStyles</c>。
/// </summary>
/// <remarks>
/// 为什么不每次改都写：样式是拖滑块改的，每一下都走「读整份聚合 + 原子写」会卡住鼠标
/// （低级鼠标钩子与 UI 同线程）。上游同样只记「成功提交」的外观。关窗 / 导入备份前调
/// <see cref="Flush"/>，别让最后一下丢在缓存里。
/// </remarks>
internal static class CaptureStyleStore
{
    private const int SaveDelayMs = 300;

    private static readonly object Gate = new();
    private static ToolStyleSet? _cached;
    private static System.Threading.Timer? _timer;
    private static bool _dirty;

    /// <summary>当前样式（首次访问读盘，读失败按默认）。</summary>
    public static ToolStyleSet Current
    {
        get
        {
            lock (Gate) return _cached ??= Load();
        }
    }

    /// <summary>改动之后调一次：300ms 内没新改动就落盘。</summary>
    public static void SaveSoon()
    {
        lock (Gate)
        {
            if (_cached == null) return;
            _dirty = true;
            _timer ??= new System.Threading.Timer(_ => Flush(), null,
                Timeout.Infinite, Timeout.Infinite);
            _timer.Change(SaveDelayMs, Timeout.Infinite);
        }
    }

    /// <summary>立刻落盘（关窗 / 退出 / 导入备份前调）。没有待写的东西就什么都不做。</summary>
    public static void Flush()
    {
        ToolStyleSet? set;
        lock (Gate)
        {
            if (!_dirty) return;
            _dirty = false;
            set = _cached;
        }
        if (set != null) Write(set);
    }

    /// <summary>丢掉缓存（导入备份 / 自检夹具改了 <c>ui.json</c> 之后调）。</summary>
    public static void Forget()
    {
        Flush();
        lock (Gate) _cached = null;
    }

    private static ToolStyleSet Load()
    {
        try
        {
            string path = ConfigStore.ConfigPath;
            if (string.IsNullOrEmpty(path)) return ToolStyleSet.FromFile(null);

            return ToolStyleSet.FromFile(UiStore.Read(path).CaptureStyles);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "读取截图样式失败：" + ex.Message);
            return ToolStyleSet.FromFile(null);
        }
    }

    private static void Write(ToolStyleSet set)
    {
        try
        {
            void Change(UiStateFile ui) => ui.CaptureStyles = set.ToFile();

            if (string.IsNullOrEmpty(ConfigStore.ConfigPath)) UiStore.Modify(Change);
            else UiStore.Modify(ConfigStore.ConfigPath, Change);
        }
        catch (Exception ex)
        {
            AppLog.Warn("CapturePro", "保存截图样式失败：" + ex.Message);
        }
    }
}
