# UltraZip 统合方案：用一个工具取代本机的 7-Zip / Bandizip / WinRAR

> 背景：本机同时装了 7-Zip、Bandizip、WinRAR，三者职责重叠，右键菜单三套、默认关联一套。目标是把日常压缩/解压收敛到 UltraZip 一个入口。
>
> 关联文档：压缩创建的引擎与参数见 [bandizip-feature-parity.md](bandizip-feature-parity.md) §4.5；右键菜单与注册表实现见 [ui-and-shell-plan.md](ui-and-shell-plan.md) §2。

---

## 0. 结论先行

UltraZip 现有的解压面（7z / ZIP / RAR / GZ / TAR / BZ2 / XZ + 分卷 + 加密 + 拼接 ZIP + 伪装扫描）**已经覆盖三者的解压能力**，且自带 `tools/7z/7z.exe`，不依赖系统安装的 7-Zip。压缩创建（SFX 除外）、壳集成、三层识别和归档浏览代码均已落地；右键/关联可按需注册且不抢默认应用。剩余验收与打磨项是：

1. **Shell 实机验收**：真实注册/注销、Explorer 展示、多选和另一账户尚未执行；
2. **大包与交互验收**：1 GB 列表性能、4 GB 创建和外部程序双击查看尚未自动化。

**日常场景可以只留 UltraZip**；但有 4 个场景仍需要保留原工具（§7），其中「RAR 创建」是无解的，其余是优先级问题。

---

## 1. 本机现状（实测）

| 工具 | 位置 | 当前职责 | 被取代的难度 |
|---|---|---|---|
| 7-Zip | `C:\Program Files\7-Zip`（`7z.exe`、`7zFM.exe` 在 PATH） | 命令行引擎 + 图形界面（浏览/解压/创建） | **低**：UltraZip 内置同款 `7z.exe`，卸载不影响；只有「浏览压缩包」界面需要 UltraZip 先补上 |
| Bandizip | `C:\Program Files\Bandizip`（`Bandizip.exe`、`bz.exe`） | **默认关联持有者**（`.zip .7z .rar .tar .gz` 的 UserChoice 都是 `Bandizip.*`）、右键菜单、ALZ/EGG/ZIPX 等韩系格式 | **中**：日常可取代；ALZ/EGG/ZIPX 不能（7z 不认） |
| WinRAR | `C:\Program Files\WinRAR` | RAR 创建 / 修复；解压 | **高**：RAR 创建无法替代（许可，见 §7）；解压可替代（7z 支持 RAR5） |

注册表事实（`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\<ext>\UserChoice`）：

```
.zip -> Bandizip.zip     .7z -> Bandizip.7z      .rar -> Bandizip.rar
.tar -> Bandizip.tar     .gz -> Bandizip.gz
```

Bandizip 还注册了 30 多个 ProgID（`Bandizip.alz`、`Bandizip.egg`、`Bandizip.zipx`、`Bandizip.zst`、`Bandizip.lz4`…），这些是它真正的护城河，也是 UltraZip 短期补不上的部分。

---

## 2. 统合后的目标形态

```
双击任意压缩包 ──┐
右键 → UltraZip ──┼──> UltraZip（唯一 UI）
拖放到窗口   ──┘         └── tools/7z/7z.exe（唯一引擎）

7-Zip.exe    → 卸载（引擎已被内置）
Bandizip.exe → 卸载（除 ALZ/EGG/ZIPX 需求）
WinRAR.exe   → 保留或卸载（除 RAR 创建需求）
```

原则：**一个界面、一个引擎、一套右键**。不再出现「解压用 A、创建用 B、分卷用 C」。

---

## 3. 能力对照矩阵

| 场景 | 现在谁做 | UltraZip 现状 | 结论 |
|---|---|---|---|
| 解压 ZIP / 7Z / RAR / TAR / GZ / BZ2 / XZ | 三家都行 | ✅ | 可直接取代 |
| 分卷解压（`.7z.001` / `partN.rar` / `.r00` / `.z01`） | 三家都行 | ✅ 且更细（缺卷明确提示） | 可直接取代 |
| 加密包解压 + 密码自动尝试 | 三家都要手输 | ✅ 密码库自动尝试 | **强于三家** |
| 拼接 ZIP / 前置数据 / WinZip-AES | 常失败 | ✅ 内置 ZipAesEngine | **独有** |
| 伪装文件（视频里藏包）扫描 | 无 | ✅ | **独有** |
| 浏览压缩包内容（看清单再挑着解） | 7-Zip、Bandizip | ✅ 目录树/明细/选择性解压/双击查看 | 可直接取代 |
| 创建 7Z / ZIP / TAR / GZ / BZ2 / XZ | 三家都行 | ✅ 另支持 TGZ/TBZ2/TXZ | 可直接取代 |
| 创建分卷 / 加密 / SFX | 7-Zip、Bandizip、WinRAR | ◐ 分卷与 AES 加密已做，SFX 未做 | 日常可取代 |
| 右键菜单 | 三家都有 | ✅ 可选注册，代码已完成 | 待 Explorer 实机验收 |
| 默认关联（双击打开） | Bandizip | ✅ 加入候选，不抢 UserChoice | 待系统设置实机验收 |
| ALZ / EGG / ZIPX / ZST / LZ4 解压 | **仅 Bandizip** | ❌ 7z 不支持 | 保留 Bandizip 的唯一理由 |
| ISO / CAB / WIM 解压 | 7-Zip、Bandizip | ✅ 魔数 + 7z 探测 | 可直接取代 |
| 自解压 EXE（`.exe`）解包 | 7-Zip、WinRAR | ✅ 7z 探测 + 安全提示 | 可直接取代 |
| 前置数据包（视频里贴 ZIP）双击解压 | 多数工具失败 | ✅ EOCD/ZIP64 自动显示偏移 | **独有** |
| RAR 创建 | **仅 WinRAR** | ❌ 不做（许可） | 保留 WinRAR 的唯一理由 |
| 修复损坏 ZIP / 密码恢复 | WinRAR、Bandizip（Pro） | ❌ P3 | 低频，可暂缓 |

---

## 4. 压缩功能规格（摘要）

完整参数映射见 parity §4.5，这里只列决策。

**格式**：7Z（默认）、ZIP（兼容）、TAR、GZ / BZ2 / XZ、TGZ / TBZ2 / TXZ（TAR+压缩两步）、EXE（`-sfx`，需随包分发 `7z.sfx`）。

**不做**：RAR（UnRAR 许可禁止实现 RAR 压缩）、ZIPX / ALZ / EGG（7z 不支持）、ISO / WIM / LZH（收益低）。

**默认值建议**：

| 项 | 默认 | 理由 |
|---|---|---|
| 格式 | 7Z | 压缩率、AES-256、可加密文件名；日常首选 |
| 级别 | 5（标准） | 与 7-Zip 默认一致 |
| 线程 | `-mmt=on` | 按核数 |
| 密码 | 空 | 需要时从密码库选 |
| 分卷 | 不分卷 | 需要时选预设 |
| 压缩后校验 | 开 | 大包可关，但默认开更稳 |

**三个必须处理的坑**（实现时照做，详见 parity §4.5）：

1. `7z a` 对已存在的包是**追加/更新**，不是新建 → 目标存在必须先删。
2. 取消会留下**半个包** → 先写 `.tmp`，校验通过再改名。
3. 7z 做不出 WinRAR 兼容的 `.z01` 分卷 → 统一用 `.zip.001` 风格并在 UI 说明。

---

## 5. 落地步骤（用户视角）

| 步 | 动作 | 说明 |
|---|---|---|
| 1 | UltraZip `--register` 写右键菜单 | 默认写 HKCU，免管理员；`--register-all-users` 需 UAC |
| 2 | 在「设置 → 默认应用」把 `.zip .7z .rar .tar .gz` 改成 UltraZip | **不自动抢**：`UserChoice` 有哈希保护，硬改会被系统重置 |
| 3 | 验证：双击、右键、拖放各跑一遍 | 见 §6 验收 |
| 4 | 卸载 7-Zip | 引擎已内置，随时可做 |
| 5 | 视需求卸载 Bandizip / WinRAR | 保留理由见 §7 |

回滚：`--unregister` 清右键；默认应用改回 Bandizip；三者的安装包都还在，重装即恢复。

> 注意：**不要**用「恢复 Win10 右键菜单」的注册表 hack 来让菜单显眼。Win11 把静态 verb 折叠进「显示更多选项」是系统行为，7-Zip 也一样；改全局菜单会影响所有软件。

---

## 6. 验收清单

- [ ] 双击 `.zip` / `.7z` / `.rar` / `.tar.gz` 都进 UltraZip
- [ ] 右键压缩包出现 4 项（打开 / 解压到当前 / 解压到… / 测试），ZIP 在第三方 ProgID 关联下也能展开
- [ ] 右键文件或文件夹出现带图标的“添加到压缩文件…”
- [ ] 右键 `.txt` **不**出现解压项
- [ ] 多选 3 个包右键解压，只起 1 个实例、不弹 3 个窗口
- [x] 创建 7Z、ZIP、分卷、加密包，都能被 7-Zip / WinRAR 正常打开（小包实测，4 GB 未测）
- [x] 创建中途取消，目录里无残留
- [ ] 加密包解压自动命中密码库
- [ ] 卸载 7-Zip 后功能不受影响

**前置项（不满足则先别卸载 7-Zip / Bandizip）**：

- 浏览压缩包内容（列表 + 选择性解压）——ui-and-shell-plan M3；
- 右键菜单与关联——M2。

---

## 7. 什么情况下还得留着

| 需求 | 必须保留 | 原因 |
|---|---|---|
| 解 ALZ / EGG / ZIPX / ZST / LZ4 | Bandizip | 7z 引擎不支持，自研成本高 |
| 创建 RAR | WinRAR | UnRAR 源码许可禁止实现 RAR 压缩 |
| 修复损坏的 ZIP / 密码恢复 | WinRAR / Bandizip Pro | UltraZip 的 P3，未实现 |
| 压缩包内图片预览 | Bandizip Pro | 同上 |

判断标准很简单：**如果你从来没用过 ALZ/EGG、也不需要创建 RAR，那这三个工具都可以卸。**

---

## 8. 格式识别的三个盲区与解决办法

原状（[FormatDetector.cs](../UltraZip/Services/FormatDetector.cs)）：**魔数 → 扩展名 → Unknown**，魔数表 8 种（7z / ZIP / RAR4 / RAR5 / GZip / XZ / BZip2 / TAR）。现已按 §8.4 落地三层识别，实际结果见 §8.5 末尾。

三个原盲区都在表外，但成因不同：一个是被自己的创建功能造出来的，一个是魔数天生看不见，一个是引擎能解而表里没写。

### 8.1 SFX 自解压：自己能造，却认不出

**现象**：`-sfx` 产出的 `.exe` 以 `MZ`（PE 头）开头，魔数表里没有 → 拖进来显示"未识别"。这是自洽性问题：**自己造的东西自己不认识**。

**解决办法**（按优先级）：

1. **三层兜底（§8.4）**：对 `MZ` 开头的文件调 `7z l -slt`，退出码 0 即认定，类型取输出里的 `Type = ` 字段。这一条同时解决三个盲区，是首选。
2. **快路径（可选）**：读 PE 头后在文件尾部找 7z 数据特征，或匹配 SFX 模块的固定字符串。**特征串需实测确认**（不同版本的 `7z.sfx` 可能不同），不稳定就别做，直接走第 1 条。
3. **创建端自检**：创建 SFX 成功后立刻 `t` 一次，并把"能识别自己刚造的文件"写成回归用例——否则这个 bug 会等到用户双击时才发现。

**注意**：SFX 本质是可执行文件。UltraZip 只解包、不执行；右键菜单里不提供"运行"，UI 上也要标"自解压程序，请勿直接运行未知来源的 exe"。

### 8.2 前置数据包：魔数在偏移 0 天生看不见

**现象**：MP4（`ftyp`）+ 后贴 ZIP。偏移 0 不是 `PK`，魔数必然失败；扩展名可能是 `.mp4`，`FormatFromExtension` 也判不出。

**现状**：只能手动点"扫描伪装压缩包"，再走两级解压。识别阶段完全看不出它是个压缩包。

**解决办法**：

1. **EOCD 反推（首选，比魔数扫描更准）**：读文件尾部 64 KB（ZIP 注释上限 65535 字节，所以尾部窗口够用），找 `PK\x05\x06`，取 `cd_size` / `cd_offset`：

   ```
   delta = eocd_pos − cd_size − cd_offset
   ```

   `delta > 0` 就说明前面贴了别的东西，而且**直接得到偏移量**。这套逻辑 `ZipAesEngine` 里已经有了（偏移修正），把它抽出来提前复用即可，不用新写。
2. **接进识别流程**：魔数失败且扩展名属于 ZIP 家族时，自动跑一次 EOCD 探测；命中则标成"前置数据 ZIP（偏移 0x…）"，主界面直接给"解压内嵌包"入口，而不是显示"未识别"再让用户去点扫描。
3. **魔数扫描（兜底）**：7z / RAR 没有 EOCD，只能靠 `PolyglotScanner` 全盘扫；保持**手动触发**，避免每次打开大文件都扫一遍（你那个 1.zip 是 1.6 GB，全盘扫描代价不小）。

**成本**：尾部 64 KB 读一次，毫秒级。

**边界**：ZIP64 要走 ZIP64 EOCD 记录，不能只读 32 位字段；`delta` 为 0 时就是普通 ZIP，不要误标。这两点**需用 1.zip 实测**。

### 8.3 ISO / CAB / WIM：引擎能解，表里没有

**现象**：`7z x a.iso` 完全正常，但拖进 UltraZip 显示"未识别"——因为表里没这几条。这是"能力有、声明无"，最不应该。

**办法 A：补表**（准确、零成本）

| 格式 | 魔数 | 偏移 |
|---|---|---|
| ISO 9660 | `CD001` | `32768 + 1` |
| CAB | `MSCF` | 0 |
| WIM | `MSWIM\0\0\0` | 0 |
| ZSTD | `28 B5 2F FD` | 0 |
| LZ4 frame | `04 22 4D 18` | 0 |
| ARJ | `60 EA` | 0 |
| LZH | `-lh` | 2 |

实现注意：`DetectByMagic` 现在固定读 8 字节，ISO 要 `Seek(32768)`（照抄 TAR 的 257 写法），**不要**把缓冲区一步加大到 32 KB。配套还要改四处：`ArchiveFormat` 枚举、`FormatName`、`FormatFromExtension`、`Browse_Click` 的过滤器——即 AGENTS 里「新增一种压缩格式识别」那五步。

**办法 B：不补表，全靠 §8.4 的 7z 探测**。省事，但每次多一个进程，且要把 `Type` 字段的字符串映射回枚举。

**建议**：高频的 ISO / CAB / WIM 补表；长尾（ARJ / LZH / ZSTD / LZ4）交给探测，别为每个格式维护一条正则。

### 8.4 统一方案：三层识别 + 一层兜底

```
① 魔数（表驱动；偏移 0 / TAR 257 / ISO 32768）
     命中 → 完成
② 扩展名（ZIP 家族先做 EOCD 探测，能发现"前置数据"）
     命中 → 完成
③ 7z 探测：7z l -slt <file>，退出码 0 → 认；类型取 Type 字段
     命中 → 完成（覆盖 SFX、ISO/CAB/WIM、长尾格式）
④ 仍失败 → "未识别"，提示可跑伪装扫描（PolyglotScanner）
```

设计原则一句话：**魔数是快路径，7z 是最终裁决。** 既然目标是"什么都能开"，最终裁决权就不该在魔数表手里，而在引擎手里。

| 层 | 成本 | 触发条件 |
|---|---|---|
| ① 魔数 | 微秒 | 总是 |
| ② 扩展名 / EOCD | 微秒 / 毫秒 | ① 失败 |
| ③ 7z 探测 | 30–100 ms（一个进程） | ①② 都失败 |
| ④ 伪装扫描 | 秒级（按文件大小） | 用户手动 |

**顺带收益**：③ 上线后，新增格式不必先改代码——只要 7z 认识，UltraZip 就认识。魔数表退化成"优化项"，而不是"能力边界"。

### 8.5 实现清单与验收

| 文件 | 改动 |
|---|---|
| `Services/FormatDetector.cs` | 加 ISO 等魔数、EOCD 探测；魔数失败时调探测 |
| `Services/SevenZipEngine.cs` | 新增 `ProbeAsync(path)`：`l -slt` 退出码 0 → 返回类型名 |
| `Models/Models.cs` | 补表时加 `ArchiveFormat` 枚举值 |
| `Services/ZipAesEngine.cs` | 抽出 EOCD / 偏移计算供识别阶段复用（纯重构，不动解压逻辑） |
| `MainWindow.xaml.cs` | 未识别时给"扫描内嵌包"入口；`Browse_Click` 过滤器加扩展名 |

**验收**：

- [x] 造一个 SFX，双击能识别成"自解压 7z"并解开
- [x] 用 `1.zip` 那种前置数据包，双击直接提示内嵌偏移，不必先点扫描
- [x] `.iso` 拖进来显示 ISO 并解压成功
- [x] 任意不认识的文件，落到"未识别 + 扫描入口"，不报错、不卡顿

**实际落地**：已补 ISO/CAB/WIM/ZSTD/LZ4 魔数与扩展名；`ZipAesEngine.GetPrefixSize` 复用现有 EOCD/ZIP64 布局解析，`SevenZipEngine.ProbeAsync` 在快速识别失败后异步裁决，未知 Type 以 `ArchiveFormat.Other` 交给 7z。SFX 显示自解压警告，前置 ZIP 显示十六进制偏移，新格式同步加入浏览过滤器和 Shell 注册集合。真实样例 27 项断言通过：SFX、前置 MP4+ZIP、最小 ISO9660、CAB 均实际解压，真实 `1.zip` 偏移为 21139992；WIM/ZSTD/LZ4 验证魔数，ZIP64 沿用既有解析路径。

---

## 9. 与现有文档的关系

| 文档 | 内容 |
|---|---|
| 本文 | 为什么要统合、统合到什么程度、怎么落地；§8 三个识别盲区与三层方案 |
| [bandizip-feature-parity.md](bandizip-feature-parity.md) | 功能全景、差距对照、§4.5 压缩引擎参数、路线图 |
| [ui-and-shell-plan.md](ui-and-shell-plan.md) | 窗口体系、§2 右键菜单注册表结构、里程碑 |
