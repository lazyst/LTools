# LTools 资源目录与配置备份（导出 / 导入）

> **状态**：已实现（待手测）　**关联**：默认工作目录已落地（commit `b1d28be`）；SQLite 评估后决定**暂不引入**。
>
> **后续变更（2026-11，见 `docs/ARCHITECTURE.md`）**：配置已拆成 `configs/*.json`（P1），动作改成一个动作一个目录
> `actions/<id>/`（P2），运行期截图改落 `cache/screenshots/`、采纳时素材按内容哈希硬链接去重、
> 启动时回收动作目录孤儿图、备份改为「目录白名单 + manifest」并支持导出单个动作 `.ltact`（P5）。
> 本文下面关于 `LTools.json` 的段落是当时的实现记录；备份现在总是打包
> `configs/` + `actions/`，再按勾选加 `resources/` + `速记/`，另有 `macros/` 与 `staging/`；**不再兼容旧单文件备份**
> （导入只认 `configs/`，旧版 `LTools.json` 备份会被 `Inspect` 拒绝）。
>
> **背景**：用户希望在应用目录下放一个「资源目录」存放脚本 / 程序等，`runCommand` 动作可用相对路径引用；并新增配置备份的导出 / 导入，一个包同时备份设置与资源目录（含速记）。

---

## 1. 目标与非目标

### 1.1 目标

- 提供用户自有的 `resources/` 目录，存放脚本 / 可执行文件 / 其他素材。
- `runCommand` / `launchApp` 可用相对路径引用其中文件（默认工作目录已是应用目录）。
- 配置备份：导出 / 导入 `.ltbak` 包，含配置（`configs/` + `actions/`）+ `resources/` + `速记/`（速记可取消勾选）。
- 导入时弹安全警告并列出引用 `resources/` 的动作（防投毒）。

### 1.2 非目标

- 不引入 SQLite / 数据库。
- 不做云同步、版本历史、增量备份。
- 不对 `resources/` 做沙箱或权限隔离（与应用同信任域）。
- 不修改 `速记/` 的存储格式（仍为文本文件树）。

---

## 2. 数据根目录（DataRoot）—— 前置重构

当前 `AppPaths.AppDirectory` = `AppContext.BaseDirectory`，dev 下落到 `bin/Debug/net10.0-windows`，与 `ConfigLocator` 的 dev（仓库根）不一致。三样用户数据（配置 / 资源目录 / 速记）应共用同一根，故引入 **`DataRoot`**：

| 布局 | `DataRoot` |
|---|---|
| dev（仓库根含 `LTools.csproj`） | 仓库根 |
| prod | `exe` 同级（`AppContext.BaseDirectory`） |

`AppPaths` 重构：

```csharp
public static string AppDirectory => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory); // exe 所在
public static bool IsDevLayout => File.Exists(Path.Combine(RepoRoot, "LTools.csproj"));
public static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
public static string DataRoot => IsDevLayout ? RepoRoot : AppDirectory;        // 用户数据根
public static string ResourceDir => Path.Combine(DataRoot, "resources");
public static string DefaultWorkdir() => Directory.Exists(DataRoot) ? DataRoot
    : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
```

- `ConfigLocator` 的 `RepoRoot` / `IsDevLayout` 改为委托 `AppPaths`，删除重复。
- `DefaultWorkdir` 由 `AppDirectory` 改为 `DataRoot`（行为变化：dev 下默认工作目录由 `bin/Debug` 变仓库根——已与用户确认）。
- `ResolveWorkdir` / `ResolveTarget` / `ToRelativeIfUnderDataRoot`（见 §8）统一解析相对路径、相对工作目录，并把数据根下的绝对路径自动转成相对路径。
- **终端经 Windows Terminal（`wt.exe`）包装时，新标签页不继承 `ProcessStartInfo.WorkingDirectory`**（用 profile 默认目录）。故 `TerminalLauncher` 的 wt 分支一律用 `new-tab --startingDirectory "<解析后的工作目录>"`，含工作目录留空时（此时为 `DefaultWorkdir`），否则 `pwd` 会显示 PowerShell profile 默认目录而非默认工作目录。
- `速记/` 路径：`QuickNote.ComputeDefaultDir` 已是「配置文件所在目录 + 速记」，与 `DataRoot` 一致，无需改。

---

## 3. 资源目录 `resources/`

- 路径：`AppPaths.ResourceDir` = `DataRoot/resources`。
- 启动时 `EnsureResourceDir()`：不存在则 `CreateDirectory`，并写入一份 `README.txt`（仅首次；已存在不覆盖）。
- 仓库提交 `resources/README.txt`（dev 用）+ `resources/.gitkeep`；**发布 zip 不带 `resources/`**，由首启生成（与 `configs/` 同理：升级解压不碰用户已有目录）。
- `README.txt` 内容（约 5 行）：用途说明、相对路径用法（`resources\foo.ps1`）、升级不会删除本目录、可放任意文件。
- 找图模板约定（见 `Features/Flow/ImageLibrary.cs`）：
  - `actions/<动作id>/files/`——该动作独占的模板图（采纳草稿时搬进来），随动作目录删除 / 备份；
    采纳时若与别的动作已有文件内容相同（SHA-256），用**硬链接**共享同一份内容，磁盘上只存一次；
  - `resources/images/`——程序生成的共享 / 历史截图（`shot-时间戳.png`），启动与删除动作时按引用回收；
  - `resources/user/`——用户自己的素材，文件选择器默认打开这里，整个目录不参与自动删除；
  - `<数据根>/cache/screenshots/`——「截图」步骤的运行期输出，下次运行开始 / 程序启动时整体清空，
    **不进备份**（`cache/` 是纯运行缓存）。
  `resources/` 下两个目录由备份 `.ltbak` 一起打包，`actions/` 也总是进备份，无需额外处理。
- **动作目录孤儿图**：启动时 `Core/Storage/OrphanFiles.Collect` 扫 `actions/<id>/files/` 的图片，
  没有任何 `image` 参数引用的移进 `trash/<时间戳>/orphans/<id>/`（只搬不删）。只看
  `ActionDto.Program`；出现 `${变量}` / `{"var": …}` / 内联取值这类动态引用时整个动作跳过。

---

## 4. 备份格式 `.ltbak`

本质是 zip（扩展名 `.ltbak`，关联「打开方式」非本应用职责）。包内固定结构（P5 起）：

```
manifest.json
configs/*.json        （六个域文件）
actions/<id>/...      （动作体 + 该动作自己的 files/）
resources/...         （勾选「资源目录」时）
速记/...              （勾选「速记」时）
macros/...            （存在宏时）
staging/...           （存在未采纳草稿时）
```

**目录白名单**（`BackupService.BackupDomain`）是唯一来源：导出内容、体积估算、manifest 的
`includes` 都由同一张表生成；表里没列的目录（`cache/`、`ai/`、`logs/`、`trash/`）永远不进备份。
新增一类目录只加一行。

`manifest.json`：

```json
{
  "format": "ltools-backup",
  "formatVersion": 1,
  "appVersion": "2.5.0",
  "createdAt": "2026-11-09T12:00:00",
  "includes": ["config", "actions", "resources", "notes", "macros", "staging"]
}
```

- 始终含 `config`；`actions` / `macros` / `staging` 存在就带；`resources` / `notes` 按勾选。
- `includes` 只声明**实际打进包里**的域；导入按它整体替换（缺目录 = 该域是空的）。
- zip 条目名 UTF-8（`ZipFile` 默认），保留中文路径。
- `formatVersion` 预留向后兼容；导入端只认 `1`，高于则提示版本不支持。

### 4.1 单个动作 `.ltact`

`BackupService.ExportAction` 只打包一个动作目录：`manifest.json`（`format = "ltools-action"`，
带 `actionId` / `actionName`）+ `action.json` + `files/…`。入口在面板磁贴右键、
「动作管理」磁贴右键的「导出动作…」（后台导出，完成后 toast 报路径）。
`.ltact` 不是完整备份，导入端（`Inspect`）会以「格式标识不符」拒绝。

---

## 5. 导出流程

入口：设置面板「备份与恢复」页 →「导出…」。

1. 范围复选框：`☑ 资源目录` `☑ 速记`（配置固定含）。默认全勾。
2. `SaveFileDialog`：默认文件名 `LTools-backup-yyyyMMdd-HHmmss.ltbak`，过滤器 `LTools 备份 (*.ltbak)|*.ltbak`。
3. 后台线程执行（避免阻塞钩子 / UI）：
   - 按目录白名单（见 §4）收集：`configs/`（**不存在**——全新安装、从未保存过——时先按内存配置
     写出一份，manifest 已声明含 `config`，不能产出「声称含配置、实际没有」的备份）；
     `actions/`、`macros/`、`staging/` 存在就带；`resources/`、`速记/` 按勾选（跳过 `.bak-*` 等现场备份）。
   - 写 `manifest.json` + 各条目到临时 zip（`.part-随机`），完成后移到目标路径——导出到一半失败不会留下半个 zip。
4. 体积 > 50 MB 时导出前确认（确认按钮为「确认导出」，非红色）。
5. 完成后用 `ToastService` 提示路径（不再走托盘气泡）。

---

## 6. 导入流程

入口：设置面板「备份与恢复」页 →「导入…」。

1. `OpenFileDialog`：过滤器 `LTools 备份 (*.ltbak)|*.ltbak`。
2. 解压到临时目录 `%TEMP%/ltools-import-<guid>/`（用 `ZipFile.ExtractToDirectory`，.NET 内置 zip-slip 校验）。
3. 校验 `manifest.json`：`format == "ltools-backup"`、`formatVersion == 1`、`includes` 合法、`configs/` 存在且可解析为 `AppConfig`。
4. **安全扫描**（仅针对 `resources/`，配置本身不算可执行）：
   - 统计可执行文件数：扩展名 ∈ `{.exe,.bat,.cmd,.ps1,.vbs,.js,.msi,.com,.scr,.lnk,.wsf,.jar}`。
   - 列出引用 `resources` 的动作：解析导入的 `configs/` + `actions/`，看动作体的 `Program` 步骤（`app.launch.target` / `app.runCommand.command` / `file.open.path`），含 `resources`（不区分大小写、忽略分隔符差异）的条目取「名称 + 片段」。
5. 弹 `ConfirmDialog` 警告：
   > 此备份含 **N** 个可执行文件，并将替换当前配置。导入后以下动作可能以**管理员权限**执行其中脚本：
   > • 名称1 — `resources\foo.ps1`
   > • …
   > 仅导入来源可信的备份。继续？
6. 用户确认后执行**整体替换**（仅替换 `includes` 中的项，未含项保持不动）：

   | 项 | 备份当前 | 写入导入 |
   |---|---|---|
   | 配置 | `configs/` → `configs.bak-<ts>` | 整体替换为导入的 `configs/` |
   | 资源目录 | `resources/` → `resources.bak-<ts>` | 替换为导入的 `resources/` |
   | 速记 | `速记/` → `速记.bak-<ts>` | 替换为导入的 `速记/` |

   - **替换以 manifest 的 `includes` 为准**：zip 不为空目录创建条目，故「备份里没有该目录」可能只是「备份的是空目录」。若以「备份里确实存在目录」为准，「导出一个空 `resources/`」的备份导入后会既不替换也无提示。改为：`includes` 声明包含就整体替换，缺目录 = 替换成空目录。
   - 目录替换用「改名 `.bak` + 从临时目录拷贝」（`Directory.Move` 仅在同卷可用；拷贝兼容跨卷）。
   - 每步失败回滚：删除已写入项，还原对应 `.bak`。
7. `ConfigStore.Reload()`（刷新菜单 / 动作清单 / 超级面板 / 终端路由）。
8. 按新配置重设 `AppState` 全局开关（`CapsLock 修饰键` / `边缘滚轮音量` / `超级面板` / `长按阈值` / `鼠标速度`）并刷新设置页控件——这几项原本只在启动时读入，不重设则需重启才生效。
9. `TrayService.Notify` 成功，并提示「旧数据已备份为 `*.bak-<ts>`」。

---

## 7. 设置面板 UI（`ConfigHelper`）

新增导航项 `tag="backup"`，标题「备份与恢复」：

```
┌─ 导出 ─────────────────────────────┐
│  范围：☑ 资源目录  ☑ 速记（配置固定）│
│  [ 导出… ]                          │
│  <状态文字>                         │
├─ 导入 ─────────────────────────────┤
│  [ 导入… ]                          │
│  说明：仅导入信任来源的备份；备份   │
│  含可执行文件，导入后可能以管理员    │
│  权限执行。                         │
└─────────────────────────────────────┘
```

- 复用现有 `ConfirmDialog`（导入警告）、`OpenFileDialog` / `SaveFileDialog`。
- 操作期间禁用按钮，状态文字显示「正在导出 / 导入…」。

---

## 8. 动作编辑器改动（`ActionEditorDialog`）

- 「工作目录」标签 + 行旁新增「打开资源目录」按钮：资源管理器打开 `AppPaths.ResourceDir`（不存在先 `EnsureResourceDir`）。
- `launchApp` / `runCommand` 两处下方加 hint `TextBlock`：「提示：脚本建议放 `resources\`，工作目录留空即可用相对路径」。
- 不新增宏（`${app}` 等）：默认工作目录已是 `DataRoot`，相对路径足够。
- 相对路径解析（`AppPaths.ResolveWorkdir` / `ResolveTarget` / `ResolvePath`）：工作目录留空→默认数据根；相对工作目录（如填 `resources`）按数据根解析。`ResolveTarget` 依次按**数据根**、**工作目录**查找，任一存在即返回绝对路径，否则原样交出（裸文件名继续走 PATH、URL / 文档交给 ShellExecute 回退）。不依赖目标里有没有 `\` / `/`——`foo.bat`、`resources\foo.bat`、`resources/foo.bat` 一视同仁。规避 CreateProcess 解析相对 exe 用父进程 CWD 而非 `WorkingDirectory` 的坑。Git Bash / WSL 下反斜杠会被 bash 当转义符，命令里请用 `resources/foo.ps1`。
- **`openFile` / `openFolder` 同样解析**（`AppPaths.ResolvePath`）：这两个类型走 ShellExecute，原样把 `Path` 交出去，LTools 自身 CWD 不是数据根时（如开机自启 = `System32`）相对文件会打不开、备份换机后同样失效。现与 `launchApp` 一致，执行前按数据根 → 工作目录解析；`openFolder` 额外允许命中目录（`ResolvePath(allowDirectory: true)`）。`openUrl` 不做解析（URL 不是路径）。
- **可移植路径（`AppPaths.ToRelativeIfUnderDataRoot`）**：「启动程序」的程序路径与「工作目录」、「打开文件」的文件路径、「打开文件夹」的文件夹路径，在**浏览选择**或**保存**时，若指向数据根之下就自动转成相对路径（相对数据根）；数据根本身 → 空（默认工作目录）。这样动作在备份换机 / 改换安装目录导入后仍能工作。数据根之外的绝对路径原样保留；已是相对路径的原样保留。

---

## 9. 错误处理与边界

| 情况 | 处理 |
|---|---|
| 导入文件非本格式 zip | 校验失败 → 提示「不是有效的 LTools 备份」 |
| `formatVersion` > 1 | 提示「备份来自更新版本，请升级 LTools 后导入」 |
| 解压条目路径越界（zip slip） | `ExtractToDirectory` 抛 → 捕获并提示损坏 |
| 配置写失败（被占用 / 共享冲突） | `File.Copy` 失败即抛出 → 触发回滚恢复 `.bak`，并向用户报错（未做重试，复用 `AppConfig.Load` 的重试仅作用于读取侧） |
| 跨卷移动 | `CopyDir`（递归 `File.Copy`）而非 `Directory.Move`，兼容 `%TEMP%` 与 `DataRoot` 不同卷 |
| `resources/` 与 `速记/` 同名 `.bak` 已存在 | `.bak-<ts>` 后追加 `-<n>` 序号避免冲突 |
| 备份体积过大 | 仅导出 > 50 MB 时弹确认；导入不设硬阈值（由警告文案 + 可执行文件清单提示） |

---

## 10. 文件清单

| 文件 | 改动 |
|---|---|
| `Core/AppPaths.cs` | 改：新增 `DataRoot` / `ResourceDir` / `RepoRoot` / `IsDevLayout`；`DefaultWorkdir` 用 `DataRoot` |
| `Core/ConfigLocator.cs` | 改：`RepoRoot` / `IsDevLayout` 委托 `AppPaths`，删重复 |
| `Core/BackupService.cs` | 新：`Export` / `Inspect` / `Import` / `Cleanup` + 清单 + 安全扫描 + 回滚 |
| `Core/ResourceDir.cs` | 新：`Ensure()` / `OpenInExplorer()` |
| `Views/ConfigHelper.xaml` + `.cs` | 改：新增「备份与恢复」导航页 |
| `Views/ActionEditorDialog.xaml` + `.cs` | 改：「打开资源目录」按钮 + hint |
| `resources/README.txt`、`resources/.gitkeep` | 新（提交，dev 用；发布 zip 不带） |
| `App.xaml.cs` | 改：启动调用 `ResourceDir.Ensure()` |
| `CHANGELOG.md` | 改：未发布条目 |
| `docs/RESOURCE_DIR_AND_BACKUP.md` | 本 spec |

---

## 11. 实现步骤

- [x] 1. `AppPaths` 重构（`DataRoot` / `ResourceDir` / dev 对齐），`ConfigLocator` 委托；`DefaultWorkdir` 改 `DataRoot`；构建 0 错 0 警，回归默认工作目录行为。
- [x] 2. `ResourceDir.Ensure()` + `OpenInExplorer()`；`App` 启动调用；提交 `resources/README.txt` + `.gitkeep`。
- [x] 3. `BackupService`：`manifest` 模型 + `Export`（zip 写 + 范围）。
- [x] 4. `BackupService.Import` / `Inspect`：解压 + 校验 + 安全扫描 + 整体替换 + 回滚。
- [x] 5. `ConfigHelper`「备份与恢复」页 + 按钮接线。
- [x] 6. `ActionEditorDialog`：「打开资源目录」按钮 + hint。
- [x] 7. `CHANGELOG` + 文档收尾；构建 0 错 0 警。（全流程手测待用户进行）

---

## 12. 验收标准

- dev 下默认工作目录 = 仓库根；`resources/` 出现在仓库根并含 `README.txt`。
- 放 `resources/foo.ps1`，`runCommand` 命令填 `resources\foo.ps1`、工作目录留空 → 能执行。
- 导出 `.ltbak` 含 `manifest.json` + `configs/` + `actions/` + `resources/` + `速记/`（按勾选）。
- 删除上述三项后导入 → 数据恢复，且各留一份 `*.bak-<ts>`。
- 导入一份含 `resources/evil.ps1` 且配置有引用动作的备份 → 弹警告、列出动作、用户取消则不改动。
- 导入非 LTools 备份 / 损坏 zip → 友好提示，不动现有数据。
- 构建 0 错 0 警。

---

## 13. 测试用例

### 13.1 测试准备

- **被测版本**：仓库构建的开发版（数据目录 = 仓库根）或发布包（数据目录 = `LTools.exe` 所在文件夹）。下文统一称数据目录为「**程序目录**」。
- **打开设置**：`CapsLock + \`。
- **重要**：每个会改动数据的用例执行前，先整体复制一份程序目录到别处（或用产品「导出」做一份备份），测完可就地还原。
- 术语：`configs/` = 配置目录（六个域文件）；`actions/` = 动作目录；`resources` = 资源目录；`速记` = 速记目录；`.ltbak` = 备份文件。

### 13.2 生成测试样本

在**程序目录**打开 PowerShell，执行：

```powershell
New-Item -ItemType Directory -Force resources | Out-Null
Set-Content -Encoding UTF8 resources\test.ps1 "Write-Output 'hello-ltools'"
New-Item -ItemType File -Force resources\演示.exe | Out-Null
Set-Content resources\old.bak-20260101.txt "旧备份，不应进入包"
Set-Content resources\dir.bat "echo %CD% & pause"
fsutil file createnew resources\大文件.bin 62914560
```

> `演示.exe` 是空文件，仅用于让安全警告统计可执行文件数量，不会被执行。

### 13.3 用例表

| 编号 | 用例名称 | 前置条件 | 操作步骤 | 预期结果 |
|---|---|---|---|---|
| R01 | 首次启动生成资源目录 | 程序目录下没有 `resources` 文件夹 | ① 启动 LTools　② 打开程序目录查看 | 自动出现 `resources` 文件夹，内含 `README.txt`；用记事本打开中文显示正常 |
| R02 | 不覆盖用户已有的说明文件 | 在 `resources\README.txt` 末尾加一行「自测标记」 | ① 退出 LTools　② 重新启动 | 「自测标记」仍在，文件未被重置 |
| R03 | 打开资源目录按钮 | 已启动 LTools，存在任意「运行命令」动作（没有就先新建一个） | ① 设置 → 动作管理 → 编辑该动作　② 在工作目录一行点「资源目录」 | 资源管理器打开，地址栏为「程序目录\resources」 |
| R04 | 工作目录提示文字 | — | ① 打开「运行命令」动作编辑器 | 标签显示「工作目录（可选，留空 = LTools 所在目录）」；下方显示「提示：脚本建议放 resources\（Git Bash / WSL 请用 resources/）；工作目录留空即可用相对路径」 |
| R05 | 相对路径运行脚本 | 已执行 13.2 生成 `resources\test.ps1` | ① 新建「运行命令」动作，命令填 `resources\test.ps1`，终端选 PowerShell 7，勾「保持窗口」，工作目录留空　② 保存并执行 | 终端窗口输出 `hello-ltools`；再执行 `pwd`，显示的路径是程序目录 |
| R06 | 启动程序也用默认目录 | 已执行 13.2 生成 `resources\dir.bat` | ① 新建「启动程序」动作，程序路径填 `resources\dir.bat`，工作目录留空　② 执行 | 窗口打印的当前路径是程序目录（不是桌面，也不是 `C:\Windows\System32`） |
| R07 | 留空时终端开在默认目录（回归） | 已安装 Windows Terminal | ① 新建「运行命令」动作，命令填 `pwd`，终端选 PowerShell 7，勾「保持窗口」，工作目录留空　② 执行 | 终端一打开 `pwd` 就输出程序目录；**不能**是 `C:\Users\<你>` 或 PowerShell 安装目录 |
| R08 | 浏览选数据根下路径存为相对 | `resources\dir.bat` 存在 | ① 新建「启动程序」，「程序路径」点「浏览…」选 `resources\dir.bat`，「工作目录」点「浏览…」选 `resources` ② 确定 | 程序路径框显示 `resources\dir.bat`；工作目录框显示 `resources`（都不是绝对路径）；保存后配置里同样是相对值 |
| R09 | 旧绝对路径编辑时转相对 | 旧动作的程序路径 / 工作目录是数据根下的绝对路径 | ① 打开该动作编辑器 | 框里显示的是相对路径（如 `resources\foo.exe`、`resources`）；点确定保存后配置里也是相对值 |
| R10 | 「打开文件 / 文件夹」用相对路径 | `resources\test.ps1` 与 `resources` 目录存在 | ① 新建「打开文件」动作，路径填 `resources\test.ps1`，保存　② 新建「打开文件夹」动作，路径填 `resources`，保存　③ 分别执行 | 「打开文件」用记事本（或默认程序）打开 `test.ps1`，不报「找不到文件」；「打开文件夹」在资源管理器打开 `resources` 目录；配置里两项均为相对值 |
| R11 | 编辑器打开旧相对路径显示为相对 | 旧动作的文件路径是数据根下的绝对路径 | ① 打开该「打开文件 / 打开文件夹」动作的编辑器 | 框里显示的是相对路径（如 `resources\test.ps1`）；点确定后配置里也是相对值 |
| E01 | 默认导出包含三项 | `resources\test.ps1` 存在；速记里至少有一条 | ① 设置 → 备份与恢复（两个勾选框默认已勾）　② 点「导出…」，保存为桌面 `test-全量.ltbak` | 文件生成；用压缩软件打开可看到 `manifest.json`、`configs` 文件夹、`actions` 文件夹、`resources` 文件夹、`速记` 文件夹 |
| E02 | 只导出配置 | — | ① 取消勾选「资源目录」「速记」　② 导出为 `仅配置.ltbak` | 包内只有 `manifest.json`、`configs/` 与 `actions/`，没有 `resources` 和 `速记` |
| E03 | 旧备份文件不进包 | `resources\old.bak-20260101.txt` 存在 | ① 勾选资源目录，导出全量 | 包内看不到 `old.bak-20260101.txt` |
| E04 | 大文件导出提醒 | `resources\大文件.bin`（约 60 MB）存在 | ① 导出全量　② 先点「取消」，再重复一次并点「确定」 | 弹出确认「备份约 60.x MB，确定导出吗？」，确认按钮为「确认导出」（不是「确认删除」）；取消后不生成文件；确定后生成 |
| E05 | 覆盖已有备份 | 目标路径已存在同名 `.ltbak` | ① 导出到该路径并确认覆盖 | 文件内容更新，无报错；目标文件夹内没有 `.part` 之类残留文件 |
| E06 | 导出中不能重复点击 | 使用大文件以便耗时 | ① 点「导出…」选好路径，趁进度未完成再点一次「导出…」 | 第二次点击无反应；完成后状态只显示一次「已导出：…」 |
| E07 | 从未保存过设置也导出含配置 | 程序目录下**没有** `configs/`（如刚解压的全新安装，也没改过任何设置） | ① 直接点「导出…」并保存　② 用压缩软件打开 | 包内含 `manifest.json` 和 `configs/`（不能只有 manifest）；`manifest.json` 的 `includes` 含 `config` |
| I01 | 全量恢复 | 先用 E01 导出 `test-全量.ltbak` | ① 删除 `resources\test.ps1`　② 在设置里新建一个动作（制造差异）　③ 删除一条速记　④ 导入 `test-全量.ltbak` 并点「确定」 | 脚本文件恢复；多建的动作消失；被删速记恢复；程序目录出现 `configs.bak-…`、`actions.bak-…`、`resources.bak-…`、`速记.bak-…` 四份；设置中的列表立即刷新 |
| I02 | 可执行文件安全警告 | `resources\演示.exe` 与 `test.ps1` 存在，且有动作的命令包含 `resources\test.ps1` | ① 导出全量　② 导入该备份 | 弹窗标题「导入备份」，正文写明「2 个可执行文件」并列出该动作的名称与命令片段；确认按钮为「确认导入」 |
| I03 | 取消导入不改数据 | 任意有效备份 | ① 导入，在警告框点「取消」 | 配置、`resources`、`速记` 均无变化；程序目录没有新增 `.bak`；`%TEMP%` 下没有 `ltools-import-` 开头的残留目录 |
| I04 | 备份不含速记时不碰速记 | 使用 E02 的 `仅配置.ltbak` | ① 导入并确定 | 速记内容保持原样，没有出现 `速记.bak-…` |
| I05 | 普通压缩包被拒绝 | — | ① 把任意 `.zip` 或文本文件改名为 `x.ltbak`　② 导入 | 提示「不是有效的 LTools 备份…」；现有数据不变 |
| I06 | 更新版本的备份被拒绝 | 一个有效备份 | ① 用压缩软件打开备份，把 `manifest.json` 中 `"formatVersion": 1` 改为 `2`　② 重新打包为 `.ltbak`　③ 导入 | 提示「备份来自更新版本，请升级 LTools 后再导入」；数据不变 |
| I07 | 配置损坏被拒绝 | 一个有效备份 | ① 解压后把 `configs/settings.json` 内容改为 `{ 坏`　② 重新打包　③ 导入 | 提示「备份里的配置无法解析（或文件已损坏）」；数据不变 |
| I08 | 恶意路径条目被拒绝 | 按 13.4 生成 `slip.ltbak` | ① 导入 `slip.ltbak` | 提示「导入失败」；`evil.txt` 没有出现在程序目录或其上一级；现有数据不变 |
| I09 | 连续导入两次 | 同一有效备份 | ① 连续导入两次 | 第二次产生的备份名形如 `configs.bak-…-1`；第一次的 `.bak` 仍在 |
| I10 | 导入失败自动恢复（选测） | 需开发协助制造写入失败 | ① 导入全量备份，在资源目录写入阶段人为制造占用 / 失败 | 提示「导入失败」；配置恢复为导入前内容；资源目录与速记未停留在半替换状态 |
| I11 | 空资源目录的备份清空本地 | 一个 `resources` 里没有文件的备份：删空 `resources` 里的文件（保留 `README.txt` 也行）后导出全量 | ① 在 `resources` 里放入一个新文件（如 `keep.txt`）　② 导入刚才的备份 | `resources` 目录仍存在但已不含 `keep.txt`；`keep.txt` 出现在 `resources.bak-…` 里；不能是「什么都不发生」 |
| I12 | 导入后全局开关立即生效 | 一个把「CapsLock 修饰键」关掉的备份（先把开关关掉再导出） | ① 把「CapsLock 修饰键」重新打开　② 导入该备份 | 导入完成后不用重启：CapsLock 快捷键立即失效；设置页「通用」页里的开关显示为已关闭 |
| G01 | 备份往返一致 | 已执行 13.2 | ① 导出全量　② 记录 `test.ps1` 内容、速记条数、动作数量　③ 删除部分数据　④ 导入　⑤ 复核 | 三项均与记录一致（`.bak` 文件不计入） |
| G02 | 发布包不含用户数据 | 发布 zip | ① 解压查看 | 没有 `resources` 文件夹，也没有 `configs/`（首次启动才生成） |
| G03 | 设置页记忆 | — | ① 停在「备份与恢复」页　② 关闭设置　③ 用 `CapsLock + \` 重新打开 | 仍停在「备份与恢复」页 |
| G04 | 换机导入后相对路径动作仍可用 | 用 R08 的相对路径动作做全量备份 | ① 在另一台机器（或把程序目录整体挪到新路径）导入　② 执行该「启动程序」/「运行命令」动作 | 动作在新环境找到脚本 / 程序（相对路径解析到新程序目录），不因旧机器的绝对路径失败 |

### 13.4 生成恶意路径样本

在 PowerShell 执行（输出到桌面）：

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$out = "$env:USERPROFILE\Desktop\slip.ltbak"
Remove-Item $out -ErrorAction Ignore
$zip = [System.IO.Compression.ZipFile]::Open($out, 'Create')
$m = $zip.CreateEntry('manifest.json')
$w = New-Object System.IO.StreamWriter($m.Open())
$w.Write('{"format":"ltools-backup","formatVersion":1,"appVersion":"test","createdAt":"2026-01-01T00:00:00","includes":["config"]}')
$w.Close()
$e = $zip.CreateEntry('../evil.txt')
$w2 = New-Object System.IO.StreamWriter($e.Open())
$w2.Write('x')
$w2.Close()
$zip.Dispose()
```

---

## 14. 开放问题

- `README.txt` 措辞与是否英文双语（当前仅中文用户，单中文即可）。
- 是否在导出时排除 `resources/.bak-*` / `速记/.bak-*`（草案：排除，避免备份里嵌套旧备份）。
