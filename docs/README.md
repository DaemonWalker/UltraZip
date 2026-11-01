# docs 索引：新会话怎么接

> 这张表是给「新开一个对话继续开发」用的。三个文档各有分工，**只丢文件名不够**——要指明章节，否则范围会漂。

---

## 1. 文档地图

| 文档 | 回答什么问题 | 什么时候读 |
|---|---|---|
| `AGENTS.md` | 工程约束、构建方式、已踩过的坑（**DSH 自动加载**） | 每个会话自动 |
| `consolidation-plan.md` | 为什么统合、统合到什么程度、本机装了哪些工具、§8 识别盲区 | 定方向、决定能不能卸载别的压缩软件 |
| `bandizip-feature-parity.md` | Bandizip 功能全景、差距对照、**§4.5 压缩参数**、P0–P4 路线图 | 写引擎前查规格 |
| `ui-and-shell-plan.md` | 窗口体系、**§2 右键菜单注册表结构**、M1–M5 里程碑 | 做 UI / 壳集成 |

---

## 2. 依赖关系与推荐顺序

```
压缩创建（parity §4.5 + ui M5）   ─┐
右键 + 关联（ui §2）              ─┼─ 三者互不依赖，可任意顺序
格式识别补强（consolidation §8）  ─┘
                                  ↓
归档浏览（ui M3）                 ← 最大、且要新抽象，放最后
```

**建议顺序：压缩创建 → 右键/关联 → 识别补强 → 归档浏览。**

当前进度：**A（压缩创建）、B（右键/关联/单实例）、C（格式识别补强）与 D（归档浏览）已完成**。四个基础任务已落地；后续打磨按 `ui-and-shell-plan.md` M4（任务化进度与结果）继续。

- 压缩创建先做：没有它，Bandizip / WinRAR 一个都卸不掉（它们是唯一能造包的）。
- 右键/关联第二：这才让"统合"在日常里成立；它独立于引擎，改的是启动路径。
- 识别补强随时可插队：只动 `FormatDetector`，不影响解压主流程。
- 归档浏览最后：需要 `ArchiveSession` 抽象，和上面三件都不冲突，但工作量大。

---

## 3. 四个任务的启动 prompt（直接复制）

### A. 压缩创建（推荐第一个）

```
读 AGENTS.md 和 docs/bandizip-feature-parity.md §4.5，实现压缩创建。

范围：
1. Models/Models.cs 加 CreateFormat / CreateOptions / CreateResult
2. Services/SevenZipEngine.cs 加 CreateAsync + BuildCreateArgs：
   工作目录取源的公共父目录、先写临时文件、成功后 t 校验再改名、失败或取消清理干净
3. Views/CreateArchiveDialog：源列表、格式、级别、密码、分卷、输出路径
4. MainWindow 加「新建压缩包」入口；拖入非压缩包时提示压缩

不做：RAR 创建、ZIPX/ALZ/EGG、包内编辑。
验收标准见 §4.5 末尾与 ui-and-shell-plan.md M5。
完成后 dotnet build UltraZip.sln 必须 0 错误 0 警告，并把与文档不一致的地方回写文档。
```

### B. 右键菜单 + 关联 + 单实例

```
读 AGENTS.md 和 docs/ui-and-shell-plan.md §2.1–2.6，实现壳集成。

范围：
1. app.manifest（PerMonitorV2）
2. App.OnStartup 参数解析，去掉 StartupUri
3. Mutex + 管道单实例转发（右键多选要聚合成一个任务队列）
4. Services/ShellIntegration.cs：HKCU 静态 verb + 级联子菜单 +
   SystemFileAssociations 按扩展名注册 + --register/--unregister/--register-all-users
5. 关联：ProgID + OpenWithProgids + Capabilities/RegisteredApplications

不做：COM 扩展、MSIX 打包、安装器、抢 UserChoice。
验收见 §2.2 末尾与 M2 的验收段。
```

### C. 格式识别补强

```
读 AGENTS.md 和 docs/consolidation-plan.md §8，实现格式识别三层化。

范围：
1. FormatDetector 补 ISO/CAB/WIM/ZSTD/LZ4 魔数（ISO 在 32768+1，要 Seek）
2. 加 EOCD 探测：尾部 64KB、delta = eocd_pos − cd_size − cd_offset、ZIP64 分支
3. SevenZipEngine.ProbeAsync：7z l -slt 退出码 0 → 取 Type 字段
4. 识别顺序改成 魔数 → 扩展名(含 EOCD) → 7z 探测 → 未识别提示扫描
5. 未识别时给「扫描内嵌包」入口

不动解压逻辑。验收见 §8.5。
```

### D. 归档浏览

```
读 AGENTS.md 和 docs/ui-and-shell-plan.md §1.2 与 M3，实现归档浏览。

范围：
1. 7z l -slt 解析成 ArchiveEntry（路径/大小/压缩后/时间/CRC/方法/是否目录）
2. 左树 + 右明细列表，列宽可拖
3. 勾选后只解选中条目（7z x arch -o<dir> path1 path2）
4. 双击条目 = 解到临时目录并打开
5. 状态栏统计条目数/原始大小/压缩率

验收：1 GB 包 2 秒内出列表；勾 3 个文件只解 3 个；中文名不乱码。
```

---

## 4. 会话交接规矩

1. **一次只做一个里程碑**，不要把 A+B 塞进一个会话，评审和回滚都难。
2. **完成标准**：`dotnet build UltraZip.sln` 0 错误 0 警告 + 文档里的验收项勾掉。
3. **有偏差就改文档**——文档是唯一的跨会话记忆，只在对话里说等于没说。
4. **动了约定要明说**：改 `AGENTS.md` 的「约定」段必须在回复里点名。
5. **未实现前不宣称**：README 和界面文案不得写「支持创建压缩包」。
6. **每个里程碑结束**，在该文档对应章节补一行「实际做了什么 / 差在哪」。
