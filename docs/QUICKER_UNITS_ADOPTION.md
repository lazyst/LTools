# Quicker 功能单元借鉴评估（优先级排序与 LTools 落地建议）

> 上游数据：`docs/QUICKER_UNITS_SURVEY.md`（REA 分析 Quicker 2.3.36.0 得到的 139 个 V1 动作单元，筛选后 118 个）。
> 性质：方案评估，不是已批准的排期。**只借鉴「能力清单」，不借鉴 Quicker 的模型**（见 §6）。
> 术语一律用 `CONTEXT.md`：Quicker 的「模块 / 步骤」在本文统一说成**动作单元**，Quicker 的「输出端口绑定到变量」说成**输出（`out` 映射 / `OutputSpec`）**。
> 分析日期：2026-11-15。

## 1. 结论摘要

1. **LTools 已经覆盖了 Quicker 功能单元的大半**。筛选后的 118 个单元里，约 **68 个已有等价动作单元**（键鼠、剪贴板、文件、窗口、屏幕、界面、流程、网络基本齐全），约 **15 个部分覆盖**（缺某个分支/某种格式），真正**缺失且值得评估的约 25 个**，其余约 10 个是 Quicker 宿主专有能力，搬过来没有意义。
2. 因此本次借鉴**不是「补一套动作系统」，而是「补几个明确的能力缺口」**。最值得做的只有 **2 个 P0 + 5 个 P1**（§4、§5），其余多数是「有则更好」。
3. 有几个看似诱人的 Quicker 设计**明确不建议照搬**：`operation` 三选一的大杂烩单元、输出靠 UI 手绑变量、`stopIfFail` 逐单元重复、脚本语言堆叠、自定义 XAML 窗口——这些正是 LTools 已经踩过坑并收敛掉的方向（§6）。

## 2. 对照基线：LTools 现有动作单元

以 `Features/Units/BuiltInUnits.cs` 的 `RegisterAll` 与 `Core/Actions/FlowUnits.cs` 为准（本文核对过源码，不靠印象）：

| 分类（`UnitCategories.Order`） | 现有键 |
|---|---|
| 流程 | `flow.set` / `flow.if` / `flow.repeat` / `flow.forEach` / `flow.while` / `flow.break` / `flow.continue` / `flow.stop` / `flow.return` / `flow.delay` / `flow.waitFor` / `flow.lastError` / `flow.lastErrorStep` / `sub.run` |
| 键鼠与输入 | `input.sendKeys` / `input.mouse` / `input.cursor` / `input.clickText` / `input.clickImage` / `input.waitKey` / `input.macro`；`text.send` / `text.selection` |
| 文本与数据 | `text.process` / `text.regex` / `text.encode` / `text.clipboard`；`data.list` / `data.json` / `data.calc` / `data.time` / `data.random`（含 `guid`） / `data.noteWrite` / `data.noteRead`；`state.set` / `state.get` |
| 剪贴板 | `clipboard.files` / `clipboard.image` / `clipboard.history` / `clipboard.clear` |
| 文件 | `file.openFile` / `file.select` / `file.read` / `file.write` / `file.operation` / `file.exists` / `file.hash` / `file.list` / `file.zip` / `file.tempPath` / `file.folderPath` / `file.explorer` / `file.explorerPath` / `file.selection` / `path.parts` / `image.info` |
| 网络 | `web.open` / `web.request` |
| 窗口与程序 | `app.launch` / `app.runCommand` / `app.process` / `app.log`；`window.find` / `window.wait` / `window.foreground` / `window.operate` / `window.message` |
| 屏幕 | `screen.capture` / `screen.pickPoint` / `screen.findImage` / `screen.findText` / `screen.ocr` / `screen.pixel` / `image.transform` |
| 界面 | `ui.toast` / `ui.input` / `ui.confirm` / `ui.select` / `ui.textWindow` |
| 脚本 | `script.ahk` |
| 内置功能（**不是动作单元**，不进左栏） | `common.*` / `ltools.*`（音量、面板、速记等，`FeatureCatalog`） |

**已核实的能力边界**（避免把「已有」误判成「缺」）：

- `data.random` 的 `operation` 已有 `number / text / guid / pick` → Quicker `randomNum` + `newGuid` **已覆盖**。
- `text.encode` 已有 `base64 / unbase64 / url / unurl / html / unhtml`，且支持**文件/图片转 Base64**、写回文件 → Quicker `imgToBase64` **已覆盖**。
- `file.exists` + `file.hash` 合起来覆盖 Quicker `checkPathExists` 的绝大部分输出（存在性、类型、大小、时间、只读/隐藏/系统、.lnk 目标、MD5/SHA1/SHA256/CRC32）。
- `screen.pixel` 取色并输出 `#RRGGBB` + `r/g/b` + 对象 → Quicker `color` 的「取色」部分**已覆盖**，只缺「解析任意颜色串并按指定格式输出」。
- `image.transform`（裁剪/缩放/旋转/灰度/翻转/格式转换）+ `image.info` → Quicker `imgProcess` / `imageinfo` **基本覆盖**。
- `data.calc`（含 `round`）、`data.time`（格式/偏移/时间戳）、`data.json`、`data.list` → Quicker 文本/计算类**基本覆盖**。
- `ui.textWindow` 已区分「只读 / 可编辑」两种模式 → Quicker `showText` **已覆盖且更细**。
- `web.request` 是唯一 HTTP 出口、静态 `HttpClient` + 停止热键中断 → Quicker `http` **已覆盖**（但缺「下载落盘」）。

## 3. 评分模型与筛选口径

只评估**「LTools 缺失或部分覆盖」且「本机可独立运行」**的单元；上游已按 `QUICKER_UNITS_SURVEY.md` §5 排除 Quicker 云服务与第三方软件连接，这里不再重复评估。

- **必要性 N（1–5）**：是不是自动化的基础能力；缺了是否必须绕远路或用脚本硬凑。
- **实用性 U（1–5）**：日常动作里的使用频次、覆盖面。
- **成本 C（1–5，越大越贵）**：是否引入新依赖 / 新窗口 / 新线程模型 / 复杂参数表单；1 = 纯函数几行代码，5 = 整套 UI 子系统。
- **得分 P = N + U − C**，按 P 降序排列；P ≥ 6 为 **P0**，4–5 为 **P1**，2–3 为 **P2**，≤ 1 为**不做**。

评分是人工判读（依据 §2 的源码核对 + 上游文档），不是测量值；排序用于决策，不代表精确工作量。

## 4. 优先级主表（按得分排序）

| # | Quicker 单元 | 能力 | LTools 现状 | N | U | C | P | 级别 | 建议 |
|---|---|---|---|---|---|---|---|---|---|
| 1 | `enc` | 文本/字节哈希、HMAC、AES/DES/RSA 加解密 | 缺：`text.encode` 只有 Base64/URL/HTML，`file.hash` 只对**文件**流式哈希 | 5 | 4 | 2 | **7** | **P0** | 新增 `text.hash` + `crypto.encrypt` + `crypto.decrypt`（**拆开，别做一个大 operation**），见 §5.1 |
| 2 | `waitClipboardChange` | 等待剪贴板内容变化 | 缺：自动触发有 `clipboard.change` 事件源，但**动作内**没有等待步骤 | 4 | 4 | 2 | **6** | **P0** | 新增 `input.waitClipboard`，走 `Timeouts` 三值 + `ctx.Wait`，见 §5.2 |
| 3 | `createQrCode` / `readQrCode` | 生成 / 识别二维码与条码 | 缺 | 3 | 4 | 2 | **5** | **P1** | 拆成 `image.qrcodeGen` / `image.qrcodeRead` 两个单元；识别默认本地、不加在线兜底，见 §5.3 |
| 4 | `htmlExtract` | XPath 从 HTML/XML 提取节点/属性 | 缺：`web.request` 只给文本或 JSON，无 DOM 解析 | 4 | 4 | 3 | **5** | **P1** | 新增 `web.html`，输出 `value` + `list` + `count` + `found`，见 §5.4 |
| 5 | `download` | 下载文件落盘（进度/自动重命名/MD5/ETag） | 缺：`web.request` 适合取响应体，不适合大文件 | 4 | 3 | 2 | **5** | **P1** | 新增 `web.download`；复用 `web.request` 的 `HttpClient` 与停止令牌，见 §5.5 |
| 6 | `uiautomation` / `flauiautomation` | UIA 找控件、触发菜单、设值 | 部分：有 `FlaUI.UIA3` 依赖、`ltools_uia_tree`（只读树）、截图吸附的 UIA 探测，但**没有动作单元** | 4 | 3 | 3 | **4** | **P1** | 新增 `ui.uia`（控件操作）；复用 `Features/Flow/TextLocator` 的区域语义与已有 UIA 探测，见 §5.6 |
| 7 | `numberprocess` | 进制转换、多种取整、千分位/百分比格式化 | 部分：`data.calc` 只有 `round(digits)` | 2 | 3 | 1 | **4** | **P1** | 新增 `data.number`（纯函数、零依赖），见 §5.7 |
| 8 | `showmenu` | 树形 / 图标弹出菜单，选中项带数据 | 部分：`ui.select` 是列表选择，无树形/图标/右键菜单 | 3 | 3 | 3 | **3** | **P2** | 需先定菜单数据形状（本项目没有 `CommonOperationItem` 模型），成本偏高，暂缓 |
| 9 | `color` | 解析 `#RGB`/`ARGB`/`rgb()`/`rgba()` 并转格式 | 部分：`screen.pixel` 已取色并给 `#RRGGBB` + r/g/b | 2 | 2 | 1 | **3** | **P2** | 给 `screen.pixel` 加一个 `format` 参数即可，不必新建单元 |
| 10 | `charInfo` | 字符 Unicode 编码 / 拼音（首字母） | 缺：`Core/Pinyin.cs` 有现成表但只服务工具箱搜索 | 2 | 2 | 1 | **3** | **P2** | 新增 `text.charInfo`，复用 `Pinyin`；成本低但需求窄 |
| 11 | `textCounter` | 行数 / 字符数 / 可见字符数 | 缺 | 2 | 2 | 1 | **3** | **P2** | 新增 `text.count`，纯函数 |
| 12 | `getSysInfo` | 机器名/用户名/系统版本/分辨率/环境变量 | 缺动作单元：MCP `ltools_health` 有 `environment`，但不是步骤 | 3 | 2 | 2 | **3** | **P2** | 新增 `system.info`，只取本机项，**不要**照搬 Quicker 版本/专业版/UnionId 等宿主专有输出 |
| 13 | `computeTime` / `getCurrentTime` | 解析时间串、两时间相减 | 部分：`data.time` 有格式化与 `offsetMs` | 2 | 2 | 1 | **3** | **P2** | 给 `data.time` 补 `parse` / `diff` 分支即可 |
| 14 | `dictOperations` | 词典增删改查 | 部分：`data.json` 与对象值可绕 | 2 | 2 | 1 | **3** | **P2** | 给 `data.json` 补 `set/get/keys` 分支即可 |
| 15 | `form` | 一次收集多个字段 | 部分：`ui.input` 单字段、`ParamInputDialog` 是动作参数框（运行前，不是步骤） | 3 | 3 | 4 | **2** | **P2** | 需要一套表单定义 + 动态控件，成本高；先用多个 `ui.input` 组合 |
| 16 | `winservice` | 服务状态 / 服务列表 / 读注册表 | 缺 | 2 | 2 | 2 | **2** | **P2** | 新增 `system.service` / `system.registry`（拆两个），低优先 |
| 17 | `fileSystemWatch` | 动作内等待文件事件 | 部分：自动触发已有 `file.change` 事件源 | 2 | 2 | 2 | **2** | **P2** | 若要补，优先补「等待文件出现」而非整套监控回调 |
| 18 | `audioControl` / `imeControl` / `keyoperation` / `playSound` / `shelloperation` / `reportProgress` | 系统小能力 | 缺或部分（音量已有内置功能） | 2 | 2 | 3 | **1** | 不做 | 单项成本低但加起来是长尾；按用户实际反馈再排 |
| 19 | `httpserver` / `websocket` / `smtp` | 起服务 / WS / 发邮件 | 缺（MCP 已有本机 HTTP 服务） | 2 | 2 | 3 | **1** | 不做 | `httpserver` 与现有 MCP 服务重叠；`smtp` 需外部账号；先不做 |
| 20 | `manageList` / `tableoperation` | 列表 / 表格可视化编辑 | 缺 | 2 | 2 | 4 | **0** | 不做 | 需整块表格 UI；与 LTools 的抽屉/表单体系不兼容 |
| 21 | `showImage` / `whiteboard` / `pinImage` / `longScreenshot` / `screenRecording` | 图片窗口 / 手写板 / 钉图 / 长截图 / 录屏 | 缺 | 1–2 | 1–2 | 4 | **≤0** | 不做 | 新窗口子系统 + 新交互，收益不明确 |
| 22 | `getChromeUrl` / `webview2` / `customwindow` / `custompanel` / `reactui` | 宿主 UI / 浏览器窗口 | 缺 | 1 | 1 | 5 | **−3** | 不做 | 见 §6 |

> 表中「部分」= 已有等价能力但缺某个分支；补齐一处比新建单元便宜，已写进「建议」列。
> 排序严格按 P 降序：1–2 为 P0，3–7 为 P1，8–17 为 P2，18–22 为「不做」。

## 5. P0 / P1 详述

### 5.1 P0-1 `text.hash` + `crypto.encrypt` / `crypto.decrypt`（源：`sys:enc`）

**为什么**：签名/校验/接口鉴权是自动化常见刚需；现在只能在 `script.ahk` 或表达式里硬凑，或者对**文件**用 `file.hash`，对**文本**没有哈希，也没有 HMAC 与加解密。

**关键约束：不要照搬成一个单元。** Quicker `sys:enc` 用 `operation` 混了「哈希 / HMAC / 对称加密 / 非对称加密 / 自用加密」5 类语义，正是本项目 `file.open` 三合一退役的同一类问题（`AGENTS.md`：「别再往任何一个里加 `operation` 三选一」）。按单一职责拆：

| 建议键 | 输入 | 输出 | 语义 |
|---|---|---|---|
| `text.hash` | `text` / `algorithm`（md5,sha1,sha256,sha512）/ `hmacKey`（可空）/ `encoding`（hex,base64） | `hash` / `text` | 纯摘要；带 `hmacKey` 即 HMAC，仍属「算摘要」 |
| `crypto.encrypt` | `text` / `algorithm`（aes,des,rsa）/ `key` / `iv` / `keyEncoding` | `text`（Base64）/ `hex` | 加密 |
| `crypto.decrypt` | `text` / `algorithm` / `key` / `iv` / `keyEncoding` | `text` / `found` | 解密；失败按 `ActionErrors.Precondition` 标性质 |

- 全部 `UnitKind.pure`，可进干跑；`Outputs` 用 `OutputSpec` 声明（输出声明是唯一来源，决定参数对话框类型徽标与变量表自动登记）。
- 复用 `text.encode` 已有的 Base64/HEX 编解码约定与 `charset` 语义，别另写一套。
- 用 `System.Security.Cryptography`，**不引新 NuGet 包**。
- 校验器：`algorithm` 必须来自 `Options`；`iv` 在对称算法下必填 → 用 `OptionParams` 表达，不写 `VisibleWhen` 手写串。

### 5.2 P0-2 `input.waitClipboard`（源：`sys:waitClipboardChange`）

**为什么**：「复制 → 等结果 → 读剪贴板」是高频流程；现在只能 `flow.waitFor` 手写轮询或靠自动触发，动作内没有一等公民。

- 输入：`timeoutMs`（**必须可见参数，走 `Timeouts` 三值**：`0` 只判一次 / `-1` 一直等 / 有限值夹 1–600000）、`intervalMs`（高级）、`includeRecentMs`（高级）。
- 输出：`changed`（bool）/ `text` / `elapsed` / `found`。
- 语义按**等待类**而非命中类：超时是 `changed=false`，**不报错**（照 `window.wait` / `input.waitKey` 口径）；要不要当失败由调用方用 `flow.if` 判。
- 循环必须 `ctx.EnterIteration()` + `ctx.Wait`，停止热键 500ms 内返回；干跑只判一次。
- 读剪贴板走 `Native/NativeClipboard`（原始 Win32），**不要**用 WinForms/OLE。
- 官方文档的注意事项要写进 `Description`：等待结束后 Ctrl/C 可能还没抬起，后续模拟按键前建议加 100–200ms 延迟。

### 5.3 P1-1 `image.qrcodeGen` / `image.qrcodeRead`（源：`sys:createQrCode` / `sys:readQrCode`）

- **拆成两个单元**（生成 vs 识别，输入输出完全不同，别合并）。
- 生成输入：`text`、`size`、`darkColor` / `lightColor`、`icon`（`InputEditor.image`）、`quietZone`；输出 `file`（png 路径，图片在值模型里一律是 png 文件路径）。目标路径为空时按 `cache/screenshots/` 的运行期截图生命周期处理，或由用户显式给路径——**新增消费图片的入口一律传 `ctx.ResourceRoot`**。
- 识别输入：`image`；输出 `value` / `list`（多码）/ `type` / `found`；找不到 = `found=false`，不报错。**不加在线识别兜底**（上游已按 A 排除在线服务）。
- 需要条码/二维码编解码库（如 `ZXing.Net` / `QRCoder`）；这是**唯一可能引入新依赖**的 P0/P1 项，落地前先确认许可证与包体积，并同步 `AGENTS.md` 的第三方依赖清单。

### 5.4 P1-2 `web.html`（源：`sys:htmlExtract`）

- 输入：`source`（HTML 文本或 `${web.request 的文本输出}`）、`xpath`（必填）、`attribute`（可空）、`encoding`、`mode`（single/list）。
- 输出：`value` / `list` / `count` / `found`（照 `PathListOutput` / 「产出列表」的既有约定：找不到 `found=false`，语法/形状错误才抛）。
- 与 `web.request` 串成「请求 → 提取」两步，不给 `web.request` 加 `operation`（保持「`web.request` 是唯一 HTTP 出口」的单一职责）。
- 需 HTML 解析库（`HtmlAgilityPack` 或 `AngleSharp`）；同样要过依赖评审。

### 5.5 P1-3 `web.download`（源：`sys:download`）

- 输入：`url`、`savePath`、`saveName`、`headers`、`cookie`、`timeoutMs`、`autoRename`、`skipCertVerify`。
- 输出：`file` / `size` / `md5` / `etag` / `found`。
- **复用 `web.request` 的静态 `HttpClient` + `CancellationToken`（与 `ctx.Token` 串联）**，不要另建一套网络栈；大文件用流式 `HttpCompletionOption.ResponseHeadersRead`。
- 失败语义：HTTP 非 2xx = `ActionErrors.NotFound`/`precondition`（按性质标），超时标 `timeout`；停止热键立即中断。

### 5.6 P1-4 `ui.uia`（源：`sys:uiautomation` / `sys:flauiautomation`）

- 现状可复用的地基：`FlaUI.UIA3` 依赖、`Core/Ai/AiUia.cs` 的 `ltools_uia_tree`（扁平元素清单、`depth/limit` 上限、每个属性单独 try/catch）、`Views/ScreenSnapTargets` 的按窗口取整棵子树 + 缓存、`Features/Flow/TextLocator` 的 `ScreenRegion` 语义。
- 建议输入：`window`（标题/类名，走 `WindowFinder`）、`control`（Name / AutomationId / XPath）、`action`（click / setValue / expand / invoke / toggle / getText）、`value`、`timeoutMs`、`intervalMs`、`depth`、`limit`。
- 输出：`found` / `text` / `bounds` / `count`。
- 约束：UIA 在动作线程（MTA）直接调，别包 `StaRunner`（`ltools_uia_tree` 已验证）；等待走 `ctx.Wait`；找不到按 `ActionErrors.NotFound` 标性质，可配 `RetryOn=notFound`。
- 成本主要在「控件定位语义」的设计，建议先只做 `Name` + `AutomationId` + 深度限制的精确匹配，XPath 留到有真实需求再加。
- 2026-11-21 已补最后一环：编辑器「▾ 来源 → 选控件…」（`Views/ControlPickOverlay` + `Views/ControlTreeDialog`），
  一次填好名称 / AutomationId / 类型 / 窗口类名；控件树与 `ltools_uia_tree` 共用 `Features/ControlTargets.cs` 的遍历。
  本节其余取舍（不做 XPath / expand / invoke）仍然有效。

### 5.7 P1-5 `data.number`（源：`sys:numberprocess`）

- 纯函数、零依赖、成本最低的一个。输入：`number` / `text`、`operation`（`toBase` / `fromBase` / `round` / `ceil` / `floor` / `thousands` / `percent`）、`base`（2/8/10/16）、`digits`、`rounding`（`round45` / `banker` / `truncate`）。
- 输出：`number` / `text`（十六进制/二进制/带千分位/百分比文本）/ `hex` / `binary`。
- 建议**不新建分类**，归「文本与数据」；`OptionParams` 表达「哪个 operation 显示哪些参数」。
- 也可顺手把 `data.calc` 的 `round` 统一到同一套 `rounding` 规则，避免两处行为分叉。

## 6. 明确不建议照搬的 Quicker 设计

这一节比「新增哪些单元」更重要——LTools 已经为这些方向付过学费。

| Quicker 做法 | 为什么不建议 | 项目依据 |
|---|---|---|
| 一个单元用 `operation` 混多种语义（`mouse` 19 参数、`fileOperation`、`windowOperations`、`imgProcess`、`chromecontrol` 27 参数） | 参数表单变成「13 个可能字段」且带多套失败语义；LTools 的 `file.open` 三合一已整体退役 | `AGENTS.md`：「别再往任何一个里加 `operation` 三选一」「要第四种打开目标就新建一个动作单元」 |
| 输出靠用户在编辑器里把端口**手绑**到变量；失败靠每个单元一个 `stopIfFail` 布尔 | LTools 已有 `OutputSpec` 唯一来源（决定类型徽标与变量自动登记）、统一 `OnFail` / `Retry` / `RetryDelayMs` / `RetryOn`、`ActionErrors` 5 种性质与单点重试门禁 | `AGENTS.md`：输出声明、失败重试与等待条件、失败性质与「只重试某类失败」 |
| `visibleExpression` / `variableMode` / `$$` / `$=` 各写一套 | LTools 已有唯一实现：`VisibleWhen` + `VisibilityModel` + `OptionParams`、`StepArg` 三态、`ArgFreeze`（非字面量开关保留原引用）、不可见参数运行时不解析 | `AGENTS.md`：参数显示规则唯一实现、冻结状态机 |
| 叠脚本语言（C# / JS / Python） | LTools 已有 `script.ahk`（随包运行时 + 前言接管错误 + 行号换算 + 编码兜底）与表达式引擎；再加语言是重复建设且扩攻击面 | `AGENTS.md`：脚本动作单元 |
| 自定义 XAML 窗口 / 内嵌浏览器 / React UI（`customwindow` / `custompanel` / `webview2` / `reactui`） | 需要整套窗口与前端子系统，且与 LTools 的 `DialogShell` / `WindowSizing` / 主题圆角三档 / 双 STA 线程模型冲突；成本高、收益不确定 | `AGENTS.md`：WPF 约定、窗口尺寸、 Modern.xaml |
| 宿主生态步骤（`quickeroperations` / `runAction` / 动作库分享相关输出项） | 是 Quicker 自身面板 / 动作库能力；LTools 对应的是面板 / 悬浮 / 内置功能，模型完全不同 | `CONTEXT.md`：面板、悬浮项、内置功能 |
| 「失败后停止」与「是否成功」输出耦合 | LTools 的失败已由引擎统一处理，步骤行状态用 `StepRowChip` 的 chip 表达，不往标题/摘要里拼状态 | `AGENTS.md`：步骤行状态是 chip |
| 长截图 / 录屏 / 钉图 / 手写板 | 新窗口 + 新交互 + 新资源生命周期，本次评估里成本最高而必要性最低 | 见 §4 第 21 行 |

**一句话**：借鉴的是「Quicker 提供了哪些能力」这份清单，不是「Quicker 怎么组织这些能力」。LTools 的 `UnitSpec` / `OutputSpec` / 流程单元 / 失败语义 / 可见性模型比 Quicker 更收敛，照搬模型是倒退。

## 7. 落地检查清单（真要加动作单元时逐条过）

按 `AGENTS.md` 的既有约定，新增一个动作单元至少同步这些点：

1. **一个动作单元一个文件**：`Features/Units/<实现领域>/<XxxUnit>.cs`；注册统一在 `Features/Units/BuiltInUnits.cs` 的 `RegisterAll` 加一行（流程控制除外，走 `Core/Actions/FlowUnits.cs`）。
2. **分类顺序只在 `Core/Actions/UnitCategories.Order` 定**，用现有分类，别在编辑器里另排；分类名与实现目录不必一致。
3. **`UnitSpec` 是唯一声明**：`Inputs`（`InputEditor` / `Pickers` / `Description` / `Default` / `Advanced` / `Options` + `Labels` / `OptionParams`）、`Outputs`（`OutputSpec` 的 `Type` 决定类型徽标与变量表自动登记）、`Kind`（`pure` / `action`，干跑判定 `UnitSpec.DryRun`）。
4. **枚举驱动一律写 `OptionParams`**；`VisibleWhen` 只写它表达不了的附加条件；**别写 `operation` 三选一**（§6）。
5. **等待类单元**：`timeoutMs` 必须是可见参数，走 `Timeouts` 三值（`0` / `-1` / 有限）、`Timeouts.Loop` 循环、每轮 `ctx.Wait` + `ctx.EnterIteration`；超时是 `found=false` 不是失败。
6. **失败性质**：语义明确处抛 `ActionErrors.NotFound/Timeout/Precondition`，别按错误文本猜；`aborted` 不进重试范围。
7. **UI 副作用**：WPF 对象只能在 UI 线程碰（`Ui.Post` / `Dispatcher.Invoke`）；流程在后台 MTA，提示一律 `ui.toast`，别弹 `MessageBox`。
8. **引用型资源**：若新单元会引用图片/宏/文件，在 `ImageLibrary.CollectAction` / `MacroStore.CollectGarbage` / `ProgramWalker` 按参数名接上，否则删动作后资源成孤儿。
9. **文本与显示**：新增枚举值先补 `Core/Actions/OptionLabels.cs`；步骤摘要补 `StepSummary`；变量类型中文名走 `ValueKindText.Of`。
10. **自检**：改引擎 / 校验器 / `StepText` / 动作单元声明时**先加或改 `Core/SelfTest` 断言**；日常回归 = 构建 0 错 0 警 + `LTools.exe --selftest` 全绿 + 真机点一遍相关路径（本项目**没有测试项目**，不要找 `dotnet test`）。
11. **给 agent 看的知识包**：`Core/Ai/KnowledgePack` 从 `StepRegistry` 自动生成 `units.md`，**别手改**；改了 `UnitSpec` 后三份文件会按内容比对重写。
12. **术语**：若引入 `CONTEXT.md` 表里没有的词，先确认一句并当场补进 `CONTEXT.md` → `AGENTS.md` → 代码标识符全链路；**不做向后兼容**（不写迁移器、不认旧字段、不留别名）。
13. **文档**：改了动作系统事实同步 `docs/ACTION_SYSTEM.md`；本文与 `QUICKER_UNITS_SURVEY.md` 是外部调研，不随实现改动而「修正对方的事实」。
14. **提交**：按仓库约定，提交前先给改动清单 + 提交信息、等批准；`git push` / 打 tag 各自单独批准。

## 8. 建议的推进顺序

```text
P0-1 加密/哈希（拆 3 个纯函数单元）   ← 无新依赖，先做
P0-2 等待剪贴板变化                    ← 无新依赖，先做
   ↓ 跑 --selftest + 真机各点一遍
P1 数字处理（纯函数，顺手做）
P1 二维码（需评审新依赖许可证/体积）
P1 HTML 提取（需评审新依赖）
P1 文件下载（复用 web.request）
P1 UIA 控件操作（需先定控件定位语义）
   ↓ 每一批都是：新增单元 → 注册 → 自检断言 → 构建 0 警 → selftest 全绿 → 真机
P2 及以下：等真实需求，不做预研式铺开
```

> 本文只给评估与建议，**不包含已批准的实现排期**；任何落地都要先按 `AGENTS.md` 走「改动清单 + 提交信息待批准」的流程。
