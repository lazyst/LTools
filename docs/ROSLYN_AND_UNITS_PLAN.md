# Roslyn C# 脚本层与 Quicker 借鉴单元落地计划（2026-11-15）

> 本文是**计划**，不是规格，也不是已批准的排期：实现约束看 `AGENTS.md`，术语看 `CONTEXT.md`，
> 动作系统设计看 `docs/ACTION_SYSTEM.md`，参数表单看 `docs/PARAM_FORM.md`。
> 上游数据：`docs/QUICKER_UNITS_SURVEY.md`（REA 分析 Quicker 2.3.36.0 得到 139 个 V1 单元，筛选后 118 个）
> 与 `docs/QUICKER_UNITS_ADOPTION.md`（§4 优先级主表、§7 落地检查清单、§8 推进顺序）。
>
> **阶段编号用 S0–S4**，刻意不叫 P0–P5：`docs/ACTION_SYSTEM_OPTIMIZATION.md` 那套 P0–P5
> 已于 2026-11-14 全部实施完成（见其 §9「实施记录」/ §10「完成状态」），两者不是同一件事。
> 本文的 P0 / P1 / P2 一律指 `QUICKER_UNITS_ADOPTION.md` §4 的分级（2 / 5 / 10 项）。
>
> 进度按阶段追加在 §9「实施记录」；计划本身改动时同步改 §3 对应条目，别只改代码。
>
> 术语待办：`script.csharp` 为拟新增动作单元键，「C# 脚本层」为本文暂用的描述性说法；
> 若 `CONTEXT.md` 表里没有，按 S0 步骤 4 先确认一句并补全 `CONTEXT.md` → `AGENTS.md` →
> 代码标识符链路，再开始 S1。本文不擅自改术语表。

---

## 0. 结论先行

| 维度 | 判断 | 依据（可验证） |
|---|---|---|
| 插值 | **不借鉴** | `Core/Actions/Template.cs` 的 `${名}` + `.成员` / `[0]` + 整串单变量保留 list/object 类型已覆盖；`$$` 语义相反（LTools 转义 `$`、Quicker 插值前缀），改则破坏存量动作 |
| 参数内 `$=` C# 表达式 | **不做** | 变量引用藏进自由文本会让 `ProgramWalker` / `RenameReferences` / `ActionValidator` 退化；要动的是 `StepArg` 三态这条全局契约 |
| Roslyn | **引，但只引在脚本动作单元层** | `InputSpec.Raw: true` + `InputEditor.code` 是项目现成的代码参数口子；新增 `script.csharp` 不动 `StepArg` / `StepText` / 校验器 / 知识包结构 |
| P0 / P1 | **全部仍做成一等动作单元** | 需要参数表单、枚举可见性、等待语义、统一失败性质、静态引用回收；脚本只兜底长尾组合 |
| P2 | **触发式，不预研式铺开** | `QUICKER_UNITS_ADOPTION.md` §8 原话；纯函数型优先由脚本层覆盖 |
| 回归门禁 | **沿用既有，不新建测试工程** | `--selftest` + 构建 0 警 + 真机 + 无头冒烟，见 §2 |

**一句话目标**：用 Roslyn 换掉 `script.ahk` 那套跨进程桥、并给长尾计算一个兜底出口；同时按
`QUICKER_UNITS_ADOPTION.md` 的评分补齐 2 个 P0 + 5 个 P1 能力缺口，两者互不替代、不重复建设。

**五阶段**（顺序不可换，详见 §3 / §4）：

| 阶段 | 主题 | 核心交付 | 新增依赖 |
|---|---|---|---|
| S0 | 基线与评审 | 体积/延迟基线、Roslyn 依赖评审、术语链路、分工判据 | 无 |
| S1 | `script.csharp` | Roslyn 脚本动作单元（进程内、类型保真、停止可中断） | `Microsoft.CodeAnalysis.CSharp.Scripting` |
| S2 | P0（2 项） | `text.hash` + `crypto.encrypt/decrypt`、`input.waitClipboard` | 无 |
| S3 | P1（5 项） | 二维码 / HTML 提取 / 下载 / UIA / 数字处理 | 2 项待评审（二维码库、HTML 库） |
| S4 | P2（触发式） | 按真实需求单项触发，默认不产生代码 | 无 |

> 本方案**仅为规划**；任何落地都要先按 `AGENTS.md` 走「改动清单 + 提交信息待批准」的流程。

---

## 1. 现状证据（只列能指到代码行的）

| # | 判断 | 证据 |
|---|---|---|
| 1 | 模板明确不做表达式 / 函数 | `Core/Actions/Template.cs:9` 注释「不提供表达式 / 函数调用——需要计算结果时用「计算」动作单元」 |
| 2 | 计算只有 10 个固定数值运算 | `Features/Units/Data/CalcUnit.cs:10-11` 的 `Operations` 数组 |
| 3 | 没有表达式求值器 | 全仓库 grep `Roslyn` / `CodeAnalysis` / `CSharp.Scripting` / `NCalc` / `DynamicExpresso` 无匹配；`LTools.csproj` 7 个 `PackageReference` 无 CodeAnalysis 传递依赖 |
| 4 | `script.ahk` 是一套 473 行的跨进程桥 | `Features/Script/AhkRunner.cs`：前言（`OnError` / `OnExit` / `@@OUT@@` / `@@ERR@@`）、`UserLineOffset` 行号换算、UTF-8 带 BOM、stdout UTF-8/OEM 双解码、`/ErrorStdOut /validate`、`TryKill(entireProcessTree)`、`LT_Var/LT_Set/LT_Print` 变量桥 |
| 5 | 脚本写回变量被压成文本 | `Features/Units/Script/ScriptUnits.cs:79` 的 `Value.FromText(pair.Value)` |
| 6 | 「检查语法」要起外部进程且有超时 | `Views/ActionEditor/CodeArgEditor.cs:59` 调 `AhkRunner.Check`；`AhkRunner.cs:124` 带 `CheckTimeoutMs` |
| 7 | `StepArg` 仍是三态 | `Core/Actions/Step.cs:96-103`（literal / variable / inline），`RenderLiteral` 是 `${…}` 求值的唯一入口 |
| 8 | 代码参数有现成的 `Raw` 口子 | `Core/Actions/UnitSpec.cs:85`（`Raw: true` 不做模板渲染）、`Core/Actions/PickerKind.cs:81`、`Core/Actions/InputHint.cs:30`、`Core/Ai/KnowledgePack.cs:376` |
| 9 | 上游优先级与落地清单已定稿 | `docs/QUICKER_UNITS_ADOPTION.md` §4（P 降序 22 行）、§5（P0/P1 详述）、§7（14 项检查清单）、§8（推进顺序） |
| 10 | 另一套 P0–P5 已完成 | `docs/ACTION_SYSTEM_OPTIMIZATION.md` §9.1–9.7、§10 |

---

## 2. 原则（每个阶段都按这几条裁）

1. **先定分工，再动代码。** Roslyn 与 P0/P1/P2 有重叠，按下面的判据裁，避免「一个东西两条路」：
   - **做成一等动作单元**（满足任一）：需要 GUI 参数表单 / 枚举 / `OptionParams` 可见性；需要等待语义（`Timeouts` 三值 + `ctx.Wait` + 停止热键）；输出会被 `${变量}` 引用因而需要改名同步 / 资源回收 / 静态校验；需要统一失败性质（`ActionErrors`）与重试门禁；或复用已有地基（`HttpClient`、`NativeClipboard`、FlaUI、`ScreenRegion`）。
   - **交给 `script.csharp` 兜底**：低频、纯 BCL 一行、组合多变的胶水计算与长尾。
2. **单点实现、加法兼容。** 新机制要么替掉旧的，要么别加；不做迁移器、不认旧字段、不留别名（沿用 `AGENTS.md` 既有策略）。
3. **不碰全局契约。** 不把 C# 表达式塞进任意参数、不给 `StepArg` 加第四态、不复刻 `$$` / `$=`。
4. **每阶段独立可交付、可回滚。** 任何一阶段停下来，仓库都处于可构建、可发版的状态；S1 可整体摘除而不影响 S2/S3。
5. **不预研式铺开 P2。** 按真实需求单项触发；§4 #18–22「不做」项不因本计划复活。
6. **沿用既有门禁。** 不新建测试工程、不引测试框架；「新增单元不许悄悄逃过测试」由 `--selftest` 的 `declaration` 覆盖清单保证。

### 贯穿全程的统一验收门禁（§3 每项不再重复）

| 编号 | 门禁 | 判据 |
|---|---|---|
| G1 | 构建 | 先 `Stop-Process -Name LTools`；`dotnet build -p:NoWin32Manifest=true` **0 错 0 警** |
| G2 | 自检 | `LTools.exe --selftest` 全绿（报告在 `%TEMP%\ltools-selftest\report.txt`）；改引擎 / 校验器 / `StepText` / 单元声明**先加断言**，记录例数 / 断言数增量（写法参照 `ACTION_SYSTEM_OPTIMIZATION.md` §9） |
| G3 | 真机 | 点一遍相关路径：参数表单、试运行、停止热键、失败提示 |
| G4 | 冒烟 | 改到 UI / 参数表单 / 模板时跑仓库外无头冒烟（`%TEMP%\opencode\*-smoke`，反射驱动 `LTools.dll`） |
| G5 | 知识包 | `KnowledgePack.Regenerate()` 三份一起重生、`units.md` **不手改**；`units_list/get` 能查到新单元 |
| G6 | 依赖评审 | 新 NuGet 记许可证 / 包体积 / 发布 target 清理 / 同步 `AGENTS.md` 第三方依赖清单 |
| G7 | 性能 | `--perf` 量化：Roslyn 首次编译延迟、发布包体积增量、切页 / 开窗无可见卡顿 |
| G8 | 提交 | 改动清单 + 提交信息待批准；`git push` / 打 tag 各自单独批准 |

---

## 3. 阶段计划

### S0 · 基线与评审

**目标**：在做任何功能改动前，把「引 Roslyn 值不值」变成可复核的数字与结论。

**步骤**
1. 记基线：构建 0 警、`--selftest` 当前例数 / 断言数、动作单元数、`publish` 产物字节数。
   发布命令必须显式带项目文件：`dotnet publish LTools.csproj -c Release -r win-x64 --self-contained true -o publish`。
2. Roslyn 依赖评审：`Microsoft.CodeAnalysis.CSharp.Scripting` 版本对齐 .NET 10、许可证、依赖树、发布包增量估算；确认 csproj 现有 7 个包无 CodeAnalysis 传递依赖（§1 #3 已核实，评审时复核）。
3. `--perf` 量冷 / 热首次 `CSharpScript.Create/Compile` 延迟，定 UI 线程预算（对齐「切页不卡鼠标」19ms → 0.06ms 那套口径）。
4. 术语：「C# 脚本层」/ `script.csharp` 若 `CONTEXT.md` 表里没有，先确认一句并补进 `CONTEXT.md` → `AGENTS.md` → 代码标识符链路；**不做向后兼容**。
5. 把 §2.1 的分工判据写成一页结论，作为 S2 / S3 的裁量依据。

**验收**
- 有可复核的基线数字与评审结论（引哪个包、许可证、预计增量、是否可接受）；**评审未通过不进 S1**。
- 术语链路补齐、无「第二种叫法」。
- `git diff` 无功能代码改动（仅文档 / 评审记录）。

---

### S1 · `script.csharp`（Roslyn C# 脚本层）

**目标**：进程内跑 C# 脚本，换掉跨进程桥的复杂度、拿到类型保真与编译期诊断；**不碰参数模板**。

**步骤**
1. `Features/Script/CSharpRunner.cs`：`ScriptOptions` 用**白名单**引用集与 `AddImports`
   （`System` / `System.Linq` / `System.Text.Json` / `System.Text.RegularExpressions` / `System.IO` /
   `System.Security.Cryptography` 等），不开放任意 `AddReferences`；`globals` 注入宿主对象
   （读写 `VariableScope`、输出、`ctx`）；`CancellationToken` 与 `ctx.Token` 串联。
2. `Features/Units/Script/CSharpScriptUnit.cs`：`Key = "script.csharp"`、用现有「脚本」分类；
   `Inputs`：`code`（`InputEditor.code` + `Raw: true`、必填）、`timeoutMs`（**可见参数**、`Timeouts` 三值）；
   `Outputs` 对齐 `AhkScriptUnit` 形状但**类型保真**（不 `Value.FromText`）。
3. `Features/Units/BuiltInUnits.cs` 的 `RegisterAll` 加一行；`script.ahk` 全程保留、并存不互斥。
4. 「检查语法」：`Views/ActionEditor/CodeArgEditor.cs` 按单元键分流——`script.csharp` 走
   `CSharpScript.Create(...).Compile()` → diagnostics（带行号），**同步、无进程、无 `CheckTimeoutMs`**；
   `script.ahk` 仍走 `AhkRunner.Check`。
5. 失败语义：编译 / 运行错误 → `ActionException` 带**用户行号**（Roslyn 直接给，不需要 `UserLineOffset` 换算），
   走统一提示 / 轨迹链；停止 → `ActionAbortedException`（不是失败）；超时 → `ActionErrors.Timeout`。
6. `UnitSpec.DryRun = false`（脚本不进干跑，与 `ui.input` / `file.select` 同口径）；
   `Raw: true` 保证 `${…}` 不被模板求值（知识包已有特例说明，见 `KnowledgePack.cs:376`）。
7. **静态分析不动**：确认 `StepArg` 仍三态，`ProgramWalker` / `RenameReferences` / `ImageLibrary` /
   `MacroStore` 无行为分支改动；`docs` 写明边界——脚本内按名读写的变量**不参与**改名同步，
   脚本内引用的图片 / 宏**不进**引用回收保护。
8. 自检断言：语法检查（合法 / 非法）、变量读写类型保真、停止中断、超时、行号、干跑跳过。
9. `Regenerate()` 三份一起；编译结果按代码哈希缓存；**启动时在后台线程 JIT 预热一次空脚本**
   （S0 实测冷 `Compile` = 1074 ms，同步跑会卡住 UI 线程 1 秒），「检查语法」只走热路径（实测 20–25 ms）。

**验收**（G1–G8 + 下列专属项）
- 进程内执行、**不产生任何外部进程**，无随包 exe / 无 GPL 运行时。
- 停止热键 500ms 内中断：编译 / 求值阶段与脚本主动调用 `vars.CheckStop()` 的检查点生效，
  抛 `ActionAbortedException`（不是失败）。**纯 CPU 紧密循环 Roslyn 无法强制中断**（见 §9.2 已知限制）。
- 「检查语法」同步返回、非法代码给带行号 diagnostics、**不执行脚本且无副作用**；
  后台预热完成后冷启动不再出现 >100 ms 的 UI 线程阻塞（`--perf` 复核）。
- 类型保真：脚本写 list / object / number，后续 `${变量}` 读到对应类型。
- `git diff` 不含 `StepArg` / `StepText` / `ActionValidator` / `VisibleWhen` / `ArgFreeze` 的行为分支改动。
- 发布包增量 ≤ §5 阈值（≤ 15 MB）、首次编译延迟 ≤ §5 预算；`script.ahk` 原有动作照常跑。
- 边界限制已写进 `docs`，且 `units_list` 能查到 `script.csharp`。

---

### S2 · P0（2 项，无新依赖）

#### S2-1 `text.hash` + `crypto.encrypt` / `crypto.decrypt`

**步骤**
1. 三个文件三个单元（`Features/Units/<实现领域>/`），**不做 `operation` 三合一**
   （`docs/QUICKER_UNITS_ADOPTION.md` §5.1：`AGENTS.md`「别再往任何一个里加 `operation` 三选一」）。
2. `UnitSpec` 是唯一声明：`UnitKind.pure`、`OutputSpec` 声明（决定类型徽标与变量表自动登记）、
   `Options` + `Labels`（补 `Core/Actions/OptionLabels.cs`）、`OptionParams` 表达 iv / 非对称差异。
3. 实现走 `System.Security.Cryptography`，**不引新 NuGet**；复用 `text.encode` 已有的 Base64 / HEX 约定与 `charset` 语义。
4. `crypto.decrypt` 失败按 `ActionErrors.Precondition` 标性质。
5. 自检加各算法向量断言（含 HMAC）、空文本、编码输出、`decrypt(encrypt(x)) == x`。
6. 注册 + 知识包重生 + `StepSummary` 摘要。

**验收**
- `--selftest` 新增向量断言全绿；三个单元独立注册、参数里**没有** `operation`。
- 参数表单：`algorithm` 下拉是中文名、`iv` 只在对称算法下可见（`OptionParams`，非手写 `VisibleWhen`）。
- 干跑可跑且无副作用；`units_list` 三条目、`Outputs` 类型徽标正确。
- 与 `script.csharp` 边界写明（脚本只兜底长尾组合）；G1 / G2 / G3 / G5 / G8。

#### S2-2 `input.waitClipboard`

**步骤**
1. `Features/Units/Input/WaitClipboardUnit.cs`。
2. `timeoutMs` **可见参数**（不 `Advanced`），走 `Timeouts` 三值（`0` 只判一次 / `-1` 一直等 /
   有限值夹 1–600000）+ `Timeouts.Loop`；`intervalMs` 收进高级区。
   `includeRecentMs` 本批未实现（见 §9.3）。
3. 循环 `ctx.EnterIteration()` + `ctx.Wait`；干跑只判一次。
4. 读剪贴板走 `Native/NativeClipboard`（原始 Win32），**不用** WinForms / OLE。
5. 输出 `changed`（bool）/ `text` / `elapsed` / `found`；**超时 = `changed = false`，不报错**
   （等待类口径，照 `window.wait` / `input.waitKey`）。
6. `Description` 写明「等待结束后 Ctrl/C 可能还没抬起，后续模拟按键前建议加 100–200ms 延迟」。
7. 自检：声明、`Timeouts` 三值、命中 / 未命中纯判定各一条；完整等待循环走真机 G3。

**验收**
- 停止热键 500ms 内返回；`timeoutMs` 三态正确。
- 超时不抛异常、不算步骤失败（`flow.if` 能判 `changed`）。
- 符合 `param-form-smoke` 的 `[wait]` 口径：`timeoutMs` 不在高级区、说明里写全三个值。
- 真机「复制 → 等待 → 读到新内容」一条龙；G1 / G2 / G3 / G5 / G8。

---

### S3 · P1（5 项）

**S3-0 依赖评审（先于 S3-2 / S3-3）**：二维码库（ZXing.Net / QRCoder）与 HTML 库
（HtmlAgilityPack / AngleSharp）过 G6，含许可证、包体积、是否需要 csproj 清理 target
（参照 OpenCvSharp 的 `opencv_videoio_ffmpeg` target 做法）。评审结论见 §9.4、落地实测见 §9.5。
**验收**：结论入本文 §9.4 / §9.5；`AGENTS.md` 第三方依赖清单已随 S3-B 加包同步；
**未过则对应项降级为「脚本层覆盖」，不阻塞其它项**（两个包都过 G6，未降级）。

| 批次 | 步骤要点 | 验收标准 |
|---|---|---|
| S3-1 `data.number` | 纯函数零依赖；`OptionParams` 表达「哪个 operation 显示哪些参数」；`rounding` 规则与 `data.calc.round` 统一 | `--selftest` 进制 / 取整 / 千分位 / 百分比向量全绿；两处 `round` 行为一致；不新建分类，归「文本与数据」 |
| S3-2 `image.qrcodeGen` / `image.qrcodeRead` | 拆两个单元；生成输出 png 路径，**识别侧**把 `ctx.ResourceRoot` 传给 `ImageLibrary.Resolve`；识别 `found = false` 不报错、**不加在线识别兜底**；入参命名 `image`，`ImageLibrary.CollectAction` 按 `ProgramWalker.LiteralText(step,"image")` 自动覆盖引用回收（零改动） | 生成 → 识别往返成功；**删动作后生成图不成孤儿**（生成图落 `cache/screenshots/`，运行期清空）；`units_list` 两条目 |
| S3-3 `web.html` | 复用 `web.request` 的文本输出、**不给 `web.request` 加 `operation`**（保持「唯一 HTTP 出口」）；输出 `value` / `list` / `count` / `found` | XPath 命中 / 未命中 / 语法错三语义分明（找不到 `found = false`、语法错才抛）；与 `web.request` 串成两步 |
| S3-4 `web.download` | 复用 `web.request` 的静态 `HttpClient` + `HttpCompletionOption.ResponseHeadersRead` + 与 `ctx.Token` 串联的令牌；失败按 HTTP / 超时标 `ActionErrors` 性质 | 大文件流式不 OOM、按停止立即中断；`etag` / `md5` / `autoRename` 输出正确；不另建网络栈 |
| S3-5 `ui.uia` | 先只做 `Name` + `AutomationId` + 深度限制精确匹配，XPath / `expand` / `invoke` / `toggle` 留到有真实需求再加；复用 `Core/Ai/AiUia.cs` 的共享 `UIA3Automation` 与限深 / 限量口径、`Features/MouseOps`；MTA 直调**不包 `StaRunner`**；`ctx.EnterIteration` + `ctx.Wait`；找不到按 `ActionErrors.NotFound` 标性质 | 真机点一次按钮；`timeoutMs` 可见且三值齐全；找不到按性质标且能配 `RetryOn = notFound`；`ltools_uia_tree` 不受影响 |

**每批出口**（沿用 `QUICKER_UNITS_ADOPTION.md` §8 原话）：
新增单元 → 注册 → 自检断言 → 构建 0 警 → `--selftest` 全绿 → 真机点一遍。

---

### S4 · P2（触发式，不预研）

对应 `QUICKER_UNITS_ADOPTION.md` §4 第 8–17 行，分三类，**默认不产生代码**：

- **补一处分支即可**：#9 `color` 给 `screen.pixel` 加 `format`；#13 给 `data.time` 补 `parse` / `diff`；
  #14 给 `data.json` 补 `set` / `get` / `keys`。不新建单元。
  **验收** = 新分支的 `--selftest` 断言 + 参数表单可见性 + 原有分支回归。
- **纯函数窄需求**：#10 `text.charInfo`（复用 `Core/Pinyin.cs`）、#11 `text.count`、
  #12 `system.info`（只取本机项，**不照搬** Quicker 宿主专有输出）、#16 `system.service` / `system.registry`。
  **优先由 `script.csharp` 覆盖**，只有出现真实高频需求才升格为单元。
  **验收** = 若升格则过 G1–G8；否则在 `docs` 记一句「由脚本层覆盖」。
- **需新 UI 子系统 / 监控回调**：#8 `showmenu`、#15 `form`、#17 `fileSystemWatch`。暂缓，
  按真实需求单独立项；`fileSystemWatch` 若要补，优先补「等待文件出现」而非整套监控回调。

**P2 总验收**：每次触发单项仍过 G1–G8，`units.md` 自动同步、不手改知识包；
**§4 第 18–22 行「不做」项不因本计划复活**。

---

## 4. 顺序与依赖

| 阶段 | 依赖 | 为什么排在这里 |
|---|---|---|
| S0 | 无 | Roslyn 是唯一重量级新依赖，先量化体积 / 延迟再定阈值，避免做完才发现发布包膨胀 |
| S1 | S0 评审通过 | 单独一批、与功能单元解耦；失败可整体摘除，S2 / S3 不依赖脚本层 |
| S2 | S1（分工判据） | 无新依赖；顺带验证「单元 vs 脚本」判据是否成立，并给 S3 提供模板 |
| S3 | S2 + S3-0 依赖评审 | 内部顺序「依赖评审 → 纯函数 → 复用现有栈 → 需设计语义」，沿用采纳文档 §8 |
| S4 | 真机反馈 | 不预设，按需求单项触发 |

---

## 5. 度量

| 指标 | 目标 |
|---|---|
| 停止响应（可取消单元 / 脚本） | < 500ms |
| Roslyn 冷编译（首次 `Compile`） | 实测 **1074 ms**（5.9.0 临时探针）→ **不得在 UI 线程同步跑**；S1 必须后台 JIT 预热，预热后 ≤ 30 ms |
| Roslyn 热编译 / 求值 | 实测 **20–25 ms**；「检查语法」走缓存 + 后台，UI 线程无可感卡顿 |
| 发布包体积增量 | S0 只量到 4 个主 dll = 10,354,376 B；**S1 实测 +10,381,304 B（≈9.9 MB / +3%）**（清掉 13 语言卫星资源后）→ 阈值 **≤ 15 MB** |
| S0 实测基线 | 构建 0 错 0 警（12.77 s）；`--selftest` **112 例 / 2867 断言**（5590 ms，退出码 0）；动作单元 **80**；`publish` **345,625,900 B（≈329.6 MB）/ 633 文件** |
| 切页 / 开窗 | 无可感卡顿（对齐 19ms → 0.06ms 口径，`--perf` 量化） |
| `--selftest` | 例数 / 断言数只增不减；新增单元 100% 进 `declaration` 覆盖清单 |
| 每批构建 | 0 错 0 警 |

---

## 6. 非目标

- 把 C# 表达式塞进任意参数、`StepArg` 第四态、复刻 `$$` / `$=` 插值前缀。
- `operation` 三合一单元；输出靠 UI 手绑变量；`stopIfFail` 逐单元重复（采纳文档 §6 已列）。
- 迁移器 / 旧字段别名 / 归档 `.migrated`（沿用「不向后兼容」）。
- 新建测试工程、引入 xunit / NUnit 等测试框架（沿用 `--selftest`）。
- 让脚本层替代一等动作单元的输出声明 / 静态校验 / 轨迹粒度。
- 预研式铺开 P2；复活 §4 #18–22「不做」项。
- 把「表达式与插值」的结论补进 `QUICKER_UNITS_ADOPTION.md`（用户 2026-11-15 明确「不补」）。

---

## 7. 风险与回滚

| 风险 | 应对 |
|---|---|
| Roslyn 包体积 / 首次编译延迟 / 任意代码执行面扩大 | S1 单独一批，**可整体摘除而不影响任何 P0 / P1 单元**（它们不依赖脚本层）；阈值由 S0 定 |
| 脚本内变量 / 资源引用脱离静态扫描 | S1 步骤 7 的边界声明写进 `docs`；不把 C# 塞进任意参数、不加第四态、不复刻 `$$` |
| 依赖评审不过 | 二维码 / HTML 项降级为「脚本层覆盖 + 单元留空」，不阻塞其它项 |
| `script.ahk` 受影响 | 全程保留、并存；任何时刻可回退 |
| 「检查语法」编译阻塞 UI 线程 | S0 实测冷 `Compile` = 1074 ms；结果按代码哈希缓存 + **启动时后台 JIT 预热**，「检查语法」只走 20–25 ms 热路径，G7 复核 |
| Roslyn 卫星资源把发布包撑大 | S1 实测 13 语言 `Microsoft.CodeAnalysis*.resources.dll` 约 7 MB；照 OpenCV ffmpeg 先例加 `RemoveRoslynSatelliteResources` target，构建 / 发布后删。诊断只用 `InvariantCulture`，缺卫星程序集时 .NET 自动回退主程序集里的中性资源，不抛异常 |
| 纯 CPU 紧密循环无法被停止热键中断 | 实测 `while(true)` 不响应 `CancellationToken`；`timeoutMs` 用 `Task.Wait` 超时保护 + `vars.CheckStop()` 协作式检查点。超时后动作返回失败，但脚本线程可能继续占 CPU 直到进程退出——Roslyn 无 `Thread.Abort`，进程隔离又违背「进程内」目标，作为已知限制接受 |

---

## 8. 决策记录

| 日期 | 决策 | 理由 |
|---|---|---|
| 2026-11-15 | 插值不借鉴 | LTools `${名}` 已覆盖且更贴 5 种值模型；`$$` 语义相反，改则破坏存量动作 |
| 2026-11-15 | 参数内 `$=` C# 表达式不做 | 变量引用藏进自由文本会让静态扫描退化；要动 `StepArg` 三态这条全局契约，净收益为负 |
| 2026-11-15 | Roslyn **只引在脚本动作单元层** | `Raw: true` + `InputEditor.code` 是现成口子；新增 `script.csharp` 不动全局契约，且省掉 `AhkRunner` 的跨进程桥、拿到类型保真与编译期诊断 |
| 2026-11-15 | P0 / P1 全部仍做成一等动作单元，脚本只兜底长尾 | 单元有参数表单 / 轨迹 / 失败语义 / 静态引用回收；脚本一步把这些变黑盒（步骤粒度变粗） |
| 2026-11-15 | P2 触发式、不预研 | `QUICKER_UNITS_ADOPTION.md` §8；纯函数型优先由脚本层覆盖 |
| 2026-11-15 | 阶段编号用 S0–S4，方案独立成文 | 与已实施的 P0–P5 区分；用户明确「不补」进采纳文档 |
| 2026-11-15 | Roslyn 版本建议 **5.9.0** | S0 评审：稳定版（2026-08-17 / 86 万下载）、MIT、net10.0 兼容；5.12.0 发布仅 2 天，暂不取；5.3.0 作保守备选 |
| 2026-11-15 | S1 必须后台 JIT 预热 | S0 实测冷 `Compile` = 1074 ms，同步跑会卡 UI 线程约 1 秒；改了 §3 S1 步骤 9 与验收 |
| 2026-11-15 | 不新增术语 | 照 `script.ahk`「AutoHotkey 脚本」先例叫「C# 脚本」、沿用现有「脚本」分类；`CONTEXT.md` 的「脚本」是「动作 / 宏」的**别名禁用**，不是禁止动作单元名称 |
| 2026-11-15 | 清掉 Roslyn 卫星资源 | S1 实测 13 语言 `*.resources.dll` 约 7 MB；诊断只用 `InvariantCulture`，缺资源自动回退中性资源；加 csproj target 后增量从 16.9 MB 降到 9.9 MB |
| 2026-11-15 | 停止改协作式 + 超时保护 | 实测 Roslyn 不强制中断纯 CPU 循环；`vars.CheckStop()` + `Task.Wait(timeout)`，§3 S1 验收口径与 §7 风险表相应调整 |

---

## 9. 实施记录

> 每批完成后按下面的模板追加一节；**先写验收数字，再写落地内容**。
> 计划本身改动时同步改 §3 对应条目，别只改代码。

### 模板

```markdown
### 9.x <阶段 / 批次>（YYYY-MM-DD）

**验收**

- 构建 0 错 0 警；`--selftest` N 例 / M 断言全绿（约 X 秒）；动作单元 K 个。
- <该批专属验收项，逐条对应 §3>
- 真机：<点过的路径>；冒烟：<跑过的 *-smoke>。

**落地内容**

| 文件 | 作用 |
|---|---|
| ... | ... |

**偏差 / 决策变更**：<无，或写清改了 §3 哪一条、为什么>
```

### 9.1 S0 · 基线与评审（2026-11-15，实测完成）

**验收 / 实测基线**（`dotnet build -p:NoWin32Manifest=true` → `LTools.exe --selftest` →
`dotnet publish LTools.csproj -c Release -r win-x64 --self-contained true -o publish`）

| 项 | 值 |
|---|---|
| 构建 | 0 错 0 警，12.77 s |
| `--selftest` | **112 例 / 2867 断言全绿**，5590 ms，退出码 0；真实动作库往返 9 份；动作单元 **80** 个 |
| 版本 | LTools v2.6.7.0 · .NET 10.0.15 |
| `publish` | **345,625,900 B（≈329.6 MB）/ 633 文件** |

**依赖评审（`Microsoft.CodeAnalysis.CSharp.Scripting`）**

| 项 | 结论 |
|---|---|
| 版本 | **建议 5.9.0**（2026-08-17，86 万下载）；5.12.0（2026-11-13，2 天 / 1560 下载）暂不取；5.3.0 保守备选 |
| 许可证 | MIT |
| 框架 | net10.0 与 netstandard2.0 兼容；项目为 .NET 10.0.15 |
| 依赖树 | `Microsoft.CodeAnalysis.Common` / `.CSharp` / `.Scripting.Common` / `.Analyzers` + `Microsoft.CSharp`；net10.0 下其余依赖为框架自带 |
| 体积 | 实测 4 个 `Microsoft.CodeAnalysis*.dll` = **10,354,376 B（≈9.9 MB）**，对基线约 **+3%** |
| 冷 / 热编译 | 临时 console 探针（`%TEMP%\opencode\roslyn-probe`，5.9.0）：冷 `Compile` **1074.4 ms**、热 23.7 / 20.2 ms、`EvaluateAsync` 24.9 ms |

**偏差 / 决策变更**

- 体积增量远小于起草时预估的 20–30 MB（实测 ≈9.9 MB / +3%）；§5 阈值定为 ≤ 15 MB。
- **新增硬约束**：冷编译 1074 ms 不得落在 UI 线程，S1 步骤 9 改为「启动时后台 JIT 预热 + 结果缓存」，
  验收补「预热后冷启动不再出现 >100 ms 的 UI 线程阻塞」；§7 风险表同步。
- **S0 步骤 4 术语结论**：不新增术语——`script.csharp` 中文名照 `script.ahk`「AutoHotkey 脚本」的先例叫
  「C# 脚本」，分类沿用现有「脚本」；`CONTEXT.md` 里「脚本」是「动作 / 宏」的**禁用别名**，不是禁止动作单元名称；
  文档标题的「C# 脚本层」为描述性说法、非术语，**未**写入术语表。若要升格为正式术语，需另行确认并补
  `CONTEXT.md` → `AGENTS.md` → 代码标识符。
- **S0 步骤 5**：分工判据已落在本文 §2.1，作为 S2 / S3 裁量依据。

**评审结论：通过，可进入 S1。可调整项**（不阻塞 S1）：Roslyn 版本最终确认（建议 5.9.0）、
术语是否升格（建议不升格）。

---

### 9.2 S1 · `script.csharp`（2026-11-15，已实现）

**验收**

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build -p:NoWin32Manifest=true` **0 错 0 警**（4.44 s） |
| `--selftest` | **118 例 / 2917 断言全绿**，9371 ms，退出码 0；真实动作库往返 9 份；动作单元 **81** 个（+1） |
| 新增自检 | `csharp` 组 6 例 / 50 断言：声明完整、语法检查（合法 / 非法 / 不执行）、返回值类型保真、变量读写、编译错误行号、停止点 |
| 进程内 | `CSharpRunner` 直接 `CSharpScript.Create/RunAsync`，**无外部进程、无随包 exe、无 GPL 运行时** |
| 「检查语法」 | `CodeArgEditor` 按 `UnitKey` 分流：`script.csharp` → `CSharpRunner.Check`（进程内 `Compile()`，同步、无 `CheckTimeoutMs`）；`script.ahk` 仍走 `AhkRunner.Check` |
| 类型保真 | `ToValue` 把任意 .NET 对象经 `ValueJsonConverter.FromElement` 转成 5 种值；自检断言 list / number / bool 不被压成文本，`vars.Set` 写回的 list 保真 |
| 失败语义 | 编译错误带**用户代码行号**（Roslyn 直接给，无 `UserLineOffset`）；运行时错误从堆栈提 `:line N`；超时 → `ActionErrors.Timeout`；停止 → `ActionAbortedException` |
| 静态分析未动 | `git diff` 不含 `StepArg` / `StepText` / `ActionValidator` / `VisibleWhen` / `ArgFreeze` 的行为分支改动；`code` 走既有 `Raw: true`，`DryRun = false` |
| 发布包增量 | **+10,381,304 B（≈9.9 MB / +3%）** ≤ §5 阈值 15 MB；13 语言卫星资源清 0 残留 |
| 后台预热 | `App.OnStartup` 末尾 `Task.Run(CSharpRunner.Warmup)`，只排队不等；`Warmup` 编译一次空脚本吃掉冷 JIT |
| 知识包 | `script.csharp` 已进 `StepRegistry.Default`（`declaration` 组覆盖 81 个单元）；`units.md` 由启动时 `KnowledgePack.Ensure()` 按内容比对自动重生，不手改 |

**落地内容**

| 文件 | 作用 |
|---|---|
| `LTools.csproj` | 加 `Microsoft.CodeAnalysis.CSharp.Scripting` 5.9.0；加 `RemoveRoslynSatelliteResources` / `...OnPublish` 两个清理 target |
| `Features/Script/CSharpRunner.cs` | 固定 `ScriptOptions`（白名单引用集 + 默认 using）、`CSharpGlobals` / `CSharpHost`（`vars` 读写变量 / 输出 / `CheckStop`）、`Run` / `Check` / `Warmup` / `ToValue` |
| `Features/Units/Script/CSharpScriptUnit.cs` | `script.csharp` 声明与执行；`code`（`Raw: true`）+ 可见 `timeoutMs`，5 个输出 |
| `Features/Units/BuiltInUnits.cs` | 注册一行 |
| `Views/ActionEditor/ArgEditor.cs` / `StepFormView.Editors.cs` | `ArgEditorContext.UnitKey`，构造时传 `_spec.Key` |
| `Views/ActionEditor/CodeArgEditor.cs` | 按 `UnitKey` 分流「检查语法」与工具提示 |
| `App.xaml.cs` | 启动末尾后台 JIT 预热 |
| `Core/SelfTest/Suites/CSharpSuite.cs` / `SelfTestRunner.cs` | 新增 `csharp` 组 6 例并注册 |
| `AGENTS.md` | 第三方依赖清单同步（G6） |

**已知限制 / 偏差（改了 §3 S1 验收与 §7 风险表）**

- **纯 CPU 紧密循环无法强制中断**：两次临时探针（`while(true)`）均不响应 `CancellationToken`，
  且失控线程会拖住进程。Roslyn 没有 `Thread.Abort`，进程隔离又违背「进程内」目标。
  实现改为 `vars.CheckStop()` 协作式检查点 + `Task.Wait(timeoutMs)` 超时保护；超时后动作返回
  `ActionErrors.Timeout`，但脚本线程可能在后台继续占 CPU 直到进程退出。
  编译 / 求值阶段与主动检查点的停止响应 < 500ms，**不调用检查点的紧密循环不在保证范围**。
- **S0 体积估算不完整**：S0 只量了临时 console 工程 bin 里的 4 个主 dll（9.9 MB），
  漏了 13 语言卫星资源；publish 首测 **+16.9 MB 超阈值**。加清理 target 后降到 **+9.9 MB**。
  §5 阈值 ≤ 15 MB 保持不变，卫星资源清理写进 §7 / §8。
- **编译结果缓存未做**：`ScriptOptions` 静态缓存，但每次 `Run` / `Check` 仍新建 `Script` 并 `Compile()`
  （热路径 20–25 ms）。冷 JIT 由后台预热解决，按代码哈希缓存编译结果留到有实测需求再加。
- **`script.ahk` 真机回归 / 参数表单真机点验 / 「检查语法」UI 分流**：本批为无头构建，
  未做真机点一遍（G3 / G4），待用户点验；`script.ahk` 全程保留、并存不互斥。
- `--selftest` 耗时 5590 → 9371 ms（+3.8 s，Roslyn 冷 JIT + 6 例编译），仍远低于 90 秒门禁。

---

### 9.3 S2 · P0 两项（2026-11-15，已实现）

**验收**

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build -p:NoWin32Manifest=true` **0 错 0 警**（22.52 s） |
| `--selftest` | **124 例 / 3109 断言全绿**，9345 ms，退出码 0；动作单元 **85** 个（+4） |
| 新增自检 | `s2` 组 6 例 / 192 断言：哈希 4 条标准向量 + Base64 + HMAC、`OptionParams` 收起 iv、AES/DES 往返（含 hex 密钥）、RSA PEM 往返、三类前置条件、等待类声明与 `Timeouts` 三值 |
| 无新依赖 | 全部走 `System.Security.Cryptography` / `NativeClipboard` / `Timeouts`，`LTools.csproj` 未变 |
| 拆开而非三合一 | `text.hash` / `crypto.encrypt` / `crypto.decrypt` 三个独立单元，**参数里没有 `operation`**；`Spec.Validate()` 全过 |
| 加密声明 | `algorithm` 用私有 `Labels` 给中文名，`OptionParams` 让 `iv` 只在 AES / DES 下可见（RSA 下不可见时引擎不解析、不报缺参）；`declaration` 组覆盖 85 个单元 |
| 失败性质 | 密钥 / IV 长度不对、密文不是 Base64 → `ActionErrors.Precondition`（自检断言 `Kind`） |
| 等待类 | `input.waitClipboard` 超时返回 `changed = false` **不抛异常**；`ctx.EnterIteration()` + `ctx.Wait`；干跑只判一次；读剪贴板走 `NativeClipboard.TryGetText` |
| 知识包 / 摘要 | 4 个单元进 `StepRegistry.Default`（`declaration` 覆盖）；`units.md` 启动期自动重生；步骤摘要走通用 `StepSummary`，未加特例 |

**落地内容**

| 文件 | 作用 |
|---|---|
| `Features/Units/Text/HashUnit.cs` | `text.hash`：MD5 / SHA-1 / SHA-256 / SHA-512 + 可选 HMAC，hex / Base64 输出；`Compute` 纯函数供自检直调 |
| `Features/Units/Data/CryptoAlgorithms.cs` | 加解密共用纯计算：密钥解码（utf8 / hex / base64）、AES / DES 对称、RSA PEM |
| `Features/Units/Data/CryptoEncryptUnit.cs` / `CryptoDecryptUnit.cs` | `crypto.encrypt` / `crypto.decrypt`；`OptionParams` 收起 iv |
| `Features/Units/Input/WaitClipboardUnit.cs` | `input.waitClipboard`；`Changed` 纯判定供自检直调 |
| `Features/Units/BuiltInUnits.cs` | 注册 4 行 |
| `Core/SelfTest/Suites/S2Suite.cs` / `SelfTestRunner.cs` | 新增 `s2` 组 6 例并注册 |

**偏差 / 决策变更**

- **`includeRecentMs` 本批未实现**（改了 §3 S2-2 步骤 2 / 7）：Quicker 的语义是回溯「等待开始前
  N 毫秒内的变化」，而 `NativeClipboard` 只暴露 `LastOwnWriteTick`（本程序自己的写入时刻），
  没有通用的剪贴板变化时间戳，可靠实现需要另建监听。本批只做 `timeoutMs` + `intervalMs`，
  留到有真实需求时补，不假装支持。
- **完整等待循环未跑真机**（G3 / G4）：自检只覆盖声明、`Timeouts` 三值与命中 / 未命中纯判定；
  「停止热键 500ms 内返回」「复制 → 等待 → 读到新内容」「超时不抛异常」待用户真机点一遍。
- `docs/ACTION_SYSTEM.md` 未改：其 §3.6 / W* 表格是历史批次清单（W2/W3、2026-11 补的 13 个单元等），
  本批没改变动作系统模型，不往历史记录里插；新单元事实记在本文 §9.2 / §9.3，权威来源是 `StepRegistry.Default`。
- 对称算法要求**精确字节长度**的密钥 / IV（AES 16/24/32、DES 8），长度不对按
  `ActionErrors.Precondition` 报错并说明可用 hex / base64 给精确字节；不做「密码自动派生」，
  避免加密端与解密端语义分叉。

---

### 9.4 S3-A · 依赖评审 + `data.number` / `web.download`（2026-11-15，已实现）

**S3-0 依赖评审结论**（本批只评审、不加包；§3 S3-0 验收口径已同步）

| 包 | 版本 / 许可证 / 体积 | 结论 |
|---|---|---|
| ZXing.Net | 0.16.11 · Apache-2.0 · 约 9.96 MB | 生成 + 识别一个包够用；net5.0+ 主包**只含核心类**，写 PNG 需要 binding 包或直接用 `PixelData` 自己编码。S3-B 落地时二选一，不引第二个条码库 |
| HtmlAgilityPack | 1.13.1 · MIT · 约 1.37 MB | 原生 XPath + 容错 HTML，正好对应 `web.html` 的「命中 / 未命中 / 语法错」三语义；无需清理 target |

两个包都过 G6（许可证宽松、体积可控），**不降级为脚本层**。本批零新依赖，`LTools.csproj` 未变；
`AGENTS.md` 第三方依赖清单等 S3-B 真正加包时再同步。

**验收**

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build -p:NoWin32Manifest=true` **0 错 0 警**（10.83 s） |
| `--selftest` | **130 例 / 3313 断言全绿**，9438 ms，退出码 0；动作单元 **87** 个（+2） |
| 新增自检 | `s3a` 组 **6 例 / 102 断言**（`--filter s3a --verbose` 实测）；另 102 条来自 `declaration` 组对 2 个新单元的自动覆盖，合计 +204 断言 |
| 向量 | 进制：255→FF / FF→255 / 1010₂→10 / 10→1010₂ / 255→377₈ / 15→F；`OptionParams` 收起；三种舍入 + 与 `Math.Round` 逐点一致；千分位 / 百分比；四类前置条件；下载声明；`GuessName` / `SanitizeFileName` / `UniquePath` 纯判定 |
| 零新依赖 | `data.number` 走 `System.Globalization`，`web.download` 走既有 `HttpClient` / `System.Security.Cryptography`；`LTools.csproj` 未变 |
| 共用网络栈 | `web.download` 调 `WebRequestUnit.Send`（内部 `HttpCompletionOption.ResponseHeadersRead`）/ `ApplyHeaders` / 共享 `Client`；**不给 `web.request` 加 `operation`**（自检断言），`web.request` 仍是唯一 HTTP 出口 |
| 舍入规则统一 | `CalcUnit` 的 `round` 改为委托 `NumberUnit.Round(a, digits, "banker")`；banker = `Math.Round` 默认（ToEven），自检逐点比对 `Math.Round`，**`data.calc` 语义不变**。`data.number` 默认 `round45`（中文「四舍五入」= 0.5 进位），另给 `banker` / `truncate` |
| 下载写盘 | 80 KB 分块流式写 + `IncrementalHash` 算 MD5，每块 `ctx.EnterIteration()` 响应停止；先写 `.ltools-part`、成了才 `File.Move`（`autoRename=false` 即覆盖）；失败 `finally` 删半成品，**不留半个文件** |
| 失败性质 | 404 → `NotFound`、其它非 2xx → `Precondition`、超时 → `Timeout`、停止 → `ActionAbortedException`；非法地址 / 空目录 → `Precondition` |
| 声明 / 可见性 | `OptionParams` 让 `base` 只在进制转换、`digits` / `rounding` 只在取整格式化下显示；`Spec.Validate()` 与 `declaration` 组覆盖 87 个单元全过 |
| 知识包 / 摘要 | 2 个单元进 `StepRegistry.Default`，`units.md` 启动期自动重生；步骤摘要走通用 `StepSummary`，未加特例 |

**落地内容**

| 文件 | 作用 |
|---|---|
| `Features/Units/Data/NumberUnit.cs` | `data.number`：进制转换 / 取整 / 千分位 / 百分比；`Compute` / `Round` 纯函数供自检直调 |
| `Features/Units/Data/CalcUnit.cs` | `round` 委托 `NumberUnit.Round(..., "banker")`，统一舍入规则、语义不变 |
| `Features/Units/Web/WebDownloadUnit.cs` | `web.download`：流式下载 + MD5 / ETag / 自动改名；`GuessName` / `SanitizeFileName` / `UniquePath` 纯判定 |
| `Features/Units/Web/WebRequestUnit.cs` | `Client` / `Send` / `ApplyHeaders` 改 `internal static` 供同程序集复用（行为不变） |
| `Features/Units/BuiltInUnits.cs` | 注册 2 行 |
| `Core/SelfTest/Suites/S3ASuite.cs` / `SelfTestRunner.cs` | 新增 `s3a` 组 6 例并注册 |

**偏差 / 决策变更**

- **`skipCertVerify` 未实现**：Quicker 的下载步允许跳过证书校验；LTools 共用 `SocketsHttpHandler` 的默认校验，
  不为下载单独开一个「不安全」旁路（`web.request` 也没有这个开关）。自签名站点走系统信任根，不写进动作单元。
- **`data.number` 输入合并成一个 `value`**（改了 §3 S3-1 的设计）：Quicker 把「数字」与「进制串」分成两个输入框，
  本项目参数表单里会出现两个含义重叠的框；这里一个 `value` 通吃（`FF` / `1010` / `1234.5`），输出仍按
  「数值 + 文本 + hex + binary」四份给。
- **`timeoutMs` 收进「高级参数」**（与 `web.request` 一致）：它是 HTTP 超时（两值夹 1000–600000），
  不是等待类单元的「`0` 只判一次 / `-1` 一直等」三值；`param-form-smoke` 的 `[wait]` 只管 5 个等待类单元，
  不覆盖这里。
- **`ACTION_SYSTEM.md` 未改**：其 §3.6 / W* 表格是历史批次清单，本批没改变动作系统模型；
  新单元事实记在本文 §9.4，权威来源是 `StepRegistry.Default`。
- **真机点验仍欠**（G3 / G4）：本批为无头构建，自检不碰网络；「大文件流式不 OOM」「按停止热键立即中断」
  「404 / 超时 / 重名自动改名 / 覆盖」「`etag` / `md5` 与实际文件一致」待用户真机点一遍。
  S1 / S2 欠的真机点验也仍挂着。
- 首轮自检发现 `Path.GetFileName("a:b.txt")` 在 Windows 把 `a:` 当盘符、只留 `b.txt`；
  `SanitizeFileName` 改为**先换冒号再去目录**，已补断言「非法字符换下划线」守着。

---

### 9.5 S3-B · 二维码 / HTML 提取 / UIA 控件操作（2026-11-15，已实现）

**依赖落地实测（修正 §9.4 的体积口径）**

§9.4 评审表里的 9.96 MB / 1.37 MB 是 **NuGet 包体积（含多个 TFM 的 dll）**，不是本项目输出增量。
`%TEMP%\opencode\zxing-probe` 探针 + 实际构建 / 发布后的数字：

| 项 | 实测 |
|---|---|
| `zxing.dll` | **516,096 B**（主包 373 个类型：`QRCodeWriter` / `MultiFormatReader` / `RGBLuminanceSource` / `HybridBinarizer` / `BitMatrix` / `ErrorCorrectionLevel` 全在） |
| `HtmlAgilityPack.dll` | **170,496 B** |
| 卫星资源 / 原生库 | **0**（`*.resources.dll` 无新增）→ **不需要加 csproj 清理 target** |
| `publish` | `dotnet publish LTools.csproj -c Release -r win-x64 --self-contained true -o publish` → **356,767,867 B / 639 文件**（S1 基线 356,007,204 B / 633 → **+760,663 B / +6 文件**） |

ZXing 关键坑（探针实测）：`RGBLuminanceSource.BitmapFormat` 是**嵌套枚举** `ZXing.RGBLuminanceSource+BitmapFormat`；
3 参构造 `RGBLuminanceSource(byte[],w,h)` 按 24bpp 解释、喂 32bpp 数据 `decode` 返回 `NULL`，
**必须显式传 `BGRA32`**（= 8）。中文二维码在 `CHARACTER_SET=UTF-8` + `POSSIBLE_FORMATS` +
`TRY_HARDER` 下往返 `MATCH`；空白图 `decode` 返回 `NULL`（正好对应「找不到不报错」）；
`MultiFormatReader` / `QRCodeReader` **没有 `decodeMultiple`**（反射确认），只有 `decode` / `decodeWithState`。
HAP 的非法 XPath 抛 `System.Xml.XPath.XPathException`（"Expression must evaluate to a node-set."）。

**验收**

| 项 | 结果 |
|---|---|
| 构建 | `dotnet build -p:NoWin32Manifest=true` **0 错 0 警**（9.90 s） |
| `--selftest` | **136 例 / 3547 断言全绿**，9743 ms，退出码 0；动作单元 **91** 个（+4） |
| 新增自检 | `s3b` 组 **6 例 / 77 断言**（`--filter s3b --verbose` 实测 255 ms）；另 157 条来自 `declaration` 等组对 4 个新单元的自动覆盖，合计 +234 断言 |
| 向量 | 二维码英文 / 中文往返（内存位图，不落盘）、空白图识别不到、四种纠错等级、声明与 `image` 参数名；HTML 命中 / 未命中 / 属性 / `&amp;` 实体解码 / 非法 XPath 抛 `XPathException` / 空 XPath 抛 `Precondition`；`web.html` 与 `ui.uia` 声明、`OptionParams` 收起 `value`、`timeoutMs` 可见、窗口点选器入口；Pinyin 4 个名称首字母串 + `DuplicateCount == 0` |
| 拆开而非三合一 | 4 个独立单元，**参数里没有 `operation` 混不同目标对象**；`Spec.Validate()` 与 `declaration` 组覆盖 91 个单元全过 |
| 引用回收零改动 | `image.qrcodeRead` 的入参就叫 `image`，`ImageLibrary.CollectAction` / `CollectGarbage` / `OrphanFiles` 按 `ProgramWalker.LiteralText(step,"image")` 全量扫描，删动作后引用的图自动按引用回收 |
| `ctx.ResourceRoot` | 识别侧 `ImageLibrary.Resolve(ScreenArgs.ImagePath(args), ctx.ResourceRoot)`，草稿 `files/x.png` 先在草稿资源根找、正式动作先在 `actions/<id>/files/` 找 |
| 唯一 HTTP 出口 | `web.html` 只吃「HTTP 请求」的 `${response}` 文本，`web.request` 未加 `operation`（自检断言）；两步串起来 |
| UIA 复用 | `AiUia.Uia` 提为 `internal` 共享 `UIA3Automation`，`ui.uia` MTA 直调、不包 `StaRunner`；限深 12 / 限量 1000 / 每层 200 / 4 秒预算，`ctx.EnterIteration()` + `ctx.Wait`；`ltools_uia_tree` 语义不变 |
| 失败语义 | `web.html` 语法错 → `Precondition`，未命中 → `found=false`；`ui.uia` 超时 → `NotFound`（可配 `RetryOn=notFound`）、停止 → `ActionAbortedException`；`image.qrcodeRead` 找不到 → `found=false`，文件不存在 → `NotFound` |
| 知识包 / 摘要 / Pinyin | 4 个单元进 `StepRegistry.Default`，`units.md` 启动期自动重生；补「生 / 成 / 二 / 维 / 控」5 字与 `e` 行，自检断言首字母串 |

**落地内容**

| 文件 | 作用 |
|---|---|
| `LTools.csproj` | 加 `ZXing.Net` 0.16.11 / `HtmlAgilityPack` 1.13.1 两行；**无清理 target**（实测无多余文件） |
| `Features/Units/Screen/QrCodeAlgorithms.cs` | 两个二维码单元共用纯计算：`BitMatrix` → GDI+ `Bitmap`（BGRA32）、`Bitmap` → `RGBLuminanceSource` → 文本；`Generate` / `Read` / `LevelOf` 供自检直调 |
| `Features/Units/Screen/QrCodeGenUnit.cs` / `QrCodeReadUnit.cs` | `image.qrcodeGen` / `image.qrcodeRead` 两个独立单元 |
| `Features/Units/Web/HtmlUnit.cs` | `web.html`；`Extract` 纯函数供执行与自检共用 |
| `Features/Units/Ui/UiaUnit.cs` | `ui.uia`：BFS 限深 / 限量精确匹配 + `MouseOps` / `ValuePattern` 操作 |
| `Core/Ai/AiUia.cs` | `Uia` 提为 `internal` 共享实例（行为不变） |
| `Features/Units/BuiltInUnits.cs` | 注册 4 行（界面 / 屏幕 / 网络三段） |
| `Core/Pinyin.cs` | 补新单元名称用字，注释里写死的「160 个」改掉 |
| `Core/SelfTest/Suites/S3BSuite.cs` / `SelfTestRunner.cs` | 新增 `s3b` 组 6 例并注册 |
| `AGENTS.md` | 第三方依赖清单同步（G6） |
| `docs/ROSLYN_AND_UNITS_PLAN.md` | §9.5 + §3 S3-0 / S3-2 / S3-5 / §10 同步 |

**偏差 / 决策变更**

- **多码识别未做**：ZXing.Net 0.16.11 没有 `decodeMultiple`（探针反射确认）。`image.qrcodeRead` 仍按采纳文档
  声明 `list` / `count`，但本批最多 1 项；单元 `<remarks>` 与本节都写明，不假装支持。
- **不引 `ZXing.Net.Bindings.*`**：主包只含核心类，`BitMatrix` 画 png、读图都走 `QrCodeAlgorithms`
  的 GDI+ 位图（项目已有 `System.Drawing`），避开 System.Drawing.Common / SkiaSharp 绑定包与额外 dll。
- **`image.qrcodeGen` 本批不做 `darkColor` / `lightColor` / `icon`**（采纳文档 §5.3 列过）：黑白二维码
  够日常用，颜色串与中心图标的叠加规则要另定，留到有真实需求；参数表因此只有
  `text` / `size` / `margin` / `level` / `target`。
- **生成图落 `cache/screenshots/`**（`ImageLibrary.SaveCapture`）而非 `resources/images/`：照采纳文档
  §5.3「按运行期截图生命周期处理」，下次运行开始 / 启动整体清空、不进备份；删动作后不留孤儿。
  只有用户填了 `target` 才写到指定路径。这与「截图」步骤的输出共用同一套生命周期，没有第二套。
- **`web.html` 不设 `mode` / `encoding`**：`value` = 第一个命中、`list` = 全部命中，少一个语义分叉；
  传进来的已经是解码好的文本。三语义仍是「未命中 `found=false` / 语法错 `Precondition` / 命中」。
- **`ui.uia` 的 action 收窄**为 `click` / `doubleClick` / `rightClick` / `getText` / `setValue` / `focus`；
  采纳文档 §5.6 列的 `expand` / `invoke` / `toggle` 与 XPath 定位留到有真实需求（计划 §3 S3-5 本就要求
  「先只做 Name + AutomationId + 深度限制精确匹配」）。`action` 是「对同一个控件做的不同操作」——
  目标一致、失败语义一致（找不到即失败），与 `file.open` 三合一退役时「混三种打开目标」不是一回事。
- **干跑**：`ui.uia` 是 `UnitKind.action`，按框架默认在 MCP `dryRun` **整步跳过**（不注入键鼠）；
  `image.qrcodeRead` / `web.html` 是 `pure`，干跑照跑（纯内存计算，安全）。
- **`ACTION_SYSTEM.md` / `LTools.example.json` 未改**：前者 §3.6 / W* 表格是历史批次清单，本批没改
  动作系统模型；后者无新配置字段。新单元事实记在本文 §9.5，权威来源是 `StepRegistry.Default`。
- **未新增术语**：「二维码 / 控件 / XPath」是通用技术词，不是 LTools 专有概念，也没有第二种叫法，
  不进 `CONTEXT.md`（沿用 S2「哈希 / 加解密」、S3-A「进制 / 下载」同一判断）。若要把「控件」升格为
  正式术语，再补 `CONTEXT.md` → `AGENTS.md` → 代码标识符。
- **真机点验仍欠**（G3 / G4）：本批为无头构建，自检不碰真实 UIA 树 / 网络 / 磁盘图片路径；
  「真机点一次按钮」「落盘路径的生成 → 识别往返」「删动作后生成图不成孤儿」「`ui.uia` 超时
  `NotFound` + `RetryOn=notFound`」「`ltools_uia_tree` 不受影响」待用户点一遍。
  S1 / S2 / S3-A 欠的真机点验也仍挂着。
- 首轮构建抓到 `ControlType` 是**值类型**、不能用 `?.`（CS0023），照 `AiUia` 改成 `.ToString()`。

---

（代码阶段已收口。后续若触发 S4 单项，按上方模板在 §9 追加 9.x，并同步 §10 / §11.5。）

---

## 10. 完成状态

| 阶段 | 状态 |
|---|---|
| S0 基线与评审 | 实测完成、评审通过（见 §9.1）；版本 / 术语建议值待确认 |
| S1 `script.csharp` | **已实现，见 §9.2**；真机点验（G3 / G4）待做 |
| S2 P0（2 项） | **已实现，见 §9.3**；真机点验待做 |
| S3-A P1（依赖评审 + `data.number` / `web.download`） | **已实现，见 §9.4**；真机点验待做 |
| S3-B P1（二维码 / HTML / UIA，4 个单元） | **已实现，见 §9.5**；真机点验待做 |
| S4 P2（触发式） | **已收口，见 §11**；默认不产生代码，按真实需求单项触发；累计真机点验清单见 §11.3 |

---

## 11. 附录 · S4 收口清单（2026-11-15）

代码阶段（S0–S3）全部完成。本附录把 S4「默认不产生代码」的判据、10 项 P2 的触发条件，
以及四批累计的真机点验欠账**一次性显性化**：不在没有真实需求时预研铺开，也不让欠账散在
§9.2–§9.5 的偏差段落里被忘掉。

### 11.1 判据（沿用 `QUICKER_UNITS_ADOPTION.md` §8）

- **默认不产生代码**：P2 只在出现真实需求、或用户明确点单时才动工；不因为「清单上有」就补。
- **每次触发单项仍过 G1–G8**（声明 → 注册 → 自检 → 构建 0 警 → `--selftest` 全绿 → 真机点一遍 →
  依赖评审 → 参数表单可见性）；`units.md` 由 `KnowledgePack.Ensure()` 自动同步，**不手改知识包**。
- **优先脚本层**：纯函数窄需求先由 `script.csharp` 覆盖，只有出现真实高频需求才升格为动作单元；
  升格判据见本文 §2.1「单元 vs 脚本」。
- **§4 第 18–22 行「不做」项不因本计划复活**（§11.4）。

### 11.2 P2 触发清单（#8–#17，共 10 项）

| # | 能力 | 类别 | 触发条件 | 落地形态 | 验收 |
|---|---|---|---|---|---|
| 8 | `showmenu` 树形 / 图标弹出菜单 | 需新 UI 子系统 | 用户明确要求「树形 / 带图标的右键菜单」 | 暂缓；按真实需求单独立项 | 单独立项时过 G1–G8 |
| 9 | `color` 解析颜色串并按格式输出 | 补一处分支 | 需要在动作里解析 `#RGB` / `rgb()` 并转格式 | 给 `screen.pixel` 加 `format` 参数，**不新建单元** | 新分支 `--selftest` 断言 + 参数表单可见性 + 原有分支回归 |
| 10 | `charInfo` 字符 Unicode / 拼音首字母 | 纯函数窄需求 | 出现真实高频需求 | **优先 `script.csharp`**；升格则新建 `text.charInfo`，复用 `Core/Pinyin.cs` | 未升格则在 `docs` 记「由脚本层覆盖」；升格过 G1–G8 |
| 11 | `textCounter` 行数 / 字符数 | 纯函数窄需求 | 同上 | 优先 `script.csharp`；升格则 `text.count` | 同上 |
| 12 | `getSysInfo` 本机信息 | 纯函数窄需求 | 同上 | 优先 `script.csharp`；升格则 `system.info`，**只取本机项**，不照搬 Quicker 宿主专有输出（版本 / 专业版 / UnionId） | 同上 |
| 13 | `computeTime` 解析时间串 / 两时间相减 | 补一处分支 | 需要在动作里解析 / 相减时间 | 给 `data.time` 补 `parse` / `diff` 分支 | 同 #9 |
| 14 | `dictOperations` 词典增删改查 | 补一处分支 | 需要按键读写对象 | 给 `data.json` 补 `set` / `get` / `keys` 分支 | 同 #9 |
| 15 | `form` 一次收集多个字段 | 需新 UI 子系统 | 用户明确要求「一次填多个字段」 | 暂缓；先用多个 `ui.input` 组合 | 单独立项时过 G1–G8 |
| 16 | `winservice` 服务状态 / 读注册表 | 纯函数窄需求 | 出现真实高频需求 | 优先 `script.csharp`；升格则拆 `system.service` / `system.registry` | 同 #10 |
| 17 | `fileSystemWatch` 动作内等待文件事件 | 需监控回调 | 用户明确要求「等文件出现」 | 暂缓整套监控回调；若补，优先补**「等待文件出现」**（等待类，超时 `found=false`） | 单独立项时过 G1–G8 |

> 三类顺序 = 计划 §4 的分类；#9 / #13 / #14 是唯一「不新建单元」的低成本项，但**仍不预研**。

### 11.3 累计真机点验清单（S1–S3，共 4 批欠账）

无头构建与 `--selftest` 只覆盖纯逻辑。以下各项按批次分组，**按单点即可**，点完把结果回给我修问题。

| 批次 | 点什么 | 期望 |
|---|---|---|
| S1 `script.csharp` | 动作编辑器里打开「C# 脚本」参数表单 | 等宽多行代码框、「检查语法」按钮与错误红字带行号 |
| S1 | 分别点「检查语法」于 `script.ahk` 与 `script.csharp` | AHK 仍走 `AhkRunner.Check`、C# 走 `CSharpRunner.Check`，互不串台 |
| S1 | 跑一段 `script.ahk` 旧动作 | 全程保留、与 C# 并存，行为不变 |
| S1 | 跑带 `vars.CheckStop()` 的脚本并按停止热键 | 500ms 内返回（纯 CPU 紧密循环不保证，见 §9.2 已知限制） |
| S2 `input.waitClipboard` | 复制一段文本 → 跑「等待剪贴板变化」→ 读剪贴板 | `changed=true` 且 `text` 是新内容 |
| S2 | 不复制任何东西，`timeoutMs=1500` | 超时返回 `changed=false`，**不报步骤失败**，`flow.if` 能判 |
| S2 | 等待过程中按停止热键 | < 500ms 返回 |
| S2 | `text.hash` / `crypto.encrypt` → `crypto.decrypt` 串一步 | 往返文本一致；填错密钥长度报前置条件错误 |
| S3-A `web.download` | 下一个几十 MB 的文件 | 流式写盘、不 OOM；`md5` 与系统算的一致 |
| S3-A | 下载途中按停止热键 | 立即中断，目标目录里**不留半个文件** |
| S3-A | 404 地址 / 超时 / 重名（开 / 关 `autoRename`）/ 覆盖 | 分别按 `NotFound` / `Timeout` / `(1)` 追加 / 覆盖；`etag` 有值 |
| S3-A `data.number` | 参数表单里切「转成 N 进制 / 四舍五入 / 千分位」 | `base` / `digits` / `rounding` 按 `OptionParams` 收起，隐藏项不报缺参 |
| S3-B 二维码 | 「生成二维码」→「识别二维码」两步串起来 | 落盘 png 能读回原文（含中文）；空白图 `found=false` 不报错 |
| S3-B | 删掉含二维码动作后重启 | 生成图（运行期缓存）被清空，**不成孤儿** |
| S3-B `web.html` | 「HTTP 请求」→「HTML 提取」两步 | `${response}` 能取到节点；非法 XPath 报前置条件；未命中 `found=false` |
| S3-B `ui.uia` | 真机点一次按钮（填 `name` + `action=click`） | 按钮被点中；`x` / `y` / `bounds` 与控件矩形一致 |
| S3-B | 故意填不存在的 `name`，`timeoutMs=1500` | 按 `ActionErrors.NotFound` 失败，配 `RetryOn=notFound` 能重试 |
| S3-B | 跑 `ltools_uia_tree` | 共享 `UIA3Automation` 后控件树仍完整，语义不变 |
| S3-B | 编辑器左栏搜 `ewm` / `kjcz` | 分别命中「生成二维码 / 识别二维码」「控件操作」（Pinyin 补字） |

### 11.4 不复活清单

`QUICKER_UNITS_ADOPTION.md` §4 第 18–22 行判为「不做」的能力（系统小能力长尾、`httpserver` /
`websocket` / `smtp`、列表表格可视化编辑、图片窗口 / 录屏等窗口子系统、宿主 UI / 浏览器窗口）
**不因本计划或本附录复活**；若要重评，另开一轮评估，而不是往 S4 里塞。

### 11.5 收口状态

- 代码：**S0–S3 全部完成**，6 次提交 `e229f1a` / `9b78ca4` / `03f083c` / `d219ba1` /
  `710a7d9` / `c644b17`；**无 `git push` / tag / merge / release**。
- 回归门禁：构建 0 警 + `--selftest` **136 例 / 3547 断言** + 动作单元 **91**；
  发布包 **356,767,867 B / 639 文件**。
- **未做**：真机点验（§11.3）、S4 任何代码。
- 两份调研文档 `docs/QUICKER_UNITS_SURVEY.md` / `QUICKER_UNITS_ADOPTION.md` 收口时仍未跟踪，
  随后已单独纳入版本（见后续提交）。
- 下一步只有两种触发：① 用户点 §11.2 的某一项；② 用户回 §11.3 的点验结果。

> 本文只给规划与验收口径，**不包含已批准的实现排期**；提交按 `AGENTS.md` 走
> 「改动清单 + 提交信息待批准」，`git push` / 打 tag 各自单独批准。
