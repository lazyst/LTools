# 测试（tests/）

本目录放**仓库内**的端到端冒烟工程。产品自己的纯逻辑断言不在这里，在 `Core/SelfTest/`（`LTools.exe --selftest`）。

组织参照 snow-shot：每个模块旁边挂着 `#[cfg(test)] mod tests`（纯逻辑、随源码走），
跨模块的整链路用例放各 crate 的 `tests/`（真文件 / 真窗口 / 真进程）。对应到本项目：

| snow-shot | LTools | 跑法 |
|---|---|---|
| 模块内 `#[cfg(test)] mod tests` | `Core/SelfTest/Suites/*.cs`（按功能分文件：`CaptureProSuite.Drawing.cs`、`CaptureProSuite.Behavior.cs`…） | `LTools.exe --selftest`，单组 `--filter capturePro` |
| crate 的 `tests/` 集成用例 | `tests/<工程>/`（本目录，反射驱动 `LTools.dll`） | `dotnet run -- <段名>` |
| 人工真机验收 | 本文「人工验收」 | 手动 |

## 1. 自测（CI 门禁）

```powershell
dotnet build -c Release            # 或 Debug：dotnet build -p:NoWin32Manifest=true
LTools.exe --selftest              # 退出码非 0 = 失败，报告在 %TEMP%\ltools-selftest\report.txt
LTools.exe --selftest --filter capturePro --verbose
```

- 纯逻辑：几何、像素、归一化、解析、字节序列、序列化往返。
- **不碰真窗口、真鼠标、真光标、剪贴板、文件系统以外的东西**（临时文件可以有）。
- 新增功能时先在这里补断言：能变成纯函数的部分都搬成纯函数。

## 2. 冒烟（本目录，不进 CI）

`tests/` 下的工程**不进 CI**（Windows runner 没有交互桌面、没有真实光标，也会拖慢每次 push）。
它们是「构建 0 警 + selftest 全绿」之后的第三道门，日常改完截图 / 贴图 / 录屏要自己跑。

```powershell
cd tests\CaptureOverlaySmoke
dotnet build -v q
dotnet run --no-build                 # 全量（约 10 分钟）
dotnet run --no-build -- behavior     # 只跑一段（改哪块跑哪段）
```

输出写到 `%TEMP%\opencode\capture-overlay-smoke\out\`（截图 / PDF / 配置夹具都在这，不入库）。

### `CaptureOverlaySmoke` 的段

| 段名 | 覆盖 |
|---|---|
| `behavior` | 截图行为：指针 / Esc 退出确认 / 拖边框（快速选择修改 / 贴到指针）/ HiDPI 降采样 / PDF 纸张（真文件）/ 设置页七控件往返 |
| `drawing` | 绘制行为：默认值 / 工具清单窗口 / 自动选中 / 快速选择 / Delete 删字符 / 组首按钮 |
| `colorpresets` | 颜色预设：三族 15 色 / 设置页 / 跟随工具切换 / 取色弹窗（色板·色相条·透明度条·HEX/RGB/HSB） |
| `regiondrag` | 多块选区：拖命中的那一块 / 按住 Space 整体挪 / 中途换口径 / 外接框按并集 |
| `historydisplay` | 采集历史轮播：底图落盘 / 每块屏裁图 / 卡片与大图翻张 / 重新编辑还原选区·外观·标注·撤销栈 |
| `split` / `collage` / `collagepdf` | 切图 / 拼图 / 拼图 PDF |
| `history` / `hidetotop` / `pinocr` / `clipboardmeta` | 采集历史 / 贴图收顶部 / 贴图识别 / 剪贴板元数据 |
| `record` / `webcam` / `autohide` / `render` / `globalscreen`(`globalcanvas`) | 录屏：区域框 / 选项 / 画中画 / 自动收起 / 后期渲染 / 全屏画布 |
| `recordsettings` | 录屏设置：设置页回显 / 改动落 `settings.json` / 归一 / 全局默认值进计划 |
| `keys` / `shortcuts` / `export` / `sound` / `captureEntry` / `releaseAction` | 键名 / 全局快捷键 / 导出 / 快门音 / 采集入口 / 释放动作 |
| `tray` | 托盘菜单：顶层条目与顺序 / 截图项收进二级菜单 / 真鼠标悬停展开与移开收起 |
| `recapture` | 重新截图（原位重抓）：选区 / 标注 / 撤销栈保留 / 只换底图 / 覆盖层不进画面 |
| `result` | 识别结果窗：页签与各页内容 / 最小化·最大化（铺满工作区）/ 原文栏（1:1 左右 · 上下 · 缩放 + 弹出）/ 排版 / 翻译 / 文档导出 |
| `picker` | 取色 / 坐标卡：选择工具下跟着光标 / 11×11 放大预览 / X·Y 坐标 / 色带与字色 / 显示时机 / Shift+P 相对坐标 |
| 不带参数 | 上面全部 + 覆盖层主链路（选区 / 标注 / 样式 / 多选 / 取色·坐标卡 / 贴图 / 识别 / 转换 / 翻译 / PDF…） |

### 写法约定

- `Check(条件, "中文断言文案")`：失败只记一条，不中断（最后汇总失败数）。
- 用 `Field` / `Member` / `Invoke` 反射摸 internal 类型，**不复制产品代码**。
- **调带可选参数的方法一律走 `CallStatic(type, "名字", 实参…)`**：反射不会替可选参数补默认值，
  `Capture(area, withCursor = false)` 用 1 个参数 `Invoke` 会抛 `TargetParameterCountException`
  （产品给方法加了可选参数、冒烟还按旧数量调用时，会跑到一半才炸）。
- 需要配置的段把 `ConfigStore.ConfigPath` 指到 `out\<段名>-config\`，跑完恢复原路径并 `Invalidate()` 缓存，
  不给后面的段留脏配置（见 `Program.Drawing.cs` / `Program.Behavior.cs` 的头尾）。
- 断言「看得见的行为」：几何数字、像素颜色、落盘文件字节、控件状态、光标 / 窗口真实响应。
- 每段自带 `-- <段名>` 入口：调试时只跑一段，别为了改一行等十分钟。

## 3. 人工验收

自动化覆盖不到的部分（真机 DPI、真鼠标手感、系统对话框、托盘、开机自启）改完仍要点一遍：

- 125% / 100% 两种缩放下截图与拖边框；
- 截图带指针（开关前后各截一张看指针在不在）；
- Esc 退出确认（开关前后）；
- 另存为 PDF（A4 竖 / 横 / 图片尺寸各存一份，用 Edge 打开看版面）；
- 长截图自动滚动间隔（改 300ms 看是否更慢）。
