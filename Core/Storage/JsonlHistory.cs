using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace LTools.Core.Storage;

/// <summary>
/// 倒序、定长的 JSONL 历史：一行一条、整份重写、坏行跳过（运行历史 / 触发记录共用）。
/// </summary>
/// <remarks>
/// <para>
/// 两份历史（<c>state/run-history.jsonl</c> / <c>state/trigger-history.jsonl</c>）
/// 原来逐行相同，差别只有文件名、上限与记录类型；要第三种历史就再 <c>new</c> 一个实例，
/// **别再抄一份**。
/// </para>
/// <para>
/// 放在 <c>state/</c>（是备份域），**别放 <c>logs/</c>**——那里只认 <c>ltools-*</c> /
/// <c>action-*</c> 且会被 <c>LogFiles.Roll</c> 滚动清理。
/// </para>
/// </remarks>
internal sealed class JsonlHistory<T> where T : class
{
    private readonly object _gate = new();
    private readonly List<T> _items = new();   // 倒序：最新在前
    private readonly string _path;
    private readonly int _max;
    private readonly string _logTag;
    private readonly string _what;
    private bool _loaded;

    /// <param name="path">落在数据根下的哪个文件。</param>
    /// <param name="maxRecords">最多留多少条（再多就丢最旧的）。</param>
    /// <param name="logTag"><see cref="AppLog"/> 的标签（<c>Flow</c> / <c>Triggers</c>）。</param>
    /// <param name="what">日志里怎么称呼它（「运行历史」/「触发记录」）。</param>
    public JsonlHistory(string path, int maxRecords, string logTag, string what)
    {
        _path = path;
        _max = Math.Max(1, maxRecords);
        _logTag = logTag;
        _what = what;
    }

    public string FilePath => _path;

    /// <summary>当前记录（倒序快照）。界面只读展示用。</summary>
    public IReadOnlyList<T> Load()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return _items.ToArray();
        }
    }

    /// <summary>
    /// 在锁内改内存（加一条 / 改最新一条 / 清空），改完按需落盘。
    /// </summary>
    /// <param name="change">
    /// 返回 false = 只改了内存、这次不落盘（同秒汇总那种「写放大」场景）。
    /// </param>
    public void Edit(Func<List<T>, bool> change)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (!change(_items)) return;

            if (_items.Count > _max) _items.RemoveRange(_max, _items.Count - _max);
            WriteAll();
        }
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path)) return;
            foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<T>(line) is { } item) _items.Add(item);
                }
                catch
                {
                    // 坏行跳过，别让一条坏记录毁掉整份历史
                }
            }
            if (_items.Count > _max) _items.RemoveRange(_max, _items.Count - _max);
        }
        catch (Exception ex)
        {
            AppLog.Warn(_logTag, $"读{_what}失败：{ex.Message}");
        }
    }

    private void WriteAll()
    {
        try
        {
            AtomicFile.WriteJsonLines(_path, _items);
        }
        catch (Exception ex)
        {
            AppLog.Warn(_logTag, $"写{_what}失败：{ex.Message}");
        }
    }
}
