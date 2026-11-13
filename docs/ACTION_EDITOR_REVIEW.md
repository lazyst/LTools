# 动作编辑器与动作系统评审（2026-11-13）

> 从产品角度评审四条线：**动作编辑器界面**、**动作编辑器易用性**、**动作系统健壮性**、
> **AI 帮写动作的准确性**，并给出按优先级排序的优化方案。
>
> 本文只是评审与方案，**不是规格**：实现约束见 `AGENTS.md`，术语见 `CONTEXT.md`，
> 动作系统 / 参数表单 / AI 接入的设计见 `docs/ACTION_SYSTEM.md`、`docs/PARAM_FORM.md`、
> `docs/AI_AGENT.md`。文档之间冲突时以那三份为准，先改那边再改本文。

---

## 0. 结论先行

1. **内核领先界面一个身位**。70 多个动作单元、参数三态（字面量 / 变量 / 内联取值）、
   全局子动作、试运行与失败定位都已落地；但编辑形态还是「单步模态框 + 全量重建列表」，
   能力被塞进 242px 的工具栏和 640px 的对话框里。
2. **反馈回路是半闭环**。能试运行、有失败提示、有步骤轨迹——但轨迹没有「解析后的输入」，
   变量只能看运行结束后的快照，校验结果是一个不可点击的纯文本弹窗。
   「能跑」到「能改对」之间还缺一段。
3. **健壮性不差，但校验与执行的口径不齐**。`out` 键拼错不校验、选项外的参数仍被解析、
   枚举参数是变量时会被静默改写——这三条同时也是 **AI 假阳性的直接来源**。
4. **AI 的瓶颈已不是模型，而是元数据只导出了一半**。`OptionParams`（23 处「选项 → 参数」
   映射）没有进 `units.md` / `units_get`，`format.md` 升级后不刷新，也没有截图 / 试算 / diff。

评分相对「新用户不学就能用、出错能自愈、AI 写的能信」这三条标准：

| 维度 | 完成度 | 一句话 |
|---|---|---|
| 动作编辑器界面 | 3.5 / 5 | 结构清晰，参数区与变量区太挤 |
| 动作编辑器易用性 | 3 / 5 | 能跑能试，改错很费劲 |
| 动作系统健壮性 | 4 / 5 | 预算与失败处理扎实，缺单步超时 |
| AI 帮写准确性 | 2.5 / 5 | 声明式元数据是最大优势，只导出了一半 |

**如果只做三件事**：① 校验器 + 知识包同源补全；② 修枚举静默改写；③ 轨迹加「解析后的输入」+
校验结果可点击。三件都是小改动，但分别堵住「AI 假阳性」「数据丢失」「无法定位」三个洞。

---

## 1. 评审范围与方法

**看过的代码**：`Views/ActionEditor/**`（编辑器与参数表单，约 6 000 行含 XAML）、
`Core/Actions/**`（引擎 / 校验 / 步骤模型）、`Core/Ai/**`（工具面 / 知识包 / 草稿 / 采纳）、
`Features/Units/**`（64 个动作单元）+ `Core/Actions/FlowUnits.cs`（11 个流程 / 子动作单元）、
`Views/Controls/StashPane`。

**看过的文档**：`docs/ACTION_SYSTEM.md`、`docs/PARAM_FORM.md`、`docs/AI_AGENT.md`、
`AGENTS.md`、`CONTEXT.md`。

**没做的**：真机冒烟、埋点量化、用户访谈。结论来自代码路径与既有文档记录，
属于「可从实现直接验证」的判断；性能类结论（如「全量重建」的开销）只给方向，不给毫秒数。

**判断标准**（下面每一节都按这三条打分）：

- 新用户能不能不读文档就走通「新建 → 编辑 → 试运行 → 改对」；
- 出错时能不能自己定位到「哪一行、哪个参数、为什么」；
- AI 写的动作，**静态校验通过** 是否真的等于 **能跑**。

---

## 2. 动作编辑器界面

### 2.1 现状

三栏：左工具箱 242、中步骤 `*`、右变量 + 动作属性 286（`ActionEditorWindow.xaml`）。
工具箱由 `UnitSpec` 全量生成，分类可折叠且状态全局记忆；新增动作单元不需要改 UI。
步骤行 = 折叠箭头 + 步号 + 图标 + 名称 + 一行摘要 + 输出变量 chip + 编辑 / 删除按钮；
按住整行拖动排序；嵌套用缩进，分支用「否则 / 结束如果 / 结束循环」标记行。底部是试运行栏。

**做得好的**：

- 工具箱完全由声明派生，是这套系统最值钱的架构决定（UI / 校验 / 摘要 / 知识包同源）。
- 嵌套用「扁平行 + 缩进 + 标记行」，比节点图易读，拖动 / 折叠 / 多选的实现也简单。
- 步骤选中态画在行卡片上、行内按钮按 `DataContext` 认行、多选拖动整组——细节都在。

### 2.2 问题清单

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| I1 | 参数按声明平铺，无分组、无参数内搜索、无「常用置顶」 | `StepEditorDialog.xaml.cs` 的 `BuildInputs` | 一步十几个参数时全靠滚动；用户不知道哪些是关键项 |
| I2 | 步骤行摘要只取前 2 个可见参数 | `StepSummary.Describe` 里 `parts.Count >= 2` 就 break | 失败策略 / 延迟 / 备注 / 跳过在行上都看不到 |
| I3 | 变量区 286px 塞 5 列（82 / 72 / 76 / `*` / 24），不可拖宽 | `ActionEditorWindow.xaml` 变量表头 | 变量名稍长就截断；没有描述、没有运行值 |
| I4 | 动作的描述（`ActionProgram.Description`）没有编辑入口 | 右栏只有名称 / 图标 / 代码提示 | AI 写的「一句话说明」用户既看不到也改不了 |
| I5 | 空动作只有一句三行示例，没有模板 | `EmptyHint` | 新用户从零开始，第一屏就是七十多项的目录 |

### 2.3 方向

- 参数按「必填 / 常用 / 高级」三档，允许动作单元声明分组；参数区顶部加一个搜索框（本地过滤）。
- 步骤行支持「展开一行」显示完整参数与备注，或悬停给一张参数卡；至少把延迟 / 失败策略 / 备注做成小 chip。
- 变量区改成可拖宽，或独立成底部 / 右侧 tab；补一列描述。
- 右栏补描述输入框（`Description` 已有字段，只是没有界面）。
- 空动作时给 5–8 个模板（选中文字 → 处理 → 粘贴、整理下载目录、截图 OCR、逐个提示列表……）。

---

## 3. 动作编辑器易用性

### 3.1 现状（强项，别动）

- **试运行**不必先保存，跑 `_program.Clone()` + 现读变量表；与真实运行共用互斥锁、停止热键、
  运行指示器；失败常驻提示带「第 3 行」并可点开日志。
- 轨迹按步骤路径聚合（循环 `×N`），点行定位步骤，失败自动选中。
- 撤销 / 重做覆盖结构改动**和**步骤编辑；多选拖动、跨分支移动、自嵌套拦截都在。
- 步骤搜索是「定位」不是「筛选」——不会打乱嵌套，折叠块会自动展开。

### 3.2 问题清单

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| U1 | 改一步 = 双击模态框 → 确定 → `ReloadRows()` 全量重建 | `EditSelected` / `ReloadRows` | 长动作里改参数像「进出一扇门」；没有就地编辑 |
| U2 | 内联取值用**同一个对话框**再开一层，父表单被完全遮住，且无面包屑 | `ValueField.EditInline` → `new StepEditorDialog(..., inline: true)` | 编辑「取值」时不知道自己站在哪个参数的哪一层 |
| U3 | 轨迹只有输出，没有解析后的输入 | `StepTrace` 只有 `Outputs` | 最常见的两类失败（模板解析成空 / 参数被忽略）看不到 |
| U4 | 变量快照只在运行结束后出现 | 编辑器右侧「变量（运行结束后）」 | 循环 / 多步动作没法观察中间值，只能靠 `ui.toast` 打点 |
| U5 | 校验结果是一个最多 12 行的 Info 弹窗，不可点击 | `Validate_Click` → `ConfirmDialog.Info`（改前） | `ActionIssue` 明明带路径和人话标签，却要用户自己去找。**已修：可点击诊断列表 + 行标记，见 6.7** |
| U6 | 只能「从这一行起」跑整条链，不能「只跑这一步」，不能暂停 / 单步 | 试运行栏只有「试运行 / 从此行 / 停止」 | 改第 7 步要重跑前 6 步（而且真的会动键鼠） |
| U7 | 工具箱搜索只看名称 / 键 / 分类 / 描述四个字段的子串 | `Match(UnitSpec, query)` | 中文用户习惯打拼音首字母；也没有「最近使用」 |
| U8 | 撤销栈是窗口内的，变量表不进快照 | `PushUndo` 只存步骤树 | 关窗即丢；误改变量表撤不回来 |

### 3.3 方向

- **参数抽屉化**（P1 的最大一项）：双击步骤在右侧开抽屉，步骤列表与变量区仍可见；
  内联取值在同一抽屉里下钻并显示面包屑（`第 5 行 · 图片 → 内联「截图找图」`），可逐级返回。
  折中方案（成本低）：保留模态，但内联取值换成可并排的窗 / 记住父表单位置。
- **调试增强**（性价比最高）：轨迹增加「解析后的参数」（掩码敏感值），变量面板增加「本次运行值 / 上次运行值」，
  试运行栏加「只跑这一步」（用上次快照初始化变量），步骤边界支持「暂停 / 继续」。
- **诊断面板**：校验结果落到步骤列表下方（**错误在前，同级保持步骤顺序**），点击定位并高亮步骤；步骤行显示红 / 黄角标（已实施，见 6.7）。
- 工具箱：拼音首字母、最近使用、收藏。
- 撤销栈：把变量表纳入快照；至少保证「关窗前的改动」能被 `Dirty` 快照检出（已有，但撤销做不到）。

---

## 4. 动作系统健壮性

### 4.1 强项（这些不要动）

- **三层预算**：`MaxSteps` 10 万 / `MaxLoopIterations` 2 万 / `MaxDuration` 30 分钟。
- **失败策略**：`onFail = stop | continue`，`LastError`、失败日志、常驻提示、`FailedStepLabel`。
- **状态兜底**：鼠标 / 键盘的「按下未抬起」由 `InputHoldState.ReleaseAll` 在 `finally` 释放。
- **并发**：`AutomationRunner` 单实例；自动触发在动作运行时丢弃并记日志。
- **数据**：动作 / 子动作 / 宏 / 模板图各自有回收与坏文件隔离；删除动作时清理引用位置。
- **子动作**：作用域隔离、递归上限 8、调用成环检测、输出变量未赋值给 warning。

### 4.2 问题清单

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| R1 | 30 分钟上限只在每 32 步检查；**没有单步超时 / 看门狗** | `ActionRunState.EnterStep` / `EnterIteration` | 某单元内部阻塞（UIA 读控件、等窗口、OCR 冷启动）时，停止热键要等它自己返回 |
| R2 | `out` 的键不校验是否属于该单元的输出 | `ActionValidator` 只遍历 `step.In`（改前） | `out: { "txt": "v" }` 拼错 → 运行静默不写变量，校验仍是 `ok`。**已修：校验 error，见 6.5** |
| R3 | 选项之外的参数不报错，运行时仍会被解析 | `ActionEngine.ResolveArgs` 不判 `VisibleWhen`（改前） | 残留参数里的坏模板能让整步失败；多余参数则静默空转。**已修：运行时不再解析，校验给 warning，见 6.5** |
| R4 | 输出类型与变量声明类型不比对 | 校验器没有这条规则（改前） | 列表 / 对象写进数字 / 布尔变量，运行时按数值 / 布尔取用才发现。**已修：warning，见 6.5** |
| R5 | 枚举参数是变量 / 内联取值时，编辑器会**静默改写** | `StepEditorDialog` 的 `enumList` 分支只读字面量，`SelectedIndex` 兜底 0（改前） | 打开 → 点确定，值被改成第一个选项（数据丢失）。**已修：置灰只读 + 保留原引用，见 6.5** |
| R6 | 正式动作保存没有历史快照 | `ActionStore` 无 history；草稿有 3 份（`DraftStore`） | 误改不可恢复，只能靠整包备份 |
| R7 | 枚举 / 布尔这类「只能字面量」的约束只写在文档里 | `docs/PARAM_FORM.md` 决策 2，代码没拦（改前） | 手改 JSON / AI 生成 / 导入都能绕过，界面随后显示不出来。**已修：校验 error，见 6.5** |

### 4.3 方向

- **校验器补三条规则**（R2 / R3 / R4），并让运行时与校验同口径：
  被当前位置判为不可见的参数，`ResolveArgs` 直接忽略（或与校验一样报错），
  不要「校验说没问题、运行按另一套规则走」。
- **枚举非字面量显式处理**（R5 / R7）：编辑器遇到变量 / 内联 / 未知值时不兜底选中第一项，
  改成只读标签 +「切回文本编辑（会丢弃变量）」；校验器给 error。
- **动作保存自动快照**（R6）：`actions/<id>/history/<时间戳>/` 留最近 5 份，
  动作管理 / 面板右键给「历史版本…」；复用草稿 `history` 的轮转写法。
- **单步看门狗**（R1）：后台计时超过阈值（如 30 秒）在轨迹里记一条「该步骤耗时异常」，
  并把「停止」变成可点按钮。强制中断线程不安全，**不杀线程**；能拿到 `ctx` 的单元（HTTP / 命令 / 等待）
  保持现有可取消实现。

---

## 5. AI 帮写动作的准确性

### 5.1 结构上做对的

- 工具面里**没有写正式动作的入口**，只能写草稿（`staging/*`）；改动作要先 `copyFromActionId`。
- `revision` 从文件内容重算，agent 用 fs 工具改完也能被 `check` / `save` 识别。
- `check` 的位置文案复用引擎的「第 3 行 › 满足时 › 第 1 行」，与运行失败提示一致。
- `units.md` 从 `StepRegistry` 自动生成，版本串含正文哈希，改参数会重写；`format.md` / `examples.md` 同版本重写（P0-3）。
- 采纳走人工确认；写子动作另有一套闸门与 CAS。

### 5.2 问题清单

| # | 问题 | 证据 | 影响 |
|---|---|---|---|
| A1 | `units_get` / `units.md` **不输出 `OptionParams`**，只给 `visibleWhen` | `AiToolHandlers.UnitsGet` 只序列化 `visibleWhen`；`KnowledgePack.WriteUnit` 表列同样没有 | agent 不知道 `click` 只吃 `x/y`、`drag` 才吃 `dx/dy`，写出「结构合法、运行空转」的步骤，而 `check` 返回 `ok`。**P0-3 已修**（6.6） |
| A2 | `format.md` / `examples.md` 只在**文件不存在**时写，版本只跟踪 `units.md` | `KnowledgePack.Ensure` | 老安装升级后，格式说明永久停留在旧版。**P0-3 已修**（6.6） |
| A3 | 示例只有 3 个（启动程序 / 截图 + OCR / 遍历列表） | `KnowledgePack.ExamplesText` | 文件、文本处理、窗口、剪贴板、找图、`ui.confirm` / `ui.select`、子动作调用都没有范例 |
| A4 | 没有截图 / UIA 工具 | `AiTools` 工具清单 | 找图、图片点击、按控件点击这类动作，agent 无法自己产出模板图或锚点 |
| A5 | 没有 dry-run | `staging_run` 直接 `AutomationRunner` 真的动键鼠 | 验证成本高、风险高，agent 倾向「不跑就交」 |
| A6 | 采纳前没有 diff | `StashPane` 卡片只有名称 / 步数 / 状态 | 用户只能「信」或「跑一遍」，这是信任成本最高的一环 |
| A7 | `health` 不含环境信息 | `AiToolHandlers.Health` 只给版本 / 开关 / 草稿数 / 最近错误 | 坐标类动作容易写偏（多屏、DPI、常用目录） |
| A8 | `check` 的 issue 只有 `message`，没有候选值 / 修复建议 | `CheckPayload` | agent 需要多轮试错；错误信息够准但不够「可执行」 |

### 5.3 方向

- **知识包与工具面同源补全**（A1 / A2 / A3）：把 `OptionParams`、每选项的必填集、
  输入说明一起导出成「按操作分组的参数表」；`format.md` / `examples.md` 也纳入校验
  （三份都按内容比对重写，手写部分与生成部分分开存放在同一份内置常量里）。
- **补工具**（A4 / A5 / A7）：截图（用户确认后写入草稿 `files/`）、UIA 控件树快照、
  `dryRun`（只解析参数、执行取值动作单元、不注入键鼠）、`health` 附屏幕 / DPI / 常用目录。
- **补信任手段**（A6 / A8）：采纳前给步骤级 diff（新增 / 删除 / 改参数），
  `check` 的 issue 带 `suggested` / `options` 字段。

---

## 6. 优化方案

成本是单人估算，**不含**冒烟工程与真机验收。仓库没有测试项目，验收统一按
「构建 0 错 0 警 + 仓库外无头冒烟 + 真机点一遍相关路径」。

### 6.1 P0（先做，2 周内）

| 项 | 做什么 | 为什么 | 验收 |
|---|---|---|---|
| P0-1 | 校验器补 `out` 键 / 可见性 / 输出类型三条规则；运行时对不可见参数与校验同口径 | R2 / R3 / R4，同时是 A1 的放大器 | ✅ 已实施：`out` 键拼错 = error、不可见参数 = warning、列表 / 对象写进数字 / 布尔变量 = warning；试运行与校验一致（详见 6.5） |
| P0-2 | 校验结果改成可点击诊断列表（步骤行角标 + 定位跳转） | U5 | ✅ 已实施：底部诊断条按条列出错误 / 提醒，点一条就展开祖先块、选中并滚到那一步；步骤行左侧带 `✕` / `！`（折叠块合并显示子步骤的问题，详见 6.7） |
| P0-3 | `units.md` / `units_get` 输出 `OptionParams`；`format.md` / `examples.md` 纳入版本校验 | A1 / A2 | ✅ 已实施：`units.md` 每个带选项参数的动作单元先给「选项 → 生效参数」表，参数表「可见条件」写 `operation in (…)`；`units_get` 每个参数多出 `optionParams` / `enabledWhen`；三份知识文件按内容比对重写（详见 6.6） |
| P0-4 | 枚举参数非字面量不再静默改写 | R5 / R7 | ✅ 已实施：打开含变量驱动枚举 / 开关 / 多选的步骤 → 取消 / 确定都不改值（控件置灰只读）；校验给 error |
| P0-5 | 动作保存自动快照 + 采纳前 diff | R6 / A6 | ✅ 已实施：每次内容真变化前把旧版本存进 `actions/<id>/history/`（留最近 10 份），动作管理行内 `🕘` 看差异并恢复；暂存区「覆盖原动作」先弹步骤级差异（详见 6.8） |

### 6.2 P1（2–4 周）

| 项 | 做什么 | 为什么 | 验收 |
|---|---|---|---|
| P1-1 | 参数表单抽屉化 + 内联取值面包屑 | U1 / U2 | 编辑步骤时步骤列表与变量区仍可见；内联取值可逐级返回 |
| P1-2 | 轨迹带「解析后的输入」+ 变量本次 / 上次值 + 「只跑这一步」 | U3 / U4 / U6 | 失败行能直接看到参数展开结果；局部改动不必重跑前序步骤 |
| P1-3 | 工具箱拼音 / 最近 / 收藏 + 空动作模板库 | I5 / U7 | 输入「zj」能搜到「截图」；新动作可从模板起步 |
| P1-4 | 动作参数（运行期向用户要输入） | 同一动作换参数要复制多份 | 面板运行前弹参数框，支持记忆上次值；不改步骤模型，走 `InitialVariables` |

### 6.3 P2（更长期）

| 项 | 做什么 | 为什么 |
|---|---|---|
| P2-1 | AI 工具面：截图 / UIA 树 / dry-run / JSON Schema / `health` 环境信息 | A4 / A5 / A7 |
| P2-2 | 运行历史与失败分析（最近 N 次的成功 / 失败、耗时、失败步骤分布） | 现在只有单个 `last-failure.log` |
| P2-3 | 步骤文本化（可复制 / 粘贴的 DSL），方便与 AI 往返 | 截取片段、分享、贴进对话都靠它 |

### 6.4 排期建议

1. **第 1 周**：P0-1 + P0-4（都改校验与执行口径，一起改一次冒烟）。
2. **第 2 周**：P0-2 + P0-3（人能定位、AI 不再写空转参数），同时补 P0-5 的快照部分。
3. **第 3–4 周**：P1-2（调试增强）——它决定「敢不敢用」，优先于参数抽屉化。
4. **之后**：P1-1 抽屉化（唯一大改），再做 P1-3 / P1-4。

### 6.5 实施记录（2026-11-13，P0-1 + P0-4）

两项都只改「口径」，没有动界面结构。

**校验器（`Core/Actions/ActionValidator.cs`）**

| 规则 | 级别 | 说明 |
|---|---|---|
| `out` 的键不在该动作单元的 `UnitSpec.Outputs` 里 | error | 运行时只按动作单元自己调的键写变量，`out` 里多出来的键永远匹配不上——以前「校验通过、运行却没写任何东西」，变量停在默认值 |
| 列表 / 对象输出写进声明成数字 / 布尔变量的 `out` 目标 | warning | `VariableScope.Set` 不做类型转换，取用时才失败；反向（对象写进文本变量）是常见可用写法，不报 |
| 当前选项不支持的参数（`VisibleWhen` 判定不可见） | warning | 消息带上下文：「「分隔符」在「操作 = 去空白」不会生效；这里写的值会被忽略（运行按默认值）」。被自己的 `VisibleWhen` 收起的参数改说「在当前参数组合下」 |
| 枚举 / 多选参数用变量或取值 | error | 对齐 `docs/PARAM_FORM.md` 决策 2；下拉与勾选组只表达字面量 |

**引擎（`Core/Actions/ActionEngine.ResolveArgs`）**

- 先算 `VisibleWhen.IsVisible(input, step.In, spec)`，**不可见就不解析用户写的值**：残留的 `${不存在的变量}`、数字参数里的 `abc` 不再把这一步拖失败；有 `Default` 按默认值，没有就是 `Value.Empty`。
- 只有「可见 + 必填 + 没值」才抛缺参；`Provided` 集合也只登记真正参与解析的参数。
- 选这个语义而不是「不可见就报错」，是为了让老动作照常能跑（编辑器保存时本来就会丢掉隐藏行），把严格程度放在校验器里。

**参数对话框（`Views/ActionEditor/StepEditorDialog.xaml.cs`）**

- `boolean` / `enumList` / `flags` 统一走 `FreezeIfDynamic`：值来自 `variable` / `inline` 时控件置灰只读，旁边写「来自变量 `${x}`」或「来自取值「截图」」，`read` 原样返回那个 `StepArg`。
- 旁边给一个「改用固定值」按钮当出口——只有点了才落地成控件当前值（勾选框状态 / 第一项 / 空），并顺手把提示收掉。没有它的话，被标成 error 的步骤在界面上反而没法修。
- 删掉了 `combo.SelectedIndex = index >= 0 ? index : 0` 这条兜底——它是「打开一次参数框再确定，值就被改成第一项」的根因；非字面量时下拉留空（`SelectedIndex = -1`）并说明来源，`flags` 全不勾、`boolean` 默认 false 的同款兜底也一起换成保留原引用。
- 字面量路径完全不变：下拉照旧可改、`_enumSetters` 照旧注册（框选后切「固定区域」依赖它）。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警。
- 仓库外无头冒烟 `%TEMP%\opencode\validator-smoke`：本项落地时 22 项全过 —— 干净步骤零问题、`out` 拼错 error、列表写进数字变量 warning、不可见参数 warning（且不产生 error）、枚举用变量 error；运行侧「不可见参数残留坏值不再失败」「不可见数字参数回落到默认值」「可见参数拼错仍然失败」；对话框侧三种控件遇到变量都置灰且保留引用、字面量照旧能改、点「改用固定值」才落地成第一项；**真实动作库（`actions/` 7 个动作）跑一遍，三条新规则 0 误报**（只报出既有的未知动作单元 / 缺子动作 / 缺图）。加上 6.6 的知识包用例后共 35 项全过。
- 真机待点：面板 → 编辑一个含 `input.mouse` 的动作 → 把「操作」切成「单击」再保存 → 校验应给 `dx/dy` 的 warning 而不是 error；试运行表现为忽略该值。

**与原方案的偏差（按实现收敛，已写进 AGENTS.md）**

- 原写「`click` 带 `dx` 被拦」：定为 **warning** 而非 error——运行会忽略它，老动作不该因此跑不了。
- 原写「对象写进文本变量被拦」：不报。`Value` 只在列表 / 对象写进数字 / 布尔时才必然失败。
- 原写「枚举非字面量只在对话框保留」：同时升为 error，因为那种写法在编辑器里改不了；对话框为此加了「改用固定值」出口（点一下才落地成固定值，不点就原样保留）。

---

### 6.6 实施记录（2026-11-13，P0-3）

只动知识包的生成与工具输出，没有改运行期语义；配套的是 P0-1 的可见性口径——agent 得先知道「哪个选项要哪些参数」，才不会把值写进被忽略的行。

**知识包（`Core/Ai/KnowledgePack.cs`）**

| 改动 | 之前 | 现在 |
|---|---|---|
| 三份文件的写入门槛 | 只按 `.version` 重写 `units.md`；`format.md` / `examples.md` 仅当文件**不存在**时写 | `Ensure()` 统一走 `WriteIfDifferent`，三份都按内容比对、**内置内容永远赢**：手改过的、旧版本留下的都会在下次启动恢复 |
| 版本串 | `CurrentVersion()` 内部自己再算一遍正文 | `CurrentVersion(unitsMarkdown)`，一次生成一次哈希（版本文件的内容与含义不变，仍是 `程序集版本\|动作单元数\|正文哈希16位`） |
| 选项参数 | 参数表只有「可见条件」一列，`OptionParams` 完全不导出 | 每个带 `OptionParams` 的动作单元先给一张「选项 → 生效参数」表；参数表「可见条件」由选项参数管的写成 `operation in (relativeMove, drag)`，能直接对照选项表 |
| `format.md` | 没有校验器规则清单 | 顶部加「每次启动按程序内置内容重写，手改会被覆盖」；新增「校验器会拦什么」（error / warning 分类，含枚举必须字面量、`out` 键、不可见参数不生效） |
| `examples.md` | 无提示 | 同样是「会被覆盖」的提示 |

- `RegenerateUnits()` 改名 `Regenerate()`：设置页按钮不再只是重建 `units.md`，而是三份一起写（按钮文案与 toast 同步改成「重新生成知识包」）。
- 共享判定收在 `internal static OptionDriver` / `EnabledOptions`：`units.md` 的选项表、`units_get` 的 `enabledWhen` 与校验器说的是同一件事，别再各算一遍。
- `format.md` 里写明「`optionParams` / `enabledWhen` 说明哪个选项要哪些参数，**按当前选项只填需要的参数**」。

**工具面（`Core/Ai/AiToolHandlers.cs`）**

- `units_get` 的每个 input 多两个字段：`optionParams`（只有驱动键有：`{选项 → 参数键列表}`）与 `enabledWhen`（`{key, in}`，`null` = 不受选项管，任何选项下都可见）。
- 与 `units.md` 同源（`KnowledgePack.EnabledOptions`），避免文档说一套、工具说一套。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警。
- 仓库外无头冒烟 `validator-smoke`：35 项全过。知识包 13 项：`Ensure` 写出三份 + `.version`；`units.md` 有选项表、拖动行列出 `x/y/dx/dy/button`、参数表写 `operation in (relativeMove, drag)`；`units_get("input.mouse")` 的 `dx` = `operation in (relativeMove, drag)`、`button` = `down/up/drag`、`x` 由 9 个带坐标的选项共用、`operation` 自己带 `optionParams`；把三份文件手改成废话后 `Ensure()` 全部恢复且 `.version` 不变；再 `Ensure()` 一次不重写（修改时间不动）。
- 真机待点：设置 → AI 接入 → 「重新生成知识包」应提示「format / units / examples · N 个动作单元」，打开目录能看到选项表；改一行 `format.md` 再重启程序，文件被恢复。

---

### 6.7 实施记录（2026-11-13，P0-2）

只动编辑器的「校验」出口，规则本身（`ActionValidator`）一行没改。

**新增 `Views/ActionEditor/ActionEditorWindow.Diagnose.cs`**（`Validate_Click` / `ValidateCurrent` 从 `Run.cs` 搬过来，原来「弹 `ConfirmDialog.Info`、最多列 12 条」那段删掉）

| 部分 | 说明 |
|---|---|
| 底部诊断条 `DiagnosticBar` | 标题「校验：N 个错误 / M 个提醒 · 点一条定位到步骤」+ `✕` 收起（收起只藏清单，行标记保留）；清单 `MaxHeight 132` 自己滚，每条 = 级别字形 + 位置标签 + 说明，两个文本都带 ToolTip |
| 排序 | 错误在前，同级保持校验器给的步骤顺序（不再按路径把你感兴趣的提醒埋在错误后面） |
| 步骤行标记 | `StepRowTemplate` 加一列（14px）：`✕` = 有错误（`DangerBrush`）、`！` = 只有提醒（`WarningBrush`），悬停逐条列出这一步的全部问题 |
| 折叠块 | `IssueMarkFor(step, includeDescendants: collapsed)`：折叠起来的块把子步骤的问题合并显示，否则「折叠着、里面红了」根本看不出来 |
| 定位 | `DiagnosticRow_Click`（挂在 `ListViewItem` 的 `PreviewMouseLeftButtonUp`，同一条反复点也能定位）→ `AllSteps()` 拿祖先链 → 逐个 `_collapsed.Remove` → `ReloadRows(step)` 选中并 `ScrollIntoView` |

**路径 → 步骤**（`StepOfPath`）：问题路径是 `/steps/2/then/0/in/dx`（参数）或 `…/out/键`（输出），逐段往回收直到命中某个步骤自己的路径；动作 / 变量级问题（`""`、`/variables`）没有对应步骤，只进清单（位置显示「（动作整体）」/「变量」）。

**状态按 `Step` 对象引用记**（不是路径或下标）：结构一改路径就挪位，按路径存的高亮会指到别的步骤上。

- 清单用 `ObservableCollection`：重复校验就地换行，不会留着上一次的结果。
- 点到一条「那一步已经被删掉」的诊断 → 就地清掉那几条并提示「改完再点校验」，不会点了没反应。
- `Themes/Modern.xaml` 新增 `WarningColor`（`#B45309`）/ `WarningBrush`：错误用现成的 `DangerBrush`，提醒用它，不为这一处写内联色。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警。
- 仓库外无头冒烟 `validator-smoke`：47 项全过（本项 12 项）—— 诊断条显示、条数与校验器一致、标题文案、提醒行 `！` / 错误行 `✕`、悬停提示写了原因、折叠块合并显示子步骤错误、折叠后子步骤确实没有可见行、点一条会展开祖先块并选中那一步、重复校验清单就地更新（把「单击 + `dx`」改成「拖动」后该行标记消失）、改好后再校验诊断条收起且行标记清空。
- 真机待点：编辑一个带 `dx` 的「鼠标操作（单击）」→ 点「校验」→ 底部出现「1 个提醒」、那一步行首是 `！`；点清单里那条应展开并滚到该步骤。

---

### 6.8 实施记录（2026-11-13，P0-5）

拆成「存」与「看」两半：存只有一个写入点，看是两个对话框共用一份差异实现。

**存：`Core/Storage/ActionHistory.cs` + `ActionStore.WriteIfChanged`**

| 部分 | 说明 |
|---|---|
| 唯一钩子 | `WriteIfChanged` 读旧内容 → 相同直接 return → 不同则先 `ActionHistory.CaptureBeforeChange` 再写。编辑器保存、AI 采纳、宏挂载、导入备份全都经过这里，没有第二处快照逻辑 |
| 落盘位置 | `actions/<id>/history/<yyyyMMdd-HHmmss-fff>-<内容哈希8>.json`。文件名时间戳保证排序，哈希用来去重——内容改回去再改回来不会刷出两条一样的 |
| 只存 `action.json` | 不复制 `files/`：模板图按内容去重、可能被硬链接共享，逐份复制代价大还会把去重打散。所以历史还原的是「变量 + 步骤」，图片以动作目录里当前的为准（界面里写明了） |
| 保留份数 | 最近 10 份，多出来的按文件名倒序删（被占用就留着下次清） |
| 失败不挡保存 | 快照异常只 `AppLog.Warn`，写动作本身照常进行 |

**看：`Views/ActionEditor/ProgramDiff.cs` + `Views/Controls/DiffList` + 两个对话框**

- 差异的行模型是 `DiffLine(Kind, Depth, Text)`，`Kind` = 新增 / 删除 / 修改，`Depth` 是子步骤层级（界面缩进 14px）。文本复用 `StepSummary`，所以枚举名还是同一份中文。
- 步骤对齐按**「动作单元相同即视为同一步」**做 LCS：只改参数不会被报成「删一步 + 加一步」，中间插入一步也不会让后面全部变红。代价是两个同动作单元的步骤前后调换可能被报成参数变化——可接受，不为它引入更复杂的匹配。
- 比的是：变量（类型 / 默认值 / 方向）、参数（按 `UnitSpec.Inputs` 的标签）、`out`、失败策略、延迟、跳过、备注，以及 `then` / `else` / `body` 递归。
- `ProgramDiffDialog`：草稿采纳前（`StashPane.Promote` 的覆盖路径，替换掉原来那句「覆盖原动作？」确认框）与历史恢复前共用；两份内容一样时确认按钮直接禁用。
- `ActionHistoryDialog`：左列 10 份快照（时间 + 步数 + 名称），选中即算「它 vs 当前」；恢复只返回内容，落盘仍走 `ConfigStore.UpdateAction` + `MarkDirty`，**恢复本身也会产生一条新快照**，所以恢复错了还能退回来。
- 动作管理行内新增第 4 个图标 `🕘`（操作列 96 → 124），仍走 `ActionRowAction_Click` 的 `CommandParameter` 分发。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警；Debug exe 能启动。
- 仓库外无头冒烟 `validator-smoke`：69 项全过（本项 22 项）—— 首次保存不留历史、内容变化后存一份且存的是**改动前**的内容、内容没变不重复存、连改 12 次只留 10 份且列表新的在前、`Read` 能读回某一份；差异侧参数变化报「修改」而不是删了再加、新加步骤 / 新加变量进清单、子步骤差异缩进一层、`Count` 统计与条目一致、删掉带子步骤的块写「含 N 步」；对话框侧差异清单有内容、无差异时确认按钮禁用、历史列表默认选最新且立刻显示差异、清空选中禁用「恢复此版本」。
- 真机待点：动作管理点 `🕘` 看历史与差异并恢复一次；面板暂存区对一份草稿点「覆盖原动作」，应先看到差异再决定。

---

## 7. 明确不做

- 不做节点图 / 可视化连线：步骤树 + 缩进是这套系统的优点，不要为了「看起来像编程」退回图形化。
- 不做应用内 AI 对话与模型配置（`docs/AI_AGENT.md` 已定：外部 agent + MCP）。
- 不为 AI 开直写正式动作的口子。
- 不做「变量驱动枚举」——与 `docs/PARAM_FORM.md` 决策 2 冲突；要动态分支用 `flow.if`。
- 不引入第二套表达式语言；取值与计算仍走取值动作单元和 `data.calc`。
- 不杀线程做超时（强制中断托管线程不安全），只做可观测 + 可停止。

---

## 8. 附录 A：证据索引

| 结论 | 位置 |
|---|---|
| 三栏宽度 242 / `*` / 286 | `Views/ActionEditor/ActionEditorWindow.xaml` 的 `Grid.ColumnDefinitions` |
| 右栏只有名称 / 图标，无描述 | 同上，右栏 `NameBox` / `IconSection` |
| 步骤摘要只取 2 个参数 | `Views/ActionEditor/StepSummary.cs` 的 `Describe` |
| 跳过只加「（跳过）」 | `Views/ActionEditor/ActionEditorWindow.Steps.cs` 的 `MakeRow` |
| 工具箱搜索字段 | 同上，`Match(UnitSpec, string)` |
| 校验走 Info 弹窗、最多 12 条 | `Views/ActionEditor/ActionEditorWindow.Run.cs` 的 `Validate_Click` |
| 参数对话框 640 宽 / 最高 780 | `Views/ActionEditor/StepEditorDialog.xaml` 的 `Window` 属性 |
| 枚举只读字面量、兜底第一项 | `Views/ActionEditor/StepEditorDialog.xaml.cs` 的 `enumList` 分支 |
| 内联取值再开一层对话框 | `Views/ActionEditor/ValueField.xaml.cs` 的 `EditInline` |
| 保存时丢弃隐藏参数 | `Views/ActionEditor/StepEditorDialog.xaml.cs` 的 `SyncStepArgs` |
| 引擎不判可见性 | `Core/Actions/ActionEngine.cs` 的 `ResolveArgs` |
| 轨迹无输入 | `Core/Actions/StepTrace.cs` |
| 预算只在每 32 步检查 | `Core/Actions/ActionEngine.cs` 的 `EnterStep` / `EnterIteration` |
| 校验器覆盖范围 | `Core/Actions/ActionValidator.cs` 的 `ValidateStep`（只遍历 `step.In`） |
| `units_get` 无 `OptionParams` | `Core/Ai/AiToolHandlers.cs` 的 `UnitsGet` |
| `units.md` 表列无 `OptionParams` | `Core/Ai/KnowledgePack.cs` 的 `WriteUnit` |
| `format` / `examples` 只在缺失时写 | `Core/Ai/KnowledgePack.cs` 的 `Ensure` |
| 工具清单（无截图 / UIA / dry-run） | `Core/Ai/AiTools.cs` |
| `OptionParams` 用法示例 | `Features/Units/Input/MouseUnit.cs` 的 `operation` 声明 |
| 正式动作无历史 | `Core/Storage/ActionStore.cs`（无 history）；对照 `Core/Ai/DraftStore.cs` 的 `HistoryDir` |
| 撤销只存步骤树 | `Views/ActionEditor/ActionEditorWindow.History.cs` 的 `PushUndo` |

---

## 9. 附录 B：哪些是既有结论，哪些是本轮新发现

**既有（文档已记录，本文只是汇总）**：

- 行内直接改参数「暂缓」（`docs/ACTION_SYSTEM.md` W4 末尾）。
- 变量入口在「▾ 来源」、用户反馈找不到（W6.6）。
- AI 三层结构（草稿 / 采纳 / 正式动作）与知识包设计（`docs/AI_AGENT.md`）。
- 参数表单只做两列、枚举用纯下拉（`docs/PARAM_FORM.md` 决策 2 / 3 / 5）。

**本轮新增（读代码时确认，文档里没有）**：

1. `units_get` / `units.md` 不输出 `OptionParams`——新的可见性机制上线后，AI 知识面没跟上。
2. `ActionValidator` 不校验 `out` 键、不校验选项可见性、不校验输出类型。
3. `ResolveArgs` 不判可见性，与校验器口径不一致。
4. 枚举参数是变量 / 内联取值时，编辑器会静默改写成第一个选项。
5. `format.md` / `examples.md` 永不刷新。
6. AI 工具面没有截图 / UIA / dry-run，图像类动作存在结构性上限。
7. 正式动作没有保存历史（草稿有）。
8. `StepTrace` 没有解析后的输入，调试只能看到输出。
