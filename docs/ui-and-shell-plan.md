# UltraZip UI 与 Windows 集成方案

> 范围：把 UltraZip 从「表单式解压器」补成「有窗口体系 + 有资源管理器入口」的桌面工具。
> 压缩创建的引擎与参数见 [bandizip-feature-parity.md](bandizip-feature-parity.md) §4.5；本文只负责它的 UI 部分（`CreateArchiveDialog`，P2）。

---

## 0. 现状（代码事实）

| 项 | 现状 |
|---|---|
| 主窗口 | `MainWindow.xaml`：一行 `StackPanel` 放 3 个按钮 + 拖放提示 + 文件信息 + 解压路径 + 进度条 |
| 菜单栏 | 无 |
| 归档内容列表 | 无（`Grid.Row="4"` 是个空 `Grid`，位置已经留好了） |
| 状态栏 | 只有一个 `StatusText`，没有分段统计 |
| 对话框 | 只有密码输入 / 密码库 / 伪装扫描 3 个 |
| 单实例 / 命令行 | 无，`App.xaml.cs` 是空类 |
| 文件关联 / 右键菜单 | 无 |
| manifest | 无 `app.manifest`（无 DPI 感知声明、无 requestedExecutionLevel） |

结论：现在的窗口是「填表 → 点按钮」，用户拿不到 Bandizip 那种"先看内容再决定解什么"的掌控感；而右键菜单缺失意味着必须先把文件拖进窗口，日常使用路径比 Bandizip 长两步。

---

## 1. UI 目标形态

### 1.1 主窗口骨架

```
┌──────────────────────────────────────────────────────────────┐
│ 文件(F)  编辑(E)  查看(V)  工具(T)  选项(O)  帮助(H)          │  ← 菜单栏
├──────────────────────────────────────────────────────────────┤
│ [打开] [解压] [测试] [属性] │ [上一级] │ 地址: C:\a\b.zip       │  ← 工具栏 + 路径条
├────────────────┬─────────────────────────────────────────────┤
│ 目录树          │ 名称 │ 大小 │ 压缩后 │ 修改时间 │ CRC │ 方法 │  ← 条目列表
│  ├ sub1        │ ...                                          │
│  └ sub2        │                                              │
├────────────────┴─────────────────────────────────────────────┤
│ 12 个文件, 3 个目录 │ 原始 1.2 GB │ 压缩 480 MB │ 39% │ 就绪   │  ← 状态栏
└──────────────────────────────────────────────────────────────┘
```

没打开压缩包时，中间区域退化成现在的拖放提示区（保持老用户路径：拖进来就能解压）。

### 1.2 窗口清单

| 窗口 | 用途 | 优先级 | 要点 |
|---|---|---|---|
| `MainWindow` 改造 | 菜单栏 + 工具栏 + 列表 + 状态栏 | P0 | 保留拖放；无包时显示提示区 |
| `ExtractOptionsDialog` | 冲突策略、编码、密码、是否保留目录结构、解压后打开 | P0 | 记住上次选择 |
| `SettingsWindow` | 代码页策略、临时目录、默认冲突策略、右键菜单项、关联、主题 | P0 | 存 `%APPDATA%\UltraZip\settings.json` |
| `AboutWindow` | 版本、7-Zip 许可声明（LGPL 合规必须） | P0 | 现在许可说明只在 README 里 |
| `ProgressWindow` | 任务化进度：速度、剩余时间、当前文件、暂停/取消、最小化到任务栏 | P1 | 复用 `ExtractProgress` |
| `ResultWindow` | 成功/失败条目清单、耗时、平均速度、打开目录、复制错误 | P1 | 现在只有 `SummarizeError` 的 5 行摘要 |
| `CreateArchiveDialog` | 新建压缩包：源列表、格式、级别、方法、密码、分卷、输出路径 | P2 | 引擎见 parity §4.5 |
| `ArchiveBrowserWindow` | 若主窗口不改成浏览器，则单开一个浏览窗口 | P2 | 与主窗口二选一，别做两套 |
| `RepairWindow` / `PasswordRecoveryWindow` | Pro 工具 | P3 | 见 parity 文档 |

**关键取舍**：主窗口是「改成浏览器」还是「保持表单 + 另开浏览窗口」。建议选前者但**渐进改造**——先加菜单栏/工具栏/列表，列表为空时行为和现在完全一致，等列表能用了再改按钮语义。

### 1.3 窗口行为

- 单实例：第二次启动把路径转给已运行实例（见 §2.5）。
- 记忆窗口尺寸/位置/列宽（`settings.json`）。
- 任务栏进度条（`TaskbarItemInfo.ProgressState`）。
- 高 DPI：加 `app.manifest` 声明 PerMonitorV2。
- 快捷键：`Ctrl+O` 打开、`Ctrl+E` 解压、`F5` 测试、`Del`（后期删除条目）。

---

## 2. Windows 集成

### 2.1 参考实现：7-Zip 是怎么做的

7-Zip 的 Windows 集成有三个关键事实，可以直接照抄：

| 事实 | 说明 |
|---|---|
| **没有 shell 扩展 DLL** | 纯「静态注册表 verb + 独立 exe（`7zFM.exe`）」，不注册 COM、不往 Explorer 注进程 |
| **四类注册点** | `*\shell`（所有文件）、`Directory\shell`（文件夹）、`Directory\Background\shell`（文件夹空白处）、`Drive\shell`（驱动器） |
| **级联子菜单** | 顶层项只写 `MUIVerb` + `Icon` + `SubCommands`，具体命令挂 `shell\<序号>` 子键 |

代价是 Win11 上必然落进「显示更多选项」；换来的是**不需要打包、签名、COM 注册**，程序自己就能注册。所以 UltraZip 用程序内 `--register` 自注册，不引入安装器。

层级上照搬 7-Zip 的双模式（安装器默认 HKLM，非管理员安装退 HKCU）。我们没有安装器，默认走 HKCU：

| 模式 | 写到 | 权限 | 场景 |
|---|---|---|---|
| 默认 | `HKCU\Software\Classes` | 无需提权 | 绿色版、单用户 |
| `--register-all-users` | `HKLM\Software\Classes` | 需 UAC | 多用户机器 |

`HKCR` 只是这两个键的合并视图，per-user 写 HKCU 是标准做法（7-Zip 便携版同理）。

> **与 7-Zip 的一处有意偏离**：解压/测试类动词只注册到压缩包扩展名及当前实际 ProgID，避免污染普通文件的解压菜单；所有文件和文件夹只增加一个独立的“添加到压缩文件…”命令。

### 2.2 注册结构

压缩包使用级联入口；普通文件和文件夹分别使用 `*\shell\UltraZip.Add`、`Directory\shell\UltraZip.Add` 独立命令，避免在压缩包上重复显示两个 UltraZip 级联菜单。

```
HKCU\Software\Classes\SystemFileAssociations\.zip\shell\UltraZip
  (Default)        =（不设置）
  MUIVerb          = UltraZip
  Icon             = "<exe 路径>",0
  SubCommands      = ""          ; 空串 = 用下面的 shell 子键做级联
  MultiSelectModel = Player      ; 支持多选
  \shell\01  (Default) = 打开
    \command (Default) = "<exe>" --open "%1"
  \shell\02  (Default) = 解压到当前文件夹
    \command (Default) = "<exe>" --extract-here "%1"
  \shell\03  (Default) = 解压到此处并删除压缩文件
    \command (Default) = "<exe>" --extract-here-delete --quiet "%1"
  \shell\04  (Default) = 解压到…
    \command (Default) = "<exe>" --extract-to-dialog "%1"
  \shell\05  (Default) = 测试压缩包
    \command (Default) = "<exe>" --test "%1"
```

`SubCommands` 为空串表示"用本键下的 `shell` 子键做级联"；序号 `01/02/03…` 决定显示顺序，留出插入空间。

**按扩展名各注册一份**（内容相同，只换 `.zip`）：`7z zip rar r00 z01 gz gzip tar bz2 xz tgz tbz2 txz iso cab wim zst zstd lz4 001`——`FormatDetector` 能识别的全集 + 分卷尾缀。分卷只注册首卷也无所谓，反正从任意卷进来都能找到首卷。注册时还会读取 UserChoice / 扩展名默认值定位当前实际 ProgID，在该 ProgID 下补写同一级联菜单，解决 ZIP 关联到第三方程序后子菜单不显示的问题；不会修改 UserChoice。

**多选**：`MultiSelectModel=Player` 下 Explorer 对每个选中项各起一次进程 → 由 §2.5 的单实例把 N 次调用聚合成一个任务队列。7-Zip 也是这么处理的。

**压缩入口**：`*\shell\UltraZip.Add` 与 `Directory\shell\UltraZip.Add` 注册独立带图标命令“添加到压缩文件…”，执行 `"<exe>" --add "%1"` 并打开现有创建窗口。

### 2.3 文件关联与「打开方式」

7-Zip 的老毛病是**装完不进「默认应用」列表**（社区补丁 [#463](https://sourceforge.net/p/sevenzip/patches/463/) 专门在补这个），根因是缺 `Capabilities` 注册。我们一次做对：

```
HKCU\Software\Classes\UltraZip.zip            ; ProgID
  (Default)              = ZIP 压缩包
  \DefaultIcon (Default) = "<exe>",0
  \shell\open\command    = "<exe>" "%1"

HKCU\Software\Classes\.zip\OpenWithProgids     ; 加值 UltraZip.zip（不设默认）

HKCU\Software\UltraZip\Capabilities
  ApplicationName        = UltraZip
  ApplicationDescription = 压缩包解压工具
  \FileAssociations      ; 值：.zip = UltraZip.zip …

HKCU\Software\RegisteredApplications
  UltraZip = Software\UltraZip\Capabilities
```

三条红线：

- 不写 `.zip` 的默认值、不碰 `UserChoice`（Win10+ 有哈希保护，硬改会被系统重置并弹提示）。
- 默认程序留给用户在「设置 → 默认应用」里选，我们只保证出现在候选列表里。
- `Capabilities` 放在自己的厂商键下（`Software\UltraZip`），别往系统键里塞。

### 2.4 Win11 的坑

- 注册表 verb 一定在「显示更多选项」里，官网/文档里要写清楚，否则用户以为没生效。
- 修改注册表后调 `SHChangeNotify(SHCNE_ASSOCCHANGED, ...)` 让 Explorer 立即刷新，不必重启。
- 不要用「恢复 Win10 右键菜单」的 CLSID hack（会全局影响系统）。

### 2.5 单实例 + 命令行协议

```
UltraZip.exe [<archive>] [--add | --extract-here | --extract-here-delete | --extract-to <dir> | --test | --open]
            [--password <p>] [--quiet]
            [--register | --register-all-users | --unregister]
```

- 单实例：`Mutex` 判活 + 命名管道/WM_COPYDATA 转发命令行。首个实例没有窗口参数时正常显示窗口；带 `--extract-here` 时后台跑完弹结果窗。
- 参数解析放在 `App.OnStartup`，`StartupUri` 需要去掉（否则无法拦截参数）。
- `--quiet` 用于右键"解压到当前文件夹"和"解压到此处并删除压缩文件"：不弹主窗口，只弹进度/结果；后者仅在完整解压成功后删除压缩包及全部分卷。

### 2.6 卸载/清理

`--unregister` 逐项删除（顺序无关，但**必须全**）：

```
*\shell\UltraZip.Add
Directory\shell\UltraZip.Add
SystemFileAssociations\<ext>\shell\UltraZip     ; 每个已注册扩展名
当前实际 ProgID\shell\UltraZip
UltraZip.*                                      ; ProgID
OpenWithProgids 下的 UltraZip.* 值
RegisteredApplications\UltraZip
Software\UltraZip\Capabilities
```

删完调 `SHChangeNotify(SHCNE_ASSOCCHANGED)`。设置窗口给一个「移除右键菜单」按钮——绿色软件不能要求用户去翻注册表。

---

## 3. 实施顺序

### M1 · 窗口骨架（低风险，1 周）

1. `app.manifest`（PerMonitorV2 + asInvoker）
2. 主窗口加 `Menu` + 工具栏 + 状态栏分段
3. `ExtractOptionsDialog`（冲突策略 / 编码 / 保留结构）
4. `SettingsWindow` + `settings.json` + 设置服务
5. `AboutWindow`（含 7-Zip LGPL 声明）
6. 记窗口尺寸/列宽

**验收**：不改任何解压逻辑，老流程一字不变；新增的选项能真实生效（冲突策略要改 `7z x` 的 `-aoa/-aos/-aou`）。

### M2 · 单实例 + 命令行 + 右键菜单（1 周）

1. `App.OnStartup` 参数解析 + 单实例转发（§2.5）
2. `Services/ShellIntegration.cs`：按 §2.2 写/删注册表；默认 HKCU，`--register-all-users` 走 HKLM（提权失败要回滚并提示）
3. 级联菜单 + 按扩展名批量注册 + `SHChangeNotify`
4. 关联：ProgID + `OpenWithProgids` + `Capabilities`（§2.3）
5. 设置窗口：注册开关、「移除右键菜单」、Win11 折叠说明

**验收**：右键 `.zip` 出现级联菜单四项；右键 `.txt` 不出现解压项；多选 3 个包只起 1 个实例；`--unregister` 后注册表无残留；`--register-all-users` 提权后另一账户可见。

**实际落地**：已增加 PerMonitorV2 `app.manifest`、`App.OnStartup` 参数解析、Mutex + 当前用户命名管道单实例转发、主窗口任务队列、`ShellIntegration` 注册/注销与 UAC 全员注册、`SettingsWindow` 和真实 `TestAsync`。静默解压、测试成功窗口、第二实例转发退出均已实测；为避免未经确认修改系统配置，尚未执行真实注册/注销、Explorer 右键、另一账户及三文件多选验收。

### M3 · 归档浏览（1–2 周）

1. `7z l -slt` 全量解析 → `ArchiveEntry` 模型（路径/大小/压缩后/时间/CRC/方法/是否目录）
2. 树 + 列表（`ListView` + `GridView`，列宽可拖）
3. 只解选中条目（`7z x arch -o<dir> path1 path2`）
4. 双击条目 = 解到临时目录并打开（"查看"）
5. 状态栏统计 + 排序 + 过滤框

**验收**：1 GB 包 2 秒内出列表；勾 3 个文件只解 3 个；中文名不乱码。

**实际落地**：主窗口已嵌入目录树 + 可拖列宽明细列表，显示名称/大小/压缩后/时间/CRC/方法，支持目录导航、默认排序、路径过滤、勾选、选择性解压、完整归档统计和双击临时打开。`SevenZipEngine.ListEntriesAsync` 解析 `7z l -slt`，特殊 ZIP 走内置布局；`ExtractEntriesAsync` 支持目录子项与密码库流程。22 项真实断言通过（恰好 3 文件、目录 2 子文件、加密与特殊 ZIP），主界面已实测。1 GB/2 秒性能及外部默认程序双击打开未做自动化验收；查看临时目录在应用关闭时清理。

**视图扩展已落地**：F6 启动画面、F7 目录树开关、F8 ZIP 注释、工具栏/状态栏/置顶、明细/列表样式、列显隐与排序，以及窗口尺寸、面板宽度和列布局持久化。右侧图像预览支持 PNG/JPEG/BMP/GIF/ICO/TIFF，单条最大 30 MB，复用条目解压与密码库/弹窗流程，切换条目和退出时清理临时文件。普通 ZIP、中文注释、AES 加密图片和设置重启恢复均已做真实 GUI 验收。

### M4 · 任务化进度与结果（1 周）

1. `ProgressWindow`：速度/剩余时间/当前文件/最小化
2. `ResultWindow`：成功失败清单、耗时、打开目录
3. 任务栏进度、完成提示音/通知
4. 批量任务队列（多选解压时）

### M5 · 压缩创建 UI（P2）

引擎与参数在 parity 文档 §4.5，这里只做界面：

1. `CreateArchiveDialog`：源列表（拖入 / 添加 / 删除 / 排序）、格式、级别、方法、字典、密码、分卷、输出路径
2. 主窗口拖入**非压缩包**文件时，提示「压缩为…」并打开该对话框
3. 压缩进度复用 `ProgressWindow`；完成后弹 `ResultWindow`（体积、压缩率、耗时）
4. 取消 / 覆盖 / 临时文件清理的确认文案

**验收**：拖 3 个文件夹进来，选 7z + 分卷 100 MB + 密码，生成 `.7z.001` 且自动测试通过；中途取消后目录干净。

**实际落地**：已新增 `CreateArchiveDialog`（源列表增删/排序/拖入、9 种格式、级别、密码、分卷、输出路径、独立进度与取消），主窗口已加入口及非压缩包拖放引导。M4 的 `ProgressWindow` / `ResultWindow` 尚未实现，因此本轮复用对话框内进度，完成结果由主窗口提示。真实引擎冒烟 47 项断言通过，含加密分卷回解与取消清理；未做 4 GB 大包和 GUI 自动化验收。

---

## 4. 改动文件清单

| 文件 | 改动 |
|---|---|
| `UltraZip/app.manifest` | 新增 |
| `UltraZip/App.xaml` | 去掉 `StartupUri` |
| `UltraZip/App.xaml.cs` | 单实例、参数解析、转发 |
| `UltraZip/MainWindow.xaml(.cs)` | 菜单/工具栏/列表/状态栏 |
| `ViewModels/` 或 `Models/` | `ArchiveEntry`、`AppSettings`、`ExtractOptions` |
| `Services/SettingsService.cs` | 新增 |
| `Services/ShellIntegration.cs` | 注册表 verb 注册/注销 + SHChangeNotify |
| `Services/SingleInstance.cs` | Mutex + 管道转发 |
| `Views/ExtractOptionsDialog.*` | 新增 |
| `Views/SettingsWindow.*` | 新增 |
| `Views/AboutWindow.*` | 新增（含 LGPL 文本） |
| `Views/CreateArchiveDialog.*` | 新增（P2，压缩创建） |
| `Services/SevenZipEngine.cs` | 新增 `ListEntriesAsync`、`TestAsync`、`CreateAsync`、条目过滤解压 |

---

## 5. 风险 / 不做

| 项 | 处理 |
|---|---|
| Win11 右键折叠 | 文案说明 + 后续可选 COM |
| 多选并发起多进程 | 单实例聚合，或 `MultiSelectModel=Player` |
| 抢默认关联 | 只写 `OpenWithProgids`，不碰 `UserChoice` |
| 托盘常驻 | 不做（与"轻量工具"定位不符） |
| 安装包 / 自动更新 | 暂不做，保持绿色 |
| COM 扩展 | M2 验证后再评估 |
