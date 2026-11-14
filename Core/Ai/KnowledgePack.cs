using System.Reflection;
using System.Text;
using LTools.Core.Actions;

namespace LTools.Core.Ai;

/// <summary>
/// 随包分发的「动作单元知识」：给外部 agent 按需检索的明文文件，而不是一次性塞进它看不到的系统提示词。
/// </summary>
/// <remarks>
/// <para>
/// 目录见 <c>docs/AI_AGENT.md</c> §5.1：
/// <code>
/// ai/knowledge/
///   format.md   动作 JSON 格式与工作流（内置常量）
///   units.md    从 StepRegistry 自动生成的动作单元目录
///   examples.md 3 个完整示例（内置常量）
///   .version    生成版本（程序集版本 + 动作单元数 + units.md 正文哈希）
/// </code>
/// </para>
/// <para>
/// 三份文件都**按内容比对重写**（<see cref="Ensure"/>）：手改过的、旧版本留下的都会恢复成本程序内置的内容。
/// AI 面向的规则必须和当前二进制一致，留着旧规则只会让 agent 写出程序不再接受的步骤。
/// </para>
/// <para>
/// 动作单元目录不手写：<see cref="UnitSpec"/> 的 Input / Output / VisibleWhen / Options / OptionParams
/// 已经是机器可读的，这里只负责排版。新增动作单元时不需要改这个文件，启动后 <see cref="Ensure"/> 会自动刷新。
/// </para>
/// </remarks>
internal static class KnowledgePack
{
    public static string Dir => Path.Combine(AppPaths.DataRoot, "ai", "knowledge");

    public static string FormatPath => Path.Combine(Dir, "format.md");

    public static string UnitsPath => Path.Combine(Dir, "units.md");

    public static string ExamplesPath => Path.Combine(Dir, "examples.md");

    private static string VersionPath => Path.Combine(Dir, ".version");

    /// <summary>确保知识包存在且与当前程序一致。可在后台线程调用。</summary>
    /// <remarks>
    /// 以前只写缺失的文件：手改 / 旧版本留下的 <c>format.md</c>、<c>examples.md</c> 会一直生效，
    /// agent 照着过时的规则写动作，而程序已经不再接受那套写法。现在三份都按内容比对，
    /// **内置版本永远赢**（只多一次读文件的开销，几毫秒）。
    /// </remarks>
    public static void Ensure()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Dir);

            string units = BuildUnitsMarkdown();
            WriteIfDifferent(UnitsPath, units);
            WriteIfDifferent(FormatPath, FormatText);
            WriteIfDifferent(ExamplesPath, ExamplesText);

            string version = CurrentVersion(units);
            string previous = File.Exists(VersionPath) ? File.ReadAllText(VersionPath, Encoding.UTF8).Trim() : "";
            if (!string.Equals(previous, version, StringComparison.Ordinal))
                File.WriteAllText(VersionPath, version, Utf8);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Ai", $"生成动作单元知识失败：{ex.Message}");
        }
    }

    /// <summary>强制重写三份知识文件（设置页「重新生成」）。返回写入的动作单元数。</summary>
    public static int Regenerate()
    {
        System.IO.Directory.CreateDirectory(Dir);
        string units = BuildUnitsMarkdown();
        File.WriteAllText(UnitsPath, units, Utf8);
        File.WriteAllText(FormatPath, FormatText, Utf8);
        File.WriteAllText(ExamplesPath, ExamplesText, Utf8);
        File.WriteAllText(VersionPath, CurrentVersion(units), Utf8);
        return StepRegistry.Default.Units.Count;
    }

    /// <summary>内容不同才写：避免每次启动都重写三份文件（也保住文件的修改时间）。</summary>
    private static void WriteIfDifferent(string path, string content)
    {
        if (File.Exists(path))
        {
            try
            {
                if (string.Equals(File.ReadAllText(path, Encoding.UTF8), content, StringComparison.Ordinal))
                    return;
            }
            catch (IOException)
            {
                // 读不了就照写（被占用 / 权限问题时下面那次写入会给出真正的错误）。
            }
        }

        File.WriteAllText(path, content, Utf8);
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

    private static string CurrentVersion(string unitsMarkdown)
    {
        string assembly = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";
        // 直接哈希**生成出来的正文**：动作单元参数 / 说明 / 选项 / 选项参数任何一处变了都要重写。
        // 只哈希「键 + 分类」的话，给某个动作单元换了一套参数（例如「模拟按键」重构）
        // 版本号不变，units.md 就一直停在旧内容，agent 照着旧参数写步骤会全部失败。
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(unitsMarkdown));
        return $"{assembly}|{StepRegistry.Default.Units.Count}|{Convert.ToHexString(hash)[..16].ToLowerInvariant()}";
    }

    private static string BuildUnitsMarkdown()
    {
        var units = StepRegistry.Default.Units
            .OrderBy(u => UnitCategories.Rank(u.Spec.Category))
            .ThenBy(u => u.Spec.Category, StringComparer.Ordinal)
            .ThenBy(u => u.Spec.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var md = new StringBuilder();
        md.AppendLine("# 动作单元目录");
        md.AppendLine();
        md.AppendLine("> 本文件由 LTools 从 `StepRegistry` 自动生成，**不要手改**；升级后可能整份重写。");
        md.AppendLine("> 只需要查某个动作单元时，用 `ltools_units_get(key)` 拿它的完整规格；这里适合整段读 / grep。");
        md.AppendLine();
        md.AppendLine($"共 {units.Count} 个动作单元。`pure` = 取值动作单元，可以内联在参数或条件里；`action` = 有副作用的普通步骤。");
        md.AppendLine();
        md.AppendLine("有选项参数的动作单元会先给一张「选项 → 生效参数」表；参数表的「可见条件」列写 " +
                      "`operation in (drag)` 这种形式。**只填当前选项那一行需要的参数**——" +
                      "别的选项的参数留着不会生效（运行按默认值，`check` 给 warning）。");
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

        WriteOptionGroups(md, spec);

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
                    MachineName(input.Type),
                    input.Required ? "是" : "",
                    input.Default == null ? "" : $"`{Cell(input.Default.AsText())}`",
                    VisibilityCell(input, spec),
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
                    MachineName(output.Type),
                    $"`{OutputNaming.Suggest(spec.Key, output)}`",
                    Cell(output.Description ?? ""),
                }) + " |");
            }
            md.AppendLine();
        }
    }

    /// <summary>
    /// 「哪个选项要哪些参数」——<see cref="InputSpec.OptionParams"/> 驱动的可见性在参数表里只有
    /// 「可见条件」一列，反过来（选了这个选项要填哪几行）看不出来。agent 会在「单击」里写
    /// <c>dx</c>、在「去空白」里写 <c>separator</c>，而运行时会直接忽略这些值。
    /// </summary>
    private static void WriteOptionGroups(StringBuilder md, UnitSpec spec)
    {
        InputSpec? driver = OptionDriver(spec);
        if (driver?.OptionParams == null) return;

        md.AppendLine($"选项「`{driver.Key}`」（{driver.Label}）决定这些参数是否生效；" +
                      "**不在表里的参数在任何选项下都可以填**（填了也不一定被用到）：");
        md.AppendLine();
        md.AppendLine("| 选项 | 生效参数 |");
        md.AppendLine("|---|---|");
        foreach (var pair in driver.OptionParams)
        {
            string option = $"`{pair.Key}`（{OptionLabels.For(driver, pair.Key)}）";
            string inputs = pair.Value.Count == 0
                ? "（无）"
                : string.Join(" / ", pair.Value.Select(key => $"`{key}`"));
            md.AppendLine($"| {option} | {inputs} |");
        }
        md.AppendLine();
    }

    /// <summary>
    /// 参数表的「可见条件」：自己的 <see cref="InputSpec.VisibleWhen"/> 原样写；
    /// 由选项参数管的写成 <c>operation in (drag)</c> 这种能直接对照选项表的形式。
    /// </summary>
    private static string VisibilityCell(InputSpec input, UnitSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(input.VisibleWhen))
            return $"`{Cell(input.VisibleWhen)}`";

        IReadOnlyList<string> options = EnabledOptions(input, spec);
        if (options.Count == 0) return "";

        return $"`{OptionDriver(spec)!.Key} in ({string.Join(", ", options)})`";
    }

    /// <summary>驱动键 = 声明了 <see cref="InputSpec.OptionParams"/> 的那个枚举参数（每个动作单元最多一个）。</summary>
    internal static InputSpec? OptionDriver(UnitSpec spec) =>
        spec.Inputs.FirstOrDefault(input => input.OptionParams is { Count: > 0 });

    /// <summary>
    /// 这个参数由驱动键的哪些选项值启用（空 = 不受选项参数管，任何选项下都可见）。
    /// <c>units.md</c>、<c>ltools_units_get</c> 与校验器消息共用这一条，别再各算一遍。
    /// </summary>
    internal static IReadOnlyList<string> EnabledOptions(InputSpec input, UnitSpec spec)
    {
        InputSpec? driver = OptionDriver(spec);
        if (driver?.OptionParams == null) return Array.Empty<string>();

        return driver.OptionParams
            .Where(pair => pair.Value.Any(key =>
                string.Equals(key, input.Key, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Key)
            .ToList();
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

    /// <summary>
    /// 给 agent 看的类型名（`text` / `number` / `bool` / …），写进 `units.md` 的参数表。
    /// 界面上那一套中文名在 <see cref="ValueKindText"/>，两者是给不同读者看的，别合并。
    /// </summary>
    private static string MachineName(ValueKind? kind) => kind switch
    {
        ValueKind.text => "text",
        ValueKind.number => "number",
        ValueKind.@bool => "bool",
        ValueKind.list => "list",
        ValueKind.@object => "object",
        _ => "any",
    };

    private static string Cell(string? text) =>
        (text ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    // ============ 手写部分 ============

    private const string FormatText = """
# LTools 动作格式

> 本文件随 LTools 分发，**每次启动按程序内置内容重写**：手改会在下次启动被覆盖。
> 规则要改请改 LTools 源码（`Core/Ai/KnowledgePack.cs` 的 `FormatText`）。

你是 LTools 动作编写助手。**动作只能通过暂存区工具产出草稿，永远不要试图改正式动作**——
正式动作没有写工具，这是结构上的限制，不是靠你自觉。
全局子动作（函数）另有 `ltools_subactions_*` 写入工具，同样要写入开关 + 审批（见 §7）。

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
  ]
}
```

- `variables` 声明变量（`type` 是 `text` / `number` / `bool` / `list` / `object`）；运行时也能由步骤的输出创建新变量，但写进变量表更清楚。
- 变量可以写 `"direction": "input"`：这个动作就**带参数**——用户从面板 / 快捷键 / 悬浮运行前会被问一次
  （填过的值会记住），值以初始变量注入本次运行；不写就是 `local`（不问，直接用 `default`）。
  `output` 只在子动作里有意义（见 §7）。
- `steps` 是普通数组。控制流不是节点图：`flow.if` / `flow.repeat` / `flow.forEach` / `flow.while` 就是普通步骤，分支体放在 `then` / `else` / `body` 里。
- 步骤字段：`unit`（动作单元键）、`in`（参数）、`out`（输出键 → 变量名）、`body` / `then` / `else`、`note`、`disabled`、`onFail`（`stop` / `continue`）、`delayMs`。

## 2. 参数三态

`in` 里的每个值只有三种写法：

```json
{ "text": "字面量，支持 ${变量}" }
{ "text": { "var": "变量名" } }
{ "text": { "unit": "screen.findText", "in": { "text": "确定" }, "out": { "x": "x" } } }
```

- 字面量：字符串 / 数字 / 布尔 / 数组 / 对象。**文本里的 `${变量}` 会在运行时求值**（`$$` 转义成 `$`）。
  **整串只有一个 `${名字}` 时等同于 `{"var": "名字"}`**：拿到的是变量本身，列表 / 对象不会被压成文本；只有夹在文本里（`"共 ${count} 项"`）才是拼接。
- `{"var": "名字"}`：与整串 `${名字}` 等价（写哪个都行），路径可写 `a.b[0]`。
- `{"unit": ..., "in": {...}}`：内联一个 **pure** 取值动作单元，`action` 动作单元不能内联。
- 例外：`Raw: true` 的代码参数（`script.ahk` 的 `code`）原样传入，里面的 `${…}` 不要求值。

## 3. 工作流（工具名见 MCP）

0. `ltools_health`：写**坐标类**动作（鼠标操作 / 屏幕区域 / 截图 / 图片点击）之前先看 `environment`——
   每块屏幕的物理矩形与工作区、DPI 缩放、虚拟桌面、光标位置、常用目录、默认 OCR 引擎。
   坐标一律**物理像素**（与截图 / 找图 / 鼠标注入同一套），DIP 用 `scale` 换算；多屏时 `left` / `top` 可能是负数。
   还看不清屏幕就 `ltools_screenshot`（见 §5）：整屏 / 前台窗口 / 固定区域，图片会直接回到你的上下文里。
   要知道「那个按钮叫什么 / 在哪儿」就 `ltools_uia_tree`：控件树给 name / 控件类型 / automationId / 屏幕矩形，
   把 `name` 填进「文字点击」的文本，或把 `bounds` 中心当「鼠标操作」的坐标——**别靠猜**。
1. `ltools_units_list` / `ltools_units_get`：先确认能力存在，别臆造动作单元键。
   `units_get` 里 `optionParams` / `enabledWhen` 说明「哪个选项要哪些参数」（`units.md` 里有同样的选项表），
   **按当前选项只填需要的参数**，别把别的操作的参数堆进去（运行会忽略，`check` 会给 warning）。
2. `ltools_staging_create(title, description?, copyFromActionId?)`：新草稿。改用户已有动作时传 `copyFromActionId`（只读复制到草稿）。
3. `ltools_staging_open(draftId)`：读 `program` + `files` + `revision` + 绝对目录。
4. 写草稿的两条路，选一条即可：
   - `ltools_staging_write(draftId, program, expectedRevision)`：整份写入；
   - 或直接编辑 `<dir>/program.json` 与 `<dir>/files/…`（agent 自己的 fs 工具）。
   哪条都行：`revision` 从文件内容重算，写之前拿最新 revision 再改。
5. `ltools_staging_check(draftId)`：改到 `ok: true`（`issues` 里的 `error` 必须清零，`warning` 可以留）。
6. `ltools_staging_save(draftId, expectedRevision, summary?)`：冻结为已保存版本。
7. `ltools_staging_run(draftId, startPath?, dryRun?)`：先 `dryRun: true` 干跑——只解析参数 + 跑取值动作单元
   （找图 / 识字 / 读文件 / 截图），**会动键鼠或弹窗口的步骤跳过**（轨迹里 `status = skipped`）；
   确认参数与分支对了再去掉 `dryRun` 真跑一遍（真的动键鼠）。两种都用 `ltools_run_output` 读轨迹：
   `inputs` 是**变量展开后**的参数，`status = skipped` 的步骤输出是空的，依赖它们的步骤可能报错，那是预期。
8. 交给用户在 LTools 设置页点「采纳」（`ltools_staging_promote` 需要人工确认 / full 模式）。
9. 要写**全局子动作**（可复用的函数）：`ltools_subactions_check` → `ltools_subactions_write`，见 §7。

## 4. 校验器会拦什么

`error`（必须清零才能保存草稿）：

- 未知动作单元 / 未知参数
- 必填缺失（只对可见参数）
- 字面量类型不符；枚举值不在 `options` 里
- **枚举 / 多选参数写了变量或取值**：这两类只能填确定的值（要按条件选不同值，用 `flow.if` 分支写两套步骤）
- `${变量}` 引用了未声明的变量
- **`out` 的键不是该动作单元声明的输出键**（拼错时运行不会写任何东西，变量一直停在默认值）
- 条件运算符 / 正则无效；子动作 / 宏不存在；内联了 `action` 动作单元；引用了不存在的草稿图片

`warning`（不拦，但值得改）：

- 当前选项不支持的参数：例如「单击」里写了 `dx` / `dy`（那是「拖动」才用的）——
  运行时**根本不读它**，按默认值处理
- 列表 / 对象输出写进声明成 `number` / `bool` 的变量

位置用 `第 3 行 › 满足时 › 第 1 行`，与运行失败提示一致。

子动作另有三条：`flow.return` 只能出现在子动作里（动作 / 草稿里是 error）、
输出变量没有任何步骤赋值（warning）、子动作调用成环（error）。

## 5. 图片

草稿资源只接受图片：写到 `files/xxx.png`，步骤的 `image` 参数写 `files/xxx.png`。
`ltools_staging_run` / 面板试运行 / 编辑器测试会把草稿的 `files/` 当作运行期资源根，所以**不用写绝对路径**；
`files/xxx.png` 与裸 `xxx.png` 都先在那里找。
采纳时整个 `files/` 会复制进 `actions/<动作id>/files/`，引用原样保留；正式运行时以该目录为资源根。
其它类型文件 v1 直接拒绝。

要**产出**模板图就用 `ltools_screenshot`，别自己画：传 `draftId` 时它写进草稿 `files/`（返回 `files/shot-….png`），
`source` = `screen`（整块虚拟桌面）/ `foreground`（前台窗口）/ `region`（固定区域，要 `x,y,w,h`）。
图片本身会连同「截取矩形 + 缩略比」一起回到你的上下文里：照着图里要点的东西框一块 `region` 再截一次，
就是「找图 / 图片点击」能用的模板图。坐标换算：屏幕物理像素 = 图片像素 ÷ `thumbnail.scale` + `bounds.left` / `bounds.top`。
不传 `draftId` 时写进 `resources/images/`，返回值是文件名，正式动作的 `image` 也能直接用。
写文件需要打开「允许写入」（与 `ltools_staging_write` 同一道闸）。

## 6. 纪律

- 找不到能力就说不支持，**不要臆造动作单元键**，也不要试图写配置文件或 `actions/` 目录（正式动作只经用户采纳写入）。
- 不要为了一个功能另造一套循环 / 条件 / 取值——先看 `units.md` 里现成的动作单元。
- 动作执行在后台线程；会动键鼠的步骤（点击、输入、窗口操作）要留出等待（`flow.delay` 或动作单元自带的超时 / 等待）。
- 点不动就是没找对：先用 `ltools_uia_tree` 看控件树（名字 / 类型 / 矩形），再用 `ltools_screenshot` 看形状，
  **别靠猜坐标**加偏移硬凑。
- 用户动作只能读（`ltools_actions_get`）；要改就先 `staging_create(copyFromActionId:...)`。

## 7. 子动作（函数）

全局子动作存在 `<数据根>/subactions/<id>.json`，**一个文件一个子动作、文件名就是 id**
（26 位小写 ULID，见 `Core/Ids.cs`）。它与动作同构，只多一个「方向」：

```json
{
  "id": "01j2k4m6p8q0r2s4t6v8w0x2y4",
  "name": "打招呼",
  "variables": [
    { "key": "who",  "type": "text", "default": "世界", "direction": "input" },
    { "key": "text", "type": "text", "default": "",     "direction": "output" }
  ],
  "steps": [
    { "unit": "flow.set", "in": { "name": "text", "value": "你好 ${who}" } }
  ]
}
```

- `direction`：`input`（调用方按名字传值）/ `local`（内部变量）/ `output`（按 `out` 映射写回调用方）。
- 调用方用 `sub.run`：`{"unit":"sub.run","in":{"subAction":"01j2k4m6p8q0r2s4t6v8w0x2y4","who":"LTools"},"out":{"text":"greeting"}}`
  ——**参数名就是输入变量名**，写之前先 `ltools_subactions_get(id)` 看它的签名。
- `flow.return` 结束当前子动作（已赋值的输出照常带回），**只能用在子动作里**；
  `break` / `continue` 不会穿透到调用方的循环。
- 写入：`ltools_subactions_check(program)` 先校验，再 `ltools_subactions_write(program, expectedRevision?)`
  （需要写入开关 + 审批）。`revision` 是文件内容 sha256：覆盖已有子动作时先 `ltools_subactions_get` 拿它，
  再传给 `expectedRevision`；对不上会返回 `version_conflict`，重读后再改。
- 删除：`ltools_subactions_delete(id)`，会先列出调用它的动作位置。
""";

    private const string ExamplesText = """
# 完整示例

> 本文件随 LTools 分发，**每次启动按程序内置内容重写**：手改会在下次启动被覆盖。

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
