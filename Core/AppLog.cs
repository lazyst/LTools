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
/// <b>不阻塞</b>：写入走后台线程 + 有界队列（2 万行，满了丢新行并记数），调用方只做一次入队；
/// 钩子回调路径上可以安全调用。文件按天分、单文件 20 MB 滚动，启动时按保留天数清理旧文件。
/// 日志只落在本机，不上传。
/// </para>
/// <para>
/// 详细级别（debug）会记录变量值 / 取值结果，可能包含剪贴板或选中文本等内容；
/// 常规级别（info）只记录动作名与结果大小，不记录正文。
/// </para>
/// </remarks>
internal static class AppLog
{
    /// <summary>
    /// 队列上限（行）。日志是异步写的，磁盘慢 / 被占用时队列会涨；不设上限等于把内存交给
    /// 「一个在循环里狂写日志的动作」。满了丢新行并记数，队列追平后补一行说明丢了多少。
    /// </summary>
    private const int MaxQueueLines = 20_000;

    private static readonly BlockingCollection<string> Queue =
        new(new ConcurrentQueue<string>(), MaxQueueLines);

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static Thread? _writer;
    private static volatile bool _enabled;
    private static volatile int _level = (int)LogLevel.info;
    private static volatile int _retentionDays = 14;
    private static string _directory = "";
    private static int _closed;

    /// <summary>队列满时丢弃的行数（写线程追平后一次性报出来）。</summary>
    private static long _dropped;

    /// <summary>当前这次运行的短 id（<c>run-3f2a</c>）；不在运行期为空串。</summary>
    private static volatile string _runId = "";

    /// <summary>日志是否启用。</summary>
    public static bool Enabled => _enabled;

    /// <summary>当前记录级别。</summary>
    public static LogLevel Level => (LogLevel)_level;

    /// <summary>日志目录（数据根下的 logs）。</summary>
    public static string Directory => _directory;

    /// <summary>当前运行的 id（没在跑动作为 null）；失败报告按它过滤日志行。</summary>
    public static string? CurrentRunId => _runId.Length == 0 ? null : _runId;

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

    /// <summary>
    /// 标记「从现在起属于这次运行」：之后写的每一行都带 <c>[run-xxxxxx]</c>。
    /// 返回的对象释放时恢复上一层（没有就是清空），所以真实运行 / 试运行都写
    /// <c>using var scope = AppLog.BeginRun(id);</c>。
    /// </summary>
    /// <remarks>
    /// 用静态字段而不是 AsyncLocal：自动化同一时刻只允许跑一个（<c>AutomationRunner</c> 的互斥），
    /// 而日志来自钩子、窗口、后台线程各处，用 AsyncLocal 反而会漏掉那些行。
    /// </remarks>
    public static IDisposable BeginRun(string runId)
    {
        string previous = _runId;
        _runId = (runId ?? "").Trim();
        return new RunScope(previous);
    }

    private sealed class RunScope(string previous) : IDisposable
    {
        public void Dispose() => _runId = previous;
    }

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

        string run = _runId.Length > 0 ? $"[{_runId}] " : "";
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {run}[{Tag(level)}] [{source}] {message}";
        if (ex != null)
            line += Environment.NewLine + ex;

        // 队列满就丢新行并记数（写线程追平后补一行说明），绝不阻塞调用方——钩子路径也会调这里。
        if (!Queue.TryAdd(line)) Interlocked.Increment(ref _dropped);
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.error => "ERROR",
        LogLevel.warn => "WARN ",
        LogLevel.info => "INFO ",
        _ => "DEBUG",
    };

    /// <summary>
    /// 等异步队列写完（最多 <paramref name="timeoutMs"/> 毫秒）。写失败报告前调一次，
    /// 否则报告里「这次运行的相关日志」会落后最后几行。
    /// </summary>
    public static void Flush(int timeoutMs = 500)
    {
        if (!_enabled) return;
        long deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Queue.Count > 0 && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
    }

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

                    // 队列满时丢过行：等追平了补一句，别让「日志少了」变成没人知道的谜。
                    if (Volatile.Read(ref _dropped) > 0 && Queue.Count == 0)
                    {
                        long dropped = Interlocked.Exchange(ref _dropped, 0);
                        if (dropped > 0)
                            writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [WARN ] [Log] " +
                                             $"日志队列已满，丢弃 {dropped} 行（上限 {MaxQueueLines} 行）");
                    }

                    // 单文件到上限就滚动成 name.1.log（.2、.3 顺延），下一条会重开今天的文件。
                    if (writer.BaseStream.Length >= LogFiles.MaxBytes)
                    {
                        string current = Path.Combine(_directory, FileName(DateTime.Now));
                        writer.Dispose();
                        writer = null;
                        LogFiles.Roll(current);
                    }
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

    /// <summary>删除超过保留天数的日志文件（后台线程执行）。应用 / 动作 / 失败报告一起清。</summary>
    private static void Cleanup(int days)
    {
        try
        {
            if (string.IsNullOrEmpty(_directory) || !System.IO.Directory.Exists(_directory)) return;
            DateTime cutoff = DateTime.Now.Date.AddDays(-days);
            foreach (string file in System.IO.Directory.EnumerateFiles(_directory, "*.log"))
            {
                try
                {
                    // 只认 ltools-* / action-* / last-failure*（含滚动出来的 .1.log），用户自己放进 logs 的文件不动。
                    if (!LogFiles.IsLogFile(Path.GetFileName(file))) continue;
                    if (File.GetLastWriteTime(file) < cutoff) File.Delete(file);
                }
                catch { /* 单个文件被占用时跳过 */ }
            }
        }
        catch { /* 清理失败不影响运行 */ }
    }
}
