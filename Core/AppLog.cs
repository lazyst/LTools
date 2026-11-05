using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace LTools.Core;

/// <summary>运行日志级别（数值越大越详细，配置里存小写字符串）。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogLevel
{
    /// <summary>只记错误（崩溃 / 动作失败）。</summary>
    error = 0,

    /// <summary>错误 + 警告（被失败策略吞掉的步骤等）。</summary>
    warn = 1,

    /// <summary>错误 + 警告 + 关键事件（启动、动作、流程、设置变更）。</summary>
    info = 2,

    /// <summary>全部（含每一步骤、取值结果与变量值）。</summary>
    debug = 3,
}

/// <summary>
/// 运行日志：把 LTools 自身的关键事件追加到 <c>&lt;数据根&gt;/logs/ltools-yyyyMMdd.log</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：用户遇到「按了没反应 / 流程某步失败」时，崩溃日志（%TEMP%）只覆盖异常，
/// 看不到正常路径；这里记录启动配置、动作与流程的执行、取值结果、设置变更，供用户 / agent 排查，
/// 也方便开发时复现问题。
/// </para>
/// <para>
/// <b>不阻塞</b>：写入走后台线程 + 无界队列，调用方只做一次入队；钩子回调路径上可以安全调用。
/// 文件按天分，启动时按保留天数清理旧文件。日志只落在本机，不上传。
/// </para>
/// <para>
/// 详细级别（debug）会记录变量值 / 取值结果，可能包含剪贴板或选中文本等内容；
/// 常规级别（info）只记录动作名与结果大小，不记录正文。
/// </para>
/// </remarks>
internal static class AppLog
{
    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>());
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static Thread? _writer;
    private static volatile bool _enabled;
    private static volatile int _level = (int)LogLevel.info;
    private static volatile int _retentionDays = 14;
    private static string _directory = "";
    private static int _closed;

    /// <summary>日志是否启用。</summary>
    public static bool Enabled => _enabled;

    /// <summary>当前记录级别。</summary>
    public static LogLevel Level => (LogLevel)_level;

    /// <summary>日志目录（数据根下的 logs）。</summary>
    public static string Directory => _directory;

    /// <summary>今天对应的日志文件路径（未启用时也返回路径，便于设置页展示）。</summary>
    public static string CurrentFile => string.IsNullOrEmpty(_directory)
        ? ""
        : Path.Combine(_directory, FileName(DateTime.Now));

    /// <summary>
    /// 应用配置（启动时与设置页改动时调用）。关闭后已有队列仍会写完；
    /// 打开会确保后台写入线程已启动，并按保留天数清理旧文件。
    /// </summary>
    public static void Configure(bool enabled, LogLevel level, int retentionDays)
    {
        _level = (int)level;
        _retentionDays = retentionDays < 0 ? 0 : retentionDays;
        _directory = AppPaths.LogsDir;
        _enabled = enabled;

        if (!enabled) return;
        EnsureWriter();
        if (_retentionDays > 0)
            Task.Run(() => Cleanup(_retentionDays));
    }

    public static void Debug(string source, string message) => Write(LogLevel.debug, source, message, null);
    public static void Info(string source, string message) => Write(LogLevel.info, source, message, null);
    public static void Warn(string source, string message) => Write(LogLevel.warn, source, message, null);

    public static void Error(string source, string message) => Write(LogLevel.error, source, message, null);
    public static void Error(string source, string message, Exception ex) => Write(LogLevel.error, source, message, ex);

    /// <summary>把队列写完并关闭后台线程（退出时调用，可重复调用）。</summary>
    public static void Shutdown()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _enabled = false;
        try { Queue.CompleteAdding(); } catch { /* 已关闭 */ }
        try { _writer?.Join(1500); } catch { /* 忽略 */ }
    }

    private static void Write(LogLevel level, string source, string message, Exception? ex)
    {
        if (!_enabled || (int)level > _level || Volatile.Read(ref _closed) != 0) return;

        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Tag(level)}] [{source}] {message}";
        if (ex != null)
            line += Environment.NewLine + ex;

        try { Queue.Add(line); }
        catch (InvalidOperationException) { /* 队列已关闭 */ }
        catch { /* 日志自身永不抛出 */ }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.error => "ERROR",
        LogLevel.warn => "WARN ",
        LogLevel.info => "INFO ",
        _ => "DEBUG",
    };

    private static void EnsureWriter()
    {
        lock (Gate)
        {
            if (_writer != null) return;
            _writer = new Thread(WriterLoop)
            {
                IsBackground = true,
                Name = "LTools-log",
            };
            _writer.Start();
        }
    }

    private static void WriterLoop()
    {
        StreamWriter? writer = null;
        DateTime day = default;
        try
        {
            foreach (string line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    DateTime today = DateTime.Now.Date;
                    if (writer == null || today != day)
                    {
                        writer?.Dispose();
                        System.IO.Directory.CreateDirectory(_directory);
                        writer = new StreamWriter(
                            Path.Combine(_directory, FileName(DateTime.Now)),
                            append: true, Utf8)
                        { AutoFlush = true };
                        day = today;
                    }
                    writer.WriteLine(line);
                }
                catch
                {
                    // 磁盘满 / 无权限等：日志失败绝不影响主流程
                }
            }
        }
        finally
        {
            try { writer?.Dispose(); } catch { /* 忽略 */ }
        }
    }

    private static string FileName(DateTime day) => $"ltools-{day:yyyyMMdd}.log";

    /// <summary>删除超过保留天数的日志文件（后台线程执行）。</summary>
    private static void Cleanup(int days)
    {
        try
        {
            if (string.IsNullOrEmpty(_directory) || !System.IO.Directory.Exists(_directory)) return;
            DateTime cutoff = DateTime.Now.Date.AddDays(-days);
            foreach (string file in System.IO.Directory.EnumerateFiles(_directory, "ltools-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { /* 单个文件被占用时跳过 */ }
            }
        }
        catch { /* 清理失败不影响运行 */ }
    }
}
