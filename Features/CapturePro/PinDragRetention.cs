using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LTools.Core;

namespace LTools.Features.CapturePro;

/// <summary>
/// 拖出贴图时临时文件的保留期（照上游 <c>screenshot_pinned_drag_export::FileRetention</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 拖出到别的程序时传的是**临时文件路径**，接收方可能在拖拽循环结束之后才去读（浏览器、下载器）。
/// 所以文件不能拖完就删：给它一个 30 分钟宽限期，同时用「文件数 ≤ 64 / 总量 ≤ 512 MB」挡住不停拖出。
/// 拖拽被取消（<c>DragDropEffects.None</c>）时立刻删，不占额度。
/// </para>
/// <para>
/// 删除失败（接收方还握着文件句柄）时把租约往后推 60 秒再试，额度继续占着——不然会「删不掉又放进来新的」，
/// 最后谁都清理不了。时钟与删除动作都可注入，<c>--selftest</c> 直接断言预算与过期。
/// </para>
/// </remarks>
internal sealed class PinDragRetention
{
    /// <summary>同时保留的临时目录上限（上游 <c>MaximumFileCount</c>）。</summary>
    public const int MaximumFileCount = 64;

    /// <summary>保留的临时文件总量上限（上游 <c>MaximumBytes</c> = 512 MB）。</summary>
    public const long MaximumBytes = 512L * 1024 * 1024;

    /// <summary>拖拽结束后保留多久（上游 <c>TransferGraceMilliseconds</c> = 30 分钟）。</summary>
    public const long TransferGraceMilliseconds = 30 * 60 * 1000;

    /// <summary>删不掉时多久后重试（上游是 60 秒）。</summary>
    public const long RetryMilliseconds = 60 * 1000;

    /// <summary>临时目录的根（<c>%TEMP%\ltools-drag</c>）。</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), "ltools-drag");

    /// <summary>一份保留中的临时目录。</summary>
    internal sealed class Lease
    {
        public string Directory { get; init; } = "";

        public long Bytes { get; init; }

        /// <summary>null = 还在准备 / 正在拖，不算过期。</summary>
        public long? ExpiresAt { get; set; }
    }

    private static readonly object Gate = new();

    private static Timer? _sweeper;

    /// <summary>进程内共用的一份（每次拖出都往这里登记）。</summary>
    public static PinDragRetention Shared { get; } = new();

    private readonly Func<long> _now;
    private readonly Func<string, bool> _remove;
    private readonly List<Lease> _leases = new();
    private long _bytes;

    public PinDragRetention(Func<long>? now = null, Func<string, bool>? remove = null)
    {
        _now = now ?? (() => Environment.TickCount64);
        _remove = remove ?? RemoveDirectory;
    }

    /// <summary>现在保留着几个目录 / 多少字节。</summary>
    public int Count
    {
        get { lock (Gate) return _leases.Count; }
    }

    public long Bytes
    {
        get { lock (Gate) return _bytes; }
    }

    /// <summary>为刚准备的临时目录预留额度；超出预算返回 <c>null</c>（调用方删目录并提示）。</summary>
    public Lease? Reserve(string directory, long bytes)
    {
        if (string.IsNullOrWhiteSpace(directory) || bytes < 0) return null;

        lock (Gate)
        {
            SweepUnsafe();

            if (_leases.Count >= MaximumFileCount) return null;
            if (bytes > MaximumBytes - _bytes) return null;

            var lease = new Lease { Directory = directory, Bytes = bytes };
            _leases.Add(lease);
            _bytes += bytes;
            EnsureSweeper();
            return lease;
        }
    }

    /// <summary>
    /// 拖拽循环结束后结账：<paramref name="dropped"/> = 有程序接了（保留宽限期），
    /// 否则立刻删。
    /// </summary>
    public void Complete(Lease? lease, bool dropped)
    {
        if (lease == null) return;

        lock (Gate)
        {
            if (!_leases.Contains(lease)) return;
            lease.ExpiresAt = dropped ? _now() + TransferGraceMilliseconds : _now();
            SweepUnsafe();
        }
    }

    /// <summary>把到期的临时目录删掉；删不掉的推到 60 秒后再试（额度继续占着）。</summary>
    public void Sweep()
    {
        lock (Gate) SweepUnsafe();
    }

    private void SweepUnsafe()
    {
        long now = _now();

        for (int i = _leases.Count - 1; i >= 0; i--)
        {
            var lease = _leases[i];
            if (lease.ExpiresAt is not { } expiresAt || expiresAt > now) continue;

            bool gone = !Directory.Exists(lease.Directory);
            if (!gone)
            {
                try { gone = _remove(lease.Directory); }
                catch (Exception ex)
                {
                    AppLog.Warn("PinDragRetention", "清理临时文件失败：" + ex.Message);
                    gone = false;
                }
            }

            if (!gone)
            {
                lease.ExpiresAt = now + RetryMilliseconds;
                continue;
            }

            _leases.RemoveAt(i);
            _bytes = Math.Max(0, _bytes - lease.Bytes);
        }
    }

    private void EnsureSweeper()
    {
        if (_sweeper != null) return;
        _sweeper = new Timer(_ =>
        {
            try { Shared.Sweep(); }
            catch (Exception ex) { AppLog.Warn("PinDragRetention", "定时清理失败：" + ex.Message); }
        }, null, TransferGraceMilliseconds / 4, TransferGraceMilliseconds / 4);
    }

    private static bool RemoveDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return true;
        Directory.Delete(directory, recursive: true);
        return !Directory.Exists(directory);
    }

    /// <summary>
    /// 新建一个拖出用的临时目录（<c>%TEMP%\ltools-drag\&lt;随机&gt;</c>）。
    /// </summary>
    public static string CreateDirectory()
    {
        string dir = Path.Combine(Root, Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 启动时清一次上次进程留下的临时目录（崩溃 / 被结束都来不及删），只清 30 分钟以前的，
    /// 免得把别的进程正在拖的文件删掉。
    /// </summary>
    public static int CleanStale(long? now = null)
    {
        int removed = 0;
        try
        {
            if (!Directory.Exists(Root)) return 0;

            long current = now ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (string dir in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    long age = current - new DateTimeOffset(Directory.GetLastWriteTimeUtc(dir))
                        .ToUnixTimeMilliseconds();
                    if (age < TransferGraceMilliseconds) continue;
                    Directory.Delete(dir, recursive: true);
                    removed++;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("PinDragRetention", "清理旧临时目录失败：" + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinDragRetention", "扫描临时目录失败：" + ex.Message);
        }

        return removed;
    }

    /// <summary>把目录连同内容一起删掉（准备失败 / 没拿到额度时用）。</summary>
    public static void Discard(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn("PinDragRetention", "丢弃临时目录失败：" + ex.Message);
        }
    }
}
