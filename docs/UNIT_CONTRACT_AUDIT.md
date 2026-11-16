# 动作单元契约审计（只出报告）

> 本文**不改变任何动作单元的行为**。每条「建议」都等拍板后单独提交，不批量改。
> 数据来自 `StepRegistry.Default` 的声明扫描（92 个单元 / 316 个输入 / 301 个输出），
> 判定标准只有一条：**会不会让 agent 或用户在写动作时做错决定**。
> 「一个单元有多个 operation」本身不算问题，除非它让人无法从摘要里读懂这一步在干什么。

## 0. 结论

- **没有需要拆分的单元**。`data.list`（13 个 operation）、`text.process`（12 个）、
  `input.mouse`（11 个）、`data.calc`（10 个）都是「同一份数据的操作」，
  共用输入键名、由 `OptionParams` 收起无关参数，agent 只需记 1 个单元名 + 2 个参数名。
  拆开只会让步数变多、索引变长。
- 真正该修的是**「声明的输出 vs 实际写几个」**：一个单元声明 4 个输出，
 每个 operation 只写其中 1~3 个，**没写的不会清空**（引擎不预清），
  于是 `${found}` 读到的是上一步留下的旧值。这是 agent 最容易踩、也最难自查的坑。
- 其次：5 个单元的 `found` 恒为 `true`（命中类没有「没找到」这一态）、
  19 个输入不限类型（其中 2 个可以收紧成文本）、
  `OptionLabels` 里 `move` 登记了两次，导致 `file.operation` 的下拉显示「移动到」。
- 参数命名、说明缺失、选项中文名三项：**不改**。理由见 §4 / §8。

## 1. 扫描面

| 项 | 数量 |
|---|---|
| 动作单元 | 92（`action` 48 / `pure` 44） |
| 输入参数 | 316 |
| 输出 | 301 |
| 带子步骤的单元 | 4（`flow.if` then/else；`flow.repeat` / `flow.forEach` / `flow.while` body） |
| 显式声明干跑的单元 | 16 |
| 枚举型输入 | 65 处（其中 29 个是「决定做什么」的驱动键，36 处是取值表） |

按分类：键鼠与输入 10、文件 16、文本与数据 18、流程 14、界面 6、窗口与程序 8、
屏幕 9、网络 4、剪贴板 5、脚本 2。

复现：反射扫 `LTools.dll` 的 `StepRegistry.Default.Units`，逐项读 `UnitSpec` /
`InputSpec` / `OutputSpec`（脚本在仓库外，不随包分发）。

## 2. 一单元多语义：不拆

驱动键（`operation` / `action` / `mode` / `kind`）选项数排前 8 的：

| 单元 | 驱动键 | 选项数 | 被选项收起的参数 | 判定 |
|---|---|---|---|---|
| `flow.if` / `flow.while` / `flow.waitFor` | `op` | 19 | 11 | 三处共用同一张比较表，**是复用不是重复**；不拆 |
| `data.list` | `operation` | 13 | 21 | 同一份列表的查询 / 变换 / 聚合；不拆 |
| `text.process` | `operation` | 12 | 23 | 同上；`split` / `join` 方向相反但靠 `text` / `list` 两个输入键区分，摘要能读懂 |
| `input.mouse` | `operation` | 11 | 29 | 积木式坐标操作，共用 `x` / `y` / 按钮；不拆 |
| `data.calc` | `operation` | 10 | 1 | 纯算术；不拆 |
| `text.encode` | `operation` | 6 | 2 | 编解码；不拆 |
| `image.transform` | `operation` | 6 | 7 | 图片变换；不拆 |
| `ui.uia` | `action` | 6 | 1 | **唯一值得看第二遍的**：`click` / `setValue` / `focus` 是注入，`getText` 是读取 |

`ui.uia` 的注入与读取放在一个单元里，但两者都在执行前先「找到控件」，
找不到同样按 `NotFound` 失败，**失败语义只有一套**，所以不构成「两套语义」的问题。
其余枚举（`encoding` / `charset` / `algorithm` / `position` / `folder` / `terminal` …
共 36 处）是取值表，不是语义分支。

**结论**：0 个单元需要拆分。真要减少「想不起哪个单元」的成本，
靠索引的 `produces` / `consumes` 反查与 `examples.md` 的常用链路，不靠拆单元。

## 3. 同一单元里「半写」的输出（最值得修）

### 现象

引擎在步骤执行前**不预清**输出变量（`ActionEngine.ForStep` 只在动作单元
`SetOutput` 时才写回 `Scope`），所以「声明了但这次 operation 没写」的输出，
读到的是**这一步之前**的值。

| 单元 | 输出 | 写它的 operation | 不写它的 |
|---|---|---|---|
| `data.list` | `found` | `indexOf`、`contains` | 其余 11 个 |
| `data.list` | `text` | `get`、`join`、`sum`、`avg` | 其余 9 个 |
| `data.list` | `number` | `count`、`sum`、`avg`、`split`、`indexOf`、`sort`、`unique`、`reverse`、`add`、`removeAt` | `get`、`join`、`contains` |
| `text.process` | `found` | `contains`、`startsWith`、`endsWith` | 其余 9 个（**包括 `indexOf`**） |
| `text.process` | `list` | `split` | 其余 11 个（`join` 只读 `list`，不回写） |
| `text.process` | `number` | `split`、`indexOf` | 其余 10 个 |
| `text.process` | `text` | 全部 | — |

顺带暴露一处不一致：`data.list` 的 `indexOf` 会写 `found`，
`text.process` 的 `indexOf` 只写 `number`（找不到时是 `-1`）。两个同名操作，
一个能配「如果 找到」，一个不能。

### 后果

agent 写「列表里找一下有没有 X，有就……」，最自然的写法是：

```text
data.list operation=indexOf -> found=hasX
flow.if op=found left=${hasX} ...
```

`contains` / `indexOf` 这条没问题。但写完 `indexOf` 之后再插一步 `sort`（也映射 `found`），
或者把 `get` 的 `found` 映射给同一个变量，`${hasX}` 就变成上一步的旧值 —— 而且**不报错**。
运行时看起来「条件判断忽然不生效」，排查方向会跑到表达式上。

### 三个选项（待拍板）

| 方案 | 做法 | 代价 |
|---|---|---|
| A | 引擎在 `Execute` 前把本步 `out` 映射到的变量清成空 | 改动在引擎核心，影响所有单元；`flow.set` 这类「读写同一变量」的步骤要特别小心（参数已在清空前求值，理论可行但需要专门回归） |
| B | 让两个高频单元（`data.list` / `text.process`）每个 operation 都写全部输出（没意义时写空 / 0 / false） | 局部、低风险；代价是「没找到」与「没这一项」在值上不可区分，要靠 `count` 判 |
| C | 只在知识包写清「哪些 operation 写哪些输出」 | 零风险；但 agent 得读 `units_get` 才看得到，索引层面无从得知 |

**建议**：C 先做（写进 `units.md` 的输出说明，动作单元声明里改 `OutputSpec.Description` 即可，
不新增机制），B 只对 `data.list` 的 `found` 做一次一致性选择（`indexOf` / `contains` 之外也写
`false`），A 不进本轮。

## 4. 参数命名：不改

| 输入键 | 用在几个单元 | 说明 |
|---|---|---|
| `operation` | 19 | 驱动键，统一 |
| `text` | 18 | 主体是文本 |
| `timeoutMs` / `intervalMs` | 17 / 8 | 等待类统一 |
| `path` | 13 | 主体是路径 |
| `workdir` | 10 | 统一 |
| `rect` / `region` / `image` | 6 / 5 / 5 | 屏幕区域语义统一 |
| `value` | 6 | 通用值（`flow.set` / `state.set` / `data.list` / `data.jsonEncode` / `data.number` / `ui.uia`） |
| `body` | 2 | `web.request` 请求体、`data.noteWrite` 正文 |
| `html` / `code` / `items` / `list` | 1~4 | 各自的领域名 |

判定：

- **按领域取名的地方是对的**：路径叫 `path`、网址叫 `url`、图片叫 `image`、宏叫 `macroId`。
  强行统一成 `value` 反而让摘要变成「值 · 值 · 值」。
- **通用值有三处不同叫法**：`text`（18）、`value`（6）、`body`（2）。
  这不是缺陷 —— `text` 表示「这段文本本身」，`value` 表示「任意类型的值」，
  `body` 是 HTTP 术语。真要统一，改动会穿透已保存的动作、`StepText` 文本与全部文档，
  **收益低于破坏面**。
- **约定**：新增单元里，主体是文本就用 `text`，可能是列表 / 对象 / 数字就用 `value`
  （`data.jsonEncode` 已按这个来）。

## 5. 恒为 `true` 的 `found`

| 单元 | 语义 | `found` 什么时候会 `false` |
|---|---|---|
| `crypto.encrypt` | 加密成功即返回 | 从不 |
| `state.set` | 写入成功即返回 | 从不 |
| `web.download` | 下载成功即返回 | 从不 |
| `ui.uia` | 命中类：找不到就按「未找到」失败 | 从不（声明说明里已写「不会给假」） |
| `ui.textWindow` | `show` / `close` 一定成功；`read` 看 `confirmed` | `show` / `close` 从不 |

判定：这是**命中类的正常形态** —— 「没找到」已经是失败，没有中间态可报。
`ui.uia` 的说明写得对，其余 4 个的说明没解释。

建议：只有一处要动 —— 在这 4 个单元的 `found` 说明里补一句
「这一步成功就返回 `true`；失败会整步报错，不会有 `false`」，
让 agent 不把它当返回值用。**不删输出**：删了会让已保存的动作里 `${xxxFound}` 断链。

## 6. 类型声明

### 6.1 输入不限类型（19 处）

| 输入 | 判定 |
|---|---|
| `flow.set.value` / `flow.if.left/right` / `flow.while.left/right` / `flow.waitFor.left/right` / `state.set.value` / `state.get.defaultValue` / `data.list.value` / `data.jsonEncode.value` | **故意**：值类型由调用方决定 |
| `flow.forEach.item`（输出） | **故意**：项类型跟着容器走 |
| `app.log.text` | **故意**：任意值按 `AsText` 记日志 |
| `ui.confirm.text` / `ui.select.text` / `ui.textWindow.text` | **故意**：允许塞列表 / 对象，靠 `AsText` 显示 |
| `screen.ocr.image` / `screen.findImage.image` | **故意**：可传截图输出的对象（取 `text` 成员） |
| `clipboard.image.path` / `image.info.path` | **可收紧成 `text`**：编辑器已是 `InputEditor.image`，`Coerce(null)` 与 `Coerce(text)` 行为完全一致（`AsText()` 同样会取对象的 `text` 成员）；声明成 `any` 的副作用是 `consumes:"text"` 反查漏掉它们 |

### 6.2 输出不限类型（5 处）

`flow.forEach.item`、`script.csharp.value`、`data.json.value`、`state.set.value`、
`state.get.value` —— **全部故意**（存进去什么类型，拿出来就什么类型）。

## 7. 说明与中文名

- 输入参数 316 个，**112 个没写 `Description`**，走 `InputHint` 的按类型默认说明
  （「文本 / 数字 / 文件路径 / 屏幕区域 / 变量名…」）。23 个单元一个参数的说明都没写：
  `flow.set` `flow.repeat` `flow.forEach` `flow.delay` `sub.run` `ui.toast` `ui.input`
  `text.selection` `file.select` `file.read` `file.write` `file.operation` `app.launch`
  `app.process` `script.ahk` `window.find` `window.operate` `screen.capture` `screen.ocr`
  `screen.pixel` `data.noteWrite` `data.noteRead` `data.time`。
  判定：**不是缺陷** —— 项目约定「默认说法不够时才补 `Description`」，这 23 个都是
  「文件名就叫它要填什么」的直白参数。要补也只补有坑的那几个（`file.operation` 的 `overwrite`、
  `app.launch` 的 `args`）。
- 输出 301 个，**0 个缺说明**。
- `OptionLabels` 126 个键（去重 125），**0 个选项缺中文名**（各单元的 `Labels` 覆盖另算）。
- `OptionLabels` 里 `move` 登记了两次（第 70 行「移动」= 文件操作，第 90 行「移动到」= 鼠标），
  后者覆盖前者 → **`file.operation` 的下拉显示「移动到」**（应为「移动」）。
  一行可修：给 `FileOperationUnit` 的 `operation` 加 `Labels: { ["move"] = "移动" }`。

## 8. 已确认不做

- 不加「类型转换」动作单元（`data.convert`）：转换发生在参数绑定与变量写入两个边界，
  结构化互转已有 `data.json` / `data.jsonEncode`。
- 不给参数加 `accepts`（「我能接受哪些类型」）：`ValueKind? Type` 已是目标类型，
  加第二个类型字段会让「这个参数要什么」出现两种说法。
- 不拆 `data.list` / `text.process` 的 operation：同一份数据的操作，拆开只让步数变多。
- 不在校验器里加 info 级提示：「能跑但有更好写法」的建议会淹掉 error / warning。

## 9. 建议清单（按收益 / 风险排序，逐条待拍板）

| # | 建议 | 代价 | 风险 | 收益 |
|---|---|---|---|---|
| 1 | `data.list` / `text.process` 的输出说明写清「哪个 operation 写哪个输出」 | 两个文件里的 `OutputSpec.Description` + `units.md` 自动同步 | 无 | agent 不再赌「`${found}` 有没有更新」 |
| 2 | `data.list` 的 `found` 保持一致（`indexOf` / `contains` 之外的 operation 写 `false`） | 一处改动 + 2 条断言 | 低（值上更诚实） | 「读了旧值」不会静默发生 |
| 3 | `text.process` 的 `indexOf` 补写 `found`（与 `data.list` 对齐） | 一处改动 + 1 条断言 | 低 | 两个同名操作行为一致 |
| 4 | `file.operation` 的 `move` 加 `Labels` 覆盖 | 一行 | 无 | 下拉不再显示「移动到」 |
| 5 | 4 个恒 `true` 的 `found` 补说明 | 4 行 | 无 | agent 不把它当返回值 |
| 6 | `clipboard.image.path` / `image.info.path` 收紧成 `text` | 2 行 | 无（行为等价） | `consumes:"text"` 能反查到 |
| 7 | `app.process` 的 `kill` 读回进程是否真的退出（现在 `catch {}` 吞掉失败） | 一处改动 | 中（有些进程杀了会残留子进程 / 权限拒绝） | 不假装成功 |
| 8 | 引擎在步骤执行前预清 `out` 映射到的变量 | 引擎核心 + 全量回归 | 高 | 彻底消除旧值；但要先确认 `flow.set` 自我赋值、循环里累加不受影响 |

第 1~6 条都是「声明层」，合计不到 10 行改动、不动运行时语义；
第 7、8 条动行为，建议单独一轮。
