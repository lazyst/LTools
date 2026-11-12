using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LTools.Core;
using LTools.Core.Actions;

namespace LTools.Features.Script;

/// <summary>一次 AHK 脚本执行的结果。</summary>
internal sealed class AhkRunResult
{
    public int ExitCode { get; init; }

    public string Stdout { get; init; } = "";

    public string Stderr { get; init; } = "";

    /// <summary>脚本里 <c>LT_Set</c> 写回的变量（一律是文本）。</summary>
    public Dictionary<string, string> Variables { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>失败说明（已带「第 N 行」，行号按用户代码算）；null = 成功。</summary>
    public string? Error { get; init; }
}

/// <summary>语法检查结果；<see cref="Message"/> 只失败时有内容。</summary>
internal sealed class AhkCheckResult
{
    public bool Ok { get; init; }

    public string Message { get; init; } = "";
}

/// <summary>
/// 内置 AutoHotkey v2 的调用层：把用户代码包成「前言 + 代码」临时脚本，跑起来，
/// 再把变量、stdout 与错误收回成 LTools 的值。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么要前言</b>：AHK 的运行时错误默认弹模态对话框（流程会一直卡着等人点）。
/// 前言里注册 <c>OnError</c>，把 <c>err.Message / File / Line / Stack</c> 写成 JSON 再
/// <c>ExitApp 3</c>，弹窗就不会出现；<c>OnExit</c> 负责把 <c>LT_Set</c> 攒下的变量落盘。
/// <c>#Warn All, Off</c> 关掉「变量可能未赋值」这类**警告**对话框：它同样是模态窗口，
/// 而且在未定义变量 / 函数时挡在真正的运行时错误前面。关掉之后这类写法会走到
/// <c>OnError</c>，错误消息与行号反而更准确。
/// </para>
/// <para>
/// <b>行号</b>：报错行号是生成文件里的行号，减去前言占的行数才是用户代码的行号
/// （<see cref="ScriptFile.UserLineOffset"/>）。错误信息里一律报用户行号。
/// </para>
/// <para>
/// <b>编码</b>：脚本必须写 UTF-8 <b>带 BOM</b>（AHK 靠 BOM 判定 UTF-8，否则按系统 ANSI 读）。
/// stdout 由脚本决定编码（<c>LT_Print</c> 固定 UTF-8），读取时先按 UTF-8 严格解码，
/// 失败再退回系统 OEM 代码页（脚本里直接 <c>FileAppend …,"*"</c> 就是 ANSI）。
/// </para>
/// </remarks>
internal static class AhkRunner
{
    /// <summary>随包运行时的相对路径（发布包 / bin 输出目录下）。</summary>
    public const string RelativeExePath = @"runtime\autohotkey\AutoHotkey64.exe";

    /// <summary>AHK 自己的加载期错误（语法错误）。</summary>
    public const int ExitSyntaxError = 2;

    /// <summary>前言里 <c>OnError</c> 统一用的退出码。</summary>
    public const int ExitScriptError = 3;

    /// <summary>语法检查的最长等待（校验不会执行脚本，正常 100ms 内就回来了）。</summary>
    private const int CheckTimeoutMs = 20_000;

    private static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    private static readonly Regex SyntaxErrorLine = new(
        @"\((?<line>\d+)\)\s*:\s*==>\s*(?<message>.+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Specifically = new(
        @"^\s*Specifically:\s*(?<detail>.+)$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    private static readonly IReadOnlyDictionary<string, Value> EmptyVariables =
        new Dictionary<string, Value>();

    private static Encoding? _oem;

    public static string ExePath => System.IO.Path.Combine(AppContext.BaseDirectory, RelativeExePath);

    public static bool IsAvailable => File.Exists(ExePath);

    /// <summary>确保内置运行时在位；缺了给一句能照做的提示。</summary>
    public static void EnsureAvailable()
    {
        if (IsAvailable) return;
        throw new ActionException(
            "没有找到内置的 AutoHotkey（" + RelativeExePath + "）。" +
            "发布包应带 runtime\\autohotkey；开发环境请运行 tools\\fetch-autohotkey.ps1 重新下载。");
    }

    // ============ 语法检查（不执行脚本）============

    /// <summary>
    /// <c>/validate</c> 只做语法检查、不执行任何语句（实测不会产生脚本副作用），
    /// 所以「检查语法」按钮可以放心点。
    /// </summary>
    public static AhkCheckResult Check(string code)
    {
        EnsureAvailable();

        ScriptFile file = WriteScript(code, EmptyVariables);
        try
        {
            ProcessOutcome result = Start(
                file, new[] { "/ErrorStdOut", "/validate" }, "", CheckTimeoutMs, null);

            if (result.Cancelled) return new AhkCheckResult { Ok = true };
            if (result.TimedOut)
                return new AhkCheckResult { Message = $"检查超时（{CheckTimeoutMs / 1000} 秒）" };
            if (result.ExitCode == 0 && result.Stderr.Length == 0)
                return new AhkCheckResult { Ok = true };

            string message = FormatSyntaxError(result.Stderr, file)
                ?? $"AutoHotkey 退出码 {result.ExitCode}，但没有输出错误信息";
            return new AhkCheckResult { Message = message };
        }
        finally
        {
            Cleanup(file);
        }
    }

    // ============ 执行 ============

    public static AhkRunResult Run(
        string code,
        IReadOnlyDictionary<string, Value> variables,
        int timeoutMs,
        string workdir,
        StepContext? ctx)
    {
        EnsureAvailable();

        ScriptFile file = WriteScript(code, variables);
        try
        {
            ProcessOutcome result = Start(
                file, new[] { "/ErrorStdOut" }, workdir, timeoutMs, ctx);

            if (result.Cancelled) throw new ActionAbortedException("已停止", userCancelled: true);

            // 失败时也把变量收回来：调用方可以用来看脚本跑到哪一步了（写不写回由动作单元决定）
            Dictionary<string, string> values = ReadVariables(file.OutPath);

            string? error = ReadRuntimeError(file.ErrPath, file);
            if (error == null && result.ExitCode == ExitSyntaxError)
                error = FormatSyntaxError(result.Stderr, file);

            if (error == null && result.TimedOut)
                error = $"运行超过 {timeoutMs} 毫秒，已中止";
            else if (error == null && result.ExitCode != 0 && result.ExitCode != ExitScriptError)
                error = $"脚本以退出码 {result.ExitCode} 结束" +
                        (result.Stderr.Length > 0 ? "：" + result.Stderr.Trim() : "");

            return new AhkRunResult
            {
                ExitCode = result.ExitCode,
                Stdout = result.Stdout.TrimEnd('\r', '\n'),
                Stderr = result.Stderr.TrimEnd('\r', '\n'),
                Variables = values,
                Error = error,
            };
        }
        finally
        {
            Cleanup(file);
        }
    }

    // ============ 生成脚本 ============

    private sealed class ScriptFile
    {
        public string Directory = "";
        public string Path = "";
        public string OutPath = "";
        public string ErrPath = "";

        /// <summary>前言里换行符的个数 = 用户代码第 1 行之前的行数。</summary>
        public int UserLineOffset;
    }

    private static ScriptFile WriteScript(string code, IReadOnlyDictionary<string, Value> variables)
    {
        var file = new ScriptFile
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "LTools", "script",
                IdFactory.ShortId(4)),
        };
        System.IO.Directory.CreateDirectory(file.Directory);
        file.Path = System.IO.Path.Combine(file.Directory, "script.ahk");
        file.OutPath = System.IO.Path.Combine(file.Directory, "vars.json");
        file.ErrPath = System.IO.Path.Combine(file.Directory, "error.json");

        string prelude = BuildPrelude(file, variables);
        file.UserLineOffset = prelude.Count(c => c == '\n');

        // 代码原样接在前言后面：前言已用换行结尾，用户第 1 行 = 生成文件第 UserLineOffset + 1 行。
        File.WriteAllText(file.Path, prelude + code, Utf8Bom);
        return file;
    }

    private static string BuildPrelude(ScriptFile file, IReadOnlyDictionary<string, Value> variables)
    {
        var values = new StringBuilder();
        foreach (var kv in variables)
        {
            string name = kv.Key?.Trim() ?? "";
            if (name.Length == 0) continue;
            values.Append("LT_Vars[").Append(AhkLiteral(name)).Append("] := ")
                  .Append(AhkLiteral(kv.Value?.AsText() ?? "")).Append('\n');
        }

        return PreludeTemplate
            .Replace("@@OUT@@", AhkLiteral(file.OutPath))
            .Replace("@@ERR@@", AhkLiteral(file.ErrPath))
            .Replace("@@VARS@@", values.ToString());
    }

    /// <summary>
    /// AHK v2 的字符串字面量：转义符是反引号，所以 <c>`</c>、<c>"</c>、换行都要转义。
    /// 不这样做的话，变量值里的引号会把生成的代码撕开（历史上最常见的一类注入）。
    /// </summary>
    private static string AhkLiteral(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        sb.Append('"');
        foreach (char c in text)
        {
            switch (c)
            {
                case '`': sb.Append("``"); break;
                case '"': sb.Append("`\""); break;
                case '\r': sb.Append("`r"); break;
                case '\n': sb.Append("`n"); break;
                case '\t': sb.Append("`t"); break;
                default: sb.Append(c); break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// 脚本前言。<c>@@OUT@@</c> / <c>@@ERR@@</c> 是变量与错误输出文件，
    /// 变量赋值追加在 <see cref="BuildPrelude"/> 里。
    /// </summary>
    private const string PreludeTemplate = """
#Requires AutoHotkey v2.0
#NoTrayIcon
#Warn All, Off
; ===== LTools 脚本运行器（自动生成，请勿修改）=====
LT_Vars := Map()
LT_Out := Map()
LT_OutFile := @@OUT@@
LT_ErrFile := @@ERR@@
LT_Var(name, default := "") {
    global LT_Vars
    return LT_Vars.Has(name) ? LT_Vars[name] : default
}
LT_Set(name, value) {
    global LT_Out
    LT_Out[name] := "" . value
}
LT_Json(s) {
    s := StrReplace(s, "\", "\\")
    s := StrReplace(s, '"', '\"')
    s := StrReplace(s, "`r", "\r")
    s := StrReplace(s, "`n", "\n")
    s := StrReplace(s, "`t", "\t")
    return s
}
LT_Dump(map, file) {
    s := "{", first := true
    for k, v in map {
        if !first
            s .= ","
        first := false
        s .= '"' LT_Json(k) '":"' LT_Json(v) '"'
    }
    if FileExist(file)
        FileDelete file
    FileAppend s "}", file, "UTF-8"
}
LT_OnExit(reason, code) {
    global LT_Out, LT_OutFile
    try LT_Dump(LT_Out, LT_OutFile)
    return false
}
LT_OnError(err, mode) {
    global LT_ErrFile
    try {
        m := '{"message":"' LT_Json(err.Message) '","file":"' LT_Json(err.File) '","line":' err.Line ',"stack":"' LT_Json(err.Stack) '"}'
        if FileExist(LT_ErrFile)
            FileDelete LT_ErrFile
        FileAppend m, LT_ErrFile, "UTF-8"
    }
    ExitApp 3
    return true
}
LT_Print(text) {
    FileAppend text "`n", "*", "UTF-8"
}
OnExit(LT_OnExit)
OnError(LT_OnError)
@@VARS@@; ===== 用户脚本 =====

""";

    // ============ 进程 ============

    private sealed class ProcessOutcome
    {
        public int ExitCode;
        public string Stdout = "";
        public string Stderr = "";
        public bool TimedOut;
        public bool Cancelled;
    }

    private static ProcessOutcome Start(
        ScriptFile file,
        string[] arguments,
        string workdir,
        int timeoutMs,
        StepContext? ctx)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments) psi.ArgumentList.Add(argument);
        psi.ArgumentList.Add(file.Path);

        if (!string.IsNullOrWhiteSpace(workdir) && Directory.Exists(workdir))
            psi.WorkingDirectory = workdir;

        using var process = Process.Start(psi)
            ?? throw new ActionException("无法启动 AutoHotkey：" + ExePath);

        // 先把两条流读空，避免子进程写满管道缓冲区后卡死
        Task<byte[]> stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream);
        Task<byte[]> stderrTask = ReadAllBytesAsync(process.StandardError.BaseStream);

        bool timedOut = false;
        bool cancelled = false;
        var started = Stopwatch.StartNew();

        while (!process.HasExited)
        {
            if (timeoutMs > 0 && started.ElapsedMilliseconds >= timeoutMs)
            {
                timedOut = true;
                TryKill(process);
                break;
            }

            // 有流程上下文就用它的可取消等待（停止热键能立刻收手）；没有就纯睡
            bool alive = ctx != null ? ctx.Wait(50) : Sleep(50);
            if (!alive)
            {
                cancelled = true;
                TryKill(process);
                break;
            }
        }

        if (timedOut || cancelled) process.WaitForExit(5000);

        return new ProcessOutcome
        {
            ExitCode = SafeExitCode(process),
            Stdout = Decode(stdoutTask.GetAwaiter().GetResult()),
            Stderr = Decode(stderrTask.GetAwaiter().GetResult()),
            TimedOut = timedOut,
            Cancelled = cancelled,
        };
    }

    private static bool Sleep(int milliseconds)
    {
        Thread.Sleep(milliseconds);
        return true;
    }

    private static int SafeExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch { return -1; }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { /* 已退出 / 无权限 */ }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// <summary>脚本的 stdout 可能是 UTF-8（<c>LT_Print</c>），也可能是系统 ANSI（裸 <c>FileAppend</c>）。</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return "";
        try { return StripBom(StrictUtf8.GetString(bytes)); }
        catch (DecoderFallbackException) { return Oem().GetString(bytes); }
    }

    private static string StripBom(string text) =>
        text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;

    private static Encoding Oem()
    {
        if (_oem != null) return _oem;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        }
        catch
        {
            _oem = Encoding.Default;
        }
        return _oem;
    }

    // ============ 结果解析 ============

    private static Dictionary<string, string> ReadVariables(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return values;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ReadJsonBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return values;
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                values[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? "",
                    JsonValueKind.Null => "",
                    _ => property.Value.GetRawText(),
                };
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Script", $"读取 AHK 脚本输出变量失败：{ex.Message}");
        }

        return values;
    }

    /// <summary>AHK 写文件带 BOM，<see cref="JsonDocument.Parse(ReadOnlyMemory{Byte})"/> 不认，先剥掉。</summary>
    private static byte[] ReadJsonBytes(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? bytes[3..]
            : bytes;
    }

    private static string? ReadRuntimeError(string path, ScriptFile file)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(ReadJsonBytes(path));
            JsonElement root = doc.RootElement;
            string message = Text(root, "message");
            string errorFile = Text(root, "file");
            int line = root.TryGetProperty("line", out var lineElement)
                && lineElement.TryGetInt32(out int parsed) ? parsed : 0;

            if (message.Length == 0) message = "脚本运行时出错";
            return WithLine(message, line, errorFile, file);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Script", $"读取 AHK 脚本错误信息失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>加载期（语法）错误的文本形如 <c>path (12) : ==&gt; Missing "}"</c>。</summary>
    private static string? FormatSyntaxError(string stderr, ScriptFile file)
    {
        string text = (stderr ?? "").Trim();
        if (text.Length == 0) return null;

        Match match = SyntaxErrorLine.Match(text);
        if (!match.Success) return text;

        string message = match.Groups["message"].Value.Trim();
        Match detail = Specifically.Match(text);
        if (detail.Success) message += "（" + detail.Groups["detail"].Value.Trim() + "）";

        int line = int.TryParse(match.Groups["line"].Value, out int parsed) ? parsed : 0;
        return WithLine(message, line, file.Path, file);
    }

    /// <summary>把生成文件的行号换算成用户代码的行号。</summary>
    private static string WithLine(string message, int rawLine, string errorFile, ScriptFile file)
    {
        bool ours = errorFile.Length == 0
            || string.Equals(errorFile, file.Path, StringComparison.OrdinalIgnoreCase);
        if (!ours || rawLine <= file.UserLineOffset) return message;

        return $"第 {rawLine - file.UserLineOffset} 行：{message}";
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static void Cleanup(ScriptFile file)
    {
        try { System.IO.Directory.Delete(file.Directory, recursive: true); }
        catch { /* 文件被占用 / 已删除，留给系统清理临时目录 */ }
    }
}
