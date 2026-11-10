using System.Reflection;
using System.Text;
using LTools.Core.Actions;

namespace LTools.Core.Ai;

/// <summary>
/// 随包分发的「单元知识」：给外部 agent 按需检索的明文文件，而不是一次性塞进它看不到的系统提示词。
/// </summary>
/// <remarks>
/// <para>
/// 目录见 <c>docs/AI_AGENT.md</c> §5.1：
/// <code>
/// ai/knowledge/
///   format.md   动作 JSON 格式与工作流（手写，稳定）
///   units.md    从 StepRegistry 自动生成的单元目录
///   examples.md 3 个完整示例（手写）
///   .version    生成版本（程序集版本 + 单元「键 / 分类」哈希），变了才重写 units.md
/// </code>
/// </para>
/// <para>
/// 单元目录不手写：<see cref="UnitSpec"/> 的 Input / Output / VisibleWhen / Options 已经是机器可读的，
/// 这里只负责排版。新增单元时不需要改这个文件，启动后 <see cref="Ensure"/> 会自动刷新。
/// </para>
/// </remarks>
internal static class KnowledgePack
{
    public static string Dir => Path.Combine(AppPaths.DataRoot, "ai", "knowledge");

    public static string FormatPath => Path.Combine(Dir, "format.md");

    public static string UnitsPath => Path.Combine(Dir, "units.md");

    public static string ExamplesPath => Path.Combine(Dir, "examples.md");

    private static string VersionPath => Path.Combine(Dir, ".version");

    /// <summary>确保知识包存在且与当前单元清单一致。可在后台线程调用。</summary>
    public static void Ensure()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Dir);

            string version = CurrentVersion();
            string previous = File.Exists(VersionPath) ? File.ReadAllText(VersionPath, Encoding.UTF8).Trim() : "";
            bool needsUnits = !File.Exists(UnitsPath) || !string.Equals(previous, version, StringComparison.Ordinal);

            if (needsUnits)
            {
                File.WriteAllText(UnitsPath, BuildUnitsMarkdown(), Utf8);
                File.WriteAllText(VersionPath, version, Utf8);
            }

            if (!File.Exists(FormatPath)) File.WriteAllText(FormatPath, FormatText, Utf8);
            if (!File.Exists(ExamplesPath)) File.WriteAllText(ExamplesPath, ExamplesText, Utf8);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ai", $"生成单元知识失败：{ex.Message}");
        }
    }

    /// <summary>强制重写 units.md（设置页「重新生成」）。返回写入的单元数。</summary>
    public static int RegenerateUnits()
    {
        System.IO.Directory.CreateDirectory(Dir);
        File.WriteAllText(UnitsPath, BuildUnitsMarkdown(), Utf8);
        File.WriteAllText(VersionPath, CurrentVersion(), Utf8);
        return StepRegistry.Default.Units.Count;
    }

    /// <summary>读一个知识文件（<c>format</c> / <c>units</c> / <c>examples</c>），不存在时先补齐。</summary>
    public static string Read(string name)
    {
        Ensure();
        string path = name.Trim().ToLowerInvariant() switch
        {
            "format" => FormatPath,
            "units" => UnitsPath,
            "examples" => ExamplesPath,
            _ => throw new ArgumentException($"未知的知识文件：{name}（可用 format / units / examples）"),
        };

        return File.Exists(path)
            ? File.ReadAllText(path, Encoding.UTF8)
            : throw new FileNotFoundException($"知识文件不存在：{path}");
    }

    // ============ units.md ============

    private static string CurrentVersion()
    {
        string assembly = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        // 分类也算内容：分类重排后 units.md 的「分类」行会变，只哈希 key 的话文件会一直是旧的。
        var entries = StepRegistry.Default.Units
            .Select(u => $"{u.Spec.Key}|{u.Spec.Category}")
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        byte[] hash = System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", entries)));
        return $"{assembly}|{entries.Count}|{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static string BuildUnitsMarkdown()
    {
        var units = StepRegistry.Default.Units
            .OrderBy(u => UnitCategories.Rank(u.Spec.Category))
            .ThenBy(u => u.Spec.Category, StringComparer.Ordinal)
            .ThenBy(u => u.Spec.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var md = new StringBuilder();
        md.AppendLine("# 单元目录");
        md.AppendLine();
        md.AppendLine("> 本文件由 LTools 从 `StepRegistry` 自动生成，**不要手改**；升级后可能整份重写。");
        md.AppendLine("> 只需要查某个单元时，用 `ltools_units_get(key)` 拿它的完整规格；这里适合整段读 / grep。");
        md.AppendLine();
        md.AppendLine($"共 {units.Count} 个单元。`pure` = 取值单元，可以内联在参数或条件里；`action` = 有副作用的普通步骤。");
        md.AppendLine();

        // 索引
        foreach (var group in units.GroupBy(u => u.Spec.Category))
        {
            md.AppendLine($"## {group.Key}");
            md.AppendLine();
            foreach (var unit in group)
                md.AppendLine($"- `{unit.Spec.Key}` — {unit.Spec.Name}");
            md.AppendLine();
        }

        md.AppendLine("---");
        md.AppendLine();

        foreach (var unit in units)
            WriteUnit(md, unit.Spec);

        return md.ToString();
    }

    private static void WriteUnit(StringBuilder md, UnitSpec spec)
    {
        md.AppendLine($"### `{spec.Key}` — {spec.Name}");
        md.AppendLine();

        string flags = $"分类：{spec.Category} · 种类：{(spec.Kind == UnitKind.pure ? "pure（取值，可内联）" : "action（普通步骤）")}";
        if (spec.Body == BodyKind.body) flags += " · 子步骤：`body`";
        else if (spec.Body == BodyKind.thenElse) flags += " · 子步骤：`then` / `else`";
        if (spec.DynamicInputs) flags += " · 允许声明之外的参数（按变量名传值）";
        md.AppendLine(flags);
        md.AppendLine();

        if (!string.IsNullOrWhiteSpace(spec.Description))
        {
            md.AppendLine(spec.Description.Trim());
            md.AppendLine();
        }

        if (spec.Inputs.Count > 0)
        {
            md.AppendLine("| 参数 | 名称 | 类型 | 必填 | 默认 | 可见条件 | 说明 |");
            md.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var input in spec.Inputs)
            {
                md.AppendLine("| " + string.Join(" | ", new[]
                {
                    $"`{input.Key}`",
                    Cell(input.Label),
                    TypeName(input.Type),
                    input.Required ? "是" : "",
                    input.Default == null ? "" : $"`{Cell(input.Default.AsText())}`",
                    input.VisibleWhen == null ? "" : $"`{Cell(input.VisibleWhen)}`",
                    Cell(Describe(input)),
                }) + " |");
            }
            md.AppendLine();
        }

        if (spec.Outputs.Count > 0)
        {
            md.AppendLine("输出（用步骤的 `out` 映射到变量）：");
            md.AppendLine();
            md.AppendLine("| 键 | 名称 | 类型 | 建议变量 | 说明 |");
            md.AppendLine("|---|---|---|---|---|");
            foreach (var output in spec.Outputs)
            {
                md.AppendLine("| " + string.Join(" | ", new[]
                {
                    $"`{output.Key}`",
                    Cell(output.Label),
                    TypeName(output.Type),
                    $"`{OutputNaming.Suggest(spec.Key, output)}`",
                    Cell(output.Description ?? ""),
                }) + " |");
            }
            md.AppendLine();
        }
    }

    private static string Describe(InputSpec input)
    {
        string hint = InputHint.Of(input);
        if (input.Options is { Count: > 0 })
        {
            string options = string.Join(" / ", input.Options.Select(o =>
                $"{o}（{OptionLabels.For(input, o)}）"));
            hint = hint.Length == 0 ? $"可选：{options}" : $"{hint}；可选：{options}";
        }

        return hint;
    }

    private static string TypeName(ValueKind? kind) => kind switch
    {
        ValueKind.text => "text",
        ValueKind.number => "number",
        ValueKind.@bool => "bool",
        ValueKind.list => "list",
        ValueKind.@object => "object",
        _ => "任意",
    };

    private static string Cell(string? text) =>
        (text ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // ============ 手写部分 ============

    private const string FormatText = """
# LTools 动作格式

你是 LTools 动作编写助手。**只能通过暂存区工具产出草稿，永远不要试图改正式动作**——
正式动作没有写工具，这是结构上的限制，不是靠你自觉。

## 1. 一个动作长什么样

```json
{
  "name": "动作名称",
  "icon": "",
  "description": "一句话说明",
  "variables": [
    { "key": "text", "type": "text", "default": "", "label": "可选显示名" }
  ],
  "steps": [
    { "unit": "ui.toast", "in": { "text": "你好 ${text}" } }
  ],
  "subActions": []
}
```

- `variables` 声明变量（`type` 是 `text` / `number` / `bool` / `list` / `object`）；运行时也能由步骤的输出创建新变量，但写进变量表更清楚。
- `steps` 是普通数组。控制流不是节点图：`flow.if` / `flow.repeat` / `flow.forEach` / `flow.while` 就是普通步骤，分支体放在 `then` / `else` / `body` 里。
- 步骤字段：`unit`（单元键）、`in`（参数）、`out`（输出键 → 变量名）、`body` / `then` / `else`、`note`、`disabled`、`onFail`（`stop` / `continue`）、`delayMs`。

## 2. 参数三态

`in` 里的每个值只有三种写法：

```json
{ "text": "字面量，支持 ${变量}" }
{ "text": { "var": "变量名" } }
{ "text": { "unit": "screen.findText", "in": { "text": "确定" }, "out": { "x": "x" } } }
```

- 字面量：字符串 / 数字 / 布尔 / 数组 / 对象。**文本里的 `${变量}` 会在运行时求值**（`$$` 转义成 `$`）。
- `{"var": "名字"}`：保留变量的原始类型（列表、对象都行），路径可写 `a.b[0]`。
- `{"unit": ..., "in": {...}}`：内联一个 **pure** 取值单元，`action` 单元不能内联。
- 例外：`Raw: true` 的代码参数（`script.ahk` 的 `code`）原样传入，里面的 `${…}` 不要求值。

## 3. 工作流（工具名见 MCP）

1. `ltools_units_list` / `ltools_units_get`：先确认能力存在，别臆造单元键。
2. `ltools_staging_create(title, description?, copyFromActionId?)`：新草稿。改用户已有动作时传 `copyFromActionId`（只读复制到草稿）。
3. `ltools_staging_open(draftId)`：读 `program` + `files` + `revision` + 绝对目录。
4. 写草稿的两条路，选一条即可：
   - `ltools_staging_write(draftId, program, expectedRevision)`：整份写入；
   - 或直接编辑 `<dir>/program.json` 与 `<dir>/files/…`（agent 自己的 fs 工具）。
   哪条都行：`revision` 从文件内容重算，写之前拿最新 revision 再改。
5. `ltools_staging_check(draftId)`：改到 `ok: true`（`issues` 里的 `error` 必须清零，`warning` 可以留）。
6. `ltools_staging_save(draftId, expectedRevision, summary?)`：冻结为已保存版本。
7. `ltools_staging_run(draftId)`（可选）：真正跑一遍（会动键鼠），再用 `ltools_run_output` 读轨迹。
8. 交给用户在 LTools 设置页点「采纳」（`ltools_staging_promote` 需要人工确认 / full 模式）。

## 4. 校验器会拦什么

未知单元 / 未知参数、必填缺失（只对可见参数）、字面量类型不符、枚举值不在范围、
`${变量}` 引用了未声明的变量、条件运算符 / 正则无效、子动作 / 宏不存在、内联了 action 单元、
引用了不存在的草稿图片。位置用 `第 3 行 › 满足时 › 第 1 行`，与运行失败提示一致。

## 5. 图片

草稿资源只接受图片：写到 `files/xxx.png`，步骤的 `image` 参数写 `files/xxx.png`。
`ltools_staging_run` / 面板试运行 / 编辑器测试会把草稿的 `files/` 当作运行期资源根，所以**不用写绝对路径**；
`files/xxx.png` 与裸 `xxx.png` 都先在那里找。
采纳时整个 `files/` 会复制进 `actions/<动作id>/files/`，引用原样保留；正式运行时以该目录为资源根。
其它类型文件 v1 直接拒绝。

## 6. 纪律

- 找不到能力就说不支持，**不要臆造单元键**，也不要试图写配置文件或 `actions/` 目录（正式动作只经用户采纳写入）。
- 不要为了一个功能另造一套循环 / 条件 / 取值——先看 `units.md` 里现成的单元。
- 动作执行在后台线程；会动键鼠的步骤（点击、输入、窗口操作）要留出等待（`flow.delay` 或单元自带的超时 / 等待）。
- 用户动作只能读（`ltools_actions_get`）；要改就先 `staging_create(copyFromActionId:...)`。
""";

    private const string ExamplesText = """
# 完整示例

下面三个例子都可以直接作为草稿的 `program.json`（省略了 `id` 字段；草稿由 LTools 自己管 id）。

## 1. 打开记事本写一句话

```json
{
  "name": "打开记事本写一句话",
  "description": "启动记事本，等窗口出来，输入一行文字并回车",
  "variables": [],
  "steps": [
    { "unit": "app.launch", "in": { "target": "notepad.exe" } },
    { "unit": "flow.delay", "in": { "ms": 800 } },
    { "unit": "text.send", "in": { "text": "Hello from LTools", "appendEnter": true } }
  ]
}
```

## 2. 取前台窗口截图并识别文字

```json
{
  "name": "识别前台窗口文字",
  "description": "截前台窗口，识别其中的文字并提示",
  "variables": [
    { "key": "shot", "type": "text", "default": "" },
    { "key": "words", "type": "text", "default": "" }
  ],
  "steps": [
    { "unit": "screen.capture", "in": { "mode": "foreground" }, "out": { "file": "shot" } },
    { "unit": "screen.ocr", "in": { "image": { "var": "shot" }, "engine": "v6tiny" }, "out": { "text": "words" } },
    { "unit": "ui.toast", "in": { "title": "识别结果", "text": "${words}", "durationMs": 6000 } }
  ]
}
```

截图和识字是两步：`screen.capture` 产出 png 文件路径，`screen.ocr` 只读图片，自己不截屏。

## 3. 遍历列表并逐个提示

```json
{
  "name": "逐个提示列表项",
  "description": "把一个列表里的每一项弹一条提示",
  "variables": [
    { "key": "names", "type": "list", "default": ["小甲", "小乙", "小丙"] }
  ],
  "steps": [
    {
      "unit": "flow.forEach",
      "in": { "items": { "var": "names" } },
      "out": { "item": "name" },
      "body": [
        { "unit": "ui.toast", "in": { "text": "${name}", "durationMs": 1500, "key": "loop" } }
      ]
    }
  ]
}
```

`key` 相同会在原位更新（带 ×N），循环里不会刷屏。
""";
}
