# `1.zip` 解析规格说明（可复现 / 可据此重写代码）

> 用途：把这份文档交给另一个会话，即可重新生成整套**只读**分析代码——定位三段拼接结构、解析 WinZip-AES 加密 ZIP、判断密码用的是哪种字符编码、验证 1.65 GB 密文完整性，并解释「WinRAR 能开、7-Zip 报密码错误」的原因。
>
> 文中所有数值都是实测值，可直接写成**断言**；第 6 节是**本实例**的验收清单。
>
> ⚠️ 先读第 0 节：这份文档分为「通用方法」和「本文件实测值」两部分，处理**别的文件**时只需要替换第 1 节的数字，算法与坑位清单照用。

---

## 0. 通用性说明（换文件时先看这节）

### 0.1 哪部分是通用的、哪部分只属于这个文件

| 章节 | 性质 | 换文件时 |
|---|---|---|
| 0、2、3、5、7、9 | **通用**：算法、字段布局、判据、坑位、症状→机制 | 直接复用 |
| 1、6 | **仅本文件**：偏移、长度、哈希、salt、pv | 用新文件跑一遍得到新值 |
| 2.4 的候选编码表 | 通用 | 候选列表照用，命中项由数据决定 |
| 4 的环境 | 半通用 | 依赖与本机工具版本 |
| 10 的自测记录 | 通用方法 + 本机结果 | 可作为「如何自证」的模板 |

一句话：**它是一份「ZIP 加密 + 拼接容器」的诊断规格书，不是只能解 `1.zip` 的脚本**。只要是「某个工具能打开、另一个工具说密码错误」的 ZIP 系文件，都能按它走完。

### 0.2 症状 → 动作（决策树）

```
① 7z 说 "Is not archive / 无法作为压缩包打开"
     → 先算 delta（2.1）。delta ≠ 0 说明前面贴了别的东西；
       7-Zip 对 ≥1 KB 前置数据就会直接放弃，这不是密码问题。
     → 用第 3 节的 EOCD 扫描 + delta 修正；WinRAR 会自己修正，所以它能开。

② 能列出条目，但解压/测试报 "Wrong password"
     → 确认加密类型：method 99 = WinZip-AES（2.3）；flag bit0 且 method≠99 = ZipCrypto。
     → 跑 2.4 的编码候选判定。

③ 编码判定结果
     ├─ 某个编码的 pv 匹配 + HMAC 通过   → 密码正确，文件完好；
     │     若目标工具用的是另一种编码 → 就是「工具 vs 打包工具」的编码分叉（第 7 节）。
     ├─ pv 匹配但 HMAC 不通过          → 密码对、**数据损坏**（下载截断/坏块）。
     └─ 全部不匹配                      → 密码真的不对，或用的是没列进候选的编码
                                          （可打印 pwdenc 全表、或问用户原始输入法）。

④ HMAC 通过以后再看「里面还有没有一层」
     → 本文件是两层：外层 UTF-8、内层 GBK。发现新的一层就从 ② 重跑。
```

> 判据的价值就在这个分叉：**pv 只看 16 bit**，容易误命中；**HMAC 覆盖全部密文**，能区分「密码错」和「文件坏」。很多工具把两者都报成「密码错误」，这是排错时最大的坑。

### 0.3 移植到新文件时要改的东西（只有 5 处）

1. `path`（第 1 节表格第 1 行）
2. 密码字符串（含码位，避免复制误差）
3. 期望值表（第 6 节）——先用 `zipaes.py` 跑出新值，再回填成断言
4. `delta`：**不要硬编码**，由 `EOCD − cd_size − cd_off` 算出
5. 是否需要递归进内层（本文件是两层）

其余（EOCD 扫描、AES extra 解析、PBKDF2、CTR、HMAC、编码候选）一律不改。

---

## 1. 输入文件事实（断言基准）

| 项 | 值 |
|---|---|
| 路径 | `E:\workspace\temp\1.zip` |
| 大小 | `1,675,135,956` 字节（1598 MiB） |
| 整文件 SHA-256 | `0ef20cf0f45a12b6781b169dee163bde6afa6b654c39b0b8f728303fab9fe4ca` |
| 整体布局 | MP4 视频 + ZIP + RAR5 残片（见下表） |
| 密码（用户提供） | `上老王论坛当老王` = U+4E0A U+8001 U+738B U+8BBA U+575B U+5F53 U+8001 U+738B |

### 1.1 三段区间

| 区间（字节，左闭右开） | 长度 | 内容 |
|---|---|---|
| `[0, 21139992)` | 21,139,992 | 一段**完整的 fragmented MP4**（DASH/fMP4） |
| `[21139992, 1675117502)` | 1,653,977,510 | **真正的 ZIP**（AES-256，单条目 `1635.zip`） |
| `[1675117502, 1675135956)` | 18,454 | 以 `Rar!\x1a\x07\x01\x00`（RAR5）开头，头部损坏，UnRAR 报「已损坏的文件头」，不可用 |

关键推论：ZIP 的中央目录偏移是**相对 ZIP 自身起点**记录的，所以文件开头那 21,139,992 字节是后来贴在 ZIP 前面的。这个「前置数据量」记为 `delta`。

### 1.2 MP4 段（`[0, 21139992)`）

- 首个 box：`00 00 00 24` + `ftyp`，size=36，brand=`iso5`
- 紧接 `00 00 03 be` + `moov`，size=958（含 `mvhd`、`av01`/`mp41` 等）
- 之后是数百组 `moof`（约 1,280–1,304 B）+ `mdat`（约 25–29 KB）交替
- 最后一个 `mdat` 结束位置**正好** = 21,139,992，即 ZIP 本地头 `PK\x03\x04` 的起点

> 判定方法：从 0 开始按 big-endian 读 `size(4)+type(4)`；`size==1` 时读 64 位 size；`size==0` 表示「到文件尾」。走到 `21,139,992` 时下一个 box 前 4 字节是 `PK\x03\x04`，不是合法 box size。

### 1.3 ZIP 段（精确字段值）

**EOCD 扫描结果**

| 字段 | 值 |
|---|---|
| EOCD 绝对偏移 | `1,675,117,280` |
| EOCD 签名 | `50 4b 05 06` |
| 磁盘 / CD 磁盘 | 0 / 0 |
| 条目数（本盘/总） | 1 / 1 |
| `cd_size` | 65 |
| `cd_off`（相对 ZIP 起点） | `1,653,977,223` |
| 注释长度 | 200 |
| **`delta` = EOCD 绝对位置 − (`cd_off` + `cd_size`)** | **21,139,992** |
| 实际 CD 起始（绝对） | `1,675,117,215` |

> ⚠️ 这里 `EOCD + 22 + 200 = 1,675,117,502`，而文件大小是 1,675,135,956——尾后还有 18,454 字节。所以「EOCD 之后必须到 EOF」这种校验在本文件上**会失败**，必须允许尾后数据。

**唯一中央目录条目（`PK\x01\x02`，位于 1,675,117,215）**

| 字段 | 值 |
|---|---|
| version made by | `0x0014`（MS-DOS，2.0） |
| version needed | `20` |
| general purpose flag | `0x0001`（仅 bit0 = 加密） |
| compression method | `99`（WinZip AES） |
| CRC-32 字段 | `0x3FE75BDA`（AE-1，故为真实 CRC） |
| compressed size | `1,653,977,174` |
| uncompressed size | `1,653,977,146` |
| 文件名（ASCII） | `1635.zip`，11 字节 |
| extra 字段 | 仅 `0x9901`，长度 7，共 11 字节 |
| local header offset | `0`（相对 ZIP 起点） |

**AES extra（`0x9901`）字节与解析（易错点）**

```
01 99 07 00  01 00  41 45  03  00 00
 id    size  ver=1  "AE"  strength=3  real_method=0(stored)
```

> ⚠️ vendor 是 ASCII `A E` = `0x41 0x45`，按 `<H` 读出是 **`0x4541`**。若误写成 `0x4145`（"EA"），7-Zip 会直接报 `Wrong password`（实测过），排错时别被误导。

**本地文件头（绝对偏移 21,139,992 = `0 + delta`）**

| 字段 | 值 |
|---|---|
| 签名 | `PK\x03\x04` |
| version needed / flag / method | `20` / `0x0001` / `99` |
| local 文件名 | `1635.zip` |
| local extra | 0 字节（**只有 CD 里带 AES extra**，本地头没有） |
| 数据区绝对偏移 | **`21,140,041`**（= 21,139,992 + 30 + 11） |

**加密数据区（`21,140,041` 起）**

| 片段 | 长度 | 偏移 | 值 |
|---|---|---|---|
| salt | 16 | 21,140,041 | `6e4e053f91bb27c22a21b78c9080d78f` |
| password verifier | 2 | 21,140,057 | `c5f1` |
| ciphertext | 1,653,977,146 | 21,140,059 | — |
| auth code | 10 | 1,675,117,205 | `87a019fdaee5d91c3a74` |
| 合计 | 1,653,977,174 | — | 结束于 1,675,117,215 = CD 起点 ✅ |

**ZIP 注释（200 字节，UTF-8，4 行）**

```
SHA-256 Hash of '1635.zip':
d5d3cf2736845100c9dccb2037a062b24a2af2a5b20c1070fd8ddc76b1c19f43
Timestamp '2026-10-19 10:43:53'
Timehash 'b77e7df70d0b8c46052288e6acc686f98a68a7183624b537c426b3116445d153'
```

> 注释里的 SHA-256 就是**解密后** `1635.zip` 的哈希，可作端到端校验锚点。

### 1.4 解密后的内层 `1635.zip`

| 项 | 值 |
|---|---|
| 长度 | `1,653,977,146` |
| SHA-256 | `d5d3cf2736845100c9dccb2037a062b24a2af2a5b20c1070fd8ddc76b1c19f43`（与注释一致） |
| EOCD（相对内层起点） | `1,653,977,124`，注释长度 0 |
| 条目数 | `1640` |
| 中央目录 | `cd_off = 1,653,611,428`，`cd_size = 365,696` |
| 文件名编码 | **GBK**（无 UTF-8 flag / bit 11） |
| 首条目 | 目录项：flag=`0x0000`，method=0，csize=usize=0，lho=0 |
| 普通 jpg 条目 | flag=`0x0009`（bit0 加密 + bit3 data descriptor），method=`99`，AES extra = (ver 1, "AE", strength 3, **real_method 8 = deflate**) |
| 该 jpg 的 salt / pv | `a68303586c7357e50aa2c50323666ebf` / `0443` |

> 该 jpg 的 pv 用 **GBK** 编码密码算出来正好是 `0443`，用 UTF-8 是 `4839`——即**内层 ZIP 用的是 GBK 密码**，与外层（UTF-8）相反。

---

## 2. 算法规格

### 2.1 EOCD 定位（同时修正前置数据偏移）

1. 从文件尾向前取 `min(size, 65557)` 字节（`65535 + 22`），用 `rfind(b"PK\x05\x06")`，对每个候选校验 `pos + 22 + comment_len <= len(tail)`（允许尾后有数据）。
2. 解析 EOCD：`disk(2) cd_disk(2) n_this(2) n_total(2) cd_size(4) cd_off(4) comment_len(2)`。
3. 若 `EOCD-20` 处是 `PK\x06\x07`（ZIP64 locator）且其指向处是 `PK\x06\x06`，则从 ZIP64 EOCD 取 64 位的 `n_total(32) cd_size(40) cd_off(48)`。
4. **`delta = eocd_abs - (cd_off + cd_size)`**；用 `delta` 修正所有偏移：`real_cd = eocd_abs - cd_size`，`real_local = lho + delta`。
   - 本文件 `delta = 21,139,992`。
   - 若直接按 `cd_off` 去读（不修正），读到的是 MP4 字节——这正是多数工具报「不是压缩包」的位置。

### 2.2 中央目录 / 本地头结构

中央目录条目（`PK\x01\x02`，46 字节固定部）：

```
0  sig(4) | 4 vmade(2) | 6 vneed(2) | 8 flag(2) | 10 method(2) | 12 time(2) | 14 date(2)
16 crc(4) | 20 csize(4) | 24 usize(4) | 28 fnlen(2) | 30 extralen(2) | 32 comlen(2)
34 disk(2) | 36 iattr(2) | 38 eattr(4) | 42 lho(4) | 46 name | extra | comment
```

本地头（`PK\x03\x04`，30 字节固定部）：

```
0 sig(4) | 4 vneed(2) | 6 flag(2) | 8 method(2) | 10 time(2) | 12 date(2)
14 crc(4) | 18 csize(4) | 22 usize(4) | 26 fnlen(2) | 28 extralen(2) | 30 name | extra | data
```

extra 字段链：循环读 `id(2) len(2) data(len)`；关心 `0x0001`（ZIP64）、`0x9901`（AES）、`0x7075`（Info-ZIP Unicode Path）。

### 2.3 WinZip AES 解码（method 99）

数据区布局（`salt_len` 由 strength 决定）：

```
[salt: 8|12|16][pwd_verifier: 2][ciphertext: cipher_len][auth: 10]
cipher_len = csize - salt_len - 12        # 12 = 2 字节校验值 + 10 字节认证码
```

推导关系（AES-256 时，本文件即此）：

```
salt_len = 16, key_len = 32, mac_len = 32
dk_len   = key_len + mac_len + 2 = 66
dk       = PBKDF2-HMAC-SHA1(password_as_bytes, salt, iterations=1000, dklen=66)
key      = dk[0:32]
mac_key  = dk[32:64]
pwd_ver  = dk[64:66]                 # 与文件中的 2 字节比较
cipher_len = csize - salt_len - 2 - 10
```

strength 对照：`1 → AES-128 (salt 8, key 16, mac 16)`、`2 → AES-192 (salt 12, key 24, mac 24)`、`3 → AES-256 (salt 16, key 32, mac 32)`。

**CTR 细节（最容易写错的地方）**

- 16 字节计数器，初值 **1**，**小端**存放：第 1 块 = `01 00 00 ... 00`，第 2 块 = `02 00 ... 00`，即低字节递增。
- keystream 块 = `AES-ECB(key, counter_i)`，与密文逐字节 XOR。
- ⚠️ **不要**直接用 `cryptography` 的 `modes.CTR(1 .to_bytes(16,"little"))`：它按大端 +1，只有第 1 块正确，之后全错，症状是「校验值对、解密头部对、CRC/HMAC 全错」。
- 正确做法（可向量化，见 2.5）。

**认证码**：`HMAC-SHA1(mac_key, ciphertext)` 取前 10 字节，与文件尾部 10 字节比较。这是比 CRC 更强的完整性判据，且**不需要解密**。

**AE-1 vs AE-2**：`0x9901` 的 version=1（AE-1）→ CD/本地头里的 CRC 是真实 CRC；version=2（AE-2）→ CRC 字段写 0。本文件是 **AE-1**，`crc = 0x3FE75BDA`。

### 2.4 密码编码判定（回答「为什么 7-Zip 说密码错」）

同一句中文密码，不同工具会先把字符串**转成字节**再做 PBKDF2，编码不同 → 密钥不同。因此判定流程是：

1. 候选编码逐个试：`utf-8`、`gbk`(=cp936)、`gb18030`、`utf-16-le`、`utf-16-be`、`utf-8-sig`、`cp437`、`big5`（不可编码的跳过）。
2. **弱判据**：`dk[64:66] == stored_pv`（只有 16 bit，可能误命中，但足够筛出候选）。
3. **强判据**：用该密钥解前 16 字节，必须等于 `PK\x03\x04`（本文件是 store 的内层 zip，明文就是本地头）。
4. **决定性判据**：全量密文的 `HMAC-SHA1` 与文件内 10 字节认证码一致。

本文件实测：

| 编码 | 密码字节（hex） | 派生 pv | 结论 |
|---|---|---|---|
| **utf-8** | `e4b88ae88081e78e8be8aebae59d9be5bd93e88081e78e8b` | **`c5f1`** | ✅ 命中（与文件一致） |
| gbk / gb18030 | `c9cfc0cfcdf5c2dbccb3b5b1c0cfcdf5` | `8b6c` | ✗ |
| utf-16-le | `0a4e01808b73ba8b5b57535f01808b73` | `daf7` | ✗ |
| utf-16-be | `4e0a8001738b8bba575b5f538001738b` | `c12c` | ✗ |
| utf-8 + BOM | `efbbbfe4b88a…` | `4263` | ✗ |

**结论**：外层 ZIP 的密码按 **UTF-8** 派生；而 7-Zip 的 ZIP-AES 实现把密码按**本地 ANSI 代码页**（本机 = CP936/GBK）转字节，所以 7-Zip 永远算错密钥 → 报「密码错误」。WinRAR / WinZip 按 AES 规范用 UTF-8，所以 WinRAR 正常。

内层 `1635.zip` 恰好相反（GBK 命中，见 1.4）。

### 2.5 全量解密 + 哈希（性能参考）

- 分块：每次生成 `8192` 个计数器块（128 KiB keystream），用 `numpy` 构造计数器数组后一次性 `AES-ECB` 加密，再与密文块 XOR。
- 计数器构造（`i < 2^64` 时高 8 字节恒为 0）：

```python
idx = np.arange(first_block, first_block + n, dtype=np.uint64)
counters = np.zeros((n, 16), dtype=np.uint8)
counters[:, :8] = idx.view(np.uint8).reshape(n, 8)   # x86 小端
ks = AES_ECB_encrypt(counters.tobytes())
pt = (np.frombuffer(ct, np.uint8) ^ np.frombuffer(ks[:len(ct)], np.uint8)).tobytes()
```

- 实测：1,653,977,146 字节全量解密 + SHA-256 ≈ **31 秒**（Python 3.14 + `cryptography` + `numpy`）。
- CTR **可寻址**：只需解密中央目录或某个本地头时，按 `block = off // 16 + 1` 直接起算，取几十 KB 即可（本文件读内层 CD 只解了 365,696 字节）。

### 2.6 ZipCrypto（传统 PKWARE 加密）校验

当 `flag & 1` 且 `method != 99` 时是 ZipCrypto。数据区开头固定 12 字节加密头，最后 1 字节是校验值：

```
key0 = 0x12345678, key1 = 0x23456789, key2 = 0x34567890
init  : 对密码的每个字节 b 执行 update(b)
update(c): key0 = crc32_raw(key0, c)
           key1 = (key1 + (key0 & 0xFF)) * 134775813 + 1      # 均按 32 位取模
           key2 = crc32_raw(key2, key1 >> 24)
ks()   : t = (key2 | 2) & 0xFFFF;  return ((t * (t ^ 1)) >> 8) & 0xFF
解密   : p = ct ^ ks(); update(p)      # ← 用明文推进密钥流
校验   : p[11] == (crc >> 24) & 0xFF   # 若 flag bit3（data descriptor）则用 (dostime >> 8) & 0xFF
```

两个必踩的坑：
- **密钥流用明文推进，不是密文**。写成 `update(ct)` 会得到「看起来差不多、就是对不上」的结果（本机实测：校验字节差 2 个 bit）。
- `crc32_raw` 是裸表更新 `table[(crc ^ b) & 0xFF] ^ (crc >> 8)`，**不能**用 `zlib.crc32()`（后者带 XOR-in/out 预处理，结果不同）。

ZipCrypto 只提供 1 字节校验（1/256 误判率），够用来判「密码编码对不对」，不够用来判完整性。

### 2.7 判断「ZIP 前面被贴了数据」

`delta != 0` 即存在前置数据。可再做一次确认：`lho + delta` 处应能读到 `PK\x03\x04`，且 `real_cd` 处是 `PK\x01\x02`。本文件两者都成立。

---

## 3. 复现用的最小实现骨架

```python
import struct, hashlib, hmac
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

def find_eocd(f, size):
    back = min(size, 65557)
    f.seek(size - back); tail = f.read(back)
    pos = tail.rfind(b"PK\x05\x06")
    while pos != -1:
        if pos + 22 <= len(tail) and pos + 22 + struct.unpack_from("<H", tail, pos + 20)[0] <= len(tail):
            return size - back + pos, tail, pos
        pos = tail.rfind(b"PK\x05\x06", 0, pos)
    raise ValueError("no EOCD")

def parse_aes_extra(extra):
    o = 0
    while o + 4 <= len(extra):
        hid, hsz = struct.unpack_from("<HH", extra, o)
        if hid == 0x9901:
            ver, vendor, strength, real = struct.unpack_from("<HHBH", extra, o + 4)
            assert vendor == 0x4541, "vendor must read back as 0x4541 ('AE')"
            return dict(ver=ver, strength=strength, real_method=real)
        o += 4 + hsz
    return None

def keystream(key, first_block, nblocks):
    enc = Cipher(algorithms.AES(key), modes.ECB()).encryptor()
    return enc.update(b"".join((first_block + i).to_bytes(16, "little")
                               for i in range(nblocks))) + enc.finalize()
```

---

## 4. 环境与依赖

| 项 | 值 |
|---|---|
| OS | Windows（本机 ACP=OEMCP=936），中文系统区域 |
| Python | 3.14.7 |
| 库 | `cryptography`（AES-ECB）、`numpy`（加速）；**无** `pyzipper`/`pycryptodome` |
| 对照工具 | 7-Zip 24.09 x64、WinRAR / UnRAR 7.20 beta 1 |
| 注意 | 标准库 `zipfile` **不支持** method 99（AES），必须自己实现或换 `pyzipper` |

---

## 5. 已踩过的坑（生成代码时请直接规避）

1. **vendor 大小端**：`0x9901` 里 vendor 字节是 `41 45`，`<H` 读出 `0x4541`；写反 → 7-Zip 报 `Wrong password`（假象）。
2. **`delta` 修正**：`cd_off`/`lho` 都是相对 ZIP 起点；直接用绝对偏移读取会命中 MP4 字节。
3. **认证码位置**：在 `salt + 2 + cipher_len` 之后，即 `data_off + 18 + usize`；不是 `csize - 10`，也不是 `data_off + usize`（差 18 字节会让 HMAC 永远不匹配）。
4. **CTR 递增方向**：必须小端递增（见 2.3）；用库的 CTR 模式会「第一块对、后面全错」。
5. **EOCD 尾后数据**：本文件 EOCD 后面还有 200 字节注释 + 18,454 字节 RAR 残片，别写「必须到 EOF」的强校验。
6. **7-Zip 不接受前置数据**：实测给正常 AES zip 前面贴 1 KB 垃圾，`7z l` 立刻 `Is not archive`；21 MB 同样。WinRAR 会按 EOCD 反推偏移，所以能开。
7. **7-Zip 的中文密码用 ANSI 代码页**：造三个同密码包对照——UTF-8 密钥 → `Wrong password`；GBK 密钥 → `Everything is Ok`；ASCII → `Everything is Ok`。
8. **文件名无 UTF-8 flag 时按 GBK 解**：内层 1640 个条目全部没有 bit 11，用 `name.decode("utf-8")` 会抛异常。
9. **目录条目 csize=usize=0 且 method=0**，不要误判为「未加密的正常文件」或「空文件损坏」。
10. **别整读 1.6 GB 到内存**：CTR 可寻址 + 分块流式处理。
11. **UTF-8 密码字节不是合法 GBK**（`e4 b8 8a e8 80 81 …` 中 `0x80` 不能作 GBK 首字节），所以不存在「输入乱码骗过 7-Zip」的旁路；`"上老王论坛当老王".encode("utf-8").decode("gbk")` 会直接抛 `UnicodeDecodeError`。
12. **ZipCrypto 的密钥流用明文推进**（见 2.6）：写成用密文推进，校验字节会差几个 bit，看起来「快对了」但永远不对。
13. **ZipCrypto 的 crc 用裸表更新**，`zlib.crc32()` 带 XOR-in/out 预处理，不能直接替代（这是本条最常见的一错）。

---

## 6. 验收清单（生成代码的输出应逐条对上）

| # | 检查项 | 期望值 |
|---|---|---|
| 1 | 文件大小 | `1675135956` |
| 2 | 整文件 SHA-256 | `0ef20cf0f45a12b6781b169dee163bde6afa6b654c39b0b8f728303fab9fe4ca` |
| 3 | 首 4 字节 | `00 00 00 24`（`ftyp`, size 36） |
| 4 | MP4 段结束偏移 | `21139992`，其后紧跟 `PK\x03\x04` |
| 5 | EOCD 绝对偏移 | `1675117280` |
| 6 | `cd_size` / `cd_off` / 注释长度 | `65` / `1653977223` / `200` |
| 7 | `delta` | `21139992` |
| 8 | 条目数 / 名字 / method / flag | `1` / `1635.zip` / `99` / `0x0001` |
| 9 | `csize` / `usize` / CRC | `1653977174` / `1653977146` / `0x3FE75BDA` |
| 10 | AES extra | ver=`1`, vendor=`0x4541`, strength=`3`, real_method=`0` |
| 11 | salt / pv | `6e4e053f91bb27c22a21b78c9080d78f` / `c5f1` |
| 12 | UTF-8 密码派生 pv | `c5f1`（命中）；GBK 为 `8b6c`（不命中） |
| 13 | 首 16 字节明文 | `50 4b 03 04 0a 00 00 00 …` = `PK\x03\x04` |
| 14 | 认证码 | 存储 `87a019fdaee5d91c3a74` = 计算值（VALID） |
| 15 | 解密后 SHA-256 | `d5d3cf2736845100c9dccb2037a062b24a2af2a5b20c1070fd8ddc76b1c19f43` |
| 16 | 内层条目数 / CD | `1640` / `cd_off=1653611428, cd_size=365696` |
| 17 | 内层 jpg 条目 | flag=`0x0009`, method=`99`, AES real_method=`8`, salt=`a68303586c7357e50aa2c50323666ebf`, pv=`0443`（GBK 命中） |
| 18 | 尾段 | `1675117502` 起 18,454 字节，`52 61 72 21 1a 07 01 00`，RAR5 头损坏 |

---

## 7. 结论到现象的映射（写报告时可复用）

| 现象 | 机制 |
|---|---|
| WinRAR 能正常解压 `1.zip` | ① WinRAR 容忍前置数据，会按 EOCD 反推偏移；② WinRAR 按 AES 规范用 **UTF-8** 派生密码密钥，与本包一致 |
| 7-Zip 报「密码错误 / 不是压缩包」 | ① 7-Zip 的 ZIP 读取器拒绝前置数据（≥1 KB 即失败），根本走不到解密；② 即使切掉前面 21 MB，7-Zip 仍按 **GBK** 转换中文密码 → 密钥错误 → 「Wrong password」 |
| 同一密码换软件结果不一致 | AES ZIP 只规定 PBKDF2，未强制密码字节编码；WinZip/WinRAR 走 UTF-8，7-Zip 走 ANSI 代码页，中文密码必然分叉 |
| 内层 `1635.zip` 可能「WinRAR 打不开」 | 内层是 **GBK** 密钥打的，与 WinRAR 的 UTF-8 相反；用 7-Zip 打开即可 |

---

## 8. 建议的代码组织

```
probe_layout.py     # 1.1 ~ 1.2：文件大小、MP4 box walk、EOCD/EOCD64、delta
parse_zip.py        # 2.2：CD / local header / extra 链  → 打印条目表
aes_probe.py        # 2.3 ~ 2.4：salt/pv 定位 + 多编码 PBKDF2 判定 + 首块明文校验
verify_full.py      # 2.5：全量 CTR 解密 + HMAC + SHA-256 + 内层 CD 索引
repro_7zip_test.py  # 构造 UTF-8 / GBK / ASCII 三种同密码 AES 包，验证 7-Zip 只认 ANSI
```

全部只读；若需落盘中间产物（如切出的裸 ZIP、尾段 RAR），用独立目录并在结束时清理。

> 已有一份**跑通并自测过**的通用实现：`_analysis/zipaes.py`（函数 `analyze(path, password)`，返回 `delta / entries / encoding / …`），配套自测 `_analysis/selftest_generic.py`。新会话既可以重写，也可以直接改这两个文件。

---

## 9. 支持矩阵与已知边界

### 9.1 覆盖范围

| 变体 | 是否覆盖 | 说明 |
|---|---|---|
| AES-128 / 192 / 256 | ✅ | `strength` 1/2/3 → salt 8/12/16、key 16/24/32 |
| AE-1 / AE-2 | ✅ | AE-2 时 CD 里 CRC=0，完整性只能靠 HMAC 判定 |
| ZipCrypto（传统加密） | ✅ | 12 字节头 + 1 字节校验（`crc>>24`，有 bit3 时用 `dostime>>8`）；已实测通过 |
| 前置数据（拼接视频、SFX、伪头） | ✅ | `delta` 自动求解，与前置量大小无关 |
| 尾随数据（广告/第二个包） | ✅ | EOCD 扫描允许尾后有内容 |
| ZIP64 | ✅ | locator + EOCD64 |
| 多条目 / 目录条目 | ✅ | 逐条判断，注意目录条目 `csize=usize=0` |
| 嵌套 ZIP（一层套一层） | ✅ | 需要递归调用；本文件两层且**编码相反** |
| 文件名 GBK（无 UTF-8 flag） | ✅ | 无 bit 11 时按 GBK 解 |
| store / deflate 条目 | ✅ | 判密码不依赖明文格式（用 HMAC），但「首块明文 = PK」只对 store 成立 |

### 9.2 不覆盖（需要换库或换思路）

| 情形 | 为什么 | 怎么办 |
|---|---|---|
| RAR / 7z 容器本身加密 | KDF 完全不同（7z 用 SHA-256 迭代，RAR5 用 PBKDF2-HMAC-SHA256） | 换 `py7zr` / `unrar`，只保留第 0.2 节思路 |
| 分卷 ZIP（`.z01`/`.z02`） | 偏移跨卷 | 先合并成单文件再跑 |
| 加密的 7z/RAR 里再套 ZIP | 逐层解 | 每层用对应工具，ZIP 层再套本文档 |
| 密码不在提示中（字典/爆破） | 本文档只做**验证**，不做搜索 | 验证函数可直接当字典判定器 |
| 文件被截断到 ZIP 头部之前 | 中央目录缺失 | 只能扫本地头（`PK\x03\x04` + flag bit3 里取原始大小） |
| 加密的 MP4/其他容器 | 与 ZIP 无关 | 不适用 |

### 9.3 怎么测定「某个工具用的是哪种密码编码」

不要背工具表，直接测，30 行脚本即可：

1. 用 2.3 的构造方式造 3 个内容相同、只有密码编码不同的 AES 包：`utf-8` / `gbk` / `ascii`；
2. 用目标工具分别打开，密码都输入同一句中文；
3. 哪个能开，就说明该工具把密码转成了那种字节。

本机实测结论（可作先验，但请以实测为准）：

| 工具 | ZIP-AES 密码字节 | 证据 |
|---|---|---|
| 7-Zip 24.09 x64 | 本地 ANSI 代码页（本机 CP936/GBK） | 三包测试：UTF-8 包报 `Wrong password`，GBK/ASCII 包 `Everything is Ok` |
| WinRAR / WinZip | UTF-8（AES 规范） | 与本文件外层（UTF-8 密钥）一致，故 WinRAR 能开 |
| 其他工具 | 未知 | 用上面三步自测；内层 `1635.zip` 是 GBK 密钥，可拿来反测（预计 WinRAR 打不开、7-Zip 能开） |

---

## 10. 自测记录（可复现，用来验证生成的代码是否真的通用）

`_analysis/selftest_generic.py` 会现场造 4 个变体包 + 分析真实文件，全部断言通过。记录如下：

| 样本 | 变量 | 期望 `encoding` | 实测 |
|---|---|---|---|
| `aes128_ae1_utf8.zip` | AES-128、AE-1、无前置 | `utf-8` | ✅ pv/HMAC 一致 |
| `aes192_ae2_gbk.zip` | AES-192、AE-2、无前置 | `gbk` | ✅ pv/HMAC 一致 |
| `aes256_ae1_gbk_prefixed.zip` | AES-256、前置 4,108 B | `gbk` | ✅ `delta=4108` |
| `aes256_ae1_utf8.zip` | AES-256、前置 1,000,012 B | `utf-8` | ✅ `delta=1000012` |
| `zipcrypto.zip` | ZipCrypto + deflate（7-Zip 生成） | `utf-8` | ✅ 1 字节校验命中；错误密码正确地不匹配 |
| `1.zip`（真实） | AES-256、前置 21,139,992 B、尾随 RAR | `utf-8` | ✅ `delta=21139992`、HMAC VALID、首块明文 `PK\x03\x04` |

失败模式自查（生成代码跑错时的典型症状）：

| 症状 | 大概率的错因 |
|---|---|
| 所有条目 `local header not found` | `delta` 没修正，或 `delta` 符号反了 |
| pv 永远不匹配 | `dklen` 算错（应为 key+mac+2，AES-256 时 66），或密码字符串被转成了别的编码 |
| pv 匹配、HMAC 不匹配 | 认证码起点算错（应在 `data_off + salt + 2 + cipher_len`） |
| 解密成功但 CRC 失败 | CTR 递增方向错（用了大端 / 库的 CTR 模式） |
| 内层 1640 条目名字解码抛异常 | 没回退 GBK |
| 7-Zip 对自造包报 `Wrong password` | AES extra 的 vendor 写成了 `0x4145`（应为 `0x4541`） |
| ZipCrypto 校验字节差几个 bit | 用密文推进密钥流（应用明文），或用了 `zlib.crc32` |
