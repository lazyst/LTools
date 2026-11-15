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
- 停止热键 500ms 内中断；长循环脚本按停止立刻抛 `ActionAbortedException`。
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
   有限值夹 1–600000）+ `Timeouts.Loop`；`intervalMs` / `includeRecentMs` 收进高级区。
3. 循环 `ctx.EnterIteration()` + `ctx.Wait`；干跑只判一次。
4. 读剪贴板走 `Native/NativeClipboard`（原始 Win32），**不用** WinForms / OLE。
5. 输出 `changed`（bool）/ `text` / `elapsed` / `found`；**超时 = `changed = false`，不报错**
   （等待类口径，照 `window.wait` / `input.waitKey`）。
6. `Description` 写明「等待结束后 Ctrl/C 可能还没抬起，后续模拟按键前建议加 100–200ms 延迟」。
7. 自检：超时 / 命中 / 停止 / 干跑各一条。

**验收**
- 停止热键 500ms 内返回；`timeoutMs` 三态正确。
- 超时不抛异常、不算步骤失败（`flow.if` 能判 `changed`）。
- 符合 `param-form-smoke` 的 `[wait]` 口径：`timeoutMs` 不在高级区、说明里写全三个值。
- 真机「复制 → 等待 → 读到新内容」一条龙；G1 / G2 / G3 / G5 / G8。

---

### S3 · P1（5 项）

**S3-0 依赖评审（先于 S3-2 / S3-3）**：二维码库（ZXing.Net / QRCoder）与 HTML 库
（HtmlAgilityPack / AngleSharp）过 G6，含许可证、包体积、是否需要 csproj 清理 target
（参照 OpenCvSharp 的 `opencv_videoio_ffmpeg` target 做法）。
**验收**：结论入 `AGENTS.md` 第三方依赖清单；**未过则对应项降级为「脚本层覆盖」，不阻塞其它项**。

| 批次 | 步骤要点 | 验收标准 |
|---|---|---|
| S3-1 `data.number` | 纯函数零依赖；`OptionParams` 表达「哪个 operation 显示哪些参数」；`rounding` 规则与 `data.calc.round` 统一 | `--selftest` 进制 / 取整 / 千分位 / 百分比向量全绿；两处 `round` 行为一致；不新建分类，归「文本与数据」 |
| S3-2 `image.qrcodeGen` / `image.qrcodeRead` | 拆两个单元；生成输出 png 路径并传 `ctx.ResourceRoot`；识别 `found = false` 不报错、**不加在线识别兜底**；在 `ImageLibrary.CollectAction` / `ProgramWalker` 按参数名接上引用回收 | 生成 → 识别往返成功；**删动作后生成图 / 模板图不成孤儿**；`units_list` 两条目 |
| S3-3 `web.html` | 复用 `web.request` 的文本输出、**不给 `web.request` 加 `operation`**（保持「唯一 HTTP 出口」）；输出 `value` / `list` / `count` / `found` | XPath 命中 / 未命中 / 语法错三语义分明（找不到 `found = false`、语法错才抛）；与 `web.request` 串成两步 |
| S3-4 `web.download` | 复用 `web.request` 的静态 `HttpClient` + `HttpCompletionOption.ResponseHeadersRead` + 与 `ctx.Token` 串联的令牌；失败按 HTTP / 超时标 `ActionErrors` 性质 | 大文件流式不 OOM、按停止立即中断；`etag` / `md5` / `autoRename` 输出正确；不另建网络栈 |
| S3-5 `ui.uia` | 先只做 `Name` + `AutomationId` + 深度限制精确匹配，XPath 留到有真实需求再加；复用 `Core/Ai/AiUia.cs` / `Views/ScreenSnapTargets` / `Features/Flow/TextLocator`；MTA 直调**不包 `StaRunner`**；`ctx.Wait`；找不到按 `ActionErrors.NotFound` 标性质 | 真机点一次按钮；`timeoutMs` 可见；找不到按性质标且能配 `RetryOn = notFound`；`ltools_uia_tree` 不受影响 |

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
| 发布包体积增量 | 实测 4 个 `Microsoft.CodeAnalysis*.dll` 共 **10,354,376 B（≈9.9 MB）**，对基线 329.6 MB 约 **+3%** → 阈值 **≤ 15 MB** |
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

（后续批次按上方模板追加。）

---

## 10. 完成状态

| 阶段 | 状态 |
|---|---|
| S0 基线与评审 | 实测完成、评审通过（见 §9.1）；版本 / 术语建议值待确认 |
| S1 `script.csharp` | 未开始 |
| S2 P0（2 项） | 未开始 |
| S3 P1（5 项） | 未开始 |
| S4 P2（触发式） | 未开始 |

> 本文只给规划与验收口径，**不包含已批准的实现排期**；提交按 `AGENTS.md` 走
> 「改动清单 + 提交信息待批准」，`git push` / 打 tag 各自单独批准。
