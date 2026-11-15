using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using LTools.Core;
using LTools.Core.Actions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace LTools.Features.Script;

/// <summary>一次 C# 脚本执行的结果。</summary>
internal sealed class CSharpRunResult
{
    public Value Value { get; init; } = Value.Empty;

    public IReadOnlyList<string> Printed { get; init; } = Array.Empty<string>();

    /// <summary>失败说明（编译错误带用户行号）；null = 成功。</summary>
    public string? Error { get; init; }

    public bool TimedOut { get; init; }

    public bool Cancelled { get; init; }
}

/// <summary>语法检查结果；<see cref="Message"/> 只失败时有内容。</summary>
internal sealed class CSharpCheckResult
{
    public bool Ok { get; init; }

    public string Message { get; init; } = "";
}

/// <summary>
/// 脚本里唯一能碰到的宿主对象：按名读写运行变量、写输出、检查停止。
/// 通过 Roslyn globals 的 <c>vars</c> 暴露；脚本不需要（也不该）直接引用引擎类型。
/// </summary>
public sealed class CSharpHost
{
    private readonly VariableScope _scope;
    private readonly CancellationToken _token;
    private readonly List<string> _printed = new();

    internal CSharpHost(VariableScope scope, CancellationToken token)
    {
        _scope = scope;
        _token = token;
    }

    public Value Get(string name) => _scope.TryGet(name, out Value value) ? value : Value.Empty;

    public string Text(string name) => Get(name).AsText();

    public double Number(string name) => Get(name).AsNumber();

    public bool Flag(string name) => Get(name).AsBool();

    public IReadOnlyList<Value> Items(string name) => Get(name).AsList();

    public void Set(string name, Value value) => _scope.Set(name, value);

    public void SetText(string name, string text) => Set(name, Value.FromText(text));

    public void SetNumber(string name, double number) => Set(name, Value.FromNumber(number));

    public void SetFlag(string name, bool flag) => Set(name, Value.FromBool(flag));

    public void SetList(string name, params string[] items) =>
        Set(name, Value.FromList(items.Select(Value.FromText)));

    /// <summary>往标准输出写一行（对应输出的 <c>stdout</c>）。</summary>
    public void Print(string text) => _printed.Add(text ?? "");

    /// <summary>
    /// 协作式停止点。Roslyn 不会强制中断纯 CPU 紧密循环，脚本里的长循环要自己调它；
    /// 见 <c>docs/ROSLYN_AND_UNITS_PLAN.md</c> §9.2 的已知限制。
    /// </summary>
    public void CheckStop() => _token.ThrowIfCancellationRequested();

    internal IReadOnlyList<string> Printed => _printed;
}

/// <summary>Roslyn globals：脚本里用 <c>vars</c> 访问宿主对象。</summary>
public sealed class CSharpGlobals
{
    public CSharpHost vars { get; set; } = null!;
}

/// <summary>
/// 进程内 C# 脚本层（<c>script.csharp</c>）。与 <see cref="AhkRunner"/> 的跨进程桥不同：
/// 没有前言 / BOM / 行号换算 / stdout 双解码 / 进程树 kill，变量直接在 <see cref="VariableScope"/>
/// 上读写、类型不经过文本往返，错误是带行号的 .NET 异常。
/// </summary>
internal static class CSharpRunner
{
    /// <summary>默认命名空间（白名单，脚本里写短名即可）。</summary>
    private static readonly string[] Imports =
    {
        "System", "System.Collections.Generic", "System.Linq", "System.IO",
        "System.Text", "System.Text.RegularExpressions", "System.Text.Json",
        "System.Threading", "LTools.Core.Actions",
    };

    /// <summary>固定引用集：框架程序集 + globals 所在的 LTools.dll；不开放任意 AddReferences。</summary>
    private static readonly Lazy<ScriptOptions> Options =
        new(BuildOptions, LazyThreadSafetyMode.ExecutionAndPublication);

    private static ScriptOptions BuildOptions()
    {
        string[] paths =
        {
            typeof(object).Assembly.Location,        // System.Private.CoreLib
            typeof(Enumerable).Assembly.Location,    // System.Linq
            typeof(Regex).Assembly.Location,         // System.Text.RegularExpressions
            typeof(JsonDocument).Assembly.Location,  // System.Text.Json
            typeof(CSharpHost).Assembly.Location,    // LTools.dll（globals + Value）
        };

        var references = paths
            .Where(path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))
            .ToArray();

        return ScriptOptions.Default.AddReferences(references).AddImports(Imports);
    }

    private static Script<object?> Create(string code) =>
        CSharpScript.Create<object?>(code ?? "", Options.Value, typeof(CSharpGlobals));

    /// <summary>启动时后台跑一次，把冷编译的 JIT（S0 实测 1074 ms）挪出 UI 线程。</summary>
    public static void Warmup()
    {
        try { _ = Create("1 + 1").Compile(); }
        catch (Exception ex) { AppLog.Warn("csharp", $"预热失败：{ex.Message}"); }
    }

    /// <summary>只编译、不执行脚本（参数对话框的「检查语法」）。</summary>
    public static CSharpCheckResult Check(string code)
    {
        try
        {
            Diagnostic[] errors = Create(code).Compile()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            return new CSharpCheckResult
            {
                Ok = errors.Length == 0,
                Message = errors.Length == 0 ? "语法没问题" : Describe(errors),
            };
        }
        catch (Exception ex)
        {
            return new CSharpCheckResult { Ok = false, Message = ex.Message };
        }
    }

    public static CSharpRunResult Run(
        string code, VariableScope scope, int timeoutMs, CancellationToken token)
    {
        var host = new CSharpHost(scope, token);
        var globals = new CSharpGlobals { vars = host };

        try
        {
            Script<object?> script = Create(code);
            Diagnostic[] errors = script.Compile()
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .ToArray();
            if (errors.Length > 0)
                return new CSharpRunResult { Error = Describe(errors), Printed = host.Printed };

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<ScriptState<object?>> task = script.RunAsync(globals, linked.Token);

            ScriptState<object?> state;
            if (timeoutMs > 0)
            {
                // Roslyn 不会强制中断纯 CPU 紧密循环：超时后只能不再等它，并记一条 warn。
                // 脚本线程可能在后台继续占用 CPU 直到进程退出（见 §9.2 已知限制）。
                if (!task.Wait(timeoutMs, token))
                {
                    linked.Cancel();
                    return new CSharpRunResult
                    {
                        TimedOut = true,
                        Error = $"脚本执行超时（{timeoutMs} 毫秒）",
                        Printed = host.Printed,
                    };
                }
                state = task.GetAwaiter().GetResult();
            }
            else
            {
                state = task.GetAwaiter().GetResult();
            }

            return new CSharpRunResult
            {
                Value = ToValue(state.ReturnValue),
                Printed = host.Printed,
            };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new CSharpRunResult { Cancelled = true, Error = "已停止", Printed = host.Printed };
        }
        catch (OperationCanceledException)
        {
            return new CSharpRunResult { TimedOut = true, Error = "脚本执行被取消", Printed = host.Printed };
        }
        catch (CompilationErrorException ex)
        {
            return new CSharpRunResult
            {
                Error = Describe(ex.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)),
                Printed = host.Printed,
            };
        }
        catch (Exception ex)
        {
            return new CSharpRunResult { Error = DescribeRuntime(ex), Printed = host.Printed };
        }
    }

    /// <summary>编译错误文案：Roslyn 直接给脚本行号，不需要 AhkRunner 的 UserLineOffset 换算。</summary>
    private static string Describe(IEnumerable<Diagnostic> errors)
    {
        var lines = new List<string>();
        foreach (Diagnostic diagnostic in errors.Take(5))
        {
            int line = diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1;
            lines.Add($"第 {line} 行：{diagnostic.GetMessage(CultureInfo.InvariantCulture)}");
        }
        return lines.Count == 0 ? "编译失败" : string.Join("；", lines);
    }

    /// <summary>运行时错误：Roslyn 堆栈里通常有 Submission#0.cs:line N，能取就带上。</summary>
    private static string DescribeRuntime(Exception ex)
    {
        string message = ex.InnerException?.Message ?? ex.Message;
        Match match = Regex.Match(ex.StackTrace ?? "", @":line (\d+)");
        return match.Success ? $"第 {match.Groups[1].Value} 行：{message}" : message;
    }

    /// <summary>任意 .NET 对象 → 五种值之一；类型保真，不做 <c>Value.FromText</c> 压平。</summary>
    private static Value ToValue(object? result)
    {
        if (result == null) return Value.Empty;
        if (result is Value value) return value;
        if (result is string text) return Value.FromText(text);
        if (result is bool flag) return Value.FromBool(flag);
        if (result is sbyte or byte or short or ushort or int or uint or long or ulong
            or float or double or decimal)
            return Value.FromNumber(Convert.ToDouble(result, CultureInfo.InvariantCulture));

        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
            return ValueJsonConverter.FromElement(doc.RootElement);
        }
        catch (Exception)
        {
            return Value.FromText(result.ToString() ?? "");
        }
    }
}
