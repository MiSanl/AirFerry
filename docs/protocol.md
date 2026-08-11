# 传输协议规范 (Protocol Specification)

## 概述

AirFerry 使用单向光学信道：发送端连续播放二维码视频流，接收端实时扫描。协议设计为**无握手、无确认、可随时加入**。

## 会话 (Session)

每个文件传输对应一个**会话**，由 128-bit **会话 ID** 唯一标识。

### 会话 ID 派生

会话 ID 由传输身份确定性派生（FNV-1a 128-bit）：

```
session_id = FNV1a_128(
    UTF8(名称),
    LE64(字节数),
    LE64(修改时间毫秒),
    内容指纹  // FNV1a_64(头 1KB + 尾 1KB)
)
```

**单文件**（1 个文件，不打包）：`名称=文件名`、`字节数=文件大小`、`修改时间=该文件 mtime`、指纹取文件字节头尾。

**多文件打包**（≥2 文件，见下方「多文件打包」）：身份基于整个 bundle 容器字节：`名称=各文件名用 \u0001(SOH) 连接`、`字节数=bundle 容器字节数`、`修改时间=各文件 mtime 的最大值`、指纹取 bundle 字节头尾各 1KB（头部即 `ETBUNDL1` magic）。

同一传输重复发送产生相同会话 ID → 接收端可识别并断点恢复。指纹始终取**压缩前**的字节。

## 多文件打包 (Bundle)

选择 ≥2 个文件时，发送端先把它打包成单个字节容器（`ETBUNDL1`），整批走同一条压缩 + 同一条 RaptorQ 流；接收端恢复后检测 magic 并拆包。单文件不打包（向后兼容，接收端按描述符文件名直接保存）。

### 容器格式（AirFerry Bundle v1，所有整数大端序）

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| 0 | 8 | magic | ASCII `"ETBUNDL1"`（`0x45 54 42 55 4E 44 4C 31`） |
| 8 | 2 | version | u16 = `1` |
| 10 | 2 | file_count | u16（线格式 1..65535；产品一次最多 4096） |
| 12 | … | entries | `file_count` 条，每条： |

每条 entry：

| 长度 | 字段 | 说明 |
|------|------|------|
| 2 | name_len | u16（UTF-8 字节长度，0..65535） |
| name_len | name | UTF-8 文件名 |
| 8 | size | u64（文件内容字节数） |
| size | content | 文件内容 |

无逐文件 CRC——整个 bundle 由传输层 CRC32（描述符）+ RaptorQ + 双层帧 CRC 保护。8 字节 magic 让它与普通单文件首字节的意外碰撞实际不可能。Android 接收端在 `BundleParser.kt` 中按相同布局解析。

发送端在写入前验证 `file_count`（产品上限 4096）与每个 UTF-8 文件名均可无损表示为 u16；文件名超过 65535 字节会明确报错，不允许静默截断并造成容器错位。接收端有**两个独立上限**：**压缩对象（wire）上限** `raptorq_core::MAX_OBJECT_BYTES` = **32 MiB**，以及**原始（解压后）内容上限** `raptorq_core::MAX_ORIGINAL_BYTES` = **256 MiB**。多文件包/文字超过原始上限时发送端硬性阻止并提示分批；单个真实文件则自动转为 descriptor-v4 分段，不受该根大小上限。单个 0 B 文件会在发送前明确拒绝（bundle 内的空条目仍可表示）。

**发送端 wire 上限硬门（单对象）**：由于接收端对超过 `MAX_OBJECT_BYTES`（32 MiB）的 wire 对象是**硬性拒绝**（无法还原，非警告），发送端在压缩 worker（`apps/sender/src/workers/compress.worker.ts`）内**压缩完成后**按 `compressed_size` 决定是否分段：若压缩流 ≤ `SEGMENT_RAW_BYTES`（≈ 32 MiB）则作为单对象发送；否则把压缩流切成多个段发送。压缩流模型下任何大小的内容都是可发送的（段是同一压缩流的切片），不再有"压缩后仍超 32 MiB 即失败"的硬门。

**大文件分段（descriptor v4，压缩后分段）**：逻辑传输（单个文件、多文件包 ETBUNDL1、文字 ETTEXTv1 均可）先被**整段压缩一次**（三算法选优），得到一条压缩流；若压缩流超过 `SEGMENT_RAW_BYTES`（`MAX_OBJECT_BYTES - MAX_SYMBOL_SIZE` ≈ 31.9 MiB，保证经 RaptorQ 符号补齐后不超 32 MiB wire 上限），再把**压缩字节流**切成多个段，每段独立 RaptorQ 编码、独立发送（descriptor v4 + 独立 child session id）。接收端把每段恢复出的**压缩字节**按序拼接，得到完整压缩流后**只解压一次**，再解析为文件/包/文字。与旧模型（每段独立压缩、独立解压、按原始字节偏移拼回）不同，压缩流模型的段不是可独立解压的——单一 zstd/xz 流无法切成可解码的片，因此必须收齐全部段才能恢复。

发送端为整份原始内容计算 CRC32 与 SHA-256，把同一个 `root_sha256`（解压后原文摘要）、`original_size`（解压后总大小）、`compression`（整条流的算法）、`crc32` 写进每段描述符；每段的 `compressed_size` 是该段压缩字节数，`raw_sha256` 是该段压缩字节的摘要。Android/Windows 把通过段 SHA-256 的压缩段按压缩流规范偏移写入任务私有 `.partial` 并原子更新位图；网页接收端在一个 IndexedDB 事务中提交段 Blob 与任务账本。全部段到齐后，原生端通过 `qr_protocol::compress::decompress_stream_to_file` 把 `.partial` **流式解压到磁盘**（zstd/xz streaming decoder，边解压边算 CRC32 + SHA-256，内存恒有界），校验解压后长度 + CRC32 + 根 SHA-256 后才发布——因此 > 256 MiB 的超大单文件也能在原生端恢复；网页端没有磁盘流式编解码器，仍在内存量级内拼接压缩流并解压一次（浏览器接收上限提高到 2 GiB，受 JS 内存约束）。原生端先检查可用空间并把完成文件同卷原子移动到内容库，且用根任务派生的稳定历史 ID 保证崩溃重试不会生成重复记录，避免常态二次完整拷贝；网页端优先通过 File System Access API 写入用户文件，不构造根文件大小的内存数组。无 File System Access 的 Blob 回退仅允许 ≤64 MiB，较大任务须用 Chrome/Edge 桌面版导出。

## 帧格式 (Frame Format)

每帧 = `[Header 60B][Payload T B][Footer 4B]`，其中 **T = symbol_size**（每个 QR 帧的载荷大小）。

`symbol_size` 可配置但必须在 64..=65528 且为 8 的倍数：浏览器发送端默认 **1400 字节**（整帧 **1464B → QR V27 125×125**），核心库默认 1024 字节。接收端从帧头读取并自适应；一次会话内恒定。浏览器预设为 512 / 896 / 1008 / **1400（默认）** / 1904 / 2400（见 [二维码帧格式](qr-frame-format.md)）。

所有多字节整数为大端序（network order）。

### Header (60 字节)

| 偏移 | 长度 | 字段 | 类型 | 说明 |
|------|------|------|------|------|
| 0 | 2 | magic | u16 | `0x4554` (ASCII "ET")，帧同步标识 |
| 2 | 1 | version | u8 | `1` |
| 3 | 1 | flags | u8 | 位域；bit0=`FLAG_DESCRIPTOR` |
| 4 | 16 | session_id | u128 | 会话 ID（大端） |
| 20 | 4 | sbn | u32 | RaptorQ 源块号 |
| 24 | 4 | esi | u32 | 编码符号 ID（源< K，修复≥K） |
| 28 | 4 | total_blocks | u32 | 源块总数 |
| 32 | 4 | total_symbols | u32 | 源符号总数 K |
| 36 | 4 | symbol_size | u32 | 符号大小 T（浏览器默认 **1400**；核心库默认 1024） |
| 40 | 8 | frame_index | u64 | 单调帧序号（统计用） |
| 48 | 8 | timestamp_ms | u64 | 发送端时间戳（UNIX 毫秒） |
| 56 | 4 | payload_crc32 | u32 | 载荷 CRC32 |

### Payload (T 字节)

- **数据帧**：一个 RaptorQ 符号（源符号或修复符号）
- **描述符帧**（`flags & 0x01 != 0`）：会话元数据，零填充到 T 字节（见下文）

### Footer (4 字节)

| 偏移 | 长度 | 字段 | 类型 | 说明 |
|------|------|------|------|------|
| 60+T | 4 | frame_crc32 | u32 | 覆盖 Header+Payload 的整帧 CRC32 |

## 双层 CRC 校验

1. **payload_crc32**（Header 内）：校验载荷完整性
2. **frame_crc32**（Footer）：校验整帧完整性（Header + Payload）

任一校验失败 → 丢弃该帧，依赖 RaptorQ 冗余恢复。

## 描述符帧 (Descriptor Frame)

描述符帧携带**权威的对象元数据**（OTI + 每块符号数），使晚加入的接收端能构建解码器。

发送端每 16 帧插入一个描述符帧；**首帧即描述符**，使即时加入的接收端无需缓存符号即可构建解码器。`flags` 的 bit0 置位。

### 描述符载荷布局（T 字节内）

载荷始终**零填充到 `symbol_size` 字节**。版本字段是强约束：v1 只解析固定头与块表并忽略零填充；v2 必须有完整文件元数据扩展；v3 还必须有合法 compression 标签。未知版本或非法 UTF-8 文件名均拒绝。

**固定头部 + 块表**（所有版本共有）：

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| 0 | 1 | magic | `0xD5` |
| 1 | 1 | version | `4`（分段对象；`3`/`2`/`1` 为旧版，非分段对象用 `3`） |
| 2 | 2 | num_blocks | u16 BE |
| 4 | 8 | transfer_length | u64 BE（RaptorQ 对象字节数，含填充） |
| 12 | 4 | symbol_size | u32 BE |
| 16 | 12 | oti_bytes | RFC 6330 OTI（12B 线格式） |
| 28 | 16×B | blocks[] | 每块：sbn(u32) + num_source_symbols(u32) + block_length(u64) |

记块表末尾偏移为 `P = 28 + 16×B`。

**v2 扩展**（紧跟块表；version ≥ 2 或剩余字节足够时解析）：

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| P | 1 | filename_len | u8（0..=255） |
| P+1 | filename_len | filename | UTF-8 文件名 |
| P+1+fn | 8 | original_size | u64 BE（压缩前原始字节数） |
| P+9+fn | 4 | crc32 | u32 BE（原始文件 CRC32） |

记 v2 扩展末尾偏移为 `Q = P + 1 + filename_len + 12`。

**v3 扩展**（紧跟 v2 扩展；version ≥ 3 或尾部非全零时解析）：

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| Q | 1 | compression | u8：`0`=无压缩, `1`=Zstd, `2`=XZ |
| Q+1 | 8 | compressed_size | u64 BE（压缩后负载字节数，RaptorQ 填充前） |

剩余字节（从 `Q+9` 到符号末尾）为零填充。

**v4 扩展（压缩流分段对象；version == 4）**：紧跟 v3 扩展。version 4 描述符描述一个**压缩流分段子对象**——逻辑传输（文件/包/文字）被整段压缩一次后，把**压缩字节流**切成多个 ≤ `SEGMENT_RAW_BYTES` 的段，每段独立 RaptorQ 编码、独立发送（各自携带自己的 child session id），接收端用 descriptor-v4 元数据把各段**压缩字节**按序拼接、解压一次得到完整原文。`file_meta.compressed_size` 描述**当前段的压缩字节数**；`file_meta.original_size`、`crc32`、`compression`、以及 v4 里的 `root_sha256` 描述**整份解压后原文**（跨段一致），分段坐标放在 v4 段元数据里：

| 偏移 | 长度 | 字段 | 说明 |
|------|------|------|------|
| R | 16 | root_session_id | u128 BE（根传输 ID，全局一致） |
| R+16 | 4 | segment_index | u32 BE（本段在根中的序号，0 起） |
| R+20 | 4 | segment_count | u32 BE（根传输总段数） |
| R+24 | 8 | original_offset | u64 BE（本段在压缩流中的字节偏移 = index × `SEGMENT_RAW_BYTES`） |
| R+32 | 8 | root_original_size | u64 BE（根压缩流总字节数） |
| R+40 | 32 | root_sha256 | SHA-256（完整解压后原文；每段一致） |
| R+72 | 32 | raw_sha256 | SHA-256（本段压缩字节） |

`R = Q + 9`。v4 只接受 `version == 4`；`raw_sha256` 用于逐段校验（对压缩字节），`root_sha256` 阻止把不同文件修订版中各自合法的段混到同一任务，并在最终解压后校验完整原文。固定常量 `SEGMENT_RAW_BYTES = MAX_OBJECT_BYTES - MAX_SYMBOL_SIZE = 32 MiB - 65_528 ≈ 31.9 MiB`（保证一段经 RaptorQ 符号补齐后不超 32 MiB wire 上限）、`MAX_SEGMENT_COUNT = 131072` 见 `core/transfer-engine/src/segment.rs`。本次 v4 尾部扩展为 104 字节，因此大文件分段要求收发端都升级；普通 version 3 对象不受影响。

child session id 的位级派生固定为 `FNV1a_128(ASCII("AirFerry.segment.v1") || root_session_id_BE128 || segment_index_BE32)`。接收端还会强制 `segment_count = ceil(root_original_size / SEGMENT_RAW_BYTES)`、`original_offset = index × SEGMENT_RAW_BYTES`，并要求每段压缩字节数 ≤ 其规范切片长度（发送端可为 RaptorQ 符号补齐留一个符号的余量）；因此不存在洞、重叠或借伪造段数触发巨额分配的空间。

**v2/v3 兼容说明**：真实 v2 发送端只写到 `crc32`，其后为补零区。由于载荷总是补齐到 `symbol_size`，仅凭"剩余 ≥ 9 字节"无法区分 v3 尾部与 v2 补零——全零的 9 字节尾部会被误读为 `compressed_size=0`，导致接收端把恢复结果截成空文件。解析器因此仅当 `version ≥ 3` **或**尾部非全零时才将其当作 v3 扩展；否则按 v2 处理（`compression=None`、`compressed_size == original_size`）。`version == 4` 时额外要求 104 字节 v4 尾段完整，否则拒绝。

固定开销 28 字节 + 每块 16 字节 + v2 尾部 13 字节 + filename_len + v3 尾部 9 字节。默认 symbol_size 1024 下对数百块以内的文件轻松装入一个符号。

## 压缩 (Compression)

发送端在 RaptorQ 编码前对文件进行压缩，压缩算法标签由描述符帧携带（`compression` 字段），接收端在 RaptorQ 恢复后按标签解压。

### 算法标签

| 标签值 | 算法 | 说明 |
|--------|------|------|
| `0` | 无压缩 (Raw) | 原始字节直传 |
| `1` | Zstd | 压缩等级 1（快速；对本通道常见的大体积二进制负载已够用） |
| `2` | XZ / LZMA2 | 浏览器端压缩等级 9，Rust 解压端兼容 |

### 三算法选优策略（浏览器端）

浏览器发送端对每个文件同时运行多个压缩候选，选取最小结果：

1. **Raw**：始终作为候选
2. **Zstd Lv1**：始终运行（快速；压缩启动快）
3. **Xz Lv9**：仅当 Zstd 压缩率 < 70% 时运行（70% early-exit 启发式——Zstd 几乎没缩水说明文件已压缩或不易压缩，跳过慢速 Xz）

最终选取所有已运行候选中体积最小的算法，标签写入描述符帧。

> 历史：曾用 Zstd Lv22 + 95% 阈值；后改为 Lv1（启动更快、对本通道常见二进制负载比率损失可忽略）+ 70% 阈值（仅对真正可压缩的文件才付出 Xz 的代价）。

## RaptorQ 符号坐标

| 符号类型 | ESI 范围 | 说明 |
|---------|---------|------|
| 源符号 | `[0, K_block)` | 原始数据符号 |
| 修复符号 | `[K_block, 2²⁴−1]` | 喷泉码生成的冗余符号 |

接收端只需收集足够多的**独立**符号（≥ K_block）即可解码，与顺序无关。

## 发射策略

发送端的帧发射分两个阶段：

1. **源符号阶段（仅一遍）**：所有源符号按**跨块轮询**（esi-major）输出——先每个块的 esi=0，再每个块的 esi=1……跨块交织使突发丢帧被均摊到所有块，各块同步逼近解码阈值。
2. **持续新鲜修复符号阶段**：源符号发完后，发送端持续生成修复符号，跨块轮询，每个块的修复 ESI **单调递增、永不重复**（block_b 第 m 轮的 ESI = K_b + m）；达到 `2²⁴` 前返回耗尽错误并停止。

因此源符号发完后的**每一帧都是接收端从未见过的新符号**：发送端不再产生重复帧，接收端进度近似线性增长到完成，避免了循环有限计划带来的"越往后越慢"拖尾。

`redundancy_pct`（5–50，默认 25）**仅用于发送端 UI 的传输时长估算**，不再限制实际发射的修复符号数量——发送端会一直补充新鲜修复符号，直到接收端完成。

接收端可在任意时刻加入：即使错过整个源符号阶段，仅凭修复符号也能完整恢复（RaptorQ 喷泉特性）。

## 断点恢复

核心 `ResumeState` 可保存 `session_id`、`ObjectMeta`、ESI 摘要和**实际保留的符号载荷**。新 Decoder 只能回放有载荷的符号；仅有 ESI、没有字节的数据绝不能算作已接收，否则重传会被误判为重复。恢复 JSON 输入/输出封顶 128 MiB，并在反序列化和 restore 前校验 OTI、块数、SBN/ESI、符号尺寸及本地预算。

当前实现为控制内存，在 descriptor 确认、符号喂入 Decoder 后不长期保留所有符号字节，因此普通单对象的进程重启会从当前对象重新扫描，而不是承诺“符号级无损续传”。产品级大文件断点由 descriptor-v4 的**完成段账本**承担：Android/Windows/网页均跨重启保留已通过段 SHA-256 的完整压缩段，当前尚未完成的 ~32 MiB 段最多重扫一次。历史页可查看缺失段、继续指定根任务或删除记录。**重复段早检测**：网页接收端在确认到某段描述符的瞬间就查询账本，若该段已收齐则立即提示“已接收过”并继续扫描下一段，而不是把整段扫完后再去重；Android/Windows 的完成账本同样可跨重启识别已收段。
