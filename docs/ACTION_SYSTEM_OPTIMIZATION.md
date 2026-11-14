# 动作系统深度优化计划（2026-11-14）

> 本文是**计划**，不是规格：实现约束看 `AGENTS.md`，术语看 `CONTEXT.md`，动作系统设计看
> `docs/ACTION_SYSTEM.md`，参数表单看 `docs/PARAM_FORM.md`。本文的输入是
> `docs/ACTION_EDITOR_REVIEW.md`（2026-11-13 评审，P0 / P1 / P2 均已落地）之后的现状——
> 只处理那一轮没覆盖、或落地后仍然成立的问题。
>
> 进度按阶段追加在 §9「实施记录」；计划本身改动时同步改 §3 对应条目，别只改代码。

---

## 0. 结论先行

| 维度 | 现状判断 | 依据（可验证） |
|---|---|---|
| 动作模型 | 上游水平，不需要重写 | 纯数据步骤树 + 参数三态 + 显式 `out` 映射；`StepText` 可文本往返；AI 直接读写同一份 JSON |
| 元数据声明 | 最值钱的架构决定 | `UnitSpec` 一份声明派生 UI / 校验 / 摘要 / 知识包 / 选项可见性；注册期 `Validate()` 自校验 |
| 编辑器实现 | 技术债集中点 | `StepFormView` 1175 行 + `ValueField` 1150 行命令式建控件；界面 bug 基本都出在这一层 |
| 运行时 | 扎实 | 三层预算、单实例、`finally` 释放按住键、坏文件隔离、失败可定位 |
| **回归保障** | **最薄的一环** | 仓库无测试工程、CI 只 `restore + build`（`.github/workflows/build.yml` 19 行），600+ 项冒烟散在 `%TEMP%\opencode\*-smoke` |
| 失败语义 | 够用但原始 | 只有 `onFail = stop \| continue`，重试要手搭 `flow.while` + 计数器 |
| 可中断性 | 有洞 | 30 分钟预算每 32 步才查一次；全项目只有 8 个单元文件碰 `ctx.Token` / `ctx.Wait` |

**一句话目标**：把「稳」从运行时扩展到「改得动」，把「能跑」扩展到「不用写循环也能跑稳」。

> 上表是 2026-11-14 起草计划时的现状；落地进度看 §9（P0 / P1 已实施）。

**六阶段**（顺序不可换，详见 §3 / §4）：

| 阶段 | 主题 | 核心交付 |
|---|---|---|
| P0 | 回归门禁 | 仓库内自测入口 `LTools.exe --selftest` + CI 跑它 |
| P1 | 可中断性 | 单步看门狗 + 停止待定态 + 取消审计 |
| P2 | 失败语义 | 每步 retry / retryDelay + `flow.waitFor` |
| P3 | 编辑体验 | 行内 chip + 描述编辑 + 撤销含变量 + 自动存盘 |
| P4 | 结构统一 | 可见性单一机制 + `BodyKind` 校验 + 表单拆分 |
| P5 | 能力补齐 | 按真机反馈取舍（`window.wait` 等） |

---

## 1. 现状证据（只列能指到代码行的）

| # | 判断 | 证据 |
|---|---|---|
| 1 | 仓库无测试、CI 无门禁 | `.github/workflows/build.yml` 只有 `restore` + `build`；仓库内没有测试工程 |
| 2 | 单步没有超时 / 看门狗 | `ActionEngine.cs:686` / `:699`：`(_steps & 31) == 0` 才看一次时钟 |
| 3 | 取消只在少数单元生效 | 全项目 `ctx.Token` / `ctx.Wait` 只有 12 处（`MouseUnit` / `OcrUnit` / `SendKeysUnit` / `TextUnits` / `TextWindowUnit` / `WaitKeyUnit` / `WebRequestUnit` + 三个 Probe） |
| 4 | 步骤行只显示前 2 个参数 | `StepSummary.cs:52` 的 `parts.Count >= 2` |
| 5 | 失败策略 / 延迟 / 跳过 / 备注在行上看不到 | 步骤行模板没有对应 chip（grep `ActionEditorWindow.xaml` 无 `OnFail` / `DelayMs`） |
| 6 | 动作描述有字段没入口 | `ActionProgram.Description` 存在；编辑器只有子动作那边用到（`ActionEditorWindow.xaml.cs:127`） |
| 7 | 撤销不含变量表、只窗口内有效 | `ActionEditorWindow.History.cs:32` 只 `CloneSteps(_program.Steps)` |
| 8 | 没有自动存盘 | 只有 `Commit()`（确定 / 试运行前 / 关窗三选一） |
| 9 | `BodyKind` 只在声明里，不校验 | `ActionValidator` 无任何 `BodyKind` 引用；`flow.if` 读 `Then` / `Else`，子步骤写进 `body` 会被静默忽略 |
| 10 | 失败只有两种收场 | `Step.cs:8` 的 `FailMode` = `stop` / `@continue` |

---

## 2. 原则（每个阶段都按这几条裁）

1. **先门禁，后优化。** 没有可执行断言的结构改动，一律不做。P4 排在最后就是这条的直接结果。
2. **单点实现。** 同一规则只有一处实现：可见性 = `VisibleWhen`，变量写入 = `VariableWrites`，选项参数 = `OptionDriver`。新机制要么替掉旧的，要么别加。
3. **加法兼容，不做迁移。** 新字段缺省 = 旧语义（`retry = 0` 就是不重试），所以不需要迁移器、不认旧字段、不留别名——沿用现有策略。
4. **不杀线程、不抢键鼠。** 可观测优先：先让用户看见「卡在哪」，再谈「强制结束」。
5. **每阶段独立可交付。** 任何一阶段停下来，仓库都处于可构建、可发版的状态。

---

## 3. 阶段计划

### P0 · 回归门禁

**目标**：任何引擎 / 校验 / DSL 改动，都能用一条命令证明「没改坏」。

**决策：自测入口是 `LTools.exe --selftest`，不是新的测试工程。**

| 方案 | 代价 | 结论 |
|---|---|---|
| 新建 `tests/Smoke` 工程 | `LTools.csproj` 在仓库根，默认 `**/*.cs` 会把 `tools/**` 编进主程序（要加 `Compile Remove`）；跨工程拿不到 `internal`（要加 `InternalsVisibleTo`）；TFM / WPF 引用重复一遍 | 不选 |
| 仓库内 console 断言器 + `dotnet add reference` | 同上，且 CI 多一次 restore | 不选 |
| **`LTools.exe --selftest`（本程序内的自测入口）** | 断言代码随产品发布（约 2–3k 行）；需要给 WinExe 附控制台 | **选它** |

理由：与既有 `--perf` / `--mcp-stdio` 同一套「命令行分流、跑完 `Shutdown(exitCode)`」模式；
`internal` 直接可见；CI 直接跑构建产物，零额外工程；`AGENTS.md` 的「没有测试项目」不需要推翻，
只需要把「日常回归 = 构建 0 警 + 真机点一遍」改成「构建 0 警 + `--selftest` 全绿 + 真机点改动的路径」。

**分组（按最容易改坏的部位排）**

| 组 | 覆盖 | 断言风格 |
|---|---|---|
| `engine` | 三层预算、嵌套 `if` / `while` / `repeat` / `forEach`、`break` / `continue` 穿越、子动作深度、作用域隔离、`StartPath`、失败标签、干跑、初始变量、轨迹 | 断言 `ActionRunResult` 的字段与消息关键片段，不锁整句 |
| `model` | `StepArg` 三态 JSON 往返、`${x}` 单变量保留类型、`Raw` 不渲染、未知单元报错、`ActionProgram` 往返 | 往返后逐字段比较 |
| `validator` | `out` 拼错、不可见参数、枚举非字面量、列表→数字、未知变量 / 模板、缺图、子动作成环、`flow.return` 位置 | 断言 **severity + path + 关键片段**，不锁消息全文 |
| `steptext` | 真实动作库逐份往返（有 `actions/` 时）、缩进 / `else` 归属、注释、未知单元保留 | `Write(Parse(Write(x))) == Write(x)` |
| `declaration` | 全部注册单元：名称 / 图标 / 分类合法、枚举选项有中文名、`OptionParams` 键真实存在、输出声明完整 | 逐单元生成断言，新增单元自动被覆盖 |
| `helpers` | `Compare.Test` 矩阵、`VisibleWhen.IsVisible`、`ListText`、`TextEscapes`、`Value.Coerce`、`VariableScope.Resolve` | 表驱动 |
| `templates` | `ActionTemplates.All` 每条都能通过 `ActionValidator`（「点完就能跑」） | 逐模板 |
| `purity` | 编辑器可复用的纯函数（`StepSummary` / 诊断排序 / 轨迹聚合等） | 纯函数直调 |

**覆盖清单**：`--selftest` 维护一份「已被 `engine` 或 `declaration` 触达的单元键」列表，
注册了但没进清单的单元会让这条断言变红——新增单元不许悄悄逃过测试。

**CI**：`build.yml` 在 `dotnet build` 之后追加一步，用 `Start-Process -Wait` 跑
`bin/Release/<tfm>/LTools.exe --selftest`，退出码非 0 即 job 失败；失败时把报告文件打印出来。

**验收**
- `dotnet build` 与 `dotnet build -c Release` 各 0 错 0 警；
- `--selftest` 全绿，耗时 < 90 秒，不碰键鼠 / 网络 / 用户配置；
- 故意把一条规则改坏（例如 `VisibleWhen` 的 `in` 语法），本地与 CI 都红；
- `--selftest` 的代码不创建任何窗口、不装钩子、不建托盘。

---

### P1 · 可中断性

**问题**：`MaxDuration` 只在 `EnterStep` 每 32 步查一次；一步卡在 UIA / OCR / 大文件里时，
停止热键要等它自己返回，用户看到的是「按了停止没反应」。

**做法**
- `ActionRunOptions` 增 `SlowStepWarn`（默认 30 秒）与 `OnSlowStep` 回调；
  `ActionRunState` 记当前步骤的标签与起始 tick。
- `Run()` 里挂一个 1 秒 `System.Threading.Timer`，超阈值回调一次（同一步只报一次），
  `finally` 里 `Dispose`。**不杀线程**（强杀会让注入中的键鼠状态失控）。
- 轨迹旁路：慢步骤走独立回调 `SlowStepInfo`（**不塞进 `StepTrace`**，`StepTraceStatus` 保持四态），
  warning 日志随 run 作用域自动进失败报告。
- UI：失败提示 / 编辑器显示「第 7 行已运行 42 秒」；停止按钮进入「正在停止（等待当前步骤返回）」态。
- **取消审计**：带内部循环的单元一律走 `ctx.Wait` / `EnterIteration`；新增约定写进 `AGENTS.md`。

**验收**：假单元「睡 5 秒不检查 token」→ 1–2 秒内报出慢步骤；「200ms × 50 轮」的假单元 →
停止延迟 < 500ms；真机跑一个 UIA 动作，中途停止能看到「正在停止」。

---

### P2 · 失败语义

**问题**：只有 `onFail = stop | continue`；桌面自动化最常见的失败是「窗口还没出来」，
现在必须在动作里手搭循环。

**做法**
- `Step` 增 `retry`（默认 0）、`retryDelayMs`（默认 500）、`retryOn`（`any` 起步）。
  缺省语义不变 → 不需要迁移。
- 引擎只在 `RunSteps` 一处实现重试；`StepTrace` 增 `Attempt` / `MaxAttempts`，编辑器按路径聚合显示 `⟳3`；
  运行历史记尝试次数。
- 新增 `flow.waitFor`（条件成立再继续，`intervalMs` / `timeoutMs` / 输出 `ok` / `elapsed`），
  复用 `Compare` 与 `ctx.Wait`——和 retry 解决同一类问题，先给用户一个不写循环的出口。
- `StepText` 用 `@retry=3 @retryDelay=500`；`StepSummary` / 步骤行 chip 同步；校验 `retry` 范围。

**验收**：前两次失败第三次成功 → 成功且轨迹显示 3 次；一直失败 → 按 `onFail` 收尾；
重试间隔内按停止热键立即响应。

---

### P3 · 编辑体验

- **行内 chip**：`跳过` / `失败继续` / `延迟 2s` / `备注` / `⟳3`；摘要从 2 个参数放宽到 3–4 个。
  判定抽成纯函数（`StepRowChips.Describe`）便于 `--selftest`。
- **描述编辑**：右栏补 `Description` 输入框；面板 / 悬浮的 tooltip 用上它。
- **撤销纳入变量表**：快照从 `CloneSteps` 扩成 `(steps, variables, label)`。
- **自动存盘**：编辑中每 10 秒把未保存改动写到 `cache/editor/<动作id>.autosave.json`（不进备份）；
  打开动作时若比 `action.json` 新，问「恢复未保存的改动？」；保存 / 放弃时删掉。
- 抽屉记住宽度；`Ctrl+Enter` 保存这一步。

**验收**：chip 文本矩阵、撤销恢复变量、自动存盘三条路径（写入 / 恢复 / 清理）都有断言；真机断进程后能恢复。

---

### P4 · 结构统一（唯一的结构级重写，放最后）

- **可见性单一机制**：以 `OptionParams` 为唯一声明（它有结构、能导出成知识包表格），
  把剩余 5 处 `VisibleWhen` 例外收进同一份内部模型；`VisibleWhen` 只保留布尔附加条件，
  并给 `UnitSpec.Validate()` 加 lint（引用的键必须存在、枚举字面量必须在 `Options` 里）。
- **`BodyKind` 校验**：步骤带了声明之外的子列表 = **error**；编辑器与 `StepText` 解析都拒绝，
  引擎不再静默忽略。
- **表单拆分**：`StepFormView` / `ValueField` 按控件形态拆成 `IArgEditor` 实现 + `ArgRow` 宿主，
  单文件 < 300 行；「三态 + 可见性 + 冻结」收成一个可单测的小状态机。行为零差异。

**验收**：`--selftest` 全绿且**不改任何断言**；`git diff` 里没有行为分支变化；真机点鼠标 / 截图 / 图片 / 子动作四类参数。

---

### P5 · 能力补齐（按真机反馈）

候选：`window.wait`（等待窗口出现）、`ActionErrorKind`（超时 / 未找到 / 前置条件 / 已停止，
先只用于提示文案与 `retryOn`）、`flow.lastErrorStep`、`flow.try`（**暂不做**，加子列表要同时动
`Step` / `StepText` / 校验 / 编辑器四处）。

取舍标准：只加「用现有单元要写 ≥5 步才能表达」且「失败率高」的单元，其余用模板解决。

---

## 4. 顺序与依赖

| 阶段 | 依赖 | 为什么排在这里 |
|---|---|---|
| P0 | 无 | 其余阶段都要靠它证明没改坏 |
| P1 | P0（看门狗用假单元断言） | 纯运行时改动，风险低、收益立竿见影 |
| P2 | P0 | 动 `Step` 字段与引擎主循环，必须有回归兜底 |
| P3 | P0 | 编辑器纯函数进自测后改起来才敢动 |
| P4 | P0（尤其 `param-form` 那批断言） | 最大的一次重写，断言必须先在仓库里 |
| P5 | 真机反馈 | 不预设 |

---

## 5. 度量

| 指标 | 目标 |
|---|---|
| 停止响应（可取消单元） | < 500ms |
| 慢步骤发现延迟 | 30 秒阈值，1–2 秒内报出 |
| CI 门禁耗时 | < 90 秒 |
| `--selftest` 用例数 | ≥ 150；覆盖 100% 已注册单元（清单判定） |
| 重试后成功率 | 运行历史里可统计「第 N 次尝试成功」 |

---

## 6. 非目标

- 迁移器 / 旧字段别名 / 归档 `.migrated`（沿用「不向后兼容」）。
- `StepArg` 三态改造、节点图化、并行执行、单步断点调试器。
- 把 `Value` 扩成十种类型。
- 引入 xunit / NUnit 等测试框架（见 P0 决策）。

---

## 7. 风险与回滚

| 风险 | 应对 |
|---|---|
| 自测代码随产品发布变大 | 只放断言与假单元，不引第三方；发布包体积影响 < 200KB |
| P0 搬迁断言时「为了让绿的通过而弱化断言」 | 断言只锁 severity / path / 关键片段；改断言必须写进 §9 记录 |
| P1 被误用成「强杀线程」 | 明确只做可观测与「放弃结果 + 释放按键」，线程自己收尾 |
| P2 新字段破坏旧动作 | 全部加法 + 缺省 = 旧语义，`--selftest` 有往返用例 |
| P4 拆分引入行为差异 | 先有 `param-form` 断言，再拆；拆分 PR 不含任何行为分支变化 |

---

## 8. 决策记录

| 日期 | 决策 | 理由 |
|---|---|---|
| 2026-11-14 | 自测入口做成 `LTools.exe --selftest` | 单项目 / 默认 glob / `internal` 可见 / CI 零额外工程（见 P0 表） |
| 2026-11-14 | P1 不做强制结束线程 | 强杀会破坏注入中的键鼠状态；先做「看见卡在哪」 |
| 2026-11-14 | `flow.try` 暂不做 | 加子列表要同时动 `Step` / `StepText` / 校验 / 编辑器，先用 `onFail=continue` + `flow.lastError` |

---

## 9. 实施记录

### 9.1 P0 第一批（2026-11-14）

**落地内容**

| 文件 | 作用 |
|---|---|
| `Core/SelfTest/SelfTestRunner.cs` | `--selftest` 入口：注册用例、`--filter` / `--verbose` / `--out`、`AttachConsole`、写报告、返回退出码 |
| `Core/SelfTest/Check.cs` | 断言 API（`True` / `Equal` / `Contains` / `Throws` / `Note`），只记通过 / 失败与失败明细 |
| `Core/SelfTest/FakeUnits.cs` | 假动作单元 + `TestRegistry`（流程单元 + `flow.lastError` + 全套假单元，传同名实例替换以取计数） |
| `Suites/EngineSuite.cs` | 25 例：预算、嵌套、失败策略、子动作、干跑、轨迹、停止、起点 |
| `Suites/ValidatorSuite.cs` | 13 例：级别 + 位置（`out` 拼错 / 不可见参数 / 枚举用变量 / 缺图 / 成环…） |
| `Suites/ModelSuite.cs` | 7 例：参数三态 JSON 往返、程序整体往返、内联取值 |
| `Suites/StepTextSuite.cs` | 7 例：写出 → 读回一致、缩进 / `else` / 注释、真实动作库逐份往返 |
| `Suites/HelperSuite.cs` | 7 例：`Compare` 矩阵、`VisibleWhen`、`ListText`、`TextEscapes`、`Value`、`VariableScope` |
| `Suites/DeclarationSuite.cs` | 9 例：遍历全部注册单元（名称 / 图标 / 说明 / 分类 / 子步骤 / 动态参数 / 输入 / 输出 / 枚举中文名） |
| `Suites/TemplateSuite.cs` | 2 例：起步模板清单 + 每条都能过 `ActionValidator` |
| `App.xaml.cs` / `build.yml` / `AGENTS.md` | `--selftest` 分流（在 `--mcp-stdio` 之前）、CI 步骤、日常回归口径 |

**实测**：70 个用例 / 2473 条断言 / 约 250 ms（Debug 与 Release 各跑一次，均全绿），
本机 `actions/` 10 份动作参与了 `StepText` 逐份往返。

**与计划不一致的地方（都是实施时改的判据）**

1. **没有单独的「单元键覆盖清单」。** 声明组遍历 `StepRegistry.Default.Units`，
   新增单元自动被断言，清单手写反而会漏改。P0 原方案的目的是「新增单元不许悄悄逃过测试」，这条由构造满足。
2. **轨迹是后序回调。** `StepTrace` 在步骤**跑完之后**发出，所以循环体那一条先到、外层步骤后到；
   断言按这个顺序写（`Outputs` 的键是 `out` 的键，不是变量名）。
3. **校验器的严重级别按实际来。** 「模板里引用未声明变量」是 **warning**（运行时才报错），
   断言不改代码、只锁实际级别；这类「测试写错了还是代码写错了」三处都按代码现状收敛（另两处：
   `flow.return` 的消息片段、循环轮次预算的消息片段）。
4. **`--filter` 命中 0 个用例算失败**：否则打错一个字母就「安静地绿」。
5. **顺手修掉一个真问题**：`StepArgConverter` 没开 `HandleNull`，JSON 里 `"a": null` 会在参数字典里
   留下 `null`（运行期读参数空引用），而它的 `Read` 里本来就写着「null 读成空文本」的分支。
   已加 `HandleNull => true`，`model` 组有断言守着。
6. **`purity` 组（编辑器纯函数）留到 P3**：那一层的纯函数（行内 chip、轨迹聚合）正在被 P3 改，
   先断言等于先返工；`StepSummary` 已经间接被 `StepText` / `declaration` 的措辞断言覆盖一部分。

**验收**

- `dotnet build` / `dotnet build -c Release` 均 0 错 0 警；
- `LTools.exe --selftest` 退出码 0，报告 `%TEMP%\ltools-selftest\report.txt`；
- 故意失败的回合里退出码为 1（实施过程中先后红了 4 轮，逐条修到绿）；
- 自测不建窗口、不装钩子、不建托盘、不碰键鼠 / 网络 / 用户配置（只读本机 `actions/` 做文本往返，没有就跳过）。

### 9.2 P1 · 可中断性（2026-11-14）

**落地内容**

| 位置 | 内容 |
|---|---|
| `ActionRunOptions` | `SlowStepWarn`（默认 30 秒，≤ 0 关闭）+ `OnSlowStep` + `SlowStepInfo(Path, Label, UnitKey, UnitName, Elapsed)` |
| `ActionRunState` | `PushStep` / `PopStep` 维护步骤栈（子动作 / 内联取值只盯最里面那一步）、1 秒 `Timer` 心跳、按**步骤路径**去重，超阈值回调一次 |
| `ActionEngine.Run` | `using var slowWatch = state.StartSlowWatch()`，成功 / 失败 / 停止都释放；`RunSteps` 的 `try` 加 `finally { PopStep(); }`（失败继续 / 抛出都不漏） |
| `AutomationRunner` | `ReportSlowStep`：warning 日志（自动进 `logs/last-failure.log` 的 run 作用域）+ 运行指示器「第 N 行已运行 X 秒」+ 真实运行一条 8 秒 warning toast；`Stop()` 把指示器改成「正在停止 · 等待当前步骤返回」；`TryRunProgram` 增 `slowStep` 回调 |
| 编辑器 | 状态行显示「第 7 行「…」已运行 42 秒（仍在执行）」，停止按钮文案改「正在停止…（等待当前步骤返回）」 |
| `AGENTS.md` | 新增「单步看门狗只看见、不打断」条，含 `PushStep` / `PopStep` 成对与「不可分割阻塞调用允许不可取消」 |
| 自测 | `engine` 组 +4 例：慢步骤报一次 / 同一行只报一次 / 快步骤不报 / `SlowStepWarn = 0` 不报 |

**取消审计结果**：`ctx.Wait` / `ctx.EnterIteration` / `ctx.Token` 的 9 个文件（`MouseUnit` / `SendKeysUnit` /
`TextUnits` / `WaitKeyUnit` / `TextWindowUnit` / `OcrUnit` / `WebRequestUnit` + `ImageProbe` / `TextLocator` /
`ScreenshotProbe`）已覆盖全部轮询与等待；其余长耗时都是**单次不可分割调用**（`File.Copy`、`ZipFile.CreateFromDirectory`、
UIA / OCR / OpenCV），按 P1 决策只做可观测。所以这一阶段的产出是「看得见」，不是「按一下就断」。

**与计划不一致的地方**

1. **没往 `StepTrace` 里塞「耗时异常」条目**：`StepTraceStatus` 保持四态，慢步骤走独立回调 `OnSlowStep`；
   日志那条 warning 自动落进失败报告，不用另存一份轨迹。
2. **「同一步只报一次」按步骤路径去重**：循环 200 轮的同一步只报一次，避免每秒刷提示；
   代价是「同一步每轮都很慢」只会提示一次。
3. **阈值暂时没有设置项**：先用默认 30 秒（`ActionRunOptions.SlowStepWarn`），有真实抱怨再加配置字段。
4. **自测因此变慢**：慢步骤用例要真等 1 秒心跳，`--selftest` 从 0.25 秒变成约 5 秒（仍远低于 90 秒预算）。

**验收**：`--selftest` 74 例 / 2486 断言全绿；4 个慢步骤用例覆盖「报 / 不重复报 / 不报 / 关掉」；
真机跑一个长动作能同时看到指示器、日志与 toast；按停止后指示器立刻切「正在停止」。

### 9.3 P2 · 失败语义（2026-11-14）

**落地内容**

| 位置 | 内容 |
|---|---|
| `Step` | `Retry`（默认 0 = 不重试）+ `RetryDelayMs`（默认 `DefaultRetryDelayMs` = 500）+ 常量 `MaxRetry = 10`；`Clone` 一起复制 |
| `ActionEngine.RunSteps` | `while` 重试循环：`maxAttempts = Math.Clamp(Retry, 0, MaxRetry) + 1`；**控制信号（停止 / `break` / `continue` / `return`）在各自 `catch` 里直接抛出、绝不重试**；重试间隔走 `parent.State.Wait`（按停止热键立刻返回）；`fail` / `continued` 分支才对 `onFail` 求值 |
| `StepTrace` | 尾部加 `Attempt` / `MaxAttempts`（`1/1` = 没配重试）；**一步只发一条轨迹**，带的是最终结果 |
| `flow.waitFor`（新单元） | `left` / `op` / `right` / `negate` + `timeoutMs`（默认 10000，0 = 只判断一次）+ `intervalMs`（默认 250，`Advanced`）；条件**每轮用 `ctx.ResolveArgs()` 重新求值**；输出 `ok` → `waitOk` / `elapsed` → `waitElapsed`；超时算这一步失败但 `ok` 已写（配「失败继续」做兜底判断） |
| `FlowUnits.Compare` | 新增 `ConditionUnits` / `IsConditionUnit`：条件类单元的唯一判定，校验器与步骤摘要共用（不再各自硬编码 `flow.if` / `flow.while`） |
| `ActionValidator` | `ValidateCondition` 改认 `IsConditionUnit`；`Retry` 超范围（< 0 / > `MaxRetry`）= error，`RetryDelayMs` < 0 = error，块步骤上配重试 = warning（提示把重试写在里面的那一行） |
| `StepText` | 读写 `@retry=` / `@retryDelay=`（`@retryDelay` 等于默认值时不写）；未知属性提示同步 |
| `StepSummary` | `ConditionText` 支持 `flow.waitFor`（补「最多等 X 秒」）；新增 `RetryText` → 步骤行摘要后缀 `⟳3` |
| 编辑器 | 选项区加「失败时重试 N 次，每次间隔 X 毫秒」（`RetryBox` / `RetryDelayBox`，`TrySave` 就地拦范围）；`TraceRow` 显示 `⟳n` + tooltip「重试：第 N 次尝试（最多 M 次）」；`ProgramDiff` 比对 `Retry` / `RetryDelayMs` |
| 运行历史 | `RunRecord.Attempts` 取自失败轨迹，列表失败位置显示「· 第 N 次尝试」 |
| `KnowledgePack` | `format.md` 步骤字段补 `retry` / `retryDelayMs` + 「窗口还没出来先试 retry / waitFor」 |
| `AGENTS.md` | DSL 形状补 `@retry=3 @retryDelay=800`；新增「失败重试与等待条件是两件事（P2）」条 |
| 自测 | `engine` +9（重试后成功 / 重试用尽按失败策略 / 重试间隔可停止 / 干跑不重试 / `waitFor` 立即成立 / 每轮重新取值 / 超时 / 干跑不成立不算失败）、`model` +1（克隆带上重试）、`steptext` +1（`@retry` 往返与错误）、`validator` +2（超范围 / 块步骤提醒） |

**验收**：`--selftest` 86 例 / 2570 断言全绿（约 5.4 秒），Debug 构建 0 错 0 警，动作单元 77 个。
「前两次失败第三次成功」的用例断言了调用次数 = 3、轨迹 1 条且 `Attempt = 3 / MaxAttempts = 3`；
「一直失败」分别验证了 `stop`（整体失败、位置指向那一步）与 `continue`（整体成功、尝试次数 = 1 + retry）；
「重试间隔 30 秒 + 立即停止」的用例断言实际耗时 < 5 秒。

**与计划不一致的地方**

1. **`retryOn` 没做**：它要按错误类型分流，而错误类型（`ActionErrorKind`）是 P5 的产出，
   现在先「任何失败都重试」，P5 落地后再补「只重试某几类」。
2. **干跑不重试、`waitFor` 干跑只判断一次**：干跑没真跑就没有可重试的；条件当时不成立是正常结果，
   若按超时算失败，草稿试运行会给出假红。两条都写成了断言。
3. **轨迹只发一条**：中间的重试不各自成行（否则循环里一个「时好时坏」的步骤会把轨迹刷满），
   失败 / 成功那条带 `Attempt` 说明是第几次。
4. **没有新的「失败继续」语义**：`onFail = continue` 仍然是「记错误、继续下一步」，
   只是现在它作用在**重试用尽之后**；要让后续步骤判断成败继续用 `flow.lastError`。

