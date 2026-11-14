using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LTools.Core.Actions;

/// <summary>
/// 步骤的文本形式（DSL）：一行一步、缩进表示子步骤，可以直接复制 / 粘贴，也方便和 AI 往返。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不是 JSON：贴进对话、贴进 issue 时它又长又难读，改一个参数要数括号；而「一行一步 + 缩进」
/// 跟编辑器里看到的形状一样，AI 只要会写 <c>key=value</c> 就能写出一步。
/// </para>
/// <para>格式（<c>#</c> 之前是内容、之后到行尾是注释；写的时候会在行尾带动作单元的中文名）：</para>
/// <code>
/// screen.capture source="screen" -> file=shot            # 截图
/// flow.if left="${count}" op="greaterThan" right="0"     # 如果
///   text.send text="hello"
/// else
///   ui.toast text="空"
/// flow.repeat times="3"                                  # 重复
///   flow.delay ms="100" @note="等渲染"
/// </code>
/// <para>
/// 参数值：文本写成 JSON 字符串（<c>"a\nb"</c>，换行 / 引号自动转义）、数字与布尔直接写、
/// 列表 / 对象写紧凑 JSON、<c>${名字}</c> 是变量引用、<c>@{单元 key=值}</c> 是内联取值；
/// 不带引号的裸词按「数字 → true / false → 文本」依次尝试。
/// </para>
/// <para>
/// 输出写 <c>-&gt; 输出键=变量名</c>，属性写 <c>@note="…"</c> / <c>@disabled</c> /
/// <c>@onFail=continue</c> / <c>@delay=500</c>。子步骤靠缩进：循环体与「如果」的真分支各缩进一级，
/// <c>else</c> 与自己那一行对齐（分支为空就不写内容）。
/// </para>
/// <para>
/// 往返（<c>TryParse(Write(x))</c>）保真：动作单元、参数三态、输出映射、四种子步骤、
/// 备注 / 跳过 / 失败策略 / 延迟。<b>唯一的例外</b>是「未知动作单元带子步骤」——它的
/// <see cref="BodyKind"/> 查不到，只能按循环体（<c>body</c>）猜。
/// </para>
/// </remarks>
public static class StepText
{
    /// <summary>写出来的缩进宽度（解析时不看宽度，只比大小）。</summary>
    public const int IndentSpaces = 2;

    /// <summary><c>else</c> 关键字（「否则」分支的标记行）。</summary>
    public const string ElseKeyword = "else";

    // ============ 写 ============

    /// <summary>把一串步骤写成文本（每行以 <c>\n</c> 结尾、末尾也有一个；贴进对话或记事本都正常）。</summary>
    public static string Write(IEnumerable<Step> steps)
    {
        var sb = new StringBuilder();
        WriteSteps(steps as IReadOnlyList<Step> ?? steps.ToList(), 0, sb);
        return sb.ToString();
    }

    private static void WriteSteps(IReadOnlyList<Step> steps, int indent, StringBuilder sb)
    {
        foreach (Step step in steps)
        {
            sb.Append(' ', indent);
            WriteHead(step, sb, comment: true);
            sb.Append('\n');

            if (step.Body.Count > 0) WriteSteps(step.Body, indent + IndentSpaces, sb);
            if (step.Then.Count > 0) WriteSteps(step.Then, indent + IndentSpaces, sb);
            if (step.Else.Count > 0)
            {
                sb.Append(' ', indent).Append(ElseKeyword).Append('\n');
                WriteSteps(step.Else, indent + IndentSpaces, sb);
            }
        }
    }

    /// <summary>一行里的「单元 + 参数 + 输出 + 属性」（内联取值复用，那时不带注释）。</summary>
    private static void WriteHead(Step step, StringBuilder sb, bool comment)
    {
        sb.Append(step.Unit.Length == 0 ? "?" : step.Unit);

        foreach (var (key, arg) in OrderedArgs(step))
        {
            sb.Append(' ').Append(key).Append('=');
            WriteArg(arg, sb);
        }

        List<string> outs = step.Out.Where(p => p.Value.Length > 0)
            .Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        if (outs.Count > 0)
        {
            sb.Append(" -> ");
            for (int n = 0; n < outs.Count; n++)
            {
                if (n > 0) sb.Append(' ');
                sb.Append(outs[n]).Append('=').Append(step.Out[outs[n]]);
            }
        }

        if (!string.IsNullOrEmpty(step.Note))
        {
            sb.Append(" @note=");
            WriteLiteral(Value.FromText(step.Note), sb);
        }
        if (step.OnFail != FailMode.stop) sb.Append(" @onFail=").Append(step.OnFail);
        if (step.DelayMs > 0) sb.Append(" @delay=").Append(step.DelayMs.ToString(CultureInfo.InvariantCulture));
        if (step.Disabled) sb.Append(" @disabled");

        if (comment && StepRegistry.Default.TryGet(step.Unit, out var unit))
            sb.Append("   # ").Append(unit.Spec.Name);
    }

    /// <summary>
    /// 参数按动作单元声明的顺序写（和参数表单一致），声明之外的键（动态输入 / 未知单元）
    /// 排在后面按名字排序——这样同样的步骤每次写出来的文本都一样。
    /// </summary>
    private static IEnumerable<(string Key, StepArg Arg)> OrderedArgs(Step step)
    {
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (StepRegistry.Default.TryGet(step.Unit, out var unit))
        {
            foreach (InputSpec input in unit.Spec.Inputs)
            {
                if (!step.In.TryGetValue(input.Key, out StepArg? arg)) continue;
                written.Add(input.Key);
                yield return (input.Key, arg);
            }
        }

        foreach (var pair in step.In.Where(p => !written.Contains(p.Key))
                     .OrderBy(p => p.Key, StringComparer.Ordinal))
            yield return (pair.Key, pair.Value);
    }

    private static void WriteArg(StepArg arg, StringBuilder sb)
    {
        switch (arg.Kind)
        {
            case StepArgKind.variable:
                sb.Append("${").Append(arg.VariablePath).Append('}');
                break;

            case StepArgKind.inline:
                sb.Append("@{");
                if (arg.InlineStep != null) WriteHead(arg.InlineStep, sb, comment: false);
                sb.Append('}');
                break;

            default:
                WriteLiteral(arg.Literal, sb);
                break;
        }
    }

    private static void WriteLiteral(Value value, StringBuilder sb) => sb.Append(JsonOf(value));

    /// <summary>值的 JSON 文本（紧凑、不转义中文；文本带引号，数字 / 布尔裸写）。</summary>
    private static string JsonOf(Value value)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            ValueJsonConverter.WriteElement(writer, value);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // ============ 读 ============

    /// <summary>
    /// 解析文本形式的步骤。失败时 <paramref name="error"/> 里是「第 N 行：…」。
    /// </summary>
    /// <remarks>
    /// 宽容度：公共缩进会被整体去掉（从对话里贴过来时前面常带空格）、空行与注释行忽略、
    /// 参数 / 输出 / 属性的先后顺序不限（<c>-&gt;</c> 之后的 <c>key=值</c> 都算输出）。
    /// </remarks>
    public static bool TryParse(string? text, out List<Step> steps, out string? error)
    {
        steps = new List<Step>();
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "没有步骤文本";
            return false;
        }

        List<(int No, int Indent, string Body)> lines = SplitLines(text);
        if (lines.Count == 0)
        {
            error = "没有步骤文本（只有空行或注释）";
            return false;
        }

        // 公共缩进去掉：第一行可能不是最左边那一行（从对话里贴过来的常见形状）
        int dedent = lines.Min(l => l.Indent);
        if (dedent > 0)
            lines = lines.Select(l => (l.No, l.Indent - dedent, l.Body)).ToList();

        var stack = new List<Frame> { new(ownerIndent: -1, steps, owner: null) { ItemIndent = 0 } };
        int lastLine = -1;
        Step? lastStep = null;

        foreach (var (no, indent, body) in lines)
        {
            if (body.Equals(ElseKeyword, StringComparison.OrdinalIgnoreCase))
            {
                while (stack.Count > 1 && stack[^1].OwnerIndent > indent) stack.RemoveAt(stack.Count - 1);

                Frame frame = stack[^1];
                if (frame.Owner == null || frame.OwnerIndent != indent)
                {
                    error = $"第 {no} 行：{ElseKeyword} 前面没有它对应的「如果」";
                    return false;
                }

                BodyKind? kind = SpecOf(frame.Owner)?.Body;
                if (kind != null && kind != BodyKind.thenElse)
                {
                    error = $"第 {no} 行：「{frame.Owner.Unit}」没有「否则」分支（它只有循环体）";
                    return false;
                }
                if (frame.ElseSeen)
                {
                    error = $"第 {no} 行：同一个「如果」出现了两个 {ElseKeyword}";
                    return false;
                }

                stack[^1] = new Frame(indent, frame.Owner.Else, frame.Owner) { ElseSeen = true };
                lastStep = null;
                continue;
            }

            while (stack.Count > 1)
            {
                Frame top = stack[^1];
                bool outside = top.ItemIndent >= 0 ? indent < top.ItemIndent : indent <= top.OwnerIndent;
                if (!outside) break;
                stack.RemoveAt(stack.Count - 1);
            }

            Frame target = stack[^1];
            if (target.ItemIndent < 0) target.ItemIndent = indent;
            else if (indent > target.ItemIndent)
            {
                string previous = lastStep == null
                    ? "上一行"
                    : $"第 {lastLine} 行「{Title(lastStep)}」";
                error = $"第 {no} 行：缩进比 {previous} 深，但那一步不能带子步骤（只有「如果」和循环能带）";
                return false;
            }

            if (!TryParseStep(body, out Step step, out string? lineError))
            {
                error = $"第 {no} 行：{lineError}";
                return false;
            }

            target.Items.Add(step);
            lastStep = step;
            lastLine = no;

            // 能带子步骤的单元压一帧：下一行的缩进比它深就是它的孩子。
            // 未知单元只能按循环体猜（写的时候也是一样：Body / Then / Else 原样缩进）。
            BodyKind? children = SpecOf(step)?.Body;
            if (children == BodyKind.thenElse) stack.Add(new Frame(indent, step.Then, step));
            else if (children == BodyKind.body || children == null) stack.Add(new Frame(indent, step.Body, step));
        }

        return true;
    }

    private static List<(int No, int Indent, string Body)> SplitLines(string text)
    {
        string[] raw = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var lines = new List<(int No, int Indent, string Body)>(raw.Length);

        for (int n = 0; n < raw.Length; n++)
        {
            string line = StripComment(raw[n]).TrimEnd();
            if (line.Trim().Length == 0) continue;

            int indent = 0;
            int i = 0;
            for (; i < line.Length; i++)
            {
                if (line[i] == ' ') indent++;
                else if (line[i] == '\t') indent += 4;
                else break;
            }

            lines.Add((n + 1, indent, line[Math.Min(i, line.Length)..]));
        }

        return lines;
    }

    /// <summary>去掉行尾注释。<c>#</c> 只有在行首或空白后面才算注释（别把 <c>a#b</c> 这种值切了）。</summary>
    private static string StripComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '#' && (i == 0 || char.IsWhiteSpace(line[i - 1]))) return line[..i];
        }
        return line;
    }

    private static bool TryParseStep(string body, out Step step, out string? error)
    {
        step = new Step();
        error = null;

        int i = 0;
        string unit = ReadToken(body, ref i);
        if (unit.Length == 0)
        {
            error = "缺少动作单元（一行要先写单元键，如 text.send）";
            return false;
        }
        if (unit.Contains('='))
        {
            error = $"「{unit}」不是动作单元：一行要先写单元键，再写 key=值";
            return false;
        }
        step.Unit = unit;

        bool inOutputs = false;
        while (true)
        {
            SkipSpaces(body, ref i);
            if (i >= body.Length) break;

            if (body[i] == '@')
            {
                if (!TryReadFlag(step, body, ref i, out error)) return false;
                continue;
            }

            if (body[i] == '-' && i + 1 < body.Length && body[i + 1] == '>')
            {
                i += 2;
                inOutputs = true;
                continue;
            }

            if (inOutputs)
            {
                if (!TryReadOutput(step, body, ref i, out error)) return false;
                continue;
            }

            if (!TryReadParam(step, body, ref i, out error)) return false;
        }

        return true;
    }

    private static bool TryReadParam(Step step, string body, ref int i, out string? error)
    {
        error = null;
        string key = ReadUntil(body, ref i, '=');
        if (key.Length == 0)
        {
            error = "参数缺少名字";
            return false;
        }
        if (i >= body.Length || body[i] != '=')
        {
            error = $"参数「{key}」缺少 =（写成 {key}=值）";
            return false;
        }
        i++;

        if (step.In.ContainsKey(key))
        {
            error = $"参数「{key}」写了两次";
            return false;
        }

        if (!TryReadArg(body, ref i, out StepArg? arg, out error)) return false;
        step.In[key] = arg!;
        return true;
    }

    private static bool TryReadOutput(Step step, string body, ref int i, out string? error)
    {
        error = null;
        string key = ReadUntil(body, ref i, '=');
        if (key.Length == 0)
        {
            error = "输出缺少键（写成 -> 输出键=变量名）";
            return false;
        }
        if (i >= body.Length || body[i] != '=')
        {
            error = $"输出「{key}」缺少 =（写成 -> {key}=变量名）";
            return false;
        }
        i++;

        string variable = ReadToken(body, ref i);
        if (variable.Length == 0)
        {
            error = $"输出「{key}」没写变量名";
            return false;
        }

        step.Out[key] = variable;
        return true;
    }

    private static bool TryReadFlag(Step step, string body, ref int i, out string? error)
    {
        error = null;
        i++;   // '@'
        string name = ReadUntil(body, ref i, '=');

        string value = "";
        bool hasValue = i < body.Length && body[i] == '=';
        if (hasValue)
        {
            i++;
            if (!TryReadArg(body, ref i, out StepArg? arg, out error)) return false;
            if (arg!.Kind != StepArgKind.literal)
            {
                error = $"属性「@{name}」只能是字面量";
                return false;
            }
            value = arg.Literal.AsText();
        }

        switch (name.ToLowerInvariant())
        {
            case "disabled":
                if (hasValue)
                {
                    error = "@disabled 不写值（写了就是跳过这一步）";
                    return false;
                }
                step.Disabled = true;
                return true;

            case "note":
                if (!hasValue)
                {
                    error = "备注要写成 @note=\"…\"";
                    return false;
                }
                step.Note = value;
                return true;

            case "onfail":
                if (!hasValue)
                {
                    error = "失败策略要写成 @onFail=continue（或 stop）";
                    return false;
                }
                switch (value.Trim().ToLowerInvariant())
                {
                    case "continue":
                    case "continueall":
                        step.OnFail = FailMode.@continue;
                        return true;
                    case "stop":
                        step.OnFail = FailMode.stop;
                        return true;
                    default:
                        error = $"@onFail 只认 continue / stop（写的是「{value}」）";
                        return false;
                }

            case "delay":
                if (!hasValue)
                {
                    error = "延迟要写成 @delay=毫秒";
                    return false;
                }
                if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int delay)
                    || delay < 0)
                {
                    error = $"@delay 要写非负整数毫秒（写的是「{value}」）";
                    return false;
                }
                step.DelayMs = delay;
                return true;

            default:
                error = $"不认识的属性 @{name}（只有 note / disabled / onFail / delay）";
                return false;
        }
    }

    /// <summary>读一个参数值（字面量 / 变量 / 内联取值）。</summary>
    private static bool TryReadArg(string body, ref int i, out StepArg? arg, out string? error)
    {
        arg = null;
        error = null;

        if (i >= body.Length || char.IsWhiteSpace(body[i]))
        {
            arg = StepArg.FromText("");
            return true;
        }

        char c = body[i];

        if (c == '"')
        {
            if (!TryReadJsonString(body, ref i, out string text))
            {
                error = "字符串写法无效（缺少收尾的引号，或转义不对）";
                return false;
            }
            arg = StepArg.FromText(text);
            return true;
        }

        if (c == '$' && i + 1 < body.Length && body[i + 1] == '{')
        {
            int close = body.IndexOf('}', i + 2);
            if (close < 0)
            {
                error = "变量引用缺少 }（写成 ${名字}）";
                return false;
            }
            string path = body[(i + 2)..close].Trim();
            i = close + 1;
            if (path.Length == 0)
            {
                error = "变量引用里没写名字";
                return false;
            }
            arg = StepArg.FromVar(path);
            return true;
        }

        if (c == '@' && i + 1 < body.Length && body[i + 1] == '{')
        {
            if (!TryReadInline(body, ref i, out string inner))
            {
                error = "内联取值缺少收尾的 }";
                return false;
            }
            if (!TryParseStep(inner, out Step inline, out string? innerError))
            {
                error = "内联取值：" + innerError;
                return false;
            }
            arg = StepArg.FromInline(inline);
            return true;
        }

        if (c is '[' or '{')
        {
            if (!TryReadBalanced(body, ref i, out string chunk))
            {
                error = "列表 / 对象没有收尾的括号";
                return false;
            }
            try
            {
                using var doc = JsonDocument.Parse(chunk);
                arg = StepArg.FromValue(ValueJsonConverter.FromElement(doc.RootElement));
                return true;
            }
            catch (JsonException ex)
            {
                error = "列表 / 对象的 JSON 写法无效：" + ex.Message;
                return false;
            }
        }

        arg = ArgFromBare(ReadToken(body, ref i));
        return true;
    }

    private static StepArg ArgFromBare(string token)
    {
        if (token.Length == 0) return StepArg.FromText("");
        if (token == "true") return StepArg.FromBool(true);
        if (token == "false") return StepArg.FromBool(false);
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            return StepArg.FromNumber(number);
        return StepArg.FromText(token);
    }

    // ============ 小工具 ============

    private static bool TryReadJsonString(string s, ref int i, out string value)
    {
        value = "";
        int start = i;   // 指向开头引号
        i++;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\') { i += 2; continue; }
            if (c == '"') { i++; break; }
            i++;
        }

        int end = Math.Min(i, s.Length);
        if (end - start < 2 || s[end - 1] != '"') return false;

        try
        {
            value = JsonSerializer.Deserialize<string>(s[start..end]) ?? "";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>读 <c>@{…}</c> 里那段（不含外层括号）。</summary>
    private static bool TryReadInline(string s, ref int i, out string inner)
    {
        i += 2;
        int start = i;
        int depth = 1;
        bool inString = false;

        while (i < s.Length)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == '"') inString = false;
                i++;
                continue;
            }

            if (c == '"') { inString = true; i++; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    inner = s[start..i];
                    i++;
                    return true;
                }
            }
            i++;
        }

        inner = "";
        return false;
    }

    /// <summary>读一段配平的 JSON（<c>[...]</c> 或 <c>{...}</c>，引号里的括号不算）。</summary>
    private static bool TryReadBalanced(string s, ref int i, out string chunk)
    {
        char open = s[i];
        char close = open == '[' ? ']' : '}';
        int depth = 0;
        int start = i;
        bool inString = false;

        while (i < s.Length)
        {
            char c = s[i];
            if (inString)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == '"') inString = false;
                i++;
                continue;
            }

            if (c == '"') { inString = true; i++; continue; }
            if (c == open) depth++;
            else if (c == close)
            {
                depth--;
                if (depth == 0)
                {
                    i++;
                    chunk = s[start..i];
                    return true;
                }
            }
            i++;
        }

        chunk = "";
        return false;
    }

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    /// <summary>读到空白为止。</summary>
    private static string ReadToken(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i])) i++;
        return s[start..i];
    }

    /// <summary>读到空白或 <paramref name="stop"/> 为止（不消费停下的那个字符）。</summary>
    private static string ReadUntil(string s, ref int i, char stop)
    {
        int start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != stop) i++;
        return s[start..i];
    }

    private static UnitSpec? SpecOf(Step step) =>
        StepRegistry.Default.TryGet(step.Unit, out var unit) ? unit.Spec : null;

    private static string Title(Step step) =>
        StepRegistry.Default.TryGet(step.Unit, out var unit) ? unit.Spec.Name : step.Unit;

    /// <summary>解析时的一层容器：现在往哪个列表里加步骤。</summary>
    private sealed class Frame
    {
        public Frame(int ownerIndent, List<Step> items, Step? owner)
        {
            OwnerIndent = ownerIndent;
            Items = items;
            Owner = owner;
        }

        /// <summary>容器里步骤的缩进（-1 = 还没见到第一行）。</summary>
        public int ItemIndent { get; set; } = -1;

        /// <summary>有这一层的那个步骤的缩进（<c>else</c> 靠它对齐）。</summary>
        public int OwnerIndent { get; }

        public List<Step> Items { get; }

        public Step? Owner { get; }

        /// <summary>这一层是不是已经切过 <c>else</c>（同一个如果只能有一个）。</summary>
        public bool ElseSeen { get; init; }
    }
}
