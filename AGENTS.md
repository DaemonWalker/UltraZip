# AGENTS.md — UltraZip 工程指南

## 项目简介

UltraZip 是一个 Windows 桌面压缩与解压工具：C# / WPF / net8.0-windows，主要通过进程调用官方 7-Zip 命令行（`7z.exe`）完成压缩创建与解压，特殊拼接 ZIP / WinZip-AES 由内置流式解压器处理。支持创建 7z/zip/tar/gz/bz2/xz/tgz/tbz2/txz（含分卷与加密），解压 7z/zip/rar/gz/tar/bz2/xz/iso/cab/wim/zstd/lz4、SFX、分卷、加密包（含密码库自动尝试）、伪装文件（polyglot）扫描解压；识别采用魔数 → EOCD/扩展名 → 7z 探测三层流程。主窗口支持目录树/明细浏览、选择性解压和双击查看。支持命令行、单实例转发，以及可选注册的右键菜单和文件关联。界面文案为中文。

## 构建与运行

```bash
dotnet build UltraZip.sln          # 构建（要求 0 错误 0 警告）
dotnet run --project UltraZip      # 调试运行（WPF GUI）
```

- 输出 exe：`UltraZip/bin/Debug/net8.0-windows/UltraZip.exe`
- 7-Zip 二进制位于 `UltraZip/tools/7z/`（7z.exe、7z.dll、License.txt），csproj 中以 `CopyToOutputDirectory=PreserveNewest` 复制到输出目录，运行时引擎从 `AppContext.BaseDirectory/tools/7z/7z.exe` 定位。
- 环境注意：本机 dotnet SDK 为 10.x，但目标框架固定 net8.0-windows（自动下载引用包）。`dotnet new sln` 在 SDK 10 默认生成 `.slnx`，本仓库使用传统 `UltraZip.sln`（`dotnet new sln --format sln`）。

## 目录结构

```
UltraZip.sln                              # 解决方案
UltraZip/
  UltraZip.csproj                         # net8.0-windows, UseWPF, app.manifest 与 tools/7z 拷贝配置
  app.manifest                            # PerMonitorV2 + asInvoker
  App.xaml(.cs)                           # WPF 入口：OnStartup 参数解析、注册命令、单实例启动（无 StartupUri）
  MainWindow.xaml(.cs)                    # 主窗口：归档浏览、视图布局、图片预览、选择解压、进度、命令行队列
  Views/
    PasswordPromptDialog.xaml(.cs)        # 密码输入对话框：手动输入/从库选择/“成功后保存”勾选
    PasswordManagerWindow.xaml(.cs)       # 密码库管理窗口：DataGrid CRUD
    EmbeddedArchivesDialog.xaml(.cs)      # 伪装文件扫描结果列表（格式/偏移/备注）
    CreateArchiveDialog.xaml(.cs)         # 压缩创建：源列表、格式/级别/密码/分卷/输出、进度与取消
    ColumnSettingsDialog.xaml(.cs)        # 归档列显隐、排序与恢复默认
    ArchiveCommentDialog.xaml(.cs)        # ZIP 注释只读查看
    SettingsWindow.xaml(.cs)              # 当前用户/全员注册、注销、Win11 菜单说明
  Models/Models.cs                        # 主要数据模型（见下）
  Models/ArchiveEntry.cs                  # 归档条目：路径/大小/压缩后/时间/CRC/方法/目录/勾选与显示属性
  Models/ViewSettings.cs                  # 窗口/面板/列宽与顺序/视图样式等可持久化设置
  Models/StartupRequest.cs                # 命令行动作、路径、目标、密码、quiet 与参数解析
  Services/
    SevenZipEngine.cs                     # 7z list/create/extract/test、条目列表与选择解压、特殊 ZIP 分流、密码重试
    ArchiveFileDeleter.cs                 # 解压成功后删除单个压缩包或整个已识别分卷集，返回删除失败明细
    ArchiveCommentReader.cs               # ZIP EOCD 注释读取，UTF-8 优先、GB18030 兜底
    ViewSettingsStore.cs                  # 视图设置 JSON 存 %APPDATA%\UltraZip\settings.json
    ZipAesEngine.cs                       # 拼接 ZIP / WinZip-AES 流式解压：偏移修正、密码编码尝试、HMAC/CRC 校验
    FormatDetector.cs                     # 格式识别（魔数+扩展名）与分卷集归并、缺卷检测
    PolyglotScanner.cs                    # 伪装文件魔数扫描（分块+重叠）
    PasswordVault.cs                      # 密码库：明文 JSON 存 %APPDATA%\UltraZip\passwords.json
    SingleInstance.cs                     # Local Mutex + 当前用户命名管道，JSON 参数转发
    ShellIntegration.cs                   # HKCU/HKLM 静态 verb、级联菜单、ProgID、Capabilities、SHChangeNotify
  tools/7z/{7z.exe, 7z.dll, License.txt}  # 随应用分发的 7-Zip（LGPL，独立 exe 形式分发合法）
```

`Models/Models.cs` 内含：`PasswordEntry`（Id/Label/Password/HitCount/LastUsed）、`EmbeddedArchive`（Format/Offset/Note，OffsetHex 为显示属性）、`ArchiveFormat` 枚举、`ArchiveInfo`（Format/OriginalPath/FirstVolumePath/IsVolumeSet/Volumes/MissingVolumes）、`ExtractErrorKind` 枚举（None/NeedsPassword/WrongPassword/MissingVolume/Corrupt/Canceled/Other）、`ExtractResult`、`ExtractProgress`、`ArchiveSummary`、`CreateFormat`、`CreateOptions`、`CreateResult`。

## 核心架构

### 为什么用 7z.exe 进程调用而不是托管库

RAR5、加密头 7z（-mhe=on）、分卷 RAR 等场景托管库（如 SharpCompress）有支持缺口；官方 7-Zip 命令行支持最完整。7-Zip 为 LGPL，以独立 exe 形式随应用分发（并附带 License.txt）是合规的。

### SevenZipEngine（Services/SevenZipEngine.cs）与 ZipAesEngine 分流

- `SevenZipPath`：`<输出目录>/tools/7z/7z.exe`。
- `GetSummaryAsync` / `ExtractAsync` 先调用 `ZipAesEngine`；只有含前置数据或 method 99（WinZip-AES）的 ZIP 由内置引擎接管，返回 `null` 时继续走原有 7z 流程。
- 7z 概要命令为 `7z l -slt -bse1`，统计 `^Path = ` 行数减 1 为条目数（首个是压缩包自身），`^Encrypted = +` 判断加密；输出含 `Enter password` 说明是加密头包，直接标记 IsEncrypted 返回。
- 7z 单次解压参数为 `x "<archive>" -o"<dest>" -y -bsp1 -bso1 -bse1 -sccUTF-8` + `-p"<密码>"`（有密码时）；成功判定为退出码 0 且输出含 `Everything is Ok`。
- `CreateAsync` 创建 7z/zip/tar/gz/bz2/xz 及 tar 组合格式，参数由 `BuildCreateArgs` 生成，工作目录取源公共父目录；支持级别、AES ZIP、7z 加密头与 7z/zip 的 `.001` 分卷。输出先写 `%TEMP%\UltraZip\pack_<guid>`（权限拒绝时回退输出目录并把暂存目录加入排除），`7z t` 成功后才覆盖目标，失败/取消清理；组合格式先建 TAR 再压缩。GZ/BZ2/XZ 限单文件，分卷和加密限 7z/ZIP。
- `ZipAesEngine` 从 EOCD/ZIP64 记录反推前置数据量并修正中央目录和本地头偏移，支持尾随数据、Store/Deflate、多条目、AES-128/192/256 与 AE-1/AE-2。按条目依次尝试 UTF-8、GBK、GB18030、UTF-16、UTF-8 BOM、CP437、Big5 密码编码；AES-CTR 计数器按 128 位小端递增，HMAC-SHA1 只取前 10 字节认证。输出先写同目录临时文件，HMAC、CRC 和大小校验通过后才替换目标文件，且拒绝目录穿越路径。
- `ExtractWithVaultAsync(...)`：高层流程 —— 先无密码试一次；若失败且是密码类错误，按 `PasswordVault.GetCandidatesOrdered()` 顺序逐个自动尝试（命中即 `RecordHit` 并返回）；全失败可选 `askPassword` 回调问一次。**UI 侧的重试循环在 MainWindow.RunExtractFlowAsync 里**（弹 PasswordPromptDialog，成功后按勾选 `Add` + `RecordHit`）。特殊 ZIP 与普通 7z 包共用该密码库流程。
- 密码通过 `-p"<密码>"` 传递（带引号以容忍空格）。

### 错误分类（Classify 方法，按输出文本匹配，顺序即优先级）

匹配关键词（均 OrdinalIgnoreCase，输出为 stdout + stderr 合并）：

| 关键词 | 分类 |
|---|---|
| `Enter password` | NeedsPassword（7z 在询问密码，stdin 已被我们关闭） |
| `Wrong password`、`Data Error in encrypted file` | WrongPassword |
| `Missing volume`、`Unexpected end of archive` | MissingVolume |
| `Cannot/Can not open encrypted archive` | 带 -p → WrongPassword；未带 → NeedsPassword |
| `Cannot/Can not open the file as archive`、`is not archive` | Corrupt |
| `Headers Error`、`Data Error`、`CRC Failed` | 带 -p → WrongPassword；未带 → Corrupt |
| 其余非零退出 | Other |

成功/失败的用户可见摘要由 `SummarizeError` 从输出中挑含 ERROR/Cannot 等关键词的行（最多 5 行）。

### 进度解析（ReportProgress）

7z 的进度用 `\r` 刷新而非换行，所以 `RunAsync` 按 `\r` 和 `\n` 把 stdout 切成“段”回调。每段用正则 `(?<!\d)(\d{1,3})%` 匹配百分比，**一个段内可能有多个百分比，取最后一个**；段内 ` 12% 5 - folder\file.txt` 形式的尾部提取为当前文件名；以 `- ` 开头的段是纯文件名行（Percent=-1 只更新文件名）。小/快的包 7z 可能只输出 0%，属正常。

### FormatDetector 三层识别与分卷归并（Detect 方法，按序匹配）

1. `name.7z.001` 类：文件名以 `.NNN`（≥3 位数字）结尾 → 首卷 `baseName.001`，`CollectNumberedVolumes` 连续枚举；存在跳号卷则记 MissingVolumes。格式按 baseName 扩展名（`GuessVolumeBaseFormat`）→ 首卷魔数兜底。
2. `xxx.partN.rar` → 首卷 `baseName.part1.rar`，从 part1 连续枚举，跳号记缺卷。
3. `name.r00/.r01...`（RAR 老式分卷）→ 首卷 `baseName.rar`，`CollectSuffixedVolumes` 枚举 `.r00` 起。
4. `name.z01/.z02...`（ZIP 分卷）→ 传给 7z 的是**末卷** `baseName.zip`，枚举 `.z01` 起。
5. 拖入老式 RAR 首卷 `.rar` 或 ZIP 末卷 `.zip`：若同目录有对应 `.rNN` / `.zNN`，同样归并；缺少首/末卷及中间跳号会列入 MissingVolumes。
6. 单文件第一层为 `DetectByMagic`：7z/ZIP/RAR4/RAR5/GZ/XZ/BZ2/CAB/WIM/ZSTD/LZ4 在头部，TAR 在 257，ISO 在 32769 读 `CD001`；第二层先调 `ZipAesEngine.GetPrefixSize` 复用 EOCD/ZIP64 解析，前缀写入 `ArchiveInfo.PrefixSize`，再走扩展名。
7. 前两层仍 Unknown 时，MainWindow 异步调 `SevenZipEngine.ProbeAsync`（`7z l -slt`，取最后一个 Type），`ApplyProbeType` 映射常见类型；其他非空 Type 用 `ArchiveFormat.Other` 仍允许 7z 解压。MZ 文件标记 IsSelfExtracting 并显示安全警告，探测失败才显示“未识别 + 扫描/压缩入口”。

**解压永远把 `ArchiveInfo.FirstVolumePath` 传给 SevenZipEngine**（再由其分流到内置 ZIP-AES 引擎或 7z）；UI 在 `ArchiveInfo.HasMissingVolumes` 时直接弹缺卷提示不解压。

### 归档浏览与选择性解压

- `SevenZipEngine.ListEntriesAsync` 特殊 ZIP 走 `ZipAesEngine.ListEntries`，其余执行 `7z l -slt`；`ParseArchiveEntries` 只在分隔线后解析 Path/Size/Packed Size/Modified/CRC/Method/Folder，避免把包自身和分卷元数据当条目。
- MainWindow 在 `FillSummaryAsync` 后异步加载列表，切换文件取消旧 `_listingCts`。`_entries` 保存完整列表，`_visibleEntries` 显示当前目录直接子级；`_folderNodes` 建树，支持上一级、默认目录优先排序和路径过滤。统计含文件/目录数、原始/压缩后大小和压缩率。
- `ExtractEntriesAsync` 普通包把所选 Path 逐个传给 `7z x`，特殊 ZIP 通过 `SelectEntries` 过滤；选目录包含其子路径。`ExtractEntriesWithVaultAsync` 与 MainWindow `RunEntryExtractFlowAsync` 复用密码库与弹窗重试。
- 双击目录负责导航；双击文件解到 `%TEMP%\UltraZip\view_<guid>`（权限拒绝回退源目录），UseShellExecute 调默认程序，临时目录记录到 `_viewTempDirs` 并在 OnClosed 清理。
- 视图设置由 `ViewSettingsStore` 存 `%APPDATA%\UltraZip\settings.json`：窗口尺寸、目录树/预览宽度、工具栏/状态栏/置顶、明细/列表样式、列显隐/顺序/列宽均持久化；任务运行时即使设置隐藏状态栏也强制显示，以保留进度和取消入口。
- 图像预览复用 `ExtractEntriesWithVaultAsync` 与密码弹窗，支持 PNG/JPEG/BMP/GIF/ICO/TIFF，单条最大 30 MB；每次选择独立 `_previewCts` 取消，BitmapImage 使用 OnLoad 避免锁文件，切换条目/压缩包、关闭预览及退出时清理临时目录。
- `ArchiveCommentReader` 从 ZIP 尾部 EOCD 读取注释（UTF-8 优先，GB18030 兜底），当前只读查看；ZIP 分卷的 `FirstVolumePath` 本身是末卷 `.zip`。

### PolyglotScanner（伪装文件）与两级解压策略

- 扫描：1MB 分块读全文件，块间重叠 64 字节防跨块漏检（重叠区重复扫描，靠 `SortedSet<long>` 去重）。签名表含 ZIP/7z/RAR4/RAR5/GZip；另外记录最后一个 EOCD（`PK\x05\x06`）位置与中央目录条目（`PK\x01\x02`）偏移列表，ZIP 条目备注显示“约 N 个条目 / EOCD 位于 0x…”。偏移 0 的匹配被忽略（那是文件本身的格式，归 FormatDetector 管）。
- 两级解压（MainWindow.ExtractEmbeddedAsync）：
  1. **先通过 SevenZipEngine 直接开原文件**（特殊 ZIP 走内置引擎，其余由 7z 处理）；
  2. 失败则询问用户后**从偏移处切出临时副本**（`CarveFromOffset` 流式拷贝到 `%TEMP%\UltraZip\carved_<guid>.<对应扩展名>`），再对副本走完整解压流程，用完即删。

### 命令行、单实例与 Shell 集成

- `App.OnStartup` 先处理 `--register/--register-all-users/--unregister`，其余参数由 `StartupRequest.Parse` 解析为 Open/Add/ExtractHere/ExtractHereDelete/ExtractToDialog/ExtractTo/Test；支持 `--password`、`--quiet`。`App.xaml` 不再使用 StartupUri。
- `SingleInstance` 用 `Local\UltraZip.SingleInstance` Mutex 判活，后续实例通过当前用户命名管道 `UltraZip.Commands` 发送 JSON 参数并退出；主实例 `MainWindow.EnqueueRequest` 串行处理任务。quiet 首实例隐藏主窗口，队列完成后退出，结果仍弹窗。
- `SevenZipEngine.TestAsync` 普通包执行 `7z t`，特殊 ZIP 解到临时目录做完整校验后清理；`TestWithVaultAsync` 复用密码库排序与命中记录。
- 主窗口“解压并删除压缩文件”和 `--extract-here-delete` 只在完整解压成功后调用 `ArchiveFileDeleter`：单包删除 `OriginalPath`，分卷集删除全部 `Volumes`；失败/取消不删，删除失败单独警告。窗口操作不弹删除确认，成功后清空已删除包的浏览视图。
- `ShellIntegration` 在 `*\shell` 和 `Directory\shell` 注册带图标的“添加到压缩文件…”（`--add`），按压缩扩展名及当前实际 ProgID 注册级联 verb（打开/当前目录解压/解压到此处并删除压缩文件/选择目录解压/测试）；只读 UserChoice 以定位 ProgID，不修改默认关联。同时写 ProgID、OpenWithProgids、Capabilities、RegisteredApplications，完成后 SHChangeNotify。默认 HKCU，全员 HKLM 走 runas UAC；注销清当前用户注册。
- `SettingsWindow` 提供注册/移除入口。真实注册表和 Explorer 验收需用户明确同意，不能为测试擅自改系统配置。

### PasswordVault

- 单例（`PasswordVault.Instance`），存储路径 `%APPDATA%\UltraZip\passwords.json`，**明文 JSON（用户明确要求，不要改成加密存储）**。
- 自动尝试顺序 `GetCandidatesOrdered()`：HitCount 降序 → LastUsed 降序。命中后 `RecordHit(password)` 计数并更新时间。
- `Add(label, password)` 按密码去重（同密码只更新 Label）；CRUD 均在锁内并即时 Save；读写异常静默吞掉（不阻塞主流程）。

## 重要实现细节与坑（都是实测踩过的）

- **7z 的错误文本走 stderr**：Wrong password、Unexpected end of archive 等关键错误行在 stderr。解压参数必须带 `-bse1` 把错误并入 stdout，同时代码仍把 stderr 拼进分类输入兜底。没有 `-bse1` 时密码错误/缺卷全被误分类为 Other。
- **stdin 必须关闭**：不带密码解压加密包时 7z 会打印 `Enter password` 并等待输入；`RunAsync` 启动后立即 `StandardInput.Close()`，7z 读到 EOF 退出（实测退出码 255），我们才能靠 `Enter password` 文本分类为 NeedsPassword。
- **进度用 \r 刷新**：不能按行读 stdout，必须按 `\r`/`\n` 切段；且一个段可能含多个百分比，取最后一个。
- **StringBuilder 并发崩溃教训**：曾在 `Progress<T>` 回调里 Append 共享 StringBuilder，控制台（无 SynchronizationContext）下回调并发执行导致 `ArgumentException: Destination is too short`。现在 stdout 累积只在 `RunAsync` 的单读取线程里做，Progress 回调只做无共享状态的进度上报。
- **输出编码**：进程参数带 `-sccUTF-8` 且 `StandardOutputEncoding=UTF8`，否则中文文件名乱码。
- **中文密码输入**：WPF 原生 `PasswordBox` 禁用 IME，仅设置 `InputMethod` 属性无效；密码输入统一使用 `Views/ImePasswordBox.cs`（支持 IME 的 TextBox + 掩码），不要换回原生 PasswordBox。
- 7z 对“随机数据伪装的 zip”类包可能直接成功打开（容错），所以伪装解压先直接开原文件是有效策略，切副本是兜底。
- 冒烟验证方式：临时 console 工程直接 `<Compile Include>` 引用 Services/Models 源码跑 33 项断言（全部通过）。注意 .NET 的 `Environment.GetFolderPath(ApplicationData)` **不受 APPDATA 环境变量影响**，测试 PasswordVault 会写真实 `%APPDATA%`，需自行清理。
- 机器上无 rar 创建工具时可用 `C:\Program Files\WinRAR\Rar.exe` 造 RAR 样例；老式 `.r00` 分卷样例可由 partN.rar 重命名得到（数据布局等价，7z 可读）。

## 修改/扩展指引

- **新增一种压缩格式识别**：① `Models.cs` 的 `ArchiveFormat` 加枚举值；② `FormatDetector` 加魔数字段 + `DetectByMagic` 分支 + `FormatName`/`FormatFromExtension`/`GuessVolumeBaseFormat` 映射；③ 如需参与伪装扫描，`PolyglotScanner.Signatures` 加一行；④ MainWindow 的 `Browse_Click` 文件过滤器加扩展名。解压本身不用改（7z 都认识）。
- **调整密码尝试顺序**：改 `PasswordVault.GetCandidatesOrdered()` 的排序表达式。
- **调整错误分类**：改 `SevenZipEngine.Classify`（注意匹配顺序即优先级），用户可见摘要在 `SummarizeError`。
- **加新窗口**：放 `Views/` 下，XAML + code-behind，从 MainWindow 的按钮事件里 `new Window{Owner=this}.ShowDialog()`。不引 MVVM 框架，直接 code-behind。
- **改解压参数/进度解析**：`ExtractAsync` 的参数串与 `ReportProgress`；改动后务必用真实 7z 输出重新验证（先手工跑命令观察输出再定解析）。

## 已知遗留问题

- zip 分卷（`.z01`）识别/解压路径已实现但**未实测**（7z/WinRAR 都不便制作 zip 分卷样例）。
- 密码管理器“添加”按钮流程朴素：先插入占位行再在 DataGrid 内编辑。
- GUI 拖放与取消按钮仍未自动化；已实测启动、CLI、单实例、SFX/前置 ZIP 界面识别和归档树/统计。引擎层原有 33 项解压冒烟断言，压缩创建 47 项、格式识别 27 项、归档浏览与选择解压 22 项真实样例断言通过。
- 1 GB 包 2 秒列表性能、外部默认程序双击打开未自动化验收；真实注册/注销、Explorer 右键、多选与另一账户验收未执行（避免擅自修改系统配置）。
- 压缩创建尚未实测 4 GB 大包；SFX 创建未做（仓库无 `7z.sfx`），但 SFX 解压已实测。WIM/ZSTD/LZ4 仅验证魔数。RAR 样例依赖外部 WinRAR 制作，本仓库不含 RAR 样例。

## 约定（不要违反）

- **压缩创建已实现**（方案见 `docs/consolidation-plan.md` §4 与 `docs/bandizip-feature-parity.md` §4.5），但 RAR 创建永不做（许可），ZIPX/ALZ/EGG 不做。
- **密码库保持明文存储**（用户明确要求）；README 和 PasswordManagerWindow 中都要有醒目提示。
- **不引第三方 MVVM 框架**，MVVM-lite / code-behind 风格，保持简单。
- 不要把 7-Zip 换成托管解压库（原因见“核心架构”）；压缩创建同样只经 `7z.exe`，不自研压缩器。
- 修改后要求 `dotnet build UltraZip.sln` 0 错误 0 警告。
