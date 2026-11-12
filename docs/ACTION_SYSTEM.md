# 动作系统重构方案：动作单元 · 类型化变量 · 子动作

> 目标：把现在「一个 `ActionDto` 装下所有类型 + 一个 300 行 switch」的结构，换成
> **动作单元（Unit）→ 步骤（Step）→ 动作（Action）** 三层，变量带类型，并引入可复用的子动作（函数）。
>
> 已定方向：**一次性重写** / 子动作 = **全局库**（动作内子动作已于 2026-11-10 删除，见 §3.5）/ 变量 = **5 种类型**。
> 本文件是方案与施工图，不含实现。证据见 §2。
>
> **状态（2026-11-07）**：W1 内核 + W2 全量动作单元已落地（`Core/Actions/` + `Features/Units/`，
> 36 个动作单元 + 9 个流程动作单元），无头冒烟 395 项断言通过，面板 UI 冒烟无回归。
>
> **状态（2026-11-10）**：旧格式迁移层已整体删除（见 §3.7 的说明）。本文 W3 / W5a / W5b 里出现的
> `Features/Migration/*`、`ActionDtoLegacy`、`LegacyType`、`FlowTypes` / `FlowMigration`、
> `Features/Units/LegacyFlow` 均**已不存在**；现在只有 `ActionDto.Program` 一条执行路径，
> 跨值模型取值的适配是 `Features/Units/ProbeContext`（`Of` / `Evaluate`）与 `UnitValues`。
>
> 上一代动作系统的说明在 `docs/ACTION_SYSTEM_PLAN.md`，本文档取代它。

---

## 0. 结论先行

1. **值得抄 Quicker 的**：动作单元声明式元数据（输入/输出规格 → 由它生成 UI 和校验）、
   `步骤.输入参数 = 字面量 | 变量` 的两态、控制流用「嵌套子步骤数组」而不是节点图、
   子程序 = 变量打 `IsInput/IsOutput` 标记、模块粒度停在「一类操作 + operation 参数」。
2. **不要抄的**：19 种变量类型、`$$`/`$=` 两套求值语法并存、表格/窗体/自定义类型、
   网络共享子程序、调试器级观测事件。
3. **路线：一次性重写**，但要有一个「切流点」：新内核、动作单元、迁移、编辑器全部做完并通过
   等价性验收后，**一个提交切换**，随后删除旧实现。开发期旧代码不动、应用始终可构建。
4. 重写后的模型只有**一个联合**（`Value`），而不是今天的四个
   （`ActionDto` / `FlowNode` / `Probe` / `VarValue`）。

---

## 1. 现状（LTools）

| 层 | 现状 | 问题 |
|---|---|---|
| 数据 | `ActionDto` 是扁平联合：`Type` 枚举 17 种 + 所有类型字段平铺（`Target`/`Url`/`Cmd`/`Text`/`WindowOp`/`Image`…） | 新增一种动作 = 改 DTO + 改编辑器 + 改执行器 + 改摘要，四处散落 |
| 执行 | `ActionExecutor.Run` 一个 `switch (action.Type)` 覆盖 17 个分支 | 一个文件承担全部职责；分支之间共享私有方法，边界模糊 |
| 组合 | `ActionDto.Flow: List<FlowNode>`，`FlowNodeKind` 7 种 | 每个节点又各自带一个扁平对象（`SetNode`/`IfNode`/`LoopNode`），第三套联合 |
| 取值 | `Probe` + `ProbeKind` 16 种，扁平字段 | 第四套联合；与流程节点重复（「文字点击」≈「找文字 + 鼠标点击」） |
| 值 | `VarValue`：`Text` 必填 + 可选 `Number`/`Flag`/`X`/`Y`/`W`/`H`/`Score`/`File`… | 无类型；属性靠 `Property("x")` 字符串 switch；列表/对象无法表达 |
| 复用 | 组合动作步骤是**内嵌动作快照**（`StepDto.Action.Clone()`） | 改一处要改 N 处；没有「函数」概念 |

一个典型动作经过四套联合模型，这是「职责不分明」的根：**同一种能力在四处各写一遍**。

---

## 2. Quicker 的做法（安装产物证据）

分析对象：`C:\Program Files\Quicker`（2026-11-06 构建）。

| 产物 | SHA-256（前 16） | 用途 |
|---|---|---|
| `Quicker.StepEngine.Core.dll` | `a8e7ca905663dce5` | 步骤引擎契约：参与者、参数定义、枚举 |
| `Quicker.StepEngine.dll` | — | 动作/子程序/变量的运行时模型 |
| `Quicker.Headless.Designer.Contracts.dll` | — | 设计器 gRPC 契约（变量、步骤、子程序） |
| `Quicker.ExpressionEngine.dll` | — | 表达式求值（`$=`） |

### 2.1 数据模型（三层）

```
XAction      { LimitSingleInstance, SummaryExpression, SubPrograms[], Variables[], Steps[] }
SubProgram   { Id, Name, Icon, Description, SummaryExpression,
               Variables[], Steps[], SubPrograms[], SharedId, IsProtected, ... }
ActionStep   { StepRunnerKey, InputParams: map<string, ActionStepParam>,
               OutputParams: map<string, string>,          // 输出名 → 变量名
               IfSteps[], ElseSteps[],                      // 控制流 = 嵌套子数组
               Note, Disabled, Collapsed, DelayMs }
ActionStepParam { VarKey?, Value? }                        // 二选一：取变量 / 字面量
ActionVariable  { Key, Type, DefaultValue(字符串), ParamName,
                  IsInput, IsOutput, InputParamInfo, OutputParamInfo,
                  IsLocked, Desc, SaveState, Group, TableDef, CustomType }
```

关键点：

- **子程序与动作同构**（都是 `Variables + Steps + SubPrograms`），差别只是子程序的变量带
  `IsInput` / `IsOutput` 标记。
- **控制流不是节点图**：`if` / 循环是普通步骤，分支体是步骤自己的 `IfSteps` / `ElseSteps`。
  「步骤列表」是唯一的结构，编辑器和执行器都只需处理一种递归。
- `StepType` 只有 6 种：`Action / If / Loop / Keyboard / Comment / SubProgram`。

### 2.2 步骤的声明式定义

每个模块用特性声明输入/输出（`InputParamAttribute` / `OutputParamAttribute`），字段包括：

`Key, Name, DisplayOrder, Description, Help, Type(VarType), DefaultValue, NewStepDefaultValue,
DefaultVariableName, IsRequired, VariableMode, InputSources, EditorHint, IsMultiLine,
IsControlField, ValidForList, ValidForProvider, SelectionItems, TextTools, ReplaceMode,
IsAdvanced, SkipEval, AllowInput, VisibleExpression, …`

→ **接口只声明一次**，编辑器 UI、参数校验、摘要、AI 说明全部从这份元数据派生。
这是 Quicker 能容纳 119 个模块而不失控的原因。

### 2.3 参数的来源与去向

```
InputType  = Default | Variable | Value | Expression | Interpolation | Clipboard
OutputType = None | Clipboard | Variable | DictKey
```

### 2.4 变量类型（19 种）

`Text, Number, Boolean, Image, List, DateTime, Keyboard, Mouse, Enum, Dict, Form,
Integer, Table, FormForDict, Specified, Object, Any, NA, CreateVar`

你本机的真实动作印证了枚举顺序：`type:0` = 文本，`type:4` = 列表，`type:2` = 布尔
（`_actionBackups\2026\11\06\按后缀名整理文件`）。

### 2.5 模块粒度

`Quicker.dll` 里 `sys:` 开头的键 317 个，去掉本地化变体（`…T`）后 **119 个模块**，
分 16 类。注意它**没有把一个微操作拆成一个模块**：`sys:stringProcess` 一个模块内部用
`operation` 参数区分替换/截取/大小写…，`sys:fileOperation` 同理。粒度 = **一类操作**。

### 2.6 子程序（文档 + 契约）

- 三种：**动作内子程序**（随动作存）、**公共子程序**（本地全局）、网络共享子程序。
- 输入输出：变量上的两个标记 `IsInput` / `IsOutput`，运行时进入时赋值、结束时读出。
- 作用域：子程序内变量独立，每次调用重新初始化；列表/词典按引用传递。
- 调用步骤的输入参数里有 `subProgramKey`；调用方按子程序声明的输入逐个绑定。

### 2.7 参数求值（文档）

- `$$` 开头 → 插值（结果仍是文本）；`$=` 开头 → 表达式（任意类型）；否则字面量；
- 最后按目标参数类型做一次转换（布尔/数字会再做一次公式解析）。

---

## 3. 目标模型

### 3.1 三层职责

| 层 | 是什么 | 谁负责 |
|---|---|---|
| **Unit（动作单元）** | 一类操作的**定义**：键、名称、分类、图标、输入规格、输出规格、执行函数 | 每个动作单元一个类 + 一份声明，`Features/Units/<分类>/` |
| **Step（步骤）** | 动作单元的**一次使用**：`unit` + 参数（字面量/变量/内联取值）+ 输出映射 + 子步骤 | 纯数据，可序列化 |
| **Action（动作）** | 触发器 + 变量表 + 步骤列表（+ 元数据） | 组合而非继承 |

动作单元内部再分「操作变体」（`window` 的 activate/maximize/resize）——**一个动作单元 = 一类操作**。

### 3.2 变量与值

```csharp
enum ValueKind { Text, Number, Bool, List, Object }

sealed class Value {
    ValueKind Kind;
    string? Text;  double Number;  bool Flag;
    List<Value>? Items;  Dictionary<string, Value>? Members;
}
```

- **文本 / 数字 / 布尔 / 列表 / 对象** 五种。
- 隐式转换只保留「文本 ⇄ 数字」「文本 ⇄ 布尔」，且只发生在**绑定参数**时；
  运行时不做隐式类型提升，避免「1 + "1" = 11」这类意外。
- 「坐标 / 区域 / 匹配结果」不再是 `VarValue` 的一堆可选字段，而是**对象**：
  `{ x, y, w, h, cx, cy, found, score }`；属性访问统一 `${m.x}`。
- 变量声明带类型 + 默认值；运行期 `RunContext` 里是 `Dictionary<string, Value>`。
- 对象的隐式取用：`AsText()` 取 `text`、`AsNumber()` 取 `number`、`AsBool()` 取 `found` / `flag`，
  所以「有结果」的动作单元都带 `text` 成员时，`${变量}` 与旧 `VarValue.Text` 一致。
- 表达式只支持**模板 + 成员/下标访问**（`${a.b}`、`${list[0]}`、`${list.count}`），
  不引入完整表达式语言；需要计算结果时用「计算」动作单元。
- 列表写进文本只有一套规则（`Core/Actions/ListText.cs`）：换行 / 逗号都算分隔符
  （`a,b` 与每行一项等价），项里要带逗号或要嵌套就写 JSON 数组；**绑定时才拆成列表**，
  `${列表}` 仍是引用、不会被再拆一次；空文本 = 空列表。

### 3.3 动作单元声明（关键接口）

```csharp
sealed record InputSpec(
    string Key, string Label, ValueKind? Type,
    InputEditor Editor = InputEditor.text,      // text / multiline / code / number / boolean /
                                                // enumList / file / folder / image / region / variable / condition
    bool Required = false, Value? Default = null,
    string? VisibleWhen = null,                 // 例如 "operation == 'resize'"，多个条件用 || 连接
    IReadOnlyList<string>? Options = null,      // 枚举的英文键
    IReadOnlyDictionary<string, string>? Labels = null,
    bool Raw = false,                           // 代码参数：不做 ${变量} 模板渲染
    string? Description = null);                // 输入框下方的「该填什么」

sealed record OutputSpec(
    string Key, string Label, ValueKind? Type,
    string? DefaultVariable = null, string? Description = null);

enum UnitKind { action, pure }                  // pure = 只算值，可在值位置内联

interface IStepUnit {
    UnitSpec Spec { get; }                       // 静态元数据，只声明一次
    void Execute(StepContext ctx, StepArgs args);
}
```

编辑器、参数校验、步骤摘要全部由 `Spec` 生成；**新增一个动作单元 = 新增一个类 + 一行注册**。

**参数说明**：每个输入框下方都有一行「该填什么」（`Core/Actions/InputHint`）。动作单元写了
`Description` 就用它（单位、格式、可填哪些值），没写时按 `Type` / `Editor` 给默认说明
（文本 / 数字 / 文件路径 / 屏幕区域…）；下拉与开关本身说明了选项，默认不加说明。

**取值动作单元（Pure）**：今天的「取值器」并入这套模型，但标记为 `UnitKind.Pure`，
可以出现在任何「值」的位置（参数、条件左右值），不需要先落成一个步骤。
这解决了「条件怎么写」：条件 = `左值(值源) + 比较符 + 右值(值源)`，
值源可以是变量、字面量，或一个内联的 Pure 动作单元（找图 / 找文字 / 取色…）。

### 3.4 存储格式（schema v2）

```jsonc
{
  "id": "01arz3ndektsv4rrffq69g5fav", "name": "整理下载目录", "icon": "folder",
  "variables": [
    { "key": "files", "type": "list",   "default": [] },
    { "key": "n",     "type": "number", "default": 0 }
  ],
  "steps": [
    { "unit": "file.select", "in": {}, "out": { "path": "files" } },

    { "unit": "flow.forEach", "in": { "items": { "var": "files" } },
      "out": { "item": "file", "index": "i" },
      "body": [
        { "unit": "file.operation",
          "in": { "operation": "copy", "path": { "var": "file" }, "target": "D:\\归档\\${i}" } }
      ] },

    { "unit": "flow.if",
      "in": { "left": { "var": "n" }, "op": "greater", "right": 0 },
      "then": [ { "unit": "ui.toast", "in": { "text": "已整理 ${n} 个文件" } } ] }
  ]
}
```

参数值三态：`"字面量"`（含 `${}` 模板）/ `{ "var": "name" }` / `{ "unit": "...", "in": {…} }`（内联取值）。
比 Quicker 的 `{value}/{varKey}` 多一个「裸字符串即字面量」的写法，手写配置更省事。

### 3.5 子动作（函数）

**只有一个存放位置：全局库**（`<数据根>/subactions/<id>.json`，`SubActionStore`，
与 `macros/*.json` 同一套约定：文件名就是 id，元数据都在文件内，没有嵌套）。

```csharp
sealed class SubActionDef {
    string Id;                     // 全局 "s1"（= 文件名）
    string Name, Icon, Description;
    List<VariableDef> Variables;   // 带 Direction: input / output / local
    List<Step> Steps;
}
```

动作内子动作（`ActionProgram.SubActions[]`）与「子动作里再嵌子动作」都已删除：
它们没有编辑入口，却让读取子动作要先扫动作体、再递归找嵌套。现在
`ActionProgram` 里只有变量表与步骤，`SubActionStore.Load(id)` 是唯一的解析入口。

调用动作单元 `sub.run`：**参数名就是子动作的输入变量名**（`DynamicInputs`），
输出按步骤的 `out` 映射写回调用方：

```jsonc
{ "unit": "sub.run",
  "in": { "subAction": "s1", "input": { "var": "text" } },   // input = 子动作里 Direction=input 的变量
  "out": { "output": "clean" } }                             // output = Direction=output 的变量
```

- 每次调用新建作用域，内部变量初始化；**值语义**（`Value` 不可变，列表 / 对象也是复制一份传进去，
  与 Quicker 的「按引用」不同）——要带回改动就声明成输出变量。
- 调用栈 + 递归深度上限（默认 8）；错误信息带 `动作 → 子动作 → 子动作`。
- 编辑全局子动作影响所有调用方（这是「函数」应有的语义）。
- 删除前列出引用位置（`SubActionStore.FindReferences` / `ReferenceIndex`，按「动作名 · 步骤标签」）。
- 子动作里的 `flow.return`（返回）结束当前子动作，已赋值的输出照常带回调用方；
  它只能出现在子动作里，跑到动作顶层是明确报错（静态校验也会拦）。
- 子动作里的 `flow.break` / `flow.continue` 只对子动作自己的循环有效：跑到调用方的循环上
  会转成「不在子动作自己的循环里」的错误，不会悄悄穿透。

**与动作互转**（`Core/Actions/SubActionTransfer.cs`）：两边同构，差别只在变量的「方向」。
两个方向都把变量统一成 `local`——自动猜「哪些是输入 / 哪些是输出」只会猜错（同一个变量常常既读又写），
让用户在变量表里改更省事；id 与图片不搬（子动作没有自己的 `files/`）。

- 子动作 → 动作：子动作库右键「另存为动作…」→ 打开动作编辑器（名称 / 图标可改）→ 新建动作。
- 动作 → 子动作：面板 / 动作管理磁贴右键「另存为子动作…」→ 打开子动作编辑器（标输入 / 输出）→ 落 `subactions/`。
- 两边都是「另存一份」，原对象不动，之后各走各的。

**AI 写入**（MCP）：`ltools_subactions_check(program)` / `ltools_subactions_write(program, expectedRevision?)` /
`ltools_subactions_delete(id)`，与 `staging_promote` 同一套两道闸（`AllowWrites` + 审批模式）。
写前跑 `ActionValidator.ValidateSubAction`（有 error 拒绝），`expectedRevision` = 文件内容 sha256（CAS）。
校验在动作那套规则之外补三条：`flow.return` 只能出现在子动作里、输出变量必须有人赋值（warning）、
自调用成环（按「正在写的这一份」算，不必先落盘）。

### 3.6 动作单元清单（W2/W3 已实现）

> 分类名以代码 `UnitSpec.Category` 为准：2026-11 起重排为「键鼠与输入 / 流程 / 文本与数据 / 剪贴板 / 文件 / 网络 / 窗口与程序 / 屏幕 / 界面 / 脚本」（顺序见 `Core/Actions/UnitCategories`）。下表保留当初实现时的分组。

| 分类 | 动作单元（W2 已实现） | 对应今天的 |
|---|---|---|
| **文本** | `text.selection` 取选中文字 | Probe.selection |
| | `text.clipboard` 读写剪贴板（纯文本 / HTML / RTF） | setClipboard / Probe.clipboard |
| | `text.send` 发送文本 | sendText |
| | `text.process` 去空白/大小写/替换/截取/分割/拼接/查找 | 新增（合并零碎操作） |
| | `text.regex` 正则全部命中（可指定分组）→ 列表 | 新增（W8） |
| **文件** | `file.openFile` 用默认程序打开文件（旧的三合一 `file.open` 已退役） | openFile |
| | `file.select` 选择文件/文件夹 | 新增 |
| | `file.read` / `file.write` 读写文本 | 新增 |
| | `file.operation` 复制/移动/删除/建目录/重命名 | 新增 |
| | `file.exists` 存在/类型/大小/修改时间/创建时间/只读·隐藏·系统/目录文件数/快捷方式目标 | 新增 |
| | `file.explorer` 在资源管理器中定位 / 打开文件夹 | openFolder |
| | `file.list` 列目录 → 路径列表（通配符 / 递归 / 只文件或文件夹） | 新增（W8） |
| | `file.selection` 选中的文件（资源管理器 COM / 桌面 UIA）→ 路径列表 | 新增（W9） |
| | `file.explorerPath` 资源管理器当前目录 + 全部已打开目录 | 新增（W9） |
| | `file.tempPath` 生成临时文件路径 | 新增（W9） |
| | `file.folderPath` 桌面/下载/文档/临时/AppData 等系统文件夹 | 新增（W9） |
| | `file.hash` 文件哈希（MD5 / SHA-1 / SHA-256 / CRC32） | 新增（W9） |
| | `clipboard.files` 剪贴板文件列表（读写，可剪切） | 新增（W9） |
| | `clipboard.image` 剪贴板图片（→ png 文件 / png 写回剪贴板） | 新增（W9） |
| | `image.info` 图片宽高 / 格式 / EXIF 方向与拍摄时间 | 新增（W9） |
| **网络** | `web.open` 用浏览器打开网址 | openUrl |
| **程序** | `app.launch` 启动程序 | launchApp |
| | `app.runCommand` 运行命令（→ 退出码/stdout/stderr） | runCommand |
| | `app.process` 进程数量/进程号/结束进程 | Probe.process |
| **脚本** | `script.ahk` 运行 AutoHotkey v2 脚本（随包运行时） | 新增（W7） |
| **输入** | `input.sendKeys` 模拟按键（键 + 短按 / 按下 / 抬起 + 修饰键） | sendKeys |
| | `input.mouse` 鼠标操作 | mouse |
| | `input.cursor` 读取光标位置（Pure） | 面板唤起坐标 |
| | `input.clickText` 文字点击 | clickText |
| | `input.clickImage` 图片点击 | imageClick |
| | `input.macro` 回放宏 | macro |
| **窗口** | `window.find` 查找窗口 → 对象 | 新增（复用 WindowFinder） |
| | `window.foreground` 前台窗口信息 | Probe.window |
| | `window.operate` 切前台/最大化/最小化/还原/改尺寸 | window |
| **屏幕** | `screen.capture` 截图 → 对象 | Probe.screenshot |
| | `screen.findImage` 找图 → 对象 | Probe.image |
| | `screen.ocr` 文字识别 → 文本（也可选前台窗口 / 整个屏幕 / 固定区域，运行时现截一块，不落盘） | Probe.ocr |
| | `screen.findText` 找文字 → 对象 | Probe.textSearch |
| | `screen.pixel` 取色 → 对象 | Probe.color |
| **流程** | `flow.set` 赋值/追加 | FlowNode.set |
| | `flow.if` 条件（then/else） | FlowNode.if |
| | `flow.repeat` / `flow.forEach` / `flow.while` 循环 | FlowNode.loop |
| | `flow.break` / `flow.continue` / `flow.stop` / `flow.delay` | 同名节点 / DelayMs |
| | `flow.lastError` 上一步的错误 | RunContext.LastError |
| **子动作** | `sub.run` 调用子动作（W3） | 新增 |
| **数据** | `data.noteWrite` / `data.noteRead` 速记 | quickNote / Probe.note |
| | `data.list` 列表增删改查/排序/去重 | 新增 |
| | `data.json` JSON 文本 → 值 / 列表，支持 `a.b[0]` 取值路径 | 新增（W8） |
| | `data.calc` 数值计算 | 新增 |
| | `data.time` 当前时间/格式化/偏移 | 新增 |
| **界面** | `ui.toast` 提示 | toast |
| | `ui.input` 弹输入框 | Probe.user |

> 打开面板 / 设置 / 速记、截图、调音量这类「一下就完事」的入口**不是动作单元**，见 `Features/Builtin/`
> 的 `FeatureCatalog`（常用功能 / LTools 功能）。它们只出现在面板、快捷键、悬浮里，
> 动作编辑器左栏不再有「LTools 内置功能」分类。

**粒度原则**：能用一个 `operation` 参数区分的**同类微操作**，合并在同一个动作单元里
（`text.process`、`file.operation`、`window.operate`、`data.list`、`data.calc`），
否则工具箱会变成今天 Quicker 的 119 项。**但目标语义不同类时不合并**：2026-11-12 把旧的
`file.open`（文件 / 文件夹 / 网址三合一）拆成 `file.openFile` / `file.explorer` / `web.open`
三个动作单元，网址归「网络」分类——一个 `operation` 混三种目标时，名称、参数编辑器、输出与分类
都会失真。`text.format` 已并入模板 + `text.process join`。

#### 3.6.1 对照 Quicker 补的 13 个动作单元（2026-11）

按 Quicker 的功能清单逐项对过一遍：主线（键鼠 / 窗口 / 文本 / 文件 / 屏幕 / 剪贴板 / 流程）已经够用，
整块缺的是**网络、图片处理、用户交互、事件等待**四块。专用软件控制（Photoshop / AutoCAD /
Excel COM 那类）不做——那些用「运行脚本」或外部工具接，不值得为每个软件写一个动作单元。

| 分类 | 动作单元 | 补的是什么 |
|---|---|---|
| **网络** | `web.request` | GET/POST/PUT/PATCH/DELETE、自定义请求头、响应存文件、JSON 顺手解析成对象。静态 `HttpClient` + 停止令牌串联，按停止热键能立刻中断请求 |
| **文本与数据** | `text.encode` | Base64 / URL / HTML 编解码。Base64 能直接读整个文件（图片转 Base64）或把结果写成文件（反过来） |
| | `data.random` | 随机整数（闭区间）/ 随机字符串 / GUID / 从列表抽一项 |
| | `state.set` / `state.get` | 跨动作、跨重启的持久状态（`<数据根>/state/values.json`，保留列表 / 对象类型，进备份） |
| **流程** | `app.log` | 往 `logs/action-yyyyMMdd.log` 写一行（**始终写**），同时进运行日志 |
| **键鼠与输入** | `input.waitKey` | 等用户按一个键 / 组合键（留空 = 任意键）；只吞命中的那个键，超时是 `found = false` 不是失败 |
| **文件** | `path.parts` | 按字符串拆路径（目录 / 文件名 / 主名 / 扩展名 / 根）或拼接，不碰磁盘 |
| | `file.zip` | 压缩文件夹 / 文件成 zip，或解压；目标已存在且没勾「覆盖」直接失败 |
| **窗口与程序** | `window.message` | 发 Windows 消息（`WM_CLOSE` / 自定义消息）；默认 `SendMessageTimeout` 带超时，也可以只投递 |
| **屏幕** | `image.transform` | 裁剪 / 缩放 / 旋转 / 灰度 / 翻转 / 转格式；不填目标路径时输出到 `cache/screenshots/` |
| **界面** | `ui.confirm` | 确认框，按钮可自定义（`确定\|取消`、`保存\|不保存\|取消`），第一个按钮算「肯定」 |
| | `ui.select` | 从候选列表里挑一项（列表值 / 文件列表 / 正则结果），取消是 `found = false` |

两处实现细节值得记住：

- **`ui.confirm` / `ui.select` 不用 `ShowDialog`**（和文本窗口同一条理由）：模态对话框会把 UI 线程
  卡在它自己的消息循环里，停止热键就中止不了流程。走 `Features/ChoiceWindowService` 的
  「`Show` + 后台线程轮询」，超时 / 停止都能把窗口关掉。
- **`input.waitKey` 不是钩子里的手势**：自动化运行期间键盘钩子只保留停止热键，其余按键原样放行。
  等待者由 `Features/KeyWaiter` 临时挂上，钩子见到按键先问它，命中就吞掉（连同自动重复与抬起）。
  修饰键从不吞——吞了会让系统认为 Ctrl 一直按着。

### 3.7 迁移规则（已删除）

> **2026-11 起本项目不做向后兼容**：`Features/Migration/`（`ActionMigrator` / `ActionPrograms` /
> `ActionDtoLegacy` / `LegacyActionSummary`）、`ActionDto.RawType` / `SourceType` 与 `FlowTypes` /
> `FlowMigration` 全部删除。`ActionDto` 只写 `Type`（`composite` / `macro`）+ `Program`，
> v1 类型名（`runCommand` / `sendText` / `internal` …）解析不了就是坏文件。下面这张表只作历史对照，
> 程序不再读 v1 动作；新写动作单元 / 步骤时别按它去「兼容」。

历史对照：

| 旧 | 新 |
|---|---|
| `ActionDto { Type=launchApp, Target, Args, Workdir }` | `steps:[{unit:"app.launch", in:{target,args,workdir}}]` |
| `openFile / openFolder / openUrl` | `file.openFile` / `file.explorer`（`operation=open`） / `web.open`；旧的三合一 `file.open` 已整体退役 |
| `composite.Steps[]`（内嵌快照） | 逐个铺平成 `steps[]`；`OnFail` → 步骤的 `onFail` |
| `composite.Flow[]` | `flow.*` 动作单元；`Then/Else/Body` → `then/else/body` |
| `Probe`（条件 / set 右值） | 字面量 / `{var}` / 内联 Pure 动作单元 `{unit, in}` |
| `Condition` | `flow.if` / `flow.while` 的 `left` / `op` / `right` / `negate` |
| `Condition.Bind` | 先 `flow.set` 存进变量，条件改用 `{var}` |
| `ActionDto.OutputVar` | 该步骤 `out: { "value": 变量 }` |
| `VarValue` 的 `X/Y/W/H/Score/File` | 对象成员 `x/y/w/h/score/file` |
| `Node.DelayMs` | 前置 `flow.delay`（旧语义是执行**前**等待） |
| `OnFail` / `CompositeOnFail` 缺省 | 显式写 `onFail: "continue"`（旧默认；新引擎默认 `stop`） |
| `runCommand` 无 `OutputVar` | `capture: false`（旧行为是打开终端窗口） |
| `sendKeys.Items` | 键 → `input.sendKeys`（`key` + `action` + `mods`）、文本 → `text.send`、等待 → `flow.delay` |
| `mouse.WheelDelta`（120/格） | `wheel` = 格数 |
| `ProbeKind.ocr / textSearch` 的 `OcrEngine == null` | `engine: "windows"`（新动作单元默认 PP-OCRv6） |
| `CompareOp.nearColor` | `equals` + 告警（容差无对应动作单元） |

**结果对象约定**：有结果的动作单元除具名输出外还写一个 `value` 对象输出（`{text, found, x, y, …}`），
迁移的 `OutputVar` 统一映射到它；内联求值时优先返回 `value`，因此
`${变量}` = 旧 `VarValue.Text`（PID / stdout / 路径 / 坐标串），`${变量.x}` 等照旧可用。

**变量**：旧流程的变量没有声明，迁移时从 `flow.set`、步骤 `out`、模板 `${a.b}` 里收集名字，
一律声明为 `text`（对象 / 数字在运行期按实际值覆盖声明类型）。

加载时迁移一次，写回新格式；旧 JSON 保留 `.bak-*` 备份（沿用现有惯例，最多 3 份）。

---

## 4. 三条路线（对比留档）

| | A 分阶段双轨 | **B 一次性重写（已选）** | C 薄重构 |
|---|---|---|---|
| 概念干净度 | 过渡期两套 DTO | 一次到位 | 只拆类，模型不变 |
| 交付节奏 | 每阶段可发布 | 单个切流点 | 1～2 天 |
| 回归面 | 小，逐页替换 | 大，一次全量 | 小 |
| 目标达成 | 是 | 是 | 否（无类型/子动作） |

选 B 的**前置条件**（写进施工要求）：

1. 开发在独立分支 / worktree 上做，**每一步提交都能构建**（新代码先不接入 UI）。
2. 切流前必须完成：全量迁移器 + 等价性冒烟 + 旧配置回放。
3. 切流那一个提交只做「换装配」，不改业务逻辑；切流后旧实现立即删除，不留双轨。

---

## 5. 施工图（W1～W5）

### W1 内核（不动 UI）

- `Core/Actions/Value.cs`：`Value` / `ValueKind` / 转换 / `ToText()`。
- `Core/Actions/Template.cs`：`${a.b}`、`${list[0]}`、`${list.count}`，失败给出可读错误。
- `Core/Actions/VariableTable.cs`：声明 + 运行值 + 默认值。
- `Core/Actions/UnitSpec.cs`、`IStepUnit.cs`、`StepRegistry.cs`。
- `Core/Actions/Step.cs`、`ActionProgram.cs`：schema v2 数据模型 + 序列化。
- `Core/Actions/ActionEngine.cs`：执行步骤、作用域、子步骤递归、停止信号、失败策略、步骤超时。

**验收**：无头测试跑通「取值 → 赋值 → 条件 → 循环 → 提示」五步动作。

### W2 全量动作单元 ✅

- `Features/Units/<分类>/*.cs`，每类一个文件，每个动作单元一个类 + `IStepUnit` 实现。
- 逻辑从 `ActionExecutor` / `ProbeEvaluator` / `FlowRunner` **搬**过来（不重写行为），
  共享代码下沉到 `Features/`（`TextSender`、`CommandRunner`、`ProcessLauncher`）与
  既有工具类（`WindowFinder`、`MouseOps`、`TextLocator`、`ImageProbe`、`ScreenshotProbe`）。
- 旧实现过渡：`Features/Units/UnitValues.cs`（`Value` ↔ `VarValue`）+
  `LegacyFlow.cs`（把新上下文包装成 `RunContext`）；旧实现只读变量，不回写。
- `Value.AsText()` 对对象优先取 `text` 成员，于是 `${截图}` 仍是 `"x,y,w,h"`、
  `${匹配.x}` 才拿坐标。

**验收**：`ActionSmoke` 遍历注册表，校验 Key 唯一、`Spec` 完整、枚举选项齐全，
并跑通文本/数据/文件/命令/内联取值/错误文本；共 395 项断言通过。

### W3 迁移器 ✅

- `Features/Migration/ActionMigrator.cs`：旧 `ActionDto` → v2（旧 DTO 都在 `Features/`，不放 `Core/`）。
- 覆盖：17 种动作、16 种取值、7 种流程节点、`StepDto` 快照、`OutputVar`，以及 `sub.run` 调用。
- 未知字段 / 无法表达的语义：告警列表 + 原 JSON 备份，不静默丢。
- 顺带补齐：`input.cursor` 取值动作单元、`flow.if` / `flow.while` 的 `negate`、
  条件比较新增 `found` / `notFound` / `active` / `notActive`、`SubActionStore`（全局子动作库）、
  结构化结果统一的 `value` 输出（内联求值优先返回它）。

**验收**：`ActionSmoke` 迁移 `LTools.example.json` + 手工构造的旧动作，逐项断言结构，
并**执行**迁移结果（赋值 / 条件 / 两种循环 / 命令 / 子动作输出回写）；共 574 项断言通过。

### W4 编辑器 ✅

- `Views/ActionEditor/ActionEditorWindow`：三栏（工具箱 / 步骤 / 变量 + 动作属性）。
- 工具箱完全由 `StepRegistry` 的 `UnitSpec` 生成（分类 + 搜索）；点一下插到选中行之后。
  新增动作单元**不用改 UI 代码**即可出现并能编辑。
- 步骤行：图标 + 名称 + 一行摘要 + 输出变量 chip；缩进表示嵌套，
  「否则 / 结束如果 / 结束循环」是标记行，空分支有一行可选的占位行。
  增删改、上下移（`↑` / `↓`，或 `Alt+↑ / Alt+↓`）。
- **拖动排序**：拖起来后出现幽灵 + 插入线，松手才落数据（不做实时重排——
  步骤行是「重建出来」的扁平行，实时插入会与容器回收打架）。
  可落在任意步骤的上 / 下方（换成另一条分支就是「移入该分支」），
  也可落在空分支占位行上；标记行不是落点；**不能把一步拖进它自己的分支**。
- 参数表单 `Views/ActionEditor/StepEditorDialog`：按 `InputSpec.Editor` 生成
  文本 / 多行 / 数字 / 开关 / 枚举 / 文件 / 文件夹 / 图片 / 变量 / 条件表单；
  `VisibleWhen`（`key == 'value'`）实时收起 / 展开；输出按 `OutputSpec` 勾选映射到变量；
  `sub.run` 的按名参数由子动作变量表（`DynamicInputs`）生成。
- 值源 `Views/ActionEditor/ValueField`：一个输入框 + 右侧「▾ 来源」，弹层里是变量（名字 / 类型 / 来源）、
  选择文件 / 文件夹 / 图片 / 窗口 / 进程、屏幕区域 / 取点 / 取色、录制按键、速记分类 / 时间格式 /
  文件筛选 / 分隔符、取值动作单元（二级页）与清空；输入即筛选。**只列这个参数适用的项**
  （`InputSpec.Pickers` / `PickerKind`，没声明时按 `InputEditor` 推默认）。
  其中「选择窗口 / 进程」统一走 `Views/WindowPickOverlay` 的点击窗口选取（边缘渐变高亮），
  由 `Views/SystemPickerDialog` 提供共享入口；原可搜索列表仅作「从列表选…」次要 fallback。
  值的三态没变：字面量（可含 `${变量}`）/ 变量 / 取值动作单元；整串 `${名}` 才存成变量引用（保留类型）。
  内联动作单元用同一个步骤表单继续编辑（递归到任意深度）；缺必填参数时状态行写「还缺必填参数：…」，
  填完变成「点『改参数…』调整它」；没有参数的动作单元收起按钮。新切到取值动作单元默认 `text.selection`
  （常用、参数少）。
- 输出变量：标题带「已勾 N / M」，每行「☑ 标签 [类型] → 变量名 ▾」，`value`（完整结果）有别的输出时
  收进末尾「其它字段」折叠区。默认名由 `OutputNaming.Suggest` 推（`DefaultVariable` 优先，
  否则只有 `found` / `count` / `path` / `value` 这类通用词才加动作单元前缀，避免跨步骤互相覆盖）；
  名字和已有变量同名但类型不同时当场标红提醒。
- 参数表单在**构造函数里**就建好（`StepEditorDialog(step, inline, program, variableNames, resourceRoot, variableInfos)`）：
  `sub.run` 的「子动作」是下拉（全局子动作库）；`Program` / 变量名 / 变量表必须作为构造参数
  传入，放到对象初始化器会晚一步（表单按 null 建好，子动作退化成手填 id 的文本框）。
  下拉遇到不在 `Options` 里的旧值原样保留并标「未知选项」，不静默改成第一项。
- 变量面板：名称 / 类型 / 默认值；步骤**写出的**变量自动登记——`Core/Actions/VariableWrites` 是
  「这一步写了哪个变量」的唯一判定（`Step.Out` + 设置变量的 `name`），变量表、引用统计、改名同步、
  保存时补声明与静态校验共用同一份。
- 关闭编辑器时拿打开时的快照（名称 / 图标 / 变量表 / 步骤树）比对：有没保存的改动就三选一
  （保存 / 不保存 / 取消）；「保存」走与「确定」同一个 `Commit()`，校验不过就留在编辑器里。
- **子动作库**（设置 → 子动作库）：列表（名称 / id / 摘要 / 被引用次数）+ 搜索 + 新建 / 编辑 / 复制 /
  删除 / 试运行 / 打开目录。编辑复用 `ActionEditorWindow.ForSubAction`——子动作与动作同构，只多一列
  「方向」（输入 / 本地 / 输出）；id 由新建时生成、之后不可改（它是对外调用名）。
  试运行走 `SubActionRunner`（摊平成一份 `ActionProgram` 复用编辑器试运行链路），输入值由
  `SubActionInputDialog` 收集；删除前列出「哪个动作的哪一步」会坏。
- 执行桥：`ActionDto.Program` + `Features/Migration/ActionPrograms.For`（懒迁移并缓存）；
  `ActionExecutor` 在 `Program != null` 时改走 `ActionEngine`，旧格式到 W5 再说。
- 动作管理列表：`Program` 动作显示「动作 · N 步」（旧格式仍按原类型显示）。

暂缓（后续补）：

- 行内直接改关键参数：现在双击进对话框。要做成「点一下就地改」得先把步骤行从
  「一行摘要」改成「每个参数一个小 chip」，否则整行都变成输入框，选中 / 拖动全废。
  对话框已经默认聚焦第一个参数、Enter 即确定，所以暂不为此改行结构。

**验收**：新增一个动作单元**不改任何 UI 代码**即可出现在工具箱并能编辑。
`PanelSmoke` 真实构造三栏窗口 + 生成的参数表单 + 子动作编辑器 / 子动作库页并截图，
还验证了跨分支拖动与「自嵌套拦截」；`ActionSmoke` 覆盖
迁移缓存 / 深拷贝 / JSON 往返 / `Program` 执行桥（共 601 项断言）。

### W5 切流与删除

#### W5a 装配与删除 ✅

- 执行装配：`ActionExecutor` 只剩 `Run` / `Execute` / `RunProgram`——先 `ActionPrograms.For(action)`
  （旧格式懒迁移并缓存），再交给 `ActionEngine`；不再有按动作类型的 switch。
- 会话层：新增 `Features/AutomationRunner`，接走 `FlowRunner` 的 `IsRunning` / `PrepareToRun` /
  `Run` / `Stop` / `HandleStopHotkey` / 运行指示器 / 运行期截图回收。键盘钩子、鼠标钩子、
  宏回放热键改读 `AutomationRunner`。
- 删除：
  - 旧解释器：`Features/Flow/FlowRunner`、`ProbeEvaluator`、`ProbeTest`
  - 旧编辑器：`Views/ActionEditorDialog`、`Views/CompositeActionDialog`、`Views/FlowNodeDialog`、
    `Views/Controls/ProbeEditor`、`Views/Controls/ConditionEditor`
  - 随之无引用的旧控件 / 菜单：`Views/Controls/ActionKindSwitch`、`Features/SlotMenu`
- 入口清理：「新建组合动作」从设置动作管理页、快捷菜单页、超级面板右键菜单、面板管理页全部移除
  （新建动作一律走三栏编辑器，不再有「普通 / 组合」两种新建入口）。
- 保留：`FlowTypes` / `FlowMigration`（迁移器读旧 JSON 用）、`RunContext`
  （旧取值实现与新动作单元的过渡层 `Features/Units/LegacyFlow`）、`Features/Flow/*Probe` /
  `TextLocator` / `NoteActions`（新动作单元的底层实现）。`FlowVariables` / `ActionResults` /
  `ActionMaterializer` 已无调用，W5b-1 删除。

**验收**：仓库构建 0 错 0 警；`ActionSmoke` 601 项断言通过；`PanelSmoke` 全绿
（三栏编辑器、参数表单、拖动排序、子动作编辑器 / 库、设置导航）；旧编辑器相关测试项随之删除。

#### W5b-1 旧字段搬家 ✅

- 新增 `Features/Migration/Legacy/ActionDtoLegacy.cs`：`ActionDto` 的全部类型专属旧字段
  （`Target` … `Flow`、`OutputVar`）与深拷贝都搬到这里，`ActionDto : ActionDtoLegacy`
  只留 `Id` / `Name` / `Icon` / `Type` / `MacroId` / `Program`；JSON 仍是扁平字段，旧配置照旧能读。
- 新代码不再为了复用实现去拼 `ActionDto`：图片点击 / 文字点击 / 窗口操作 / 写速记改成
  `ImageClickRequest` / `TextClickRequest` / `WindowActionRequest` / `NoteWriteRequest`。
- 旧字段的只读用法集中到 legacy：`LegacyActionSummary`（设置页动作清单摘要、
  `BackupService` 的 `resources` 引用预览）、`ObsoleteActionCleanup`（已取消动作的清理）。
- 资源回收改读新动作体：新增 `Core/Actions/ProgramWalker`，`ImageLibrary` / `MacroStore`
  扫动作与全局子动作的所有步骤（含嵌套块、内联取值步骤）的 `image` / `macroId` 参数；旧字段只兜底未迁移动作。
- 删除无调用的 `Features/Flow/ActionMaterializer`、`ActionResults`、`FlowVariables`；
  `Views/Controls/VariableList` 只收变量名（不再列属性后缀）。

#### W5b-2 枚举收窄 ✅

- `ActionType` 只剩 `composite` / `macro`；`ActionDto.Type` 标 `[JsonIgnore]`，JSON 的 `Type`
  改由 `ActionDto.RawType` 读写：新动作写 `composite` / `macro`，旧配置的 v1 类型名原样留在
  `ActionDtoLegacy.LegacyType`——未迁移的动作回写时仍是旧值，旧版本也还能读。
- `LegacyTypeNameConverter` 容错读取：`Type` 写成数字（旧枚举声明序号）也能映射回名字。
- 迁移器按 `ActionDto.SourceType`（旧类型名，或 `composite` / `macro`）分支；`ActionPrograms.For`
  迁移成功后 `ClearLegacy()`，动作从此只写 `program`；新编辑器保存同样清空旧字段。
- 旧动作摘要 / 类型标签 / 备份引用预览统一在 `LegacyActionSummary` 里按字符串分支；
  `ActionTypeLabel` 与无调用的 `FlowText` 删除。
- `git grep FlowNodeKind` 只剩 `FlowTypes`（定义）、`FlowMigration`（旧 Steps → Flow）与迁移器。

**验收**：仓库构建 0 错 0 警；`ActionSmoke` 601 项断言通过；`PanelSmoke` 全绿；
另用外部检查覆盖真实 `LTools.example.json` 读取、旧 `Type` 回写、数字 `Type`、宏动作迁移。

### W6 变量可见性（P1）✅

来自新用户反馈：「编辑动作时输入框旁没有填入变量的快捷入口」「输出到变量，但不知道输出的是什么、
是什么类型」。

- `OutputSpec` 增 `Description`（120 条输出逐个补全）：输出区每行变成
  `☑ 标签 [类型] → 变量名`，下一行「写入 ${变量}：××」；说明行**不看勾选状态**都显示
  （先读到「退出码是否为 0」才知道值不值得勾）。
- 主结果（`value`）排最前。`EnsureOutputVariables` 原来把步骤输出**一律登记成 `text`**（错的），
  改为按动作单元声明登记，并校正已有行——用户手改过类型的行不再被覆盖。
- 变量表每行补一行小字：来源（`第 2 步 · 截图（输出 file）`）；删除仍被步骤写入的变量时提示先改步骤，
  否则下次同步又会自动加回来。
- `SubActionStore.Load(id)`：编辑器与执行器共用同一个解析入口（进程内索引缓存 + 坏文件隔离）。

**验收**：构建 0 错 0 警；`ActionSmoke` 601 项断言；`PanelSmoke` 全绿（含参数对话框截图）。

### W6.1 枚举值显示中文化 ✅

用户反馈「许多操作都是英文，比如 trim、read、foreground」。

- 新增 `Core/Actions/OptionLabels.cs`：英文键 → 中文名（唯一映射），参数对话框下拉与步骤行摘要共用。
- `InputSpec.Labels`（可选）给单个参数覆盖全局表：`data.calc` 的 `add` 是「加法」，`data.list` 的是「追加一项」；
  `file.explorer` 的 `open` 是「打开文件夹」，与「用默认程序打开文件」分属两个动作单元；旧的三合一 `file.open` 已整体退役。
- **只翻译显示层**：`Options` 里仍是英文键，`VisibleWhen`、迁移器、动作单元实现、老配置一律按英文比较；
  下拉项把英文键放在 `Tag` 里读回，摘要只翻字面量（变量 / 内联取值原样显示）。

**验收**：构建 0 错 0 警；`ActionSmoke` 601 项断言；`PanelSmoke` 全绿（参数对话框下拉与摘要均为中文）。

### W6.2 屏幕区域一键框选 ✅

用户反馈：「需要用户输入区域的功能，应该能截图框选填入，而不是手填坐标」。

- 受影响的 5 个动作单元：`screen.capture`（`mode` + `rect`）、`screen.findImage` / `screen.findText` /
  `input.clickText` / `input.clickImage`（`region` + `rect`）。
- 新编辑器 `InputEditor.region`：文本输入右侧多一个「框选…」（`ScreenPicker.TryPickRegion`），
  框完写 `x,y,w,h`。配套枚举行（区域 / 取图方式）本身也挂一个「框选…」
  （`IsRegionDriver` 按 `rect` 行的 `VisibleWhen` 认驱动键），不必先切到「固定区域」。
- `StepEditorDialog.ApplyPickedRegion`：**先写文本、再切枚举**——顺序反了，切换时同步逻辑会先把隐藏行丢掉。
- `TemplateImagePicker.Captured` 在参数对话框里接上：框选模板图后顺手把搜索区域收紧到刚框处
  （旧编辑器有这个行为，W4 重写时漏了）。
- 框选结果仍是普通字面量，可手改，也保留变量 / 内联取值两种模式。

**验收**：构建 0 错 0 警；`ActionSmoke` 601 项；`PanelSmoke` 全绿
（新增 `step-editor-region.png`，并断言框选结果会把 `region` 切成 `rect`、`rect` 写成 `x,y,w,h`）。

### W6.3 步骤路径与执行轨迹 ✅

用户反馈：「失败只说第 N 步，循环 / 分支一跑就对不上编辑器里的行」。

- 引擎递归时拼**步骤路径**：`/steps/2/then/0`（每层 `<分支>/<下标>`，顶层 `/steps/2`），
  同时给出人话标签 `第 3 行 › 否则 › 第 1 行`；子动作挂 `/sub/<id>`。
- 新 `Core/Actions/StepTrace.cs`：`StepTrace(Path, Label, Depth, UnitKey, UnitName, Status,
  ElapsedMs, Error, Outputs)`；`ActionRunOptions.StepTrace` 每步回调一次（`ok` / `failed` /
  `continued` / `aborted`），回调抛异常一律吞掉（调试信息不该毁掉动作）。
- 失败文案改「第 3 行「提示弹窗」失败：…」；`ActionRunResult.FailedStepLabel` 给面板定位用。
  失败经内部 `StepFailureException` 上抛，外层只 `??=` 记位置、原样 rethrow，避免前缀套娃。
- `ActionRunOptions.StartPath`：「从这一行开始」只跑顶层某一步；路径对不上报「找不到起点步骤」。

**验收**：构建 0 错 0 警；`ActionSmoke` 626 项（新增 25 项：路径 / 标签 / 深度 / 状态 /
输出、失败条目与 `FailedStepLabel`、`StartPath` 与错误路径）。

### W6.4 运行结果提示 ✅

用户反馈：「成功了没反馈，失败了提示一闪就没，还不知道哪一步」。

- `Core/ToastService` 增等级（info / success / warning / error，色条 + 失败红边）、
  位置（六向）、`key` 去重（就地更新 + `×N`）、`StickyMs`（≤ 0 = 常驻）、点击回调。
- `AutomationRunner` 拆出 `RunCore` / `NotifyFinished`：失败**常驻**红色提示（标题带失败位置，
  点一下打开日志目录），成功按 `AppConfig.RunNotice`（默认只在失败时），停止给 2 秒 info；
  同一动作用 `run:<动作名>` 作 key，下次运行就地替换。移除原 `TrayService.Notify` 的失败提示。
- 设置 →「通用 → 运行提示」：结束后是否提示（三档）+ 默认提示位置（六向，`AppConfig.ToastPosition`）。
- `ui.toast` 增 `position`（含「跟随设置」）与 `key` 参数。
- **编辑器试运行也走同一套提示**（`noticeOverride = always`）：成功 2.5 秒、失败常驻，
  想验证失败卡片时不用去面板找动作，直接在编辑器里加一个跑不通的步骤点「▶ 试运行」。
  标题已经带位置，正文里重复的「第 N 行」会被剪掉。

**验收**：构建 0 错 0 警；`ActionSmoke` 626 项；`PanelSmoke` 全绿
（新增端到端断言：试运行一个必失败的动作后必须出现失败 Toast，标题含「第 1 行」，
截图 `toast-failed.png`）。

### W6.5 编辑器试运行 ✅

用户反馈：「想验证只能先保存再回面板点动作」「变量只能插一个提示弹窗打出来看」。

- 编辑器底部「试运行」栏：`▶ 试运行` / `▶ 从此行` / `■ 停止` + 状态行；
  跑 `_program.Clone()` + 现读变量表，**不必先保存**，关窗即停。
- 与真实运行共用 `AutomationRunner` 的互斥锁、停止热键与运行指示器（`TryRunProgram`），
  两套键鼠注入不会打架。
- 轨迹列表按 `Path` 聚合（同一步骤循环多次只一行，`×N`）：状态图标 / 行号 / 动作单元 / 耗时 /
  错误或写入的变量；点行跳到左侧步骤，失败自动选中。右侧「变量（运行结束后）」是快照。
- 轨迹从后台线程入队、120ms 定时器攒批上屏（循环几千步也不卡）。

**验收**：构建 0 错 0 警；`ActionSmoke` 626 项；`PanelSmoke` 全绿
（新增 `action-editor-run.png`：断言抽屉能展开、注入两条轨迹后列表为 2 行）。

### W6.6 表单与步骤列表的可用性修补 ✅

用户反馈：新用户不知道「取值动作单元」怎么用、四个字还显示不全；「内置功能」「调用子动作」
只有空文本框，不知道填什么；删掉两步动作的第二步时按钮「没反应」。

- 值源三态改为 **文本模板 / 变量 / 取值动作单元**；模式下拉不再写死 76px。
  取值动作单元缺必填参数时按钮显示「填参数…」、值下面一行提示（红字）列出缺少的项；
  填完变「改参数…」，无参数动作单元收起按钮。新切到取值动作单元默认 `text.selection`。
- `StepEditorDialog` 的 `Program` / 变量名改为**构造参数**（表单在构造函数里就建好）：
  「调用子动作」恢复成子动作下拉（本动作 + 全局库，没有可选项时给提示），
  `InputEditor.variable` 也有候选名了。
- 「内置功能」的 `command` 改成枚举下拉（速记窗口 / 快速搜索 / 放大镜 / 窗口置顶）；
  下拉遇到 `Options` 之外的旧值原样保留并标「未知选项」，不再静默改成第一项。
- 没有输出的动作单元不再显示空的「输出变量」区（内置功能 / 还没选子动作）。
- 步骤行内的删除 / 编辑按钮改为按按钮自己的 `DataContext` 找行；删除后自动选中下一行
  （没有则上一行），连点删除一直有效。

**验收**：构建 0 错 0 警；`ActionSmoke` 628 项；`PanelSmoke` 全绿
（新增断言：连删两行后步骤数为 0；子动作下拉出现「本动作 · 打招呼」并带输入参数；
内置功能是 4 项中文下拉、输出区收起；旧命令键 `settings.toggle` 保留为「未知选项」；
截图 `step-editor-subrun.png` / `step-editor-internal.png`）。

---

### W7 脚本动作单元：AutoHotkey v2

第一个脚本动作单元 `script.ahk`（分类「脚本」，图标 `script`）。运行时**随包分发**
（`runtime\autohotkey\AutoHotkey64.exe`，2.0.31），不依赖用户机器上装没装 AHK；
许可证与源码出处放 `licenses\`（GPL-2.0），更新用 `tools\fetch-autohotkey.ps1`。

**参数与编辑器**

- `code`（`InputEditor.code` + `InputSpec.Raw = true`）：等宽多行框，Tab 缩进、
  双向滚动，下方「检查语法」调 `AhkRunner.Check`（`/validate`，只校验不执行），
  错误红字显示，行号按用户代码算。
- **`Raw` 是必须的**：代码里的 `${…}` 不能被 `TextTemplate` 当变量求值，否则脚本里
  的模板字符串会整步失败。`ActionEngine.ResolveArgs` 按 `InputSpec.Raw` 传
  `renderLiteral: false`；这是「原样传参」唯一的开关，别在动作单元里另写一套。
- 另有两个普通参数：`workdir`（进程工作目录 = `A_WorkingDir`）、`timeoutMs`
  （默认 60000，0 = 不超时；上限 1 小时）。

**与流程交互**（前言的三个函数）

| 函数 | 作用 |
|---|---|
| `LT_Var(name, default := "")` | 读当前运行变量（`ctx.Scope.Flatten()` 摊平成文本注入 `LT_Vars`） |
| `LT_Set(name, value)` | 写回运行变量（与「设置变量」一样直接落到当前作用域，后续 `${名字}` 可见） |
| `LT_Print(text)` | 往标准输出写一行（UTF-8） |

输出声明沿用命令动作单元的命名：`stdout` / `stderr` / `exit` / `ok` / `value`
（`value` 里额外带上 `LT_Set` 写回的变量）。

**错误怎么融进同一条调试链（关键）**

AHK 默认用**模态对话框**显示运行时错误、以及 `#Warn` 警告；没人点就永远卡住，
必须由前言接管：

1. `OnError(LT_OnError)` 把 `err.Message / File / Line / Stack` 写成 JSON，再
   `ExitApp 3`。于是运行时错误变成普通步骤失败，消息里带**用户代码行号**，
   引擎照常加「第 N 行「AutoHotkey 脚本」失败：」前缀，进提示 / 轨迹 / 失败位置。
2. `#Warn All, Off` 关掉**警告对话框**。未定义变量 / 函数的第一道坎就是
   「Warning: This global variable appears to never be assigned a value」——
   它同样是模态框。关掉后这类写法继续执行，在真正使用处抛运行时错误，由
   `OnError` 给出更准确的消息与行号。
3. `/ErrorStdOut` 只覆盖**加载期**错误（语法 / `#Include` 找不到等，退出码 2，
   写 stderr），不覆盖运行时错误；两者都要处理，不能只留一个。
4. 行号换算：报错行号是「前言 + 用户代码」生成文件里的绝对行号，减去前言换行数
   （`ScriptFile.UserLineOffset`）才是用户行号；只对生成文件自身的错误换算，
   运行器内部（前言）的错误原样报。
5. `OnExit(LT_OnExit)` 负责把 `LT_Set` 攒下的变量落盘；即使脚本中途 `ExitApp`
   或触发 `OnError` 也能收回来（失败时也写回，方便看跑到哪一步）。
6. 超时 / 停止热键：`Process.Kill(entireProcessTree: true)`；停止抛
   `ActionAbortedException`（新引擎的「已停止」语义），不是失败。
7. 编码：脚本必须写 **UTF-8 带 BOM**（AHK 靠 BOM 判定 UTF-8）。stdout 先按
   UTF-8 严格解码，失败再退回系统 OEM 代码页（用户脚本里裸 `FileAppend …,"*"`
   是 ANSI）。

**已知边界**（MVP）

- `#Include` 按**生成脚本所在临时目录**解析，用户目录里的相对 include 找不到；
  要用就写绝对路径（`A_WorkingDir` 可用于普通文件操作）。
- 脚本里的 `MsgBox` / `InputBox` 仍会阻塞，直到超时或用户手动关掉；流程内提示
  仍应使用「提示弹窗」动作单元。
- AHK 的报错消息是英文（AHK 自己的本地化取决于系统，未做翻译）。

**验收**：构建 0 错 0 警；`ActionSmoke` 647 项（新增：正常脚本 / 变量进出 /
`${…}` 原样传入 / 语法检查与错误行号 / 运行时错误走 `OnError` 且此前 `LT_Set`
仍写回 / 超时中止）；`PanelSmoke` 全绿（截图 `step-editor-ahk.png` /
`step-editor-ahk-error.png`；断言等宽多行、260px 高、「检查语法」按钮、
错误红字带 `第 1 行`）。

---

### W8 列表的来源（「获取列表」）✅

反馈：「有列表变量，但缺少获取列表的功能」。核对后的结论：**读取侧本来就全**
（`flow.forEach`、`${列表.count}`、`${列表.0}`、拼接 / 去重 / 排序），缺的是**产出**
以及两处写法（变量默认值存不进去、参数里没法内联列表）。

**产出（三个新动作单元）**

| 动作单元 | 用途 |
|---|---|
| `file.list` | 目录 + 通配符（`*.txt;*.log`）+ 是否递归 + 只文件 / 只文件夹 → 路径列表；`最多项数` 填 0 = 不限，超出置 `truncated` |
| `data.json` | JSON 文本 → 值；`取值路径` 支持 `a.b[0]`，结果是数组时同时给 `list`；语法错误直接失败，路径不存在只是 `found = false` |
| `text.regex` | 正则全部命中（可指定分组）→ 列表；没命中只是 `found = false`，正则写错才失败 |

**写法（`Core/Actions/ListText.cs`，唯一规则）**

- 参数或变量默认值里写列表：换行 / 逗号都算分隔符（`a,b` 与每行一项等价），
  项里要带逗号或要嵌套时写 JSON 数组（`["a,b", 1]`）。
- 列表参数在**绑定参数时**才拆成列表（`ActionEngine.CoerceInput`）：`${列表}` 仍是引用、
  不会被再拆一次，空文本 = 空列表（「文本」变量当累加器时第一轮就是空）。
- 变量表的列表 / 对象默认值框变成多行：显示走 `ListText.Display`（列表回填成每行一项，
  不再是 `[a, b]`），保存走 `ListText.TryParseDefault`。**填错提示并拦住保存**，
  不再像以前那样静默丢掉——以前 `ReadVariables` 对列表 / 对象一律给 `null`，
  列表默认值永远存不进去。
- 参数对话框里列表参数仍能选已有变量；既不是 `${…}`、也不是已有变量名时按字面量存
  （以前会被当变量名，运行时才报「变量不存在」）。

**验收**：构建 0 错 0 警；`ActionSmoke` 713 项（新增 66：内联列表遍历 + JSON 数组内联 /
变量默认值列表 / `ListText` 文本规则 / 列目录的当前层·递归·只文件夹·截断 /
JSON 取值与错误 / 正则分组与错误）；
`PanelSmoke` 全绿（新增断言：列表变量的默认值是多行框、显示成每行一项、`ReadVariables`
能原样存回；截图 `action-editor-list-var.png`）。

---

### W9 剪贴板载荷 / 文件 / 图片（「从选中获取路径」）✅

反馈：「想要『从选中的文件获取路径』『从剪贴板读文件 / 图片』这类编写效率功能」。
按 Quicker 的对应动作单元（REA 核对 `sys.clipboard*` / `sys.fileOperation` / `sys.image` 的参数与输出）
补齐成一套完整的剪贴板 / 文件 / 图片工具，共 10 个新动作单元 + 2 处扩展。

**新增动作单元**

| 动作单元 | 用途 | 输出 |
|---|---|---|
| `clipboard.files` | 读 / 写剪贴板里的文件列表（CF_HDROP），`cut` = 粘贴后移动 | `list` `count` `first` `names` `text` `cut` `found` |
| `clipboard.image` | 剪贴板图片 → png 文件；png 文件 → 剪贴板（PNG + CF_DIB） | `path` `width` `height` `found` |
| `clipboard.history` | Windows 剪贴板历史（Win+V）：列出 / 删一条（`id` 留空 = 最新一条） | `items` `texts` `ids` `count` `first` `deleted` `found` |
| `clipboard.clear` | 清空当前剪贴板；`includeHistory` 连 Win+V 历史一起清 | `cleared` `count` |
| `file.selection` | 资源管理器 / 桌面里选中的文件与文件夹 | `list` `count` `first` `names` `folder` `text` `found` |
| `file.explorerPath` | 前台资源管理器当前目录；另给全部已打开窗口的目录 | `path` `all` `count` `found` |
| `file.tempPath` | 生成一个未被占用的临时文件路径（不创建文件） | `path` `folder` |
| `file.folderPath` | 桌面 / 下载 / 文档 / 图片 / 临时 / AppData / 程序目录… | `path` `found` |
| `image.info` | 图片宽高、格式、EXIF 方向与拍摄时间 | `width` `height` `format` `orientation` `exifDate` `found` |
| `file.hash` | MD5 / SHA-1 / SHA-256 / CRC32（流式，大文件也行） | `hash` `algorithm` `size` `found` |

**扩展**

- `text.clipboard` 增加 `format = text | html | rtf`：读 HTML 时给 CF_HTML 的 Fragment 段，
  写 HTML 时按规范补 `StartHTML` / `StartFragment` 偏移（只写这一种格式，整块替换剪贴板）。
- `file.exists` 增加 `createTime` / `isReadOnly` / `isHidden` / `isSystem` /
  `fileCount` / `totalSize`（目录取顶层文件，不含子目录）/ `lnkTarget`（`.lnk` 目标）。

**「选中的文件」怎么取（`Features/Selection/`）**

- 主路径 `ShellBridge`：`Shell.Application` 枚举 `Windows()`，按前台窗口 `HWND` 匹配，
  Win11 多标签靠窗口标题（= 活动标签的文件夹名）挑出活动标签那个；桌面不在 `ShellWindows` 里，
  改从 `NameSpace(0)` 取目录、用 UIA 读选中图标的名字再映射回桌面目录（含公共桌面）。
  COM 与 UIA 都在 `Native/StaRunner` 起的临时 STA 线程上跑（动作本身在 MTA 后台线程）。
- 兜底 `SelectionFallback`（参数 `useClipboardFallback`，**默认关**）：给前台发 Ctrl+C 读剪贴板。
  只服务资源管理器之外的第三方文件管理器；它会模拟按键、临时占用剪贴板，所以默认不启用。
  剪贴板原内容是文本 / 文件列表时用完还原，原本为空则保留这次复制的结果。
- 拿不到就只是 `found = false`（不报错、不弹窗）：后续用「条件」判断要不要继续。

**剪贴板载荷（`Native/NativeClipboard.cs`）**

- 全部走原始 Win32（`OpenClipboard` / `GetClipboardData`），不走 WinForms OLE——
  工作线程是裸 STA 无消息泵，OLE 跨进程读取会阻塞数秒（见文件头注释）。
- 覆盖 `CF_HDROP`（含 `Preferred DropEffect` 复制 / 剪切）、`CF_DIB` / `CF_DIBV5` 与注册格式
  `PNG`（CF_DIB 读入时补 `BITMAPFILEHEADER` 再交给 WPF 解码）、`HTML Format`、`Rich Text Format`。
- 剪贴板是全局独占资源，`OpenClipboard` 失败会重试 5 次 × 15ms（约 60ms 上限，远小于钩子 300ms 约束）。

**剪贴板历史（`Native/ClipboardHistory.cs`）**

- 走 WinRT 的 `Clipboard.GetHistoryItemsAsync` / `DeleteItemFromHistory` / `ClearHistory`：
  非打包 + 提权的桌面进程实测可用（`Status = Success`）。异步方法在动作线程上同步等待，
  后台 MTA 线程没有同步上下文，不会死锁。
- **只能删，不能改**：历史条目是系统快照，没有编辑接口——「改」只能是删掉再写一条。
- 值模型没有句柄类型，所以删除按 `id` 重新查一遍历史再定位；`id` 留空 = 最新一条。
- 系统关掉「剪贴板历史记录」时返回 `ClipboardHistoryDisabled`，翻成可照着做的提示并抛错
  （这是环境没准备好，不是「没找到」，静默给空列表会让人以为历史里真没东西）；
  删一条不存在的记录只是 `deleted = false`，不打断流程。

**验收**：构建 0 错 0 警；`ActionSmoke` 856 项（新增 115：文件列表 / 剪切标记 / 图片往返 /
HTML·RTF 往返 / `file.hash` 已知值 / `file.exists` 新字段 / 临时路径 / 系统文件夹 / 图片信息 /
前台为资源管理器时读到当前目录与真实选中项 / 降级时不动剪贴板；另加剪贴板历史的列出形状、
删不存在的记录、清空当前剪贴板）；
`PanelSmoke` 全绿（新增断言：工具箱出现这 10 个动作单元）；`UnitShots` 21 个参数对话框全绿。

---

## 6. UI / 交互

```
┌ 工具箱 ─────────┬ 步骤 ────────────────────┬ 变量 ─────────┐
│ 搜索框           │ ① 获取选中文字      → text │ 名   类型  默认 │
│ ▸ 文本           │ ② 文本处理 · 去空格 → clean│ text 文本  ""   │
│ ▸ 文件           │ ③ 如果 clean 为空         │ clean 文本  ""  │
│ ▸ 屏幕           │     └ 提示「没有内容」     │                │
│ ▸ 流程           │ ④ 写入剪贴板 ← clean      │ + 新建变量     │
└─────────────────┴───────────────────────────┴───────────────┘
```

- **步骤行**：图标 + 动作单元名 + 一行摘要（关键参数内联可改）+ 输出变量 chip。
- **参数编辑器**：由 `InputSpec` 生成；每个参数右侧是「取值」按钮（选变量 / 选取值动作单元）。
- **输出区**：每行 `☑ 标签 [类型] → 变量名` + 一行说明（`OutputSpec.Description`），主结果（`value`）置顶；
  说明不看勾选状态都显示，用户先读到语义再决定勾不勾。
- **条件**：保留「值 + 比较 + 值」的表单形态（易用），底层存 `flow.if` 的输入；
  高级用户可切表达式。**不要**把裸表达式丢给普通用户。
- **变量面板**：类型 + 默认值 + 一行来源小字（哪一步的哪个输出写进来）；子动作里额外有「输入 / 输出」勾选。
- **子动作库**：与动作编辑器同构（步骤 + 变量 + 输入输出标记），入口在设置里。

---

## 7. 风险与取舍

| 风险 | 处理 |
|---|---|
| 一次切换回归面大 | 切流前完成等价性冒烟；切流提交只换装配；旧代码保留到验收通过再删 |
| 迁移丢字段 | 显式映射表 + 未知字段告警 + `.bak-*` 备份 |
| 动作单元过细导致工具箱爆炸 | 粒度定在「一类操作 + operation 参数」，目标 ~40 个 |
| 类型化后旧用法报错 | 文本是万能输入：绑定参数时按需解析，失败才报错 |
| 子动作改动影响面大 | 删除前列引用；全局改动只改行为，不改调用方数据 |
| 组合动作旧语义（快照） | 明确取舍：改为**引用**；需要独立副本时用「复制为子动作」 |

---

## 8. 冒烟

沿用 `%TEMP%\opencode\lpanel-smoke` 的 `PanelSmoke.exe` 结构，新增：

- `ValuesTest`：类型转换矩阵（text↔number/bool，list/object 成员与下标）。
- `UnitsTest`：反射遍历 `IStepUnit`，断言 Key 唯一、Spec 完整、默认值类型匹配。
- `MigrateTest`：旧动作迁移后执行结果与旧执行器逐项比对。
- `SubActionTest`：全局 + 动作内两种调用、三层嵌套、递归保护、删除引用提示。
- `EditorTest`：由 Spec 生成的表单能打开并保存每个动作单元。

---

## 9. 宏收敛：一个宏就是一个动作（2026-11）

原来的 `ActionType.macro`（顶层宏动作只存 `MacroId`、没有 `Program`）在实现上是半截管道：
`ActionExecutor` 只给 `composite` 建运行会话，宏动作从任何入口触发都跑不动（异常只进日志），
而编辑器保存时又会把类型强行改回 `composite`，宏随即变成孤儿被回收。

现在的形状：

- **删掉 `ActionType` / `ActionDto.Type` / `ActionDto.MacroId`**。动作只有 `Id` / `Name` / `Icon` / `Program`。
- **一个宏 = 一个「1 步 · `input.macro`」的动作**：步骤参数 `macroId` 指向 `macros/<id>.json`，
  宏元数据仍在 `configs/macros.json` 的 `Macros.Items`。
- **`Features/MacroAction.cs` 是唯一桥梁**：`MacroIdOf` 做结构判定（单步 + `input.macro` + 字面量 `macroId`），
  `Attach` 把宏挂成动作（已有就更新），`PlaceOnPanel` 放进当前全局场景的第一个分组。
- **录制即闭环**：`MacroRecorder.MacroSaved(id)` → `App.OnMacroSaved` → `Attach` + `PlaceOnPanel` + toast。
  录制热键是全局的（按一次开始、再按一次停止），托盘 / 面板「＋」/ 设置页「键鼠宏」三个入口等价。
- **管理界面在设置 →「键鼠宏」**（`Views/Controls/MacroView`）：热键、启用开关、宏列表（改名 / 试放 / 重录 / 删除）、
  回放默认值。动作编辑器里打开宏动作时显示一条宏横幅（重录 / 打开设置）。
- **旧数据不迁移**：`"Type": "macro"` 的动作目录与它的宏文件在启动时（读配置之前）整体移进
  `trash/<时间戳>/legacy-macro/`，用户重录一遍即可。
