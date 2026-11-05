using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using LTools.Core;
using LTools.Native;
using LTools.Views;

namespace LTools.Features;

/// <summary>
/// 取值器求值 + 条件比较。set 与 if 共用同一套 Probe，因此「赋值」和「判断」的语义完全一致。
/// </summary>
internal static class ProbeEvaluator
{
    /// <summary>求值一个取值器（debug 级别记录取值结果，便于排查条件 / 赋值为何不符合预期）。</summary>
    public static VarValue Evaluate(Probe probe, RunContext ctx)
    {
        var value = EvaluateCore(probe, ctx);
        AppLog.Debug("Probe", $"{FlowText.ProbeLabel(probe)} → {Describe(value)}");
        return value;
    }

    private static VarValue EvaluateCore(Probe probe, RunContext ctx)
    {
        switch (probe.Kind)
        {
            case ProbeKind.text:
                return VarValue.FromText(Template.Render(probe.Text ?? "", ctx));

            case ProbeKind.variable:
                return ctx.ResolvePath(probe.Name ?? "");

            case ProbeKind.selection:
                return VarValue.FromText(SelectionCapture.Capture(probe.TimeoutMs > 0 ? probe.TimeoutMs : 800));

            case ProbeKind.clipboard:
                return VarValue.FromText(NativeClipboard.TryGetText(out var clip) ? clip : "");

            case ProbeKind.cursor:
            {
                var (x, y) = ctx.CurrentCursor();
                return VarValue.FromPoint(x, y);
            }

            case ProbeKind.datetime:
                return VarValue.FromText(DateTime.Now.ToString(
                    string.IsNullOrWhiteSpace(probe.Format) ? "yyyy-MM-dd HH:mm:ss" : probe.Format,
                    CultureInfo.InvariantCulture));

            case ProbeKind.user:
                return AskUser(probe);

            case ProbeKind.screenshot:
                return ScreenshotProbe.Run(probe, ctx);

            case ProbeKind.image:
            {
                // 模板图路径也支持 ${变量}（例如把上一次运行存的图片名存进变量）
                var imageProbe = probe.Clone();
                imageProbe.Image = Template.Render(probe.Image ?? "", ctx);
                return ImageProbe.Run(imageProbe, ctx);
            }

            case ProbeKind.ocr:
                return OcrProbe.Run(probe, ctx);

            case ProbeKind.textSearch:
                return TextLocator.Run(probe, ctx);

            case ProbeKind.note:
                return NoteActions.Read(probe, ctx);

            case ProbeKind.color:
            {
                int x = ParseInt(Template.Render(probe.X ?? "", ctx), "颜色探测的 X");
                int y = ParseInt(Template.Render(probe.Y ?? "", ctx), "颜色探测的 Y");
                return VarValue.FromText(ScreenCapture.GetPixel(x, y));
            }

            case ProbeKind.window:
                return ReadWindow(probe);

            case ProbeKind.process:
            {
                string name = Template.Render(probe.ProcessName ?? "", ctx).Trim();
                if (name.Length == 0) throw new FlowException("进程探测缺少进程名");
                var list = Process.GetProcessesByName(name);
                try
                {
                    return new VarValue
                    {
                        Text = name,
                        Flag = list.Length > 0,
                        Number = list.Length,
                    };
                }
                finally
                {
                    foreach (var p in list) p.Dispose();
                }
            }

            case ProbeKind.lastError:
                return VarValue.FromText(ctx.LastError);

            default:
                throw new FlowException($"未知取值方式：{probe.Kind}");
        }
    }

    /// <summary>求条件；<see cref="Condition.Bind"/> 非空时把取值结果写入变量。</summary>
    public static bool Test(Condition condition, RunContext ctx)
    {
        VarValue value = Evaluate(condition.Probe, ctx);
        if (!string.IsNullOrWhiteSpace(condition.Bind))
            ctx.Vars[condition.Bind.Trim()] = value;

        bool ok = Compare(value, condition, ctx);
        bool result = condition.Negate ? !ok : ok;
        AppLog.Debug("Probe", $"条件{(result ? "成立" : "不成立")} · {FlowText.ConditionLabel(condition)}");
        return result;
    }

    /// <summary>把取值结果压成一行（只进 debug 日志；文本截断，避免整段剪贴板刷屏）。</summary>
    private static string Describe(VarValue value)
    {
        var parts = new List<string>();
        string text = value.Text.Replace("\r", "\\r").Replace("\n", "\\n");
        if (text.Length > 200) text = text[..200] + "…";
        parts.Add($"\"{text}\"（{value.Text.Length} 字符）");
        if (value.Number is double n) parts.Add($"number={n.ToString("0.####", CultureInfo.InvariantCulture)}");
        if (value.Flag is bool f) parts.Add($"flag={f.ToString().ToLowerInvariant()}");
        if (value.X is int x && value.Y is int y) parts.Add($"({x},{y})");
        if (value.W is int w && value.H is int h) parts.Add($"{w}×{h}");
        if (value.File.Length > 0) parts.Add($"file={System.IO.Path.GetFileName(value.File)}");
        if (value.Error.Length > 0) parts.Add($"err={value.Error.Length} 字符");
        return string.Join(" ", parts);
    }

    private static bool Compare(VarValue value, Condition c, RunContext ctx)
    {
        string operand = Template.Render(c.Operand ?? "", ctx);

        switch (c.Op)
        {
            case CompareOp.isTrue: return value.Flag == true;
            case CompareOp.isFalse: return value.Flag != true;
            case CompareOp.equals: return TextEquals(value, operand);
            case CompareOp.notEquals: return !TextEquals(value, operand);
            case CompareOp.contains: return value.Text.Contains(operand, StringComparison.OrdinalIgnoreCase);
            case CompareOp.notContains: return !value.Text.Contains(operand, StringComparison.OrdinalIgnoreCase);
            case CompareOp.startsWith: return value.Text.StartsWith(operand, StringComparison.OrdinalIgnoreCase);
            case CompareOp.endsWith: return value.Text.EndsWith(operand, StringComparison.OrdinalIgnoreCase);
            case CompareOp.regex:
                try { return Regex.IsMatch(value.Text, operand, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant); }
                catch (ArgumentException ex) { throw new FlowException($"正则表达式无效：{operand}", ex); }
            case CompareOp.isEmpty: return value.Text.Length == 0;
            case CompareOp.isNotEmpty: return value.Text.Length > 0;
            case CompareOp.greater: return ToNumber(value) > ParseDouble(operand, "比较值");
            case CompareOp.less: return ToNumber(value) < ParseDouble(operand, "比较值");
            case CompareOp.greaterOrEqual: return ToNumber(value) >= ParseDouble(operand, "比较值");
            case CompareOp.lessOrEqual: return ToNumber(value) <= ParseDouble(operand, "比较值");
            case CompareOp.found: return value.Flag == true;
            case CompareOp.notFound: return value.Flag != true;
            case CompareOp.active: return value.Number == 1;
            case CompareOp.notActive: return value.Number != 1;
            case CompareOp.nearColor:
                return ColorsNear(value.Text, c.Probe.Color ?? "", c.Probe.Tolerance);
            default: throw new FlowException($"未知比较方式：{c.Op}");
        }
    }

    /// <summary>文本相等：两边都是数字时按数值比较（避免 "3" 与 "3.0" 不相等）。</summary>
    private static bool TextEquals(VarValue value, string operand)
    {
        if (double.TryParse(value.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double a) &&
            double.TryParse(operand, NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            return Math.Abs(a - b) < 1e-9;
        return string.Equals(value.Text, operand, StringComparison.OrdinalIgnoreCase);
    }

    private static double ToNumber(VarValue value)
    {
        if (value.Number is double n) return n;
        return ParseDouble(value.Text, "取值结果");
    }

    private static double ParseDouble(string text, string what)
    {
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return v;
        throw new FlowException($"{what}不是数字：{text}");
    }

    private static int ParseInt(string text, string what)
    {
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) return v;
        throw new FlowException($"{what}不是整数：{text}");
    }

    private static bool ColorsNear(string a, string b, int tolerance)
    {
        if (!TryParseHex(a, out int ra, out int ga, out int ba) ||
            !TryParseHex(b, out int rb, out int gb, out int bb))
            return false;
        return Math.Abs(ra - rb) <= tolerance &&
               Math.Abs(ga - gb) <= tolerance &&
               Math.Abs(ba - bb) <= tolerance;
    }

    private static bool TryParseHex(string text, out int r, out int g, out int b)
    {
        r = g = b = 0;
        text = (text ?? "").Trim().TrimStart('#');
        if (text.Length != 6) return false;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
        r = (v >> 16) & 0xFF;
        g = (v >> 8) & 0xFF;
        b = v & 0xFF;
        return true;
    }

    // —— 窗口 / 进程 ——

    private static VarValue ReadWindow(Probe probe)
    {
        IntPtr foreground = Win32.GetForegroundWindow();
        IntPtr found = WindowFinder.Find(probe.WindowTitle, probe.WindowClass, out string foundTitle);

        return new VarValue
        {
            Text = foundTitle,
            Flag = found != IntPtr.Zero,
            Number = found != IntPtr.Zero && found == foreground ? 1 : 0,
        };
    }

    // —— 输入文本 ——

    private static VarValue AskUser(Probe probe)
    {
        var app = Application.Current;
        if (app == null) throw new FlowException("界面未初始化，无法弹出输入框");

        // 全部在 UI 线程上做：Application.Windows 与对话框都归属 UI 线程（流程跑在后台线程）。
        var (ok, value) = app.Dispatcher.Invoke(() =>
        {
            Window? owner = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
            return InputDialog.Show(owner, "自动化", string.IsNullOrWhiteSpace(probe.Prompt) ? "请输入" : probe.Prompt!,
                probe.Default ?? "", multiline: probe.Multiline);
        });

        return new VarValue { Text = ok ? value : "", Flag = ok };
    }
}
