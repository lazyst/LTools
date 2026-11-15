# Quicker 动作功能单元调研（输入 / 功能 / 输出）

> 性质：外部产品调研报告，不是本项目规格。`CONTEXT.md` 是本项目术语的唯一来源；本文出现的「模块 / 步骤 / 组合动作 / 子程序」等只是**描述 Quicker 侧对象时的对照叫法**，不引入为 LTools 术语。
>
> 分析对象：本机安装的 **Quicker 2.3.36.0**（`C:\Program Files\Quicker`），数据目录 `C:\Users\hcz\AppData\Local\Quicker`。
> 分析日期：2026-11-15。配套文档：`docs/QUICKER_UNITS_ADOPTION.md`（借鉴优先级与落地建议）。

## 1. 目的与范围

用 REA 静态分析本机安装的 Quicker，盘点它自带的**动作功能单元**（Quicker 里叫「模块 / 步骤」，对应 LTools 的**动作单元**），逐个给出：

- **输入（参数）**：参数键、类型、默认值（一手数据出自程序集嵌入资源）；
- **功能**：这一步做什么；
- **输出（输出的变量）**：这一步声明的**输出端口**，Quicker 里由用户把端口绑定到动作变量。

按用户要求**排除**两类：

- **A. 需要 Quicker 云服务 / 在线账号 / 在线服务**的单元；
- **B. 需要连接第三方软件 / 浏览器扩展 / 外部运行时**的单元。

标准网络协议（HTTP / WebSocket / SMTP / 下载）虽然要联网，但不依赖 Quicker 云、也不连接某个特定第三方软件，故**保留并标注**。

## 2. 分析方法与证据链

以 REA 为主，其它手段只做交叉验证与补充，全部结论区分「观察 / 推断 / 未知」。

| # | 手段 | 结果 | 证据级别 |
|---|------|------|----------|
| 1 | `rea.inspect_managed_artifact`（`max_metadata_representation_bytes=260000000`） | `Quicker.dll` = PE32+ / x64 / IL-only，程序集 **Quicker 2.3.36.0**，目标框架 `.NETCoreApp,Version=v10.0`；TypeDef **20181**、MethodDef **214815**、ManifestResource **34**（resources RVA `0x2C7C000`、size 4093933）。sha256 `6d939f4f944f7d8ed6462dde090298e15ba756fd3e20effc44e464acee2c2cf4`，MVID `e8ba03f2-79fe-480b-a18d-dccc918f3963` | REA 一手观察 |
| 2 | `rea.inspect_managed_members`（4 个契约程序集） | `Quicker.Public.Abstractions` 30 类型 / `Quicker.Contracts` 52 / `Quicker.Headless.Designer.Contracts` 237 / `Quicker.StepEngine.Core` 254；确认存在独立的「步骤引擎 / 无头设计器契约」层 | REA 一手观察 |
| 3 | PE/CLI 元数据交叉验证（`System.Reflection.Metadata` 枚举 `ManifestResource`） | 34 个资源中定位到 **`Quicker.Sharing.V1StepCapabilities.json`**（515620 字节，资源区偏移 2750592）——即「步骤能力清单」 | 观察 |
| 4 | 导出该 JSON 并解析 | `schemaVersion=1`、`clientVersion=1.45.5.0`、`assemblySha256=ac815c92…`；**`modules` 共 139 项**，每项含 `name`（中文名）+ `parameters`（键 → `name/type/variableMode/defaultValue/options/allowInput/control/validFor/invalidFor/visibleExpression/fromOldField`）。**这是本文「输入」列的唯一一手来源** | REA 定位 + 一手观察 |
| 5 | 对 `Quicker*.dll` 做 UTF-8 / UTF-16 字符串扫描 | 提取到 **383 个 `sys:*` 字符串**；去掉 `T` 后缀（显示名/翻译资源键）后得到 V1 + V2 步骤键全集，发现 V1 清单之外的新增步骤（见 §7） | 观察 |
| 6 | 本地知识包 `agent/knowledge/`（`program/variables.md`、`program/IActionContext.cs`、`skills/steps/SKILL.md`） | 确认输出模型：步骤声明输出端口，用户在编辑器里把端口**绑定到变量**；V2 用 `>>: 变量名`（主输出）、`outputs:{key: 变量名}`（命名输出）、`isSuccess`；脚本类用 `context.SetVarValue` 直接写变量 | 本地一手文档 |
| 7 | 官方文档 `getquicker.net/KC/Help/Doc/*`（146 个模块页） | 抓取每页「输出参数 / 输出」小节的 `【…】` 端口名，得到 **83 个单元的输出端口中文名**；`parameters` 等文档页给出输入/输出通用模型 | 外部公开文档（可能与 2.3.36 略有出入） |

### 局限（必须连同结论一起读）

- `V1StepCapabilities.json` 的 `clientVersion=1.45.5.0` 说明它是**分享动作时校验「V1 步骤能力」的兼容表**，不是 2.3.36 的全量注册表；它不含 V2 新增步骤的参数定义。本文的逐项三元组以这 139 个 V1 单元为界。
- V2 步骤契约（`@knowledge/catalog` 下的 `sys.*.yaml`）是 **Quicker 运行时的虚拟挂载**，本机静态资源里没有结构化副本；只能给出键名（§7），不编造参数/输出。
- 输出端口名来自公开文档；文档未列输出的单元在表里写「—」，**不代表该单元运行时不写变量**。按 Quicker 的通用模型，带 `stopIfFail` 的单元通常还有「是否成功」输出。
- 官方文档的输入小节也用 `【…】`，自动提取时少数页会混入输入项；本文已人工剔除明显的输入项，但仍属「文档级」而非「二进制级」证据。
- 全程**未运行 Quicker**、未做动态观测；静态分析不能证明运行期行为。
- `sys:xxxT` 是显示名/翻译键，不是独立功能单元；383 个字符串里约一半是它，已去重。

## 3. 术语对照

| Quicker 侧（仅描述对方） | LTools 侧（`CONTEXT.md`） |
|---|---|
| 模块 / 步骤（Step） | **动作单元**（`IStepUnit` / `UnitSpec`，动作里的一行） |
| 组合动作（XAction） | **动作**（`actions/<id>/action.json`，变量表 + 步骤） |
| 子程序（SubProgram） | **子动作**（`subactions/<id>.json`） |
| 变量（Variable） | **变量**（`${名}` 引用；变量表声明类型） |
| 输入参数（固定值 / 变量 / `$$` 插值 / `$=` 表达式） | **参数**（`StepArg` 三态：文本模板 / `${名}` / 内联取值） |
| 输出（把输出端口绑定到变量） | **输出**（`OutputSpec` 声明 + `Step.Out` 映射，输出声明是唯一来源） |
| 「失败后停止」`stopIfFail` | **失败策略**（`OnFail` / `Retry` / `RetryOn` / `DelayMs`，统一在引擎一处） |
| 动作库 / 分享动作 / 专业版 | 无对应（Quicker 生态专有） |

## 4. Quicker 的输入 / 输出模型（读表前必看）

**输入**：每个参数有三种取法——① 直接选变量；② 填固定值；③ `$$` 插值 / `$=` 表达式。参数自带 `type`（`Text/Number/Integer/Boolean/List/Dict/Table/DateTime/Image/Enum/Any/Object/Form/FormForDict/Keyboard/Form…`）、`defaultValue`、`options`（枚举值）、`variableMode`（`UseVarOrInput` / `Input`）、以及 `visibleExpression`（按其它参数的取值收起/显示，作用类似 LTools 的 `VisibleWhen` / `OptionParams`）。

**输出**：**不是固定变量名**，而是「步骤声明若干输出端口，用户把每个端口绑到自己的变量」。例如 `sys:run` 声明 `PID / 主窗口句柄 / 主窗口标题 / 控制台输出 / stdout输出 / stderr输出 / 退出代码` 七个端口，只绑需要的即可；带 `stopIfFail` 的单元一般还有「是否成功」。失败时输出是否执行不确定，官方建议在循环里先给变量赋空值再用 `是否成功` 判断。

**失败语义**：每个单元几乎都带一个 `stopIfFail` 布尔参数（默认 `True`）——这是 Quicker 的失败开关，LTools 已把它收敛成统一的 `OnFail` + 重试门禁，见配套文档 §6。

## 5. 筛选口径与排除清单

### 5.1 排除 A：需要 Quicker 云服务 / 在线账号 / 在线服务（10 个）

| 单元 | 中文名 | 排除理由 |
|---|---|---|
| `ai` | AI 调用 | 需 API Key / 在线端点（含流式输出窗口） |
| `basic-ocr` | 基础OCR | 主用 QuickerServerOcr 在线接口（有离线模式但非本机能力） |
| `cloud_oss` | 第三方云存储/图床 | 阿里云/腾讯云等对象存储 + 密钥 |
| `clouddata` | 云状态存取 | Quicker 云端状态 |
| `dependencycheck` | 检查和下载依赖 | 从 Quicker 服务下载依赖包 |
| `mathocr` | 公式识别 | Mathpix 在线服务 |
| `recordSound` | 录制声音/语音识别 | 语音识别需服务商账号（录音本身本地，但整单元按在线服务计） |
| `tempcloudstore` | 临时云存储 | Quicker 临时云端存储 |
| `tempImgBed` | 临时图床 | Quicker 临时图床 |
| `translation` | 机器翻译/词典 | 有道/百度/腾讯/彩云在线翻译 |

> `readQrCode`（识别二维码）默认**本地**识别，`tryNetwork` 在线兜底可关，故**保留**并标注。

### 5.2 排除 B：需要第三方软件 / 浏览器扩展 / 外部运行时（11 个）

| 单元 | 中文名 | 排除理由 |
|---|---|---|
| `adobesoftscontrol` | Adobe系列软件控制 | 需本机装 Adobe 软件 |
| `autocadcontrol` | AutoCAD控制 | 需 AutoCAD |
| `rhinocontrol` | Rhino软件控制 | 需 Rhino |
| `chromecontrol` | 浏览器控制 | 需 Chrome/Edge 扩展 + Quicker 桥 |
| `officehelper` | Office软件辅助 | 需 Word/WPS（COM/VBA） |
| `excelObjects` / `excelRange` / `excelreadwrite` | Excel对象/区域/文件读写 | 需 Excel COM 或 NPOI 文档生态（LTools 无此依赖） |
| `everythingsearch` | 使用Everything搜索文件 | 需 Everything 常驻进程 |
| `pythonscript` | 运行Python代码 | 需外部 Python 环境 |
| `dboperation` | 数据库查询 | 需数据库驱动 + 连接串 |

> `webview2` 依赖 Windows 常见的 WebView2 Runtime（非「连接某个第三方软件」），**保留但标注**。`csscript` / `jsscript` 用的是程序自带的 Roslyn / Jint，不算外部依赖，保留。

### 5.3 V1 清单之外、同样按 B 排除的 V2 第三方控制步骤（仅键名，§7 详列）

`aftereffectscontrol`、`photoshopcontrol`、`illustratorcontrol`、`indesigncontrol`、`premierecontrol`、`coreldrawcontrol`、`cinema4dcontrol`、`blendercontrol`、`mayacontrol`、`sketchupcontrol`、`solidworkscontrol`、`revitcontrol`、`mastercamcontrol`、`wpscontrol`、`motuyuncontrol` 等——全部是「控制某个第三方设计/办公软件」，与 A/B 同类，不纳入。

### 5.4 保留但需看标注的边界项

- `http` / `download` / `httpserver` / `websocket` / `smtp` / `openUrl` / `webview2`：**需要网络或外部服务，但都是开放标准协议**，不依赖 Quicker 云、不连接特定第三方软件；保留，在表中标「需网络/外部服务」。
- `getChromeUrl`（获取当前浏览器网址）、`getSysInfo`（含 Quicker 版本/专业版/UnionId 等宿主输出项）：需要浏览器窗口或输出 Quicker 宿主信息，保留但在功能列注明「含宿主专有输出」。
- `quickeroperations` / `runAction` / `subprogram` / `customwindow` / `custompanel` / `showText` / `showmenu` / `form`：是**宿主自身 UI / 动作生态**能力，不是通用系统能力；保留在调研表里，但在配套文档里列为「不建议照搬」。

## 6. 筛选后的功能单元清单（输入 / 功能 / 输出）

共 **118 个**。表中「输入」列是 `V1StepCapabilities.json` 的**全量参数键**（一手 REA 数据）；「输出」列是官方文档声明的输出端口中文名（用户把这些端口绑定到变量）；「—」= 官方文档未列出输出端口。`stopIfFail` 是 Quicker 的失败开关，几乎每单元都有，不再逐一解释。

### 6.1 键鼠与输入（12）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `keyInput` | 模拟按键A（录入） | `holdMs,interval,keys,repeat` | 录入式模拟按键，可设重复次数/间隔/按住时长 | — |
| `sendKeys` | 模拟按键B（参数） | `keys` | 以参数形式发送按键序列 | — |
| `keyoperation` | 按键操作 | `getRealMouseState,keepMs,key,type` | 查询/设置按键状态（含 Caps/Num 锁定） | 是否按下 / 是否锁定 |
| `mouse` | 鼠标输入 | `bmp,bmpColorError,bmpPosition,bmpTargetType,bmpVar,btn,extAction,hWnd,maxFindCount,restoreMousePos,retryCount,searchRect,slowMove,stopIfFail,type,x,xy,xyForWin,y` | 鼠标移动/点击/找图定位/滚轮，一个大杂烩单元 | — |
| `inputScript` | 多步骤输入 | `data,stopIfFail` | 用一段脚本编排多步键鼠输入 | — |
| `outputText` | 发送文本到窗口 | `appendReturn,content,delayAfterPaste,delayBeforePaste,delayBetweenChar,hideInHistory,method,stopIfFail` | 发送文本；支持模拟输入 / 剪贴板粘贴，可设前后延时 | — |
| `waitKeyboard` | 等待按键 | `filterEvent,fontfamily,help,ignoreSimulated,maxWaitSeconds,modifierKeys,mouseThrough,operation,stopIfFail,waitingKeys,waitKeyUp,winLocation` | 阻塞等待指定按键（可拦截原始事件、忽略模拟按键） | 键名 / 键值 |
| `record` | 录制键鼠操作 | `autoStart,prepareSeconds,recordMouseMove,stopIfFail` | 录制键鼠事件流 | 是否成功 / 录制数据 |
| `playRecords` | 重放键鼠操作 | `data,speed` | 按录制数据回放，可调速度 | — |
| `imeControl` | 输入法状态 | `operation` | 读取/设置输入法状态 | — |
| `getSelectedText` | 获取选中的文本 | `format,repeat,stopIfFail,trim,tryNoClipboard,useActionParam,waitMs` | 取当前选中文本（默认走剪贴板 Ctrl+C，可尝试不通过剪贴板） | 内容 / URL编码的内容 / 来源网址 / 是否成功 |
| `textSelectTools` | 辅助选择工具 | `currValue,operation,stopIfFail` | 弹出辅助选择器帮用户挑文本（时间/格式/文件名筛选等） | — |

### 6.2 流程（15）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `assign` | 赋值 | `input,stopIfFail` | 把表达式/值写入变量 | 输出（即赋值结果） |
| `compute` | 计算 | `evalVar,expression,stopIfFail` | 计算表达式 | 结果 |
| `computeTime` | 计算时间 | `addDays,addHours,addMinutes,addMonths,addSeconds,addYears,formatString,stopIfFail,time1,time2,type` | 日期时间加减与格式化 | —（文档未列；推断为结果时间） |
| `numCompare` | 比较数字 | `param1,param2,type` | 两数比较 | 值 |
| `strCompare` | 比较文本 | `case,param1,param2,type` | 两文本比较（可忽略大小写） | 值 |
| `if` | 如果/否则 | `condition` | 条件分支（带否则） | — |
| `simpleIf` | 如果 | `condition` | 单分支条件 | — |
| `each` | 每个 | `concurrentThreadNum,input,progressBarTitle,stopIfFail,threadDelay,timeoutMs,useLocalContext,useMultiThread,waitAny` | 遍历列表（可多线程） | 项 / 计数 |
| `repeat` | 重复 | `count,progressBarTitle,repeatDelayMs,startIndex,stopCondition` | 固定次数循环，可设中止条件 | 计数 |
| `break` | 跳出循环(break) | — | 跳出循环 | — |
| `continue` | 跳过后续步骤(continue) | — | 进入下一轮 | — |
| `stop` | 停止(return) | `isError,method,return,showMessage` | 结束动作并可带返回值/提示 | — |
| `delay` | 等待时间 | `delayMs,monitorWaitWin` | 等待指定毫秒 | — |
| `group` | 步骤组 | `skipErr,skipWhenDebugging,useMultiThread,waitAny` | 把多步打包成组（可忽略错误、多线程） | — |
| `subprogram` | 运行子程序 | `skipDebugOutput,stopIfFail,subProgram,summary` | 调用子程序 | — |

> `comment`（注释，参数 `note`）归在 §6.9 界面/杂项；`reportProgress` 同。

### 6.3 文本与数据（21）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `formatString` | 组合成文本 | `formatString,p0,p1,p2,p3,p4` | 按格式化串拼文本 | — |
| `splitString` | 拆分文本为列表 | `data,escapeSeparator,multiSeparator,removeEmpty,separator` | 按分隔符拆成列表 | —（推断为列表） |
| `joinList` | 列表合并成文本 | `escapeSeparator,list,separator` | 列表按分隔符拼成文本 | 结果 |
| `stringProcess` | 文本处理 | `data,dstEncoding,length,method,paddingChar,srcEncoding,start,stopIfFail,totalWidth,value` | 大小写/截取/填充/转编码等 | 结果 |
| `strReplace` | 替换文本 | `batchReplaceData,escapeOld,ignoreCase,input,multiLine,new,old,replaceEscapes,singleLine,type,useRegex` | 普通/批量/正则替换 | — |
| `regexExtract` | 正则提取 | `data,getGroup,ignoreCase,multiLine,pattern,rightToLeft,singleLine,stopIfFail` | 正则匹配与捕获组提取 | 所有匹配列表 / 各捕获组 / Matches集合 / Match对象 |
| `jsonExtract` | 提取JSON内容 | `data,dateAsString,p0,p1,p2,p3,p4,stopIfFail` | 按多条 JSONPath 提取 | 值n（每个路径一个）/ 根对象 |
| `htmlExtract` | 提取HTML内容 | `attribute,encoding,operation,returnType,selectTarget,source,stopIfFail,writeToSheet,xpath` | 用 XPath 从 HTML/XML 提取节点文本/属性/节点对象 | 值 |
| `charInfo` | 获取字符信息 | `char` | 取字符的 Unicode 编码与拼音 | Unicode编码（数字）/ 拼音首字母 / 拼音 / 拼音首字母（全部）/ 拼音（全部） |
| `textCounter` | 字数统计 | `content` | 统计文本行数/字符数 | 行数 / 字符数 / 可见字符数 |
| `numberprocess` | 数字转换与处理 | `decimalPlace,operation,roundingMethod,srcBase,srcNumber,srcNumberStr,stopIfFail,toIntegerMethod` | 数字格式化/取整/进制转换 | 结果文本（不含逗号）/ 结果文本（含逗号）/ 结果文本（百分比）/ 结果整数 / 十六进制数 / 二进制值 |
| `enc` | 加密/解密/哈希 | `cipherMode,hashType,hmacAlgorithm,input,inputContentType,iv,ivContentType,key,keyContentType,operation,paddingMode,pairKey,stopIfFail` | DES/AES/RSA 加解密、MD5/SHA 哈希、HMAC、自用加密 | Base64编码结果 / 十六进制编码 / 文本结果 |
| `dictOperations` | 词典操作 | `dict,ignoreCase,key,queryString,returnEmptyIfKeyNotExist,stopIfFail,type,value` | 词典增删改查、查询字符串解析 | 结果 / 是否成功 |
| `listOperations` | 列表操作 | `item,length,list,list2,orderByScore,pattern,pos,type` | 列表增删/排序/查找/正则匹配 | 是否为空 / 列表长度 / 结果 |
| `manageList` | 管理和排序列表 | `allowAdd,allowDelete,allowEdit,help,list,note,parseData,seperator,stopIfFail,titleDelegate,windowSize,winTitle` | 弹窗让用户可视化编辑/排序列表 | 是否确认 |
| `tableoperation` | 表格数据操作 | `clearOldRows,csvDelimiter,dataText,deleteColumns,excelFilePath,exportColumns,filterExpression,gridSelectionMode,helpText,isReadOnly,rowData,sheetName,sort,startRowNum,stopIfFail,table,topMost,type,useColumnTitle,windowTitle,winSize` | 表格变量查看/编辑/筛选/排序/CSV/Excel 导入导出 | 是否成功 / 行数据 / 行数 / 行列表 / 第一行 |
| `color` | 屏幕取色/颜色转换与计算 | `colorStr,format,location,stopIfFail,type` | 解析颜色串、屏幕取色、颜色格式转换与计算 | 文本值 |
| `randomNum` | 生成随机数 | `max,min` | 生成区间内随机数 | 随机数 |
| `newGuid` | 生成Guid | `format,upper` | 生成 GUID（可指定格式/大写） | 内容（GUID 文本） |
| `getCurrentTime` | 获取日期时间 | `addDays,addHours,addMinutes,addMonths,addSeconds,format,inputCulture,inputFormat,outputCulture,source,stopIfFail,timeStampStr,timeStr,timeVar,useUtc` | 取当前时间/解析时间串/时间戳互转/偏移 | 原始值 / 文本值 |
| `stateStorage` | 状态存取 | `actionContextMenu,badgeColor,badgeText,badgeTextColor,defaultValue,inputIfEmpty,key,overlayIcon,prompt,type,value` | 跨运行读写动作状态/全局状态，可带徽标与右键菜单 | 是否成功 / 值 / 是否为空 |

### 6.4 剪贴板（6）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `getClipboardText` | 获取剪贴板文本 | `customFormat,encoding,format,stopIfFail,waitMs` | 读剪贴板文本（可指定格式/编码/重试） | 结果内容 / 是否成功 |
| `getClipboardImage` | 获取剪贴板图片 | `stopIfFail` | 读剪贴板图片 | 结果图片 / 是否成功 |
| `getClipboardFiles` | 获取剪贴板文件列表 | `stopIfFail` | 读剪贴板文件列表（`CF_HDROP`） | 文件列表 / 是否成功 |
| `writeClipboard` | 写入剪贴板 | `customFormat,fastMode,html,imageVar,input,stopIfFail,successMsg,text,type` | 写文本/图片/HTML/自定义格式到剪贴板 | — |
| `fileToClipboard` | 文件放入剪贴板 | `file,list,stopIfFail,useCut` | 把文件（列表）放剪贴板，可标记剪切 | — |
| `waitClipboardChange` | 等待剪贴板内容改变 | `maxWaitSeconds,monitorWaitWin,recentChangeMs,stopIfFail` | 等待剪贴板变化，常配合「复制」后同步；官方文档提示等待结束后 Ctrl/C 可能未抬起 | 是否改变（文档正文；自动提取未带【】） |

### 6.5 文件（16）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `readFile` | 读取文件 | `encoding,path,stopIfFail,type` | 按文本/图片/字节读文件 | 文本内容 / 图片内容 |
| `WriteTextFile` | 写入文本文件 | `addNewLine,addUtf8Bom,appendMode,content,encoding,filePath,newLineChars,stopIfFail` | 写文本文件（编码/BOM/追加/换行统一） | — |
| `WriteImageFile` | 写入图片文件 | `content,filePath,quality,stopIfFail` | 图片变量存成文件（可设质量） | — |
| `checkPathExists` | 检查路径/获取文件信息 | `path` | 判断存在性 + 取文件属性/时间/大小/哈希/快捷方式目标 | 路径是否存在 / 是否为文件夹 / 是否为文件 / 文件长度 / 是否只读 / 是否隐藏 / 是否为系统文件 / 文件夹内文件个数 / 文件夹大小 / 创建时间 / 更新时间 / 文件扩展信息 / lnk目标路径 / lnk命令行参数 / MD5 / SHA1 / SHA256 / CRC32 哈希值 |
| `GenTempFilePath` | 生成临时文件路径 | `ext` | 生成临时文件路径 | 文件路径 |
| `fileOperation` | 文件和目录操作 | `dstPath,isAll,overwrite,path,searchPattern,stopIfFail,type` | 复制/移动/删除/重命名/枚举文件目录（`type` 决定操作） | —（推断为路径列表/是否成功） |
| `pathExtraction` | 提取文件路径信息/生成路径 | `newDir,newExtension,newFileName,newFileNameWithoutExt,operation,path,path2,path3,path4,stopIfFail` | 拆路径各部分，或按新名字/目录/扩展名重组路径 | 文件名 / 文件名（去掉扩展名）/ 路径 / 扩展名 / 结果路径 |
| `zip` | Zip压缩打包 | `comment,keepBaseFolder,level,outputPath,overwrite,password,showProgress,skipOverwriteError,sourcePath,sourceZipFile,stopIfFail,targetZipFile,type` | 压缩/解压 zip，可加密码、备注、进度 | 是否成功 / 结果路径 |
| `fileSystemWatch` | 文件系统监控 | `changedCallback,createdCallback,deletedCallback,filter,includeSubdirectories,notifyFilter,operation,path,renamedCallback,stopIfFail,waitEvents,waitSeconds` | 监控目录变化并回调子程序 | — |
| `selectFile` | 选择文件 | `defaultExt,filter,initDir,initFileName,stopIfFail,title,topMost,type` | 弹文件选择框（打开/保存） | 是否成功 / 路径 / 路径列表 |
| `selectFolder` | 选择文件夹 | `initDir,prompt,showOpenedDirs,stopIfFail` | 弹文件夹选择框 | 是否成功 / 路径 |
| `SelectFileInExplorer` | 在资源管理器中定位文件 | `path,stopIfFail` | 在资源管理器里选中/定位文件 | — |
| `getSelectedFiles` | 获取选择的文件(夹) | `operation,pathList,sortType,stopIfFail,waitMs,winHandle` | 取资源管理器当前选中的文件（夹） | 是否成功 / 路径列表 / 首个路径 / 文件(夹)名列表 / 单个文件(夹)名 / 文件个数 |
| `getExplorerPath` | 获取资源管理器路径/跳转路径 | `operation,path,stopIfFail` | 取当前/全部/最近访问的资源管理器路径 | 当前窗口路径 / 所有打开的路径 / 最近访问的路径 |
| `getFolderPath` | 获取系统路径 | `folder` | 取系统特殊目录（桌面/文档/临时等） | 路径 |
| `shelloperation` | Shell文件操作 | `operation,pathList,pathOrExt,stopIfFail,title,verb` | 查询/调用文件的 Shell 动词（右键菜单项） | 动词列表 |

### 6.6 网络（6，均需网络/外部服务，但不依赖 Quicker 云）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `http` | HTTP请求 | `body,bodyType,contentType,cookie,expireSeconds,forceProxy,header,method,noAutoRedirect,resultType,showProgress,skipCertVerify,sseSpName,stopIfFail,ua,url,useSSE` | 任意 HTTP 请求，支持 SSE 流式、代理、证书忽略 | 是否成功 / 状态码 / 响应头 / 响应Cookie / 文本结果 / 图片结果 |
| `download` | 下载文件 | `autoRename,cookie,expireSeconds,header,saveName,savePath,showProgress,skipCertVerify,stopIfFail,ua,url` | 下载文件到磁盘（进度、自动重命名、请求头/Cookie） | 文件路径 / 内容MD5 / ETag / 是否成功 |
| `httpserver` | HTTP服务器 | `autoShutdownSeconds,bodyCode,customRequest,defaultDoc,docPath,enableHttps,headCode,operation,password,port,serviceId,showNotifyWhenAutoClose,stopIfFail` | 本机起 HTTP/静态文件服务，可自定义请求处理 | 服务地址 / 带账号的地址 / 服务ID |
| `websocket` | Websocket | `account,callbackOnClose,clientId,content,cookie,operation,origin,server,spName,stopIfFail` | 建 WebSocket 客户端/服务端，消息回调子程序 | — |
| `smtp` | SMTP发送邮件 | `account,attachList,bcc,cc,content,isHtml,password,port,sender,senderName,server,stopIfFail,subject,to,useSsl` | 发邮件（附件/抄送/HTML/SSL） | 是否成功 |
| `openUrl` | 打开网址 | `browser,exePath,stopIfFail,url` | 用指定浏览器打开网址 | — |

### 6.7 窗口与程序（12）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `run` | 运行或打开 | `activateWindowHotkey,activateWindowIfRunning,alternativePath,arg,envVariables,outputEncoding,password,path,runas,setWorkingDir,stopIfFail,username,waitExit,waitInputIdle,windowStyle` | 启动程序/打开路径/执行命令（管理员、工作目录、等待退出、控制台输出编码） | PID / 主窗口句柄 / 主窗口标题 / 控制台输出 / stdout输出 / stderr输出 / 退出代码 |
| `runScript` | 运行脚本 | `argTemplate,encoding,ext,outputEncoding,runAsAdmin,runner,script,scriptParams,type,waitToExit,workingDir` | 把脚本文本落临时文件后交给解释器运行（CMD/PowerShell 等） | — |
| `activateProcessMainWindow` | 激活进程主窗口 | `className,hotkey,path,process,stopIfFail,windowTitle` | 按进程/标题/类名激活窗口 | 是否成功 / PID / 主窗口句柄 / 主窗口标题 |
| `checkProcessExists` | 检查程序已启动/获取进程信息 | `process,stopIfFail` | 查进程是否运行并取进程信息 | 操作是否成功 / 是否运行 / 进程ID / 所有进程id列表 / 程序路径 / 主窗口句柄 / 主窗口标题 / 启动时间 |
| `getActiveProcessInfo` | 获取前台进程信息 | `stopIfFail` | 取前台窗口所属进程 | 程序路径 / 进程名 / PID / 是否成功 |
| `getWindowTitle` | 获取窗口信息/查找窗口 | `className,hWnd,onlyVisible,procIdOrName,requireTitle,stopIfFail,useRegex,which,windowName,winRectIncludeInvisibleBorder` | 按标题/类名/进程查找窗口 | —（推断为窗口信息） |
| `windowOperations` | 窗口操作 | `alpha,area,borderColor,height,hWnd,showCmd,stopIfFail,type,width,x,y` | 移动/缩放/最大化/最小化/透明度/边框等（`type` 决定） | 是否成功 |
| `sendMessage` | 向窗口发送消息 | `hWnd,lParam,operation,stopIfFail,textLParam,wMsg,wParam` | 向窗口 `SendMessage/PostMessage` | — |
| `uiautomation` | 窗口界面控制 | `autoCreateDir,control,controlOperation,controlType,expandDelay,menuPath,path,pointLocation,stopIfFail,type,value,window` | 用 UI Automation 找控件并触发菜单、设值、点击 | — |
| `flauiautomation` | 窗口界面控制(FlaUI) | `control,controlOperation,controlType,expandDelay,menuPath,pointLocation,stopIfFail,type,value,window` | 同上，FlaUI / XPath 版本（同一能力的两代实现） | — |
| `restoreActiveWindow` | 恢复活动窗口 | — | 恢复动作开始时的活动窗口 | — |
| `getChromeUrl` | 获取浏览器网址 | `stopIfFail` | 取当前浏览器地址栏网址（需浏览器窗口） | —（推断为网址） |

### 6.8 屏幕（8）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `screenCapture` | 屏幕截图 | `area,delay,includeRightBottomBorder,preSelectArea,stopIfFail,toClip,type,windowHandle` | 框选/窗口/全屏/固定区域截图，可写剪贴板 | 图片 / 截图区域 / 窗口句柄 |
| `searchBmp` | 屏幕找图/找色/找字 | `bmp,bmpColorError,bmpPosition,bmpTargetType,bmpVar,color,ignoreBgColor,ignoreWindowsOcr,maxFindCount,retryCount,searchRect,searchText,stopIfFail,type,x,y` | 在屏幕上找图/找色/找文字坐标 | 是否成功 / 第一个匹配点 / 所有匹配点 |
| `imgProcess` | 图片处理 | `filterParams,iconFilePath,iconSize,img,maxHeight,maxWidth,resizePercent,rotation,stopIfFail,type` | 缩放/旋转/反色/灰度/组合滤镜/生成 ico | 结果图片 |
| `imgToBase64` | 图片/Base64 转换 | `addHeader,base64,img,type` | 图片 ↔ Base64 互转，可加 data 头 | Base64编码 / 图片 |
| `imageinfo` | 读取图片信息 | `autoRotate,bmpFile,bmpVar,sourceType,stopIfFail` | 读图片宽高/拍摄时间/Exif | 宽度 / 高度 / 拍摄时间 / Exif数据 |
| `createQrCode` | 生成二维码 | `code,darkColor,drawQuietZones,icon,iconBorderWidth,iconPercent,lightColor,pixelsPerModule,saveToPdfPath,stopIfFail` | 文本生成二维码图片（颜色/图标/尺寸/导出 PDF） | 二维码 |
| `readQrCode` | 识别二维码 | `img,stopIfFail,tryNetwork` | 本地识别图片中的二维码/条码（`tryNetwork` 在线兜底可关） | 值 / 全部二维码值 / 条码类型 |
| `whiteboard` | 手写板 | `bgColor,enableTransparent,imageWithBackground,penColor,stopIfFail,winPosition` | 全屏手写/标注板 | 结果图片 |

### 6.9 界面 / 交互（14）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `MsgBox` | 弹窗提示或确认 | `buttons,customButtons,customIcon,defaultButton,icon,message,operation,restoreFocus,title` | 弹消息框/确认框，支持自定义按钮 | 选择的按钮 / 是否确认 / 选中的按钮 |
| `notify` | 提示消息 | `clickAction,maxLines,msg,style,type` | 右下角通知（可点击执行命令） | — |
| `userInput` | 用户输入 | `closeOnDeactivated,defaultValue,extraSettings,fontfamily,fontsize,help,imeState,isRequired,pattern,prompt,restoreFocus,stopIfFail,submitWithReturn,texttools,topMost,type,winLocation` | 单行/多行/密码/下拉输入，可校验正则 | —（推断为输入值/是否确认） |
| `select` | 用户选择 | `allowOkWhenEmpty,autoCloseSeconds,closeOnDeactivated,defaultValue,defaultValueMulti,enableQuickConfirm,filterContent,fontfamily,fontsize,help,iconsize,imeState,items,keepLastPos,maxWinSize,noKeyboard,note,operations,prompt,restoreForeground,showFilter,stopIfCancel,topMost,type,windowKey,winLocation` | 单选/多选列表，可搜索筛选、快速确认、自动关闭 | 是否选择 / 选择的项 / 索引号 / 选择的项列表 / 选择的菜单 / 选择的完整选项 / 选择的选项标题 |
| `showmenu` | 显示菜单 | `fontsize,iconsize,maxHeight,menuData,stopIfFail,useFocus,waitMenuClose` | 弹出（树形/图标）菜单，选中项带数据 | —（推断为选中项数据） |
| `showText` | 文本窗口 | `advancedSettings,autoCloseKey,autoSaveToState,autoWrap,bgColor,caretPosition,closeWhenLostFocus,copyWholeLine,enableEscClose,fontfamily,fontsize,highlight,operations,showBuildInToolbar,showLineNum,stopIfFail,text,textColor,title,topMost,type,updateIfExists,winLocation,winSize` | 可编辑/只读文本窗口，语法高亮、行号、工具栏、 Esc 关闭 | 结果文本（可编辑模式） |
| `showWaitWin` | 显示等待窗口 | `activateMode,autoCloseSeconds,btnText,fontsize,help,iconSize,mode,operations,progress,prompt,stopActionIfClose,title,winLocation` | 常驻「点完成继续」等待窗，可带进度与附加按钮 | — |
| `showImage` | 显示图片 | `autoCloseKey,autoCloseTime,closeCallbackParam,closeWhenLostFocus,imgVar,noActivate,opacity,path,scale,showDropShadow,showTaskbarIcon,source,tooltip,topMost,waitClose,winLocation,winPosition` | 悬浮显示图片（缩放/透明度/自动关闭） | 窗口句柄 |
| `form` | 多字段表单 | `confirm,customButtons,defaultInputWidth,dictVar,disableEnterSubmit,dynamicFormForDictDef,formDef,formForDictDef,help,markdownhelp,operation,restoreFocus,selectedGroup,stopIfFail,title,titleColumnWidth,topMost,windowHeight,windowWidth,winLocation,winSize` | 用表单定义一次收集多个字段 | —（推断为各字段值/是否确认） |
| `customwindow` | 自定义窗口 | `activateMode,autoCloseTime,closeWhenDeactivate,cscode,dataMapping,events,stopIfFail,type,windowId,windowMarkup,winLocation,winSize` | 用 XAML 自定义窗口 + 数据映射 + 事件 | 是否成功 / 窗口结果 |
| `custompanel` | 自定义操作窗 | 26 项（颜色/列数/菜单数据/位置尺寸/记忆状态等） | 常驻自定义按钮面板（可折叠、关联进程） | — |
| `reportProgress` | 显示进度条 | `percentage,progressId,text,title,type` | 显示/更新进度条 | — |
| `comment` | 注释 | `note` | 纯注释步骤 | — |
| `webview2` | WebView2浏览器窗口 | 28 项（URL/HTML、JS、Profile、下载目录、虚拟主机映射等） | 内嵌浏览器窗口，可与动作互发消息（需 WebView2 Runtime） | — |

### 6.10 系统信息 / 脚本 / 宿主生态（8）

| 单元 | 中文名 | 输入（参数） | 功能 | 输出（变量） |
|---|---|---|---|---|
| `getSysInfo` | 获取系统或动作信息 | — | 取机器/系统/环境/动作运行信息 | 机器名 / 用户名 / 系统版本号 / 是否为Win10或以上 / 是否为Win11 / 是否自动启动 / 系统正常运行秒数 / 环境变量 / 主屏分辨率 / Quicker版本 / 是否为专业版 / UnionId / Quicker启动秒数 / 动作ID / 动作名称 / 动作库ID / 动作版本号 / 运行个数 / 是否调试运行 / 触发方式 |
| `winservice` | Windows服务和注册表 | `name,operation,regKeyPath,regValueName,stopIfFail` | 查服务状态/服务列表、读注册表项 | 服务是否存在 / 显示名 / 服务状态 / 是否存在 / 值 |
| `audioControl` | 音频设备 | `deviceName,id,mute,operation,returnAll,stopIfFail,volume` | 枚举/切换默认音频设备、音量、静音 | 设备信息列表 / 原始对象列表 / 默认输出设备信息 / 默认输入设备信息 |
| `playSound` | 播放声音 | `localSound,stopIfFail,text,type,uri,wait` | 播内置提示音/本地文件/URL，或文本转语音 | — |
| `quickeroperations` | Quicker操作 | `actionId,activatePointWindow,exe,followMousePosition,position,profileId,searchText,skinId,stopIfFail,theme,type,viewMode` | 显示面板/搜索框、切场景/动作页/外观（**宿主专有**） | — |
| `runAction` | 运行或停止动作 | `actionId,debug,hideMessage,inputParam,onlyCustomMenu,stopIfFail,type,wait` | 启动/停止另一个 Quicker 动作（**宿主专有**） | 是否成功 / 动作名称 / 动作输出 |
| `csscript` | 运行C#代码 | `enableCache,mode,paramValue,reference,runOnUiThread,script,scriptForAssembly,scriptForLp,stopIfFail,waitMs,waitResp` | 编译运行 C# 脚本，通过 `context.SetVarValue` 读写变量 | 返回内容 |
| `jsscript` | 运行Javascript代码 | `allClr,script,stopIfFail` | Jint 运行 JS，可访问 .NET 程序集 | 是否成功 / 返回值 |
## 7. V1 清单之外：V2 新增步骤（只有键名，无本机结构化参数）

字符串扫描在 139 个 V1 单元之外还发现以下 `sys:*` 键。它们的参数契约由 Quicker 运行时的 `@knowledge/catalog`（虚拟挂载 / 云端知识包）提供，本机静态资源里没有副本，**因此不给三元组、也不纳入 §6 的 118 个**：

- **AI 系列（云服务，按 A 排除）**：`aiChat`、`aiClassify`、`aiExtract`、`aiText`、`aiVision`
- **脚本 / 表达式（宿主能力）**：`automationScript`、`evalexpression`、`csharpFileApp`、`browser`、`toast`、`log`、`key`、`pdf`、`remoteFile`
- **屏幕 / 截图增强**：`screenCapturePro`、`screenRecording`、`screenRecordingUI`、`longScreenshot`、`pinImage`、`textWindowPlacement`、`select_window_location`
- **系统增强**：`waitWindow`、`virtualDesktop`、`monitorBrightness`、`mobileNotification`、`getActionInfo`、`getQuickerInfo`、`getSystemInfo`
- **UI 增强**：`reactui`
- **流程关键字**：`else`、`elseif`
- **第三方软件控制（按 B 排除）**：`aftereffectscontrol`、`photoshopcontrol`、`illustratorcontrol`、`indesigncontrol`、`premierecontrol`、`coreldrawcontrol`、`cinema4dcontrol`、`blendercontrol`、`mayacontrol`、`sketchupcontrol`、`solidworkscontrol`、`revitcontrol`、`mastercamcontrol`、`wpscontrol`、`motuyuncontrol`

其中 `screenCapturePro` / `automationScript` / `reactui` / `waitWindow` 等在安装目录 `agent/knowledge/steps/` 下有**面向 AI 写动作的说明文档**（参数片段），可佐证它们确实存在于 2.3.36，但仍不是结构化契约。

## 8. 数据产物与复现

中间产物（本次分析生成，位于 `%TEMP%\opencode\`，**不进仓库**）：

| 文件 | 内容 |
|---|---|
| `V1StepCapabilities.json` | 从 `Quicker.dll` 资源区导出的 139 模块能力表（原始） |
| `modules_summary.txt` | 139 模块的中文名 + 全量参数（`key:中文名:类型=默认值`） |
| `quicker_outputs.txt` | 146 个官方模块页提取的输出端口 |
| `merged_units.txt` | 输入 + 输出合并底稿（139 行） |

复现要点：

1. `rea.inspect_managed_artifact` 需把 `max_metadata_representation_bytes` 调到 ≥ 260000000，否则 21MB 元数据会被判 `partial`。
2. 资源区文件偏移 = `CLI Header.ResourcesDirectory.RVA → 所在节 PointerToRawData` + `ManifestResource.Offset`，数据前 4 字节是小端长度。
3. `quicker.db` / `localdata.db` 是加密 SQLite（带 `.qkey`），REA 的 `inspect_sqlite_database` 读不了，且属于用户数据，与步骤定义无关。
4. 官方文档有按请求数的限流；批量抓取需节流（本次用 260ms 间隔、146 页一次成功 145 页）。
