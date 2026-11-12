# AI 帮写动作：Quicker 研究结论 + LTools 方案

> 目标：让外部 AI Agent（opencode / Claude Code / Codex …）通过 MCP 帮用户写 LTools 动作，
> 而**不**在应用内维护一套对话 UI 和模型配置。
>
> 本文上半部分是 Quicker 2.3 的实测研究（结论都有出处），下半部分是给 LTools 的设计与分期。

---

## 0. 结论先行

1. Quicker 的做法是**两条腿**：应用内 Agent（`Quicker.Ai.*` + WebView 对话页）和外部 MCP（本地 HTTP + `quicker.mcp.exe` stdio 桥）。
   **两者共用同一套工具层**，差别只在宿主：内置 Agent 有完整文件工具和知识挂载，外部 MCP 按 `AllowWrites` / 审批模式降权。
2. 它的「AI 帮写」不是让模型直接改动作，而是**三层结构**：

   ```
   工作副本（slot 目录，agent 随便改）
        ↓ quicker_save（CAS 修订号）
   暂存区草稿（draft，独立存储、独立 ID）
        ↓ quicker_promote（人工确认）
   正式动作（catalog）
   ```

   打开一个正式动作准备编辑时，Quicker 会自动为它建/取一份 draft，**正式动作本体不会被直接改写**。
3. 外部 MCP 的默认姿态是「只读 + 沙箱确认」：`AllowWrites=false`、`ApprovalMode="sandbox_confirm"`；删动作有「一次最多一个」「超过一个要用户手打确认语」这类硬限制；**授权开关本身（`agent.mcp.*` / `agent.approvalMode`）只能由用户在设置窗口里改**，模型改不了。
4. 知识不是塞进系统提示词的，而是**随包分发的明文文件**（`agent/skills/*/SKILL.md`、`agent/knowledge/**`、按需生成的 `catalog/*.yaml`），agent 用 grep/read_file 按需检索。这一条最值得照抄。
5. LTools 的形态比 Quicker 简单得多（62 个动作单元、无脚本动作、动作模型就是 `ActionProgram` 的 JSON），
   所以**不需要** Quicker 那套 C# 脚本 / 场景 / 触发器 / 浏览器插件的知识量；
   但 LTools 的 `InputSpec` / `OutputSpec` / `VisibleWhen` / `OptionLabels` 已经是机器可读的元数据，
   可以**自动生成**出比 Quicker 手写 YAML 更准的动作单元目录，这是我们的优势。

推荐方案：**B —— 暂存区三层 + 静态校验闭环 + 外部 MCP**（见 §4），分 4 期落地，约 6–9 个工作日。

---

## 1. Quicker 研究：证据从哪里来

| 证据类型 | 位置 | 权威性 |
|---|---|---|
| 随包技能与知识（明文 Markdown / .d.ts / yaml） | `C:\Program Files\Quicker\agent\skills\**`、`agent\knowledge\**`、`agent\plugins\**\plugin.json` | 发行产物，等于官方对模型的说明书 |
| 真实会话轨迹（工具调用 + 参数 + 回执） | `%LOCALAPPDATA%\Quicker\account\local\data\agent\transcripts\*.jsonl` | 本机实际运行留痕（3 个会话 / 230 次工具调用） |
| MCP 诊断输出 | `quicker.mcp.exe --check` | 实测 |
| MCP 服务端配置 | `%USERPROFILE%\.quicker\mcp\server.json` | 实测 |
| AI 设置 / 模型连接 | `%LOCALAPPDATA%\Quicker\account\local\data\ai\settings.json`、`state.json` | 实测 |
| 程序集类型与属性名 | `Quicker.Ai.Agent.dll`、`Quicker.Ai.Workspace.dll` 等的元数据字符串 | 静态观察（未反编译方法体） |
| 暂存区存储 | `data\actiondrafts.db`（同名 `.qkey`），SQLite 解析器判定 "file is not a database" | 静态观察 → 加密存储（推断） |

> 说明：Quicker 的主程序集做了字符串加密/混淆，方法体反编译收益很低；上面的结论全部来自**明文发行物 + 真实运行回执**，
> 足以还原契约，但没有覆盖内部实现细节（例如草稿脏检查的具体算法）。

---

## 2. Quicker 的 AI 帮写：完整链路

### 2.1 三条入口

| 入口 | 组装方式 | 权重 |
|---|---|---|
| 应用内 Agent | `Quicker.Ai.Agent.dll`（会话/工具/审批/压缩）+ `frontend/ai-chat`（WebView 对话页）+ `ai/settings.json`（模型连接） | 功能最全，维护成本最高 |
| 外部 MCP（HTTP） | `Quicker.exe` 内起本地 HTTP 服务（默认端口 **18765**），Bearer Token | 官方首选，agent 自带文件工具 |
| 外部 MCP（stdio） | `quicker.mcp.exe`：读 `server.json` → 转发 JSON-RPC 到本地 HTTP | 给只支持 stdio 的客户端兜底 |

`server.json` 的实际字段（实测）：

```json
{
  "Enabled": false, "Port": 18765, "AllowWrites": false,
  "BrowserControl": false, "DesktopAccess": false,
  "Token": "qk_…", "ConsentSchema": 1, "ApprovalMode": "sandbox_confirm"
}
```

`--check` 的回执形状（实测，退出码 1 = 未就绪）：

```json
{"ok":false,"settingsExists":true,"settingsParsed":true,"enabled":false,
 "allowWrites":false,"tokenPresent":true,"portReachable":false,
 "authPreflight":"not_checked","port":18765,"code":"quicker_not_running"}
```

**照抄点**：给用户/给诊断脚本一个「不泄露 token 的自检回执」，字段用 `enabled / allowWrites / tokenPresent /
portReachable / code`。设置页的「测试连接」直接调它。

### 2.2 工具面

内置 Agent 的工具层按**能力域**组装（程序集里能看到 ~30 个 `*ToolSurface`）：
动作读/管/建/改元数据/页面、日志读、图标导入、场景选择、步骤合同、设置读写、触发器、技能、运行记录、
工作副本（checkout / patch / validate / checkpoint / discard）、脚本编辑器、计划任务、记忆、电脑操作、浏览器、联网搜索、本机搜索。

外部 MCP 暴露的是同一批域的**子集**。真实会话里观察到的工具名（23 个）：

```
quicker_create  quicker_open  quicker_save  quicker_check  quicker_run  quicker_run_output
quicker_run_record  quicker_runs  quicker_steps_read  quicker_operations_read
quicker_actions_read  quicker_action_logs_read  actions_manage
skill_load  capability_load  set_location
read_file  write_file  edit_file  find_files  grep  search_files  qk_run
```

要点：

- **`quicker_create` 直接建在暂存区**：回执里带 `storageSource:"draft"`、`draftId`、`slot`、
  `editFile`、`revision`、`editVersion`、`location:"暂存区"`、以及
  `actionLink: quicker://actions/<id>?target=staging&draft=<draftId>`。
- **`quicker_open` 打开正式动作时自动落到草稿**：回执 `storageSource:"draft"`，第二次打开回 `reused:true`。
  这就是「用户动作只能查，要改先复制到暂存区」的实现方式——不是靠模型自觉，而是**入口就没有直写正式库的路**。
- **`quicker_check` 返回可运行性 + 精确定位**：`runnable`、`issues[{file,line,column,message,blocking}]`、
  `warnings`、`contractReview{sourceHash,catalogVersion,contracts}`；改动期间检查会给 `stale_revision`。
- **`quicker_save` 做 CAS + 差分预览**：回执 `saved:true`、`editVersion` 自增、`versionConflict`、
  `editPreview.beforeJson/afterJson/undoId`——保存前就把「改了哪几步」摊给用户看。
- **`quicker_run` 区分 `source:"workingCopy"`（未保存也能跑）和 `"saved"`**，跑完用
  `quicker_run_output` / `quicker_run_record` 分页读日志与返回值。
- **`quicker_promote`** 负责草稿 → 正式动作（`action-share` 技能里明确提到）。

### 2.3 状态机（从回执还原）

```
create ─→ 工作副本(slot, revision=内容哈希) ─check→ 诊断
                     │  ▲
                  save│  │discard / 重新 open
                     ▼  │
                draft(savedRevision, editVersion)
                     │ promote（人工确认 / full_control 直通）
                     ▼
                 正式动作(catalog)
```

两套修订号必须分开，否则「保存后的草稿」和「agent 手里那份」分不清谁新：

- `revision` = 工作副本文件内容的哈希（每次读文件都能重算，与是否保存无关）
- `editVersion` = 草稿的保存版本（只有 `save` / `promote` 会变）
- 所有写操作都带 `expected*`，不匹配就 `version_conflict` / `stale_revision`，要求重读。

### 2.4 审批与安全（外部 MCP）

| 机制 | 观察到的行为 |
|---|---|
| `AllowWrites` | 总闸；关着时外部 MCP 只有读能力 |
| `ApprovalMode` | 内置会话里见过 `full_control`；外部默认 `sandbox_confirm` |
| 人工确认 | 非 `full_control` 下删除/发布等破坏性写操作弹原生确认；**一次最多删 1 个**，删多个要用户手打 `我确认删除N个动作` |
| 授权开关只读 | `agent.mcp.*`、`agent.approvalMode` 等「模型不许自己放权」 |
| 幂等 | `duplicate/restore/delete` 带 `requestId`，同一 key 重放同一回执；不同参数冲突 |
| 可恢复 | 删除前落一份完整历史快照，可 restore 成**新 ID 的草稿**；清空回收站后不可恢复 |
| 首次连接 | 需要用户在设置页授权，冷启动/工具调用可能等 ~180s |
| 设置写能力 | 外部 MCP **完全不暴露**设置写；只能读 |

### 2.5 知识包（最值得抄的一条）

```
agent/
  knowledge/index.md              # 导航表：什么任务看哪
  knowledge/program/*.md          # 表达式、变量、结构化参数、子程序…
  knowledge/steps/<runner>/**     # 单个步骤的长尾文档（按需读）
  skills/<name>/SKILL.md          # 技能：什么时候用、契约、反例
  plugins/<name>/plugin.json      # 能力包：activation(ondemand/opt_in)、risk、requires、skills
```

- 模型**先读 `index.md` 的导航表**，再 `grep` / `read_file` 精确取用；不是一次性塞满上下文。
- 步骤目录按 runner 拆成 yaml 卡片（`sys.writeClipboard.yaml` 等），胖模块再按 `type` 分叶子文件。
- 技能文件里大量写**反例**（"别为它另写一套匹配"、"不要猜子文件"），这比正向描述更省 token。
- 设置页里「AI 自动配置」= 让用户复制一段提示词给外部助手，而不是内置客户端选择器。

### 2.6 不值得照抄的部分

| Quicker 的做法 | 为什么不抄 |
|---|---|
| 内置对话 + 模型连接管理（`Quicker.Ai.Agent` 5MB + `frontend/ai-chat`） | 用户已明确选外部 agent；这套自研成本极高，且要和各家模型协议赛跑 |
| C# 脚本动作（QkEval + 合同审查 + 运行记录） | LTools 没有脚本动作，只有 `script.ahk`（内联代码） |
| 场景 / 触发器 / 页面 / 浏览器插件全套工具域 | LTools 对应物只有菜单组、超级面板、悬浮项，用少量工具就够 |
| 加密 SQLite 存草稿（`actiondrafts.db` + `.qkey`） | 加密后 agent 无法用文件工具直接看；LTools 的草稿本来就是给 agent 看的目录 |

---

## 3. LTools 的现状约束

- 动作模型：`ActionDto`（Id/Name/Icon/Type/Program）+ `ActionProgram`（Name/Icon/Description/Variables/Steps/SubActions），
  一个动作一个目录 `actions/<id>/action.json`（P2 起；更早是一份 `LTools.json` / `configs/actions.json` 的数组）；`Program` 是纯数据，天然可序列化。
- 62 个动作单元，全部有 `UnitSpec`：`Key/Name/Category/Kind/Body/Inputs/Outputs/Description`；
  `InputSpec` 带 `Type/Editor/Required/Default/VisibleWhen/Options/Labels/Raw/Description`。
  **这就是现成的机器可读目录**，不需要另写一份 YAML。
- 运行入口唯一：`AutomationRunner.TryRunProgram(name, program, startPath, trace, completed)`，
  已有单实例互斥、停止热键、Toast 收尾、`StepTrace`（路径 + 人话标签）。
- 线程模型（2026-11 阶段 2 起，见 `docs/THREADING.md`）：两条 STA 线程——主 UI 线程（WPF Dispatcher）与 `HookDispatcher` 专用线程（纯 Win32 消息泵）。低级键鼠钩子回调跑在后者，**不碰任何 WPF 对象**，只读 `HookMirror` 快照、WPF 副作用走 `Ui.Post`。动作库 / 配置 / 全体 WPF 窗口仍只属于主 UI 线程，**任何 MCP 写操作都必须回主 UI 线程**（`AiUi.Run` / `Dispatcher.InvokeAsync`）；不要在 UI 线程上等 HTTP 结果（死锁）。
- 资源回收：`ImageLibrary.CollectGarbage` 只扫 `resources/images/shot-*.png`，按 `ProgramWalker` 收集的
  `image` 字面量判活；`resources/user/` 永不回收。
- 动作私有资源在 `actions/<id>/files/`：随动作目录删除 / 备份，不参与上面的按引用回收。
- 无测试项目；`check` 就是质量闸门，必须自己扛住。

---

## 4. 方案对比

| | A 只读 + 手工复制 | **B 暂存区三层（推荐）** | C 加内置对话 |
|---|---|---|---|
| MCP 工具 | `units_*` / `actions_*` / `docs_get` | A + 暂存区 CRUD / check / save / run / promote | B + 会话、模型连接、流式 UI |
| agent 写文件 | 自己用 fs 工具写任意路径 | 暂存目录（也可用 fs 工具写，revision 按内容重算） | 同 B |
| 校验 | 无（跑到一半才炸） | `check` 静态校验 + 精确定位 + 可试运行 | 同 B |
| 误改正式动作 | 靠模型自觉 | 结构上不可能：写入口只有草稿 | 同 B |
| 工作量 | 1–2 天 | 6–9 天 | +2–3 周 |
| 风险 | 生成坏动作、用户不敢用 | 校验器要覆盖 62 个动作单元 | 长期维护成本高 |

**推荐 B**：把 Quicker 的三层结构和 `check/save` 契约搬过来，去掉内置对话和脚本能力。

---

## 5. 方案 B 设计

### 5.1 目录布局（数据根下）

```
ai/
  mcp.json                     # 服务配置（人可读，token 打码显示）
  knowledge/
    format.md                  # 动作 JSON 格式（手写，稳定）
    units.md                   # 动作单元目录（启动时从 StepRegistry 生成）
    examples.md                # 3~5 个完整示例（手写）
    .version                   # 生成版本（程序集版本 + 动作单元数 + units.md 正文哈希）
  audit.log                    # 每次工具调用的审计行（轮转，最多 2MB）
  mcp-debug.log                # 只有 MCP SDK 的 Warning 以上（排错用，轮转 1MB）
staging/
  <draftId>/
    draft.json                 # 宿主状态：id/base/savedRevision/时间/来源（agent 不要改）
    program.json               # 工作副本：ActionProgram（agent 改这个）
    files/                     # 工作副本资源（v1 只接受图片）
    history/<yyyyMMdd-HHmmss>-<rev12>/
      program.json             # 快照：程序 + 当时的 files/（只留最近 3 份）
      files/
```

- `draftId` = 26 位小写 ULID（`Core/Ids.cs` 的 `IdFactory`），同时是目录名；与动作 Id 同一套生成器、互相独立。旧 12 位 hex 草稿按「不做向后兼容」不认、不迁移。
- 工作副本**只有一个内容文件** `program.json`：`ActionProgram` 已包含 Name/Icon/Description，
  不需要再维护一份 `action.json`，避免两个真源。
- `revision` = `sha256(program.json 规范化字节 + 排序后的 files 清单与内容哈希)`；任何一路写入
  （MCP 的 `staging_write` 或 agent 自己的 fs 工具）都能被 `check/save` 重算出来，**不依赖写入通道**。
- 草稿不进正式配置。备份打包 `configs/` + `actions/` + `resources/` + `速记/` + `macros/` + `staging/`
  （见 `Core/BackupService.cs` 的 manifest `includes`），换机后未采纳的草稿不会丢。

### 5.2 MCP 工具清单（v1，27 个）

| 工具 | 作用 | 写 | 审批 |
|---|---|---|---|
| `ltools_health` | 版本 / 开关 / 审批模式 / 草稿数 / 最近错误 | | |
| `ltools_docs_get(name)` | `format` / `units` / `examples` | | |
| `ltools_units_list(query?)` | 动作单元摘要列表（74 条，可过滤） | | |
| `ltools_units_get(key)` | 单个动作单元的完整 Input/Output 规格（`sub.run` 附带全部子动作签名） | | |
| `ltools_actions_list(query?, limit?)` | 正式动作列表（Id/名称/图标/步骤数/来源） | | |
| `ltools_actions_get(id)` | 正式动作的完整 Program（只读） | | |
| `ltools_subactions_list(query?, limit?)` | 全局子动作列表（id / 名称 / 输入 / 输出 / 步数） | | |
| `ltools_subactions_get(id)` | 子动作完整定义 + revision（只读） | | |
| `ltools_subactions_check(program)` | 子动作静态校验（与编辑器「校验」同一套） | | |
| `ltools_subactions_write(program, expectedRevision?)` | 新建 / 整份写入子动作（CAS，写前自动校验） | ✓ | 人工确认 |
| `ltools_subactions_delete(id)` | 删除子动作（先列出调用位置） | ✓ | 人工确认 |
| `ltools_staging_list()` | 草稿列表 + 状态 | | |
| `ltools_staging_create(title, description?, copyFromActionId?)` | 新建草稿（带复制来源） | ✓ | |
| `ltools_staging_open(draftId)` | 草稿元数据 + program + 文件清单 + revision + **绝对目录** | | |
| `ltools_staging_write(draftId, program?, expectedRevision?)` | 整份写入 program.json | ✓ | |
| `ltools_staging_read_file` / `write_file` / `delete_file` | 草稿 `files/` 的读写（base64 / 路径白名单） | ✓ | |
| `ltools_staging_check(draftId)` | 静态校验，返回 issues/warnings/revision | | |
| `ltools_staging_save(draftId, expectedRevision, summary?)` | 冻结为已保存版本（CAS） | ✓ | |
| `ltools_staging_reset(draftId)` | 回到最近保存版本 | ✓ | |
| `ltools_staging_delete(draftId)` | 删草稿（一次一个） | ✓ | 人工确认 |
| `ltools_staging_run(draftId, expectedRevision?)` | 后台运行工作副本，返回 `runId` | | |
| `ltools_run_output(runId, cursor?)` / `ltools_run_stop(runId)` | 读轨迹 / 停止 | | |
| `ltools_staging_promote(draftId, mode:"new"\|"replace", expectedRevision)` | 草稿 → 正式动作 | ✓ | 人工确认（`full` 直通） |

刻意**不做**的工具：删除/改写正式动作、改设置、执行任意命令、读写任意磁盘路径。
全局子动作是唯一的例外：它不是用户的正式动作，另有独立的 `subactions_check / write / delete`
（同样要写入开关 + 审批），改它影响所有调用方，所以覆盖前用 `expectedRevision` 做 CAS。

### 5.3 审批模型

沿用 Quicker 的两级闸门：

- `AllowWrites`（默认 **false**）：总闸，关着时所有 `✓` 工具直接拒绝（错误码 `writes_disabled`）。
- `ApprovalMode`：
  - `readOnly`：只读（与关总闸等价，便于一键切回）
  - `sandbox`（默认）：草稿 CRUD / save / run 放行；`promote`、`delete` 由 LTools 弹原生确认
  - `full`：都放行（`promote` 仍写审计）
- 令牌：首次启用时生成 `lt_` + 32 字节随机串，只在设置页可见/可复制/可重置；比较用定长时间；**日志里永远打码**。
- 绑定 `127.0.0.1`，校验 `Origin`/`Host`，拒绝非 `application/json`，请求体大小上限（如 1MB）。
- 审计：每行 `时间 工具 draftId 结果 耗时`；不记 program 内容。

### 5.4 静态校验器（`Core/Actions/ActionValidator`）

这是本方案的核心增量，也是「AI 写出来的动作敢不敢用」的答案。规则：

1. 未知动作单元键 / 未知参数键（对照 `UnitSpec.Inputs` + `DynamicInputs`）
2. 必填缺失（**只对 `VisibleWhen` 判定为可见的参数**，与引擎 / 对话框同一条规则）
3. 字面量类型不符（按 `InputSpec.Type` 试 `Coerce`）
4. 枚举值不在 `Options` 里（保留旧值的情况只警告）
5. `${变量}` 引用了未声明的变量；`out` 写到未声明的变量
6. 条件字符串可解析（复用 `VisibleWhen` 的解析器 / 条件求值器）
7. 步骤 Id 重复（如果引入 id）
8. `files/` 里引用的图片不存在；引用了草稿外的绝对路径给警告
9. 子动作调用指向不存在的 `SubActions` / 全局 `subactions`
10. 宏引用不存在
11. 内联 Pure 动作单元不能是 `action` 类型（会阻塞取值）
12. 预算：步数 / 嵌套深度 / 循环无界（`flow.while` 没有超时）给警告

输出形状照 Quicker：`{ ok, revision, issues:[{path:"steps/2/then/0", label:"第 3 行 › 满足时 › 第 1 行", key, message, severity}], warnings }`
——**位置文案直接用现成的 `StepTrace` 标签规则**，不要再造一套「第几步」。

同一个校验器顺带接进动作编辑器（「校验」按钮），否则两套规则迟早分叉。

### 5.5 运行

- `ltools_staging_run` 直接调 `AutomationRunner.TryRunProgram`（与编辑器试运行共用互斥），
  额外挂一个 `StepTrace` 收集器把轨迹按路径聚合、写进内存 run 表。
- **试运行带「运行期资源根」**：起跑时把草稿的 `staging/<id>/files` 一起传下去
  （`ActionRunOptions.ResourceRoot` → `StepContext.ResourceRoot` → `ImageLibrary.Resolve`），
  所以步骤里写 `files/x.png`（或裸 `x.png`）就能找到草稿里的图，**不需要写绝对路径**。
  正式运行时传动作自己的 `actions/<id>/files`（`ActionStore.ResourceRootFor`），解析规则相同。
  面板试运行与编辑器的「▶ 测试 / 校验」走同一套根。
- 已有运行在跑时返回 `busy`（不要排队，用户可能正在用键鼠）。
- 停止走 `AutomationRunner.Stop()`；停止热键不变。
- 轨迹回执带 `StepTrace` 的 `Path` / `Label` / `Status` / `Error` / `Outputs` / 耗时，支持 cursor 分页。

### 5.6 采纳（promote）

```
mode=new      → ActionDto { Id=ActionRegistry.NextId(), Name/Icon/Description 取 program,
                            Type=composite, Program=program.Clone() } → ConfigStore.AddAction
mode=replace  → 校验 draft.baseActionId 仍存在、baseSourceHash 未变（用户没改过）
                不一致 → 拒绝，提示「原动作已变更，请另存为新动作」
```

- 图片：草稿 `files/x.png` 整体复制进 `actions/<动作id>/files/`，步骤里的 `files/x.png` **原样保留**
  （P2 起，不再改写路径、也不再搬进 `resources/images`）。动作运行时以该动作的 `files/` 作为
  `ResourceRoot`，所以引用一直有效；删动作时整个目录进 `trash/`，图片跟着走。其它类型文件 v1 直接拒绝
  （`check` 就报错），避免出现没人回收的孤儿。
- 采纳顺序：先把 `files/` 复制进动作目录，再 `Save()` 落盘（复制失败就放弃采纳，不留悬空引用）；
  最后 `ImageLibrary.CollectGarbage()` 回收不再被引用的共享模板。
- 采纳后 `draft.json.promotedActionId` 记下来，草稿列表显示「已采纳 → 动作名」，允许再次采纳（会新建/覆盖）。
- 悬浮项 / 菜单组引用：v1 不自动挂到面板，采纳后由用户在动作管理页拖进面板（避免 AI 擅自改布局）。

### 5.6.1 服务启停（`McpServer.Apply` / `Stop`）

- `Apply`：**只在后台线程调用**（启动时 `Task.Run(McpServer.Apply)`，设置页改配置也 `Task.Run`）。
  它先摘掉旧实例、等它真正关停（有界 8 秒，端口必须释放），再按 `ai/mcp.json` 启新的。
- `Stop`：**绝不在调用线程上等关停**，只把 `StopAsync` / `DisposeAsync` 排到后台任务。
  原因：这两个 API 内部有 `await`，在 UI 线程上 `GetAwaiter().GetResult()` 会让续体排在
  Dispatcher 队列里等一个正被阻塞的线程 —— 开着 MCP 服务从托盘「退出 LTools」会直接卡死，
  托盘菜单也点不动（实测退出线程永久阻塞）。退出流程不等关停：`Shutdown()` 之后进程结束，端口自然释放。
- 关停任务串成链（`_stopTask`），`Apply` 等的是链头，所以「关一次再启一次」不会抢端口。

### 5.7 设置页「AI」+ 面板「暂存区」

**暂存区不在设置里**（照 Quicker：Quicker 的暂存区就在面板左侧栏）。设置里只有连接与审计：

- **设置 → AI 接入**（左侧导航 `AI 接入`，放在「备份」之前）：和「面板管理」一样是**二级下拉导航**
  （点父项进组 / 再点收起，展开状态与上次所在子页都会记住），内容拆成两页：
  1. **连接**（默认页，一页装完三块）：
     - 服务：总开关、端口、令牌（显示/复制/重置）、允许写入、审批模式、状态灯（运行中/端口可达/最近调用）。
       「复制配置」下拉（opencode / Claude Code / Codex / Cursor）生成对应 JSON/命令；最后一项「stdio（命令行）」只复制 `"<exe>" --mcp-stdio`（填 command / args，桥自己读令牌），照 Quicker 的「AI 自动配置」。
     - 让 agent 帮你连接：只读多行框 + `复制连接说明` + `隐藏令牌`，右上「方式」下拉在 **HTTP / stdio 两份提示词**之间切换。每份**只有一句提示词**——
       HTTP：地址 `http://127.0.0.1:<端口>/mcp`（Streamable HTTP、无状态）、令牌 `Authorization: Bearer <令牌>`（`X-LTools-Token` / `?token=` 备选）；
       stdio：命令 `"<exe>" --mcp-stdio`（command / args，桥自己读本机令牌，不需要 URL 和 Bearer）；
       两版都以「接上后先 `ltools_health`，写动作前读 `docs_get("format")` 和 `units_list`，写完我去面板采纳」收尾。
       **别再往里塞客户端 JSON 和逐条工作清单**（JSON 归「复制配置」，细节归知识包），改端口 / 重置令牌会重算；stdio 版没有令牌，`隐藏令牌` 置灰。
     - 知识包：路径、「重新生成 units.md」、打开目录（内容太少，不单开一页）。
  2. **审计**：最近 20 条工具调用，5 列表格（时间 / 工具 / 草稿 / 结果 / 耗时，表头即 DataGrid 列头；行由 `AiAudit.TailEntries` 解析定宽日志得到），「打开审计日志」按钮看完整 `ai/audit.log`。
- **面板左栏「暂存区」页**（`Views/Controls/StashPane.xaml`，左栏第二个图标，第一项是动作面板）：
  - 卡片网格：图标 chip + 名称（超宽截断，ToolTip 给全名）+ 状态行 `草稿 / 已保存 / 已采纳 · N 步`；
    空态一句「暂存区是空的 / 让 AI 助手写一个动作…」。
  - 页头：标题 + 草稿数、刷新、更多（打开暂存目录 / 清空暂存区 / AI 接入设置）。
  - 单击卡片 → **直接运行**这份草稿（照 Quicker）；要改内容走右键「在编辑器中打开」。
  - 右键卡片 → 在编辑器中打开 / 试运行 / 采纳为新动作（再采纳）/ 覆盖原动作（仅 `baseActionId` 的草稿）/ 打开草稿目录 / 删除草稿 / 刷新。
  - 右键空白 → 刷新 / 打开暂存目录 / 清空暂存区 / AI 接入设置。
  - 采纳（新建）不弹确认（点菜单就是确认），面板不关，页内卡片立刻变「已采纳」；失败用常驻提示。
  - 需要模态框的（编辑器、试运行、覆盖原动作、删除、清空）**先关面板再弹**（隐藏的 Topmost 面板会让模态框闪黑并挡住长按右键手势）。
  - 面板可见时 1 秒轮询草稿目录，签名含 `program.json` / `files` 的写入时间（agent 直接改文件也能看到）。
- 配置里 `LastNavPage` 仍是设置页的导航（存**具体子页** tag：`ai-connect` / `ai-audit`，旧值 `ai` / `superpanel` 与已并页的 `ai-prompt` / `ai-knowledge` / `ai-stash` 会自动归一到连接 / 常规）；
  两个二级下拉的展开状态另存 `NavExpandedGroups`（为空 = 都没展开；当前页所在组启动时一定展开）。
  面板停在哪一页不落盘（每次打开是动作面板，`ShowStash()` 例外）。
- 「动作管理」页顶部的 `暂存区（N）` 入口直接打开面板暂存区（`SuperPanel.ShowStash()`）。

### 5.8 接线点（改动集中在这些文件）

| 新增/改动 | 说明 |
|---|---|
| `Core/Ai/DraftStore.cs`（新） | 草稿目录读写、revision 计算、CAS、history 轮转 |
| `Core/Ai/KnowledgePack.cs`（新） | 从 `StepRegistry` 生成 `units.md`，版本变化才重写 |
| `Core/Actions/ActionValidator.cs`（新） | §5.4 的规则 |
| `Core/Ai/McpServer.cs`（新） | `ModelContextProtocol.AspNetCore` + `WebApplication`，后台线程，全部 `Dispatcher.InvokeAsync` |
| `Core/Ai/AiSettings.cs`（新） | `ai/mcp.json` 读写，token 生成 |
| `Config/AppConfig.cs` | 只加 `LastNavPage` / `NavExpandedGroups` 两个导航字段，别把 AI 配置塞进 `configs/` |
| `Views/ConfigHelper.xaml(.cs)` + `Views/Controls/AiView.xaml(.cs)`（新） | §5.7 设置页二级下拉的两页（连接〔含「让 agent 帮你连接」+ 知识包〕/ 审计） |
| `Views/Controls/StashPane.xaml(.cs)`（新）+ `Views/SuperPanel.xaml(.cs)` | §5.7 面板暂存区页面（卡片 / 右键菜单 / 轮询） |
| `Views/ActionEditor/ActionEditorWindow.xaml.cs` | 支持「打开草稿」（复用现有 `_program` 加载路径） |
| `Features/ConfigStore.cs` | 只经它写动作（promote 复用它，别绕过） |
| `Features/Flow/ImageLibrary.cs` | 共享模板图的解析 / 回收（`Resolve(path, resourceRoot)`、`ToStoredPath`） |
| `LTools.csproj` | 加 MCP SDK 依赖；构建后清理用不到的 runtime 资源 |

### 5.9 分期

| 期 | 内容 | 出口标准 |
|---|---|---|
| W1 | `DraftStore` + `ActionValidator` + 设置页「暂存区」只读列表 | 手工放一个草稿目录能列出、能校验出错误 |
| W2 | MCP 骨架（HTTP + token + `health` / `units_*` / `docs_get` / `actions_*`）+ 设置页「连接」段 | opencode 能连上，列出 62 个动作单元、读到正式动作 |
| W3 | 写工具：`staging_create/open/write/read_file/check/save/reset/delete` + `run` | 端到端：一句话 → 草稿 → check 通过 → 试运行 |
| W4 | `promote` + 审批 + 审计 + `examples.md` + 编辑器打开草稿 | 用户点「采纳」得到正式动作，重启后仍在 |
| 可选 | stdio 桥（已实现：`LTools.exe --mcp-stdio` 把 stdio 逐行转发到本机 HTTP 服务，不复制状态）、YAML 语法糖、多草稿 diff | |

### 5.10 坑（提前写下来）

1. **线程**：HttpListener/ASP.NET 回调线程上碰 `MenuSystem` / `ConfigStore` / WPF 对象必炸。全部走
   `Application.Current.Dispatcher.InvokeAsync`，且不要在 UI 线程上等 HTTP 结果（死锁）。
2. **配置按域读改写**：promote 必须走 `ConfigStore`（内部 Load→改→Save，只写 `menus.json` + `actions/`），否则和设置页并发保存会互相覆盖。单域设置走 `SettingsStore` / `UiStore` 等，落盘时 `ActionStore` 只重写内容变了的 `action.json`。
3. **不要给 agent 直写正式库的入口**：一旦有 `actions_update`，模型迟早会「顺手」改掉用户动作。
4. **revision 必须能从文件内容重算**，不能只在 MCP 写入时自增——否则 agent 用 fs 工具改完，save 会以为没变。
5. **`VisibleWhen` 是必填判断的一部分**：只校验可见参数，否则「写入时才必填」的参数会误报（这条 AGENTS.md 已写过）。
6. **动作的图要么在动作目录 `files/`，要么在 `resources/`**：采纳会把草稿 `files/` 搬进 `actions/<id>/files/`，
   运行时按该目录解析；别再往共享池写「只有草稿知道」的路径。
7. **试运行会真的动键鼠**：UI 上要能一眼看到「AI 正在试运行」，并保留停止热键；MCP 侧返回 `busy` 而不是排队。
8. **token 只存本地、只显示一次以上**：设置页可复制，不进日志、不进错误消息、不进 `health` 回执。
9. **`units.md` 会随动作单元变化漂移**：用 `.version`（程序集版本 + 动作单元数 + `BuildUnitsMarkdown()` 正文的 SHA-256）控制重生成，别手工维护；只哈希「键 / 分类」会让动作单元参数重构后的知识包停在旧内容。
10. **管理员权限**：LTools 以管理员运行，MCP 服务天然也是高权限。HTTP 必须只听 127.0.0.1 + token，
    并且默认关；否则等于给本机任意进程一个提权后门。

---

## 6. 给 AI 的提示词骨架（`ai/knowledge/format.md` 的首段）

```
你是 LTools 动作编写助手。只能通过 staging_* 工具产出草稿，永远不要试图改正式动作。
动作 = { name, icon, description, variables[], steps[] }；步骤 = { unit, in{}, out{}, body/then/else }。
参数三态：字面量（文本支持 ${变量} 模板）/ {"var":"名字"} / 内联取值动作单元 {"unit":..., "in":{...}}。
流程：units_list → units_get → staging_create → staging_write → staging_check（改到 ok）→
      staging_save → staging_run（可选）→ 交给用户点「采纳」。
找不到能力就说不支持，不要臆造动作单元键。
```
