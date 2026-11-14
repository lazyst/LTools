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
| P1-1 | 参数表单抽屉化 + 内联取值面包屑 | U1 / U2 | ✅ 已实施：参数表单抽成 `StepFormView`，编辑步骤在右栏抽屉里（步骤列表 / 工具箱始终可见、右栏在「变量 / 参数」两页间切），内联取值在同一个抽屉里下钻一层、面包屑可点回上一层（详见 6.10） |
| P1-2 | 轨迹带「解析后的输入」+ 变量本次 / 上次值 + 「只跑这一步」 | U3 / U4 / U6 | ✅ 已实施：「只跑这一步」此前已有（右键菜单）；本轮轨迹每行多带**变量展开后**的参数（悬停看参数 / 输出 / 错误全文），变量表给「本次 / 上次试运行」对比并标出变化，失败报告与 MCP 轨迹同步带上 `inputs`（详见 6.9） |
| P1-3 | 工具箱拼音 / 最近 / 收藏 + 空动作模板库 | I5 / U7 | ✅ 已实施：搜索认拼音首字母（`jt` = 截图、`wbcl` = 文本处理），左栏置顶「收藏 / 最近使用」两段，空动作给 4 条起步模板（点一下插入现成步骤，详见 6.11） |
| P1-4 | 动作参数（运行期向用户要输入） | 同一动作换参数要复制多份 | ✅ 已实施：变量表「方向 = 输入」的变量就是动作参数；面板 / 快捷键 / 悬浮运行前弹一次参数框并记住上次的值，取消 = 这次不跑，值经 `InitialVariables` 注入（详见 6.12） |

### 6.3 P2（更长期）

| 项 | 做什么 | 为什么 |
|---|---|---|
| P2-1 | AI 工具面：截图 / UIA 树 / dry-run / JSON Schema / `health` 环境信息 | A4 / A5 / A7 | ✅ 已完成：`health` 环境信息（6.13）、dry-run（6.14）、截图（6.15）、控件树（6.16）；JSON Schema 由 MCP SDK 从方法签名生成 |
| P2-2 | 运行历史与失败分析（最近 N 次的成功 / 失败、耗时、失败步骤分布） | 以前只有单个 `last-failure.log` | ✅ 已完成：每次运行落一行 `state/run-history.jsonl`（上限 500），设置 → 运行日志 →「运行历史」看统计与失败步骤分布，双击复制一条、一键复制摘要（详见 6.17） |
| P2-3 | 步骤文本化（可复制 / 粘贴的 DSL），方便与 AI 往返 | 截取片段、分享、贴进对话都靠它 |

### 6.4 排期建议

1. **第 1 周**：P0-1 + P0-4（都改校验与执行口径，一起改一次冒烟）。
2. **第 2 周**：P0-2 + P0-3（人能定位、AI 不再写空转参数），同时补 P0-5 的快照部分。
3. **第 3–4 周**：P1-2（调试增强）——它决定「敢不敢用」，优先于参数抽屉化。
4. **之后**：P1-1 抽屉化（唯一大改，已完成）、P1-3 工具箱与起步模板（已完成）、P1-4 动作参数（已完成，P0 / P1 全部落地）。

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

### 6.9 实施记录（2026-11-13，P1-2）

三项里「只跑这一步」在编辑器右键菜单里早就有（`RunSelected`：单行是「只运行这一步」，多行是「运行选中的 N 步」），
本轮只把它的轨迹补成能看见参数，另外两项是真改：轨迹带解析后的输入、变量表做「本次 / 上次」对比。

**轨迹带「解析后的输入」**

| 部分 | 说明 |
|---|---|
| `Core/Actions/StepTrace.cs` | 多一个 `Inputs`：这一步**实际拿到的参数**（`${变量}` 已展开、按声明类型转换过）。参数与输出分开，因为「写的 `${路径}`」和「真的点到了哪」是两个问题 |
| `Core/Actions/ActionEngine.RunSteps` | `ResolveArgs` 的结果留一份，成功 / 失败 / 继续 / 中止四条出口都把同一个 `StepArgs` 交给 `Trace`；参数求值本身失败时为空表（错误信息已经说明问题） |
| 收哪些参数 | `args.Has(key)` 为真才收（用户填了，或该参数有默认值）。没填又没默认值的参数运行时就是空值，列出来只会把有用的几项挤掉；**被选项收起的参数也不在里面**——这正好是 P0-1「不可见参数不解析」的镜像 |
| `Views/.../TraceRow` | 单行 `Detail` 的优先级变成「错误 > 输出 > 参数」（失败行仍先显示错误）；悬停 `ToolTip` 给全文：位置 / 耗时（含 ×N）/ 参数（解析后）/ 输出 / 错误，每节最多 16 项 |
| `Core/FailureLog` | `last-failure.log` 对失败那一步先展开参数（≤8 项）再展开输出（≤12 项），值统一走 `SafeText`——贴给 AI 的那份报告里就能直接看到展开后的参数 |
| `Core/Ai/AiToolHandlers` | `run_trace` 的每条轨迹多一个 `inputs`，与 `outputs` 并列 |

**变量「本次 / 上次值」**

- `SnapshotRow` 从 `record(Name, Value)` 变成一行四列：名称 / 本次 / 上次 / 变化标记。**变了才加粗 + 打一个 `•`**（灰阶主题里比颜色更可靠），悬停给「本次：… / 上次：…」。
- 对比逻辑抽成纯函数 `SnapshotRow.Build(variables, previous)`：按变量名排序、按**完整文本**比较（不是按显示用的截断串），第一次运行没有「上次」就留空、不标。
- 「上次」只在同一个编辑器窗口里记（`_previousValues`，关窗即丢）：要回答的是「改了一处、再跑一次，哪个变量变了」，不是长期历史——长期看的是动作历史（P0-5）。
- 双击一行仍然复制**本次**的值（`SnapshotDoubleClickValue` 没动）。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警；Debug exe 能启动。
- 仓库外无头冒烟 `validator-smoke`：82 项全过（本项 13 项）—— 轨迹参数是展开后的值（`${v}` → `  hi  `）且与输出（`hi`）是两份、被隐藏的 `count` 不进轨迹、写了空值的参数仍在、失败行单行显示错误而悬停能看到 `x = 100`、成功行单行显示输出且悬停带参数；变量快照按名排序、变了才标并带上次的值与悬停说明、第一次运行不标不显示。
- 真机待点：改一处变量再跑一次，变量表应只有变了的行加粗带 `•`；悬停轨迹里某一步应看到「参数（解析后）」。

---

### 6.10 实施记录（2026-11-13，P1-1）

用户选了「A. 真抽屉」：不只是加面包屑，而是把参数表单从模态窗口搬进编辑器右栏，
**编辑步骤时步骤列表与工具箱一直看得见**。

**表单抽成控件（`StepFormView`）**

| 部分 | 说明 |
|---|---|
| `Views/ActionEditor/StepFormView.xaml(.cs)` | 原来的 `StepEditorDialog` 主体（1274 行）整体搬过来，`Window` → `UserControl`；XAML 只留「滚动表单 + 按钮栏」，标题栏 / 外框交给宿主；所有 `x:Name` 原样保留，code-behind 不用改名 |
| 宿主通知 | 不再设 `DialogResult`：`Saved` / `Cancelled` 两个事件交给宿主；`TrySaveStep()` 供宿主「保存前先收尾」；`HasEdits` 用来判断要不要提醒 |
| `StepEditorDialog` | 变成薄壳（标题栏 + `ContentControl FormHost`），只在没有抽屉的场景用：`ValueField` 在没有宿主钩子时编辑内联取值。`Result` / `NavigateDelta` / `ContinueAdding` / `EnableNavigation` / `EnableContinueAdding` 照旧转发，调用方不用改 |
| Enter / Esc | `IsDefault` / `IsCancel` **从表单按钮上删掉**：它们会落到动作编辑器窗口自己的确定 / 取消上（一按 Enter 把整个动作存了）。改在 `StepFormView_PreviewKeyDown` 里收：Enter = 确定（多行框里让给换行）、Esc = 取消、Alt+← / Alt+→ = 上一步 / 下一步。▾ 来源菜单与录制按键在 Popup 里，键事件走它们自己的树，不会误触 |

**右栏抽屉（`ActionEditorWindow.Drawer.cs`）**

- 抽屉是一叠 `StepFormView`：根层「编辑 / 新增这一步」，内联取值在它上面再推一层。
  `DrawerFrame` 记着这一层保存后往哪写（根层是 `Owner[Index]` / 插入位置，内联层是父表单的那个 `ValueField`）。
- 右栏改成两页 tab（`变量` / `参数`）：打开抽屉切到「参数」、宽度 286 → 440；收起还原。
  窗口 `MinWidth` 因此从 900 提到 1046（242 + 360 + 440）。
- 面包屑只在**两层以上**显示（`编辑「文本处理」› 路径`），点前一段就回到那一层。
- 内联取值：`ValueField.InlineEditor` 钩子把请求交给表单的 `InlineEditorRequested`，抽屉再推一层；
  保存后 `ApplyInlineEdit` 写回父字段。钩子为空（模态壳）时仍退回弹 `StepEditorDialog`。
- 保存落位前先确认目标还在：`IsLiveList`（列表仍挂在步骤树里）+「这一格还是刚才那一步」
  （`DrawerFrame.Original` 引用比较）。编辑期间撤销 / 删除 / 拖动过就提示「步骤已经变了」而不是写错行。
- 换一个步骤编辑时，如果当前表单已经改过（`HasEdits`）先问一句「放弃当前编辑？」。
- 窗口收尾：`Commit()` 先 `TryCommitDrawer()`（校验不过就留在编辑器里，表单自己标出缺哪一项）；
  `IsDirty()` 把「抽屉里还有没保存的表单」算作改动（否则点 ✕ 会静默丢掉正在编辑的那一步）；
  取消 / 关闭时 `DiscardDrawer()` 一起清掉。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警；Debug exe 能启动。
- 仓库外无头冒烟 `validator-smoke`：106 项全过（本项 23 项）—— 抽屉打开一层、右栏切到参数页且宽度 440、`DrawerHost` 里是 `StepFormView`、步骤列表仍可见、改枚举后保存写回动作且抽屉收起 / 宽度还原；内联取值推入第二层、面包屑两段 + 可点回上一层（回来的还是父表单）；新增先拦必填、填好插到指定位置。
- 真机待点：双击步骤 → 右栏切到「参数」、步骤列表还在；改一个参数后点「确定」应只收起抽屉；带内联取值的参数点「编辑取值…」应看到面包屑（`编辑「…」› 路径`），点前一段回得去。

---

### 6.11 实施记录（2026-11-13，P1-3）

四项一起做：搜索认拼音、收藏、最近使用、空动作模板。前两项是「找得到」，后两项是「少打字」。

**拼音首字母搜索（`Core/Pinyin.cs`）**

| 部分 | 说明 |
|---|---|
| 表 | 只收项目里真实出现过的 **160 个汉字**（79 个动作单元名 + 11 个分类），一个字母一行，方便照拼音表核对。故意不引入整套拼音库：那要带几千字的数据，还会有人问「多音字为什么认错」 |
| 多音字 | 按所在词取值：`行` = xíng（运行）、`调` = diào（调用）、`重` = chóng（重复） |
| 匹配 | `原样子串（名称 / 键 / 分类 / 说明）` **或** 拼音首字母子串，所以 `jt` → 「截图」、`wbcl` → 「文本处理」，中文照样能搜 |
| 查询归一化 | 去空白 + 转小写，`jz tz` 当 `jztz` 打 |
| 兜底 | 表里没有的字不贡献字母（不会报错）；冒烟 `[拼音]` 逐字检查动作单元名与分类，**新加单元时漏补会直接红** |

**收藏 / 最近使用（`ActionEditorWindow.Toolbox.cs`）**

- 左栏三段固定顺序：**收藏 → 最近使用 → 分类**；搜索时只留分类段（固定入口在搜索里没有意义）。
- 收藏：动作单元右键「收藏（置顶）/ 取消收藏」，行尾 `★`。`FavoriteUnits` 落 `configs/ui.json`，进程内静态缓存。
- 最近使用：**真正插进动作**时记一次（`NoteUnitUsed`，直接插入与抽屉保存两处），去重顶前、最多 8 条、显示 6 条（`RecentUnits`）。
- 三样偏好（折叠 / 收藏 / 最近）共用一次 `ConfigStore.LoadConfig()`（`ToolboxPrefs`），`SaveUiField` 只写自己那几个字段。
- 键失效（单元改名 / 退役）只跳过显示，**不清盘**。

**空动作的起步模板（`Core/Actions/ActionTemplates.cs` + `ActionEditorWindow.Templates.cs`）**

| 模板 | 步骤 | 要点 |
|---|---|---|
| 处理选中文本 | `text.selection` → `text.process`(trim) → `text.send` | 变量 `selection` → `result` 串起来 |
| 截图 → 识别文字 → 粘贴 | `screen.capture`(interactive) → `screen.ocr`(image) → `text.send` | 变量 `shot` / `text` |
| 列表逐项提示 | `flow.forEach` 循环体 `ui.toast` | 顺带声明列表变量 `items`（三项示例） |
| 问一句再发送 | `ui.input` → `text.send` | 变量 `input` |

- 只在「一步都没有」时显示（`ReloadRows` 判 `total == 0`），点一下追加到末尾 + 补变量声明（同名不重复加）。
- 插进来就是**普通步骤**：进撤销栈、能拖动 / 删除 / 试运行，没有第二套模板引擎。
- 每条模板都保证「点完就能跑」（必填项有值、变量名对得上），冒烟直接拿 `ActionValidator` 逐条验。

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警；Debug exe 能启动。
- 仓库外无头冒烟 `validator-smoke`：**137 项全过**（本项 31 项）—— 拼音首字母取值 / 覆盖率（逐字查动作单元名与分类）、`jt` 与 `wbcl` 在左栏里真的搜出目标、搜不到给提示、清空回到完整列表；收藏后分组排最上面 + 行内 `★` + 写进 `ui.json`、取消后分组消失；最近使用去重顶前且最多 8 条；四条模板逐个过校验；点一下模板插进空动作、循环体与变量类型正确、面板收起。
- 真机待点：左栏搜索框打 `jt` 应只剩「截图」；右键任一动作单元「收藏（置顶）」→ 左栏最上面出现「收藏（1）」且行尾有 ★；新建空动作应看到「从模板开始」四条，点「列表逐项提示」应插入一步带循环体的步骤并在变量表出现列表变量 `items`。

---

### 6.12 实施记录（2026-11-13，P1-4）

目标是「同一个动作换个参数，别复制成好几份」。做法是**不加模型**：参数就是变量表里
**方向 = 输入**的变量，问到的值经 `ActionRunOptions.InitialVariables` 注入——和自动触发的
事件变量走同一条链路，引擎只覆盖已声明的同名变量。

| 部分 | 说明 |
|---|---|
| 编辑器 | 动作的变量表也显示「方向」列，只有 **输入 / 本地** 两项（输出在动作里没有落点）；右栏统一 360，默认值那列不被挤。提示写明「输入 = 运行前问你」 |
| 判定 | `Features/ActionParams.cs` 的 `InputsOf` / `NeedsAsk`：只认输入方向且名字非空的变量，按声明顺序 |
| 入口 | `ActionExecutor.RunRef`（面板磁贴 / 快捷键 / 悬浮按钮）与编辑器试运行；**自动触发不弹窗**（半夜被定时任务要输入是灾难），事件变量照旧注入、其余用默认值 |
| 参数框 | `Views/ParamInputDialog`（原 `SubActionInputDialog` 并入）：只画输入变量，预填上次填的值（解析不了退回默认值）、「填默认值」一键重来、取消 = 这次不跑 |
| 记忆 | `<数据根>/state/action-params.json`（`ActionParamMemory`）：按动作 Id 分组、**整组替换**（这次没问到的键不残留）、先写 `.tmp` 再替换、删动作时 `Forget`。放 `state/` 因为它在备份域里，换机器接着用 |
| 线程 | 问参数只能在 UI 线程：`Ui.Post`（钩子线程不能等对话框）；问的期间 `HotkeyRegistry.Suspended`，否则单键绑定会把用户敲进框里的字母再触发一次 |

**验收**

- 构建：`dotnet build -p:NoWin32Manifest=true` 与 `dotnet build -c Release` 均 0 错 0 警；Debug exe 能启动。
- 仓库外无头冒烟 `validator-smoke`：**166 项全过**（本项 29 项）—— 只收输入方向（含空名字 / 没有变量表 / null 动作）；
  记忆按类型往返（文本 / 数字 / 列表 / 布尔）、整组替换、落盘 `state/action-params.json`、重读仍在、忘掉后清空；
  参数框只画输入变量、预填上次的值、预填值解析不了退回默认值、「填默认值」清回默认值、没点运行没有值；
  动作编辑器显示方向列（两项）/ 右栏 360 / 载入已有输入方向 / 保存时方向与显示名都不丢 / 抽屉收起仍是 360。
- 真机待点：编辑器里给一个变量把方向改成「输入」→ 面板点这个磁贴应弹参数框（列表 / 数字框形态正确），
  填完点「运行」跑起来；再从快捷键触发同一个动作，参数框应预填上次的值；点「取消」不应跑（托盘无提示、动作没执行）。

---

### 6.13 实施记录（2026-11-13，P2-1 第一步：`health` 带环境信息）

agent 看不见用户的屏幕，「鼠标操作 / 屏幕区域 / 截图 / 图片点击」这类坐标动作只能靠猜（A7）。
`ltools_health` 回执新增 `environment`：

| 字段 | 内容 |
|---|---|
| `os` / `is64BitProcess` / `elevated` | 系统版本、位数、是否提权（UIA 读提权窗口要提权，普通权限拿到的是空树） |
| `virtualScreen` | 虚拟桌面整体矩形（<c>ScreenCapture.Virtual*</c>，物理像素） |
| `screens[]` | 每块屏的 `left` / `top` / `width` / `height` / `dpi` / `scale` / `primary` + `workArea`（`rcWork`，已排除任务栏） |
| `cursor` | 当前光标位置（拿不到就不给这个键） |
| `directories` | `dataRoot` / `actions` / `staging` / `images` / `userResources` / `notes` / `subactions` / `knowledge` / `logs` |
| `defaultOcrEngine` | 新建 OCR 步骤会预选的引擎（`windows` = 系统 OCR） |

- 显示器枚举是新加的 `Native/Screens.cs` + `Win32.EnumDisplayMonitors`：坐标一律**物理像素**，与截图 / 找图 / 鼠标注入同一套；DIP 用 `scale`（96 DPI = 1.0）换算。多屏时 `left` / `top` 可能是负数。`GetDpiForMonitor` 在 Win10 以下拿不到就当 96，不让整块信息失败。
- 目录走现成的 `AppPaths` / `ActionStore.Root(锚点)` / `DraftStore.Root` / `ImageLibrary.Dir`，**不在这里另拼路径**。
- `format.md` 的「工作流」前面加了一步 0：写坐标类动作前先看 `environment`；顺手修了里面把 `enabledWhen` 写成 `visibleFor` 的老错。
- 工具说明（`AiTools.Health`）与 `docs/AI_AGENT.md` §5.2 一并更新，agent 才想得起来去调它。

**验收**：构建 0 错 0 警；无头冒烟 **194 项全过**（本项 28 项，含 `screens` 形状、工作区不越界、
`dpi` 与 `scale` 一致、虚拟桌面不小于任一屏、目录齐全且 `actions` 指向 `actions`、整份能序列化回执、
`format.md` 指向 health 且用 `enabledWhen`）。真机待点：设置 → AI 接入页让 agent 调一次 `ltools_health`，
回执里应能看到本机分辨率 / 缩放（例如 `1920x1080` @ `1.25`）与数据根路径。

---

### 6.14 实施记录（2026-11-13，P2-1 第二步：dry-run）

真试运行要抢用户的键鼠（A5），成本高到 agent 宁可「不跑就交付」。现在
`ltools_staging_run(draftId, dryRun: true)` 只解析参数 + 跑取值动作单元，**不注入键鼠、不弹窗口**。

| 部分 | 说明 |
|---|---|
| 判定 | `UnitSpec.DryRun`（`bool?`）+ `RunsInDryRun`：不写就按 `Kind` 推（`pure` 照跑、`action` 跳过）。流程控制（`flow.set` / `if` / `repeat` / `forEach` / `while` / `break` / `continue` / `stop` / `return`）与 `sub.run` 显式 `true`——不跑它们，干跑里连一个变量都求不出来 |
| 例外 | `ui.input` / `file.select` 是 `pure` 但会弹模态框，显式 `false`；`screen.capture` 照跑，但**交互框选退化成整屏截图**（`ctx.DryRun` 判断），否则干跑会糊用户一脸遮罩 |
| 引擎 | `ActionRunOptions.DryRun` → `StepContext.DryRun`；`RunSteps` 里参数照常解析（轨迹带 `inputs`），跳过的记 `StepTraceStatus.skipped`（新状态），干跑也不等 `step.DelayMs` |
| 运行层 | `AutomationRunner.TryRunProgram(..., dryRun)`：干跑不清空 / 不回收运行期截图（那是副作用），也不给结果提示（别让它看起来像真跑了一遍） |
| 工具面 | `ltools_staging_run` 加 `dryRun`；`ltools_run_output` 多 `dryRun` / `skipped` 两个字段，每条轨迹的 `status` 可能是 `skipped` |
| 诚实边界 | 跳过步骤的输出是空的，依赖它的步骤可能报错——`format.md` 与工具说明都写明了「那是预期」 |

**验收**：构建 0 错 0 警；无头冒烟 **219 项全过**（本项 25 项）——12 条动作单元判定（流程 / 取值照跑，
按键 / 鼠标 / 发送文本 / 弹窗 / 输入框 / 选择文件 / 延迟跳过）；一份含「设置变量 → 模拟按键（带 5 秒延迟）
→ 如果（读设置出来的变量）→ 提示弹窗 → 文本处理」的动作干跑：整体成功、分支走进去、按键与弹窗 `skipped`、
跳过那步仍带解析后的参数、5 秒延迟没有等；`AiRuns.Start(..., dryRun: true)` 那条链路跑完 `dryRun = true`、
`skipped = 1`。真机待点：让 agent 对一份含「模拟按键」的草稿干跑一次——鼠标键盘不应被动过，
`ltools_run_output` 里那一步应是 `skipped` 且带 `inputs`。

---

### 6.15 实施记录（2026-11-13，P2-1 第三步之一：截图工具）

agent 看不见用户的屏幕（A4）：「找图」「图片点击」需要模板图、「鼠标操作」需要坐标，它以前只能猜。
新增 MCP 工具 `ltools_screenshot(draftId?, source?, region?, name?, maxWidth?)`——
**图片本身随回执返回**（`CallToolResult` 的 `ImageContentBlock`），同时把 png 落盘：

| 部分 | 说明 |
|---|---|
| 取图 | `Core/Ai/AiScreen.cs`：`source` = `screen`（整块虚拟桌面）/ `foreground`（前台窗口，取不到退回整屏）/ `region`（固定区域 `x,y,w,h`）。坐标系与截图 / 找图 / 鼠标注入同一套（物理像素，多屏时 `left` / `top` 可为负） |
| 落盘 | 传 `draftId` → `DraftStore.WriteFile` 进草稿 `files/`，返回 `files/x.png`（找图 / 图片点击的 `image` 直接写这个字面量）；不传 → `resources/images/`（`AiScreen.SaveToImages`，重名加序号），返回文件名，正式动作也能用 |
| 回执 | `ok` / `source` / `file` / `path` / `bytes` / `width` / `height` / `bounds`（截取矩形）/ `thumbnail`（`width` / `height` / `scale`）/ `coords`（换算式）/ `draftId` / `revision`。agent 靠「图片像素 ÷ scale + bounds.left/top」把看到的点换算成屏幕坐标 |
| 附带图片 | 按 `maxWidth`（默认 1600，0 = 原图）等比缩小，**落盘的仍是原分辨率**；缩小用高质量插值，避免小字糊成一团 |
| 门槛 | `RequireWrites()`（与 `staging_write` 同口径），**不弹确认框**：截的只是草稿文件，采纳才进正式动作 |
| 复用 | 异常翻译抽成 `AiToolHandlers.Describe`，`Call` 与 `Screenshot` 共用一份（顺手让审计里的结果码与回执里的 `error` 码一致） |

**验收**：构建 0 错 0 警；无头冒烟 **259 项全过**（本项 40 项）——整屏尺寸 = 虚拟桌面、固定区域左上角与尺寸精确、
`region` 解析不了 / 尺寸为 0 → `invalid_region`、`source` 写错 → `invalid_source`；缩略图等比（400×200 → 200×100、
`maxWidth = 0` 给原图）、png 魔数、换算比 0.5；文件名收拾（空 → `shot-时间戳.png`、非法字符换 `-`、超长截断、`.png` 不重复）；
工具声明能被 MCP SDK `McpServerTool.Create` 建出来且五个参数都在、全部可选；写入没开 → `writes_disabled`；
开启后落盘 + 回执两块（文本 + `image/png` 图片）+ 尺寸与 `bounds` 与缩略比对、文件真的是 png；
给 `draftId` 时返回 `files/find-x.png`、文件落盘、带 `revision`；草稿不存在 → `not_found` 且不写文件。
真机待点：设置 → AI 里让 agent 调一次 `ltools_screenshot`（给它一个草稿 id）——对话里应看到当前屏幕，
草稿 `files/` 下多出那张 png，且把它的 `files/x.png` 填进「找图」的 `image` 能干跑通过。

---

### 6.16 实施记录（2026-11-13，P2-1 第三步之二：控件树工具）

有截图能看到形状，但 agent 还是不知道「那个按钮叫什么、矩形在哪儿」（A4）。
新增 `ltools_uia_tree(title?, className?, depth?, limit?)`：把一个窗口的 UI Automation 子树摊平成清单。

| 部分 | 说明 |
|---|---|
| 选窗口 | 不传就用前台窗口；传 `title` / `className` 时按 `WindowFinder` 找（与「窗口」条件同一套匹配：标题包含 + 类名精确，命中最上面那个），找不到退回前台并在 `note` 里说明 |
| 遍历 | `Core/Ai/AiUia.cs` 广度优先：`depth` 默认 12 = 上限（**Chromium 类应用要 8–10 层才到真正的内容**，浅了只有一串空 Pane）、`limit` 默认 300 / 上限 1000、每个父节点最多 200 个孩子、整棵树 4 秒预算 |
| 元素 | 扁平清单：`path`（`0.2.1`）/ `depth` / `controlType`（英文 `ControlType` 名）/ `name` / `automationId` / `className` / `bounds` / `enabled` / `offscreen`；空字段省掉 |
| 诚实截断 | `truncated` 只在**真还有内容没读完**时为 true：到深度上限时会再查一次「有没有孩子」；`note` 写明是深度还是数量 / 时间截的 |
| 失败 | `FromHandle` 拿不到根 → `uia_unavailable`，提示「提权窗口要以管理员运行 LTools」并指向「截图 + 文字点击」这条退路；单个属性读取各自 try/catch（慢应用的 COM 调用会超时，坏一个不该让整棵树失败） |
| 线程 | 自己的 `Lazy<UIA3Automation>`（与框选探测同一套 300 / 400ms 超时），**MCP 请求线程（MTA）直接调**，不包 `StaRunner` |
| 门槛 | 只读，**不接写入闸** |

不引入「按控件点击」这种新动作单元：动作单元是用户的东西，工具只提供观察能力——
把 `name` 填进「文字点击」的文本、或把 `bounds` 中心当「鼠标操作」的坐标，两条现成的路都够用。

**验收**：构建 0 错 0 警；无头冒烟 **279 项全过**（本项 20 项）——默认 `depth` 12 / `limit` 300、
超上限夹到 12 / 1000、`depth = 0` 时只有窗口自己、`depth = 1` 不越层、找不到窗口退回前台并写 `note`、
每个元素 `controlType` 非空且 `path` 从 `0` 起、`bounds` 四件套齐全、`count` 与清单一致、`truncated` 是布尔；
工具声明能被 MCP SDK `McpServerTool.Create` 建出来且四个参数都在。调用点在 `Task.Run` 里（复现 MCP 的线程池路径）。
真实窗口实测（本机 OpenChamber 窗口）：79 个元素，含 `Button · 新建会话` / `Edit · 搜索会话` / `Document · … [RootWebArea]`。
真机待点：打开记事本 / 计算器，让 agent 调 `ltools_uia_tree`——回执里应能看到按钮名字与矩形；
把某个按钮的 `name` 填进「文字点击」应能点中。

---

### 6.17 实施记录（2026-11-14，P2-2：运行历史与失败分析）

以前只有一份 `logs/last-failure.log`：它答「这一次为什么失败」，答不了「最近老在哪儿失败」
「这个动作多久跑一次、要跑多久」。补一份**薄**记录（一次运行一行，几百字节），完整轨迹仍然只在失败报告里。

| 部分 | 说明 |
|---|---|
| 落盘 | `Core/RunHistory.cs` → `state/run-history.jsonl`（在备份域里，**别放 `logs/`**——那里只认 `ltools-*` / `action-*` 且会被滚动清理），整份重写（`.tmp` 再替换）、倒序、上限 500 条 |
| 一行是什么 | 时间 / `runId`（与日志行首 `[run-…]` 同一个）/ 动作名 / 来源 / 结果 / 耗时 / 步数 / 失败位置标签 / 步骤路径 / 失败动作单元 / 错误（存盘截到 400 字） |
| 谁记 | 只有 `AutomationRunner.RunCore` 的 `finally`：正式运行、编辑器试运行、草稿试运行、子动作试运行都走它，**别在别处另记一份** |
| 来源 | `RunSource` 由各入口显式传入：`panel` / `hotkey` / `floating` / `trigger` / `editor` / `mcp` / `subaction` / `manual`；自动触发走四参 `RunRef` 固定 `Trigger` |
| 干跑不记 | dry-run 不注入键鼠、不代表「真的跑过一次」，混进来会把统计搅乱 |
| 统计 | `Summarize`：成功 / 失败 / 停止、平均与最慢耗时、**失败步骤分布**（按「动作 + 失败位置 + 动作单元」分组，次数多的在前，最多 8 组）——纯函数，UI 只显示 |
| 界面 | 设置 →「运行日志」新增第三个子页「运行历史」（`Views/ConfigHelper.RunHistory.cs` + `Page_LogsHistory`）：统计行 + 7 列表格 + 右侧失败分布；`刷新` / `复制摘要`（markdown，直接贴给 AI 或提 issue）/ `清空`（只删历史） |
| 双击 | 双击一行 = 复制它（含错误全文），判定抽成 `DoubleClickRunRow` 纯函数（表头 / 空白不做事） |

- 只统计，不自动重跑、不做告警：这页答的是「看清楚」，不是「替你决策」。
- 统计窗口固定最近 50 次（`StatsWindow`），表格最多显示 200 行——完整数据走「复制摘要」。
- 失败位置用**人话标签**（`第 3 行 › 否则 › 第 1 行`），同时留 `Path` 给将来按路径跳转用。

**验收**：构建 0 错 0 警；无头冒烟 **315 项全过**（本项 36 项）——8 种来源的键与中文名、
不认识的值落「手动」、四种结果状态、`LastFailed` 取最后一条失败（空轨迹给 null、路径是编辑器步骤路径）、
统计五项数字、同一动作同一步骤合成一组且停止不算失败、分布按次数排序且最多 8 组、
`Record` → `Load` 顺序与字段（来源存英文键 / 耗时取引擎报的值 / 错误截断）、文件落在 `state/run-history.jsonl`
且是两条非空 JSONL、`Clear` 后为空、复制摘要含标题 / 统计 / 分布 / 明细、**干跑跑完不写历史**。
真机待点：跑两个动作（一个成功、一个故意失败），打开 设置 → 运行日志 → 运行历史——应各出现一行、
失败那行红色且带位置；右侧显示失败分布；双击一行能复制；「复制摘要」粘出来是完整 markdown。

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
