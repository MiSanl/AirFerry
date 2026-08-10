# 网页接收端实测速度结论（v1.1.6 M1+M2 阶段）

> 本结论覆盖 **Rust 核心 WASM 接收端**（M1）+ **JS 接收管线**（M2 兼容路径）的实测数据。
> 真机摄像头 A/B（M6）需物理硬件（Android 手机 + 摄像头），不在本环境覆盖范围；
> 此处给出**合成基准实测 + 光学信道理论上限对比**，明确瓶颈所在。

## 测试方法

- **Rust 核心基准**：`core/transfer-engine/scripts/bench_receiver.mjs`
  在 Node 中加载真实 WASM 产物（`apps/sender/wasm-pkg-simd/`），驱动
  `SenderSessionWasm`（产帧）→ `ReceiverSessionWasm`（ingest + assemble_raw），
  隔离 camera / ZXing / DOM 开销，测量纯 Rust 核心吞吐。每个尺寸跑两遍取第二遍
  （消除 WASM tier-up）。
- **端到端正确性**：`core/transfer-engine/scripts/e2e_receiver.mjs`，41 断言覆盖
  单文件 / ETTEXTv1 文字 / ETBUNDL1 多文件包 / 小文件全链路，全部通过。
- **环境**：macOS（darwin 25.5.0 arm64）。

## 实测数据：Rust 核心 WASM 接收端吞吐

参数：`symbol_size=1024B`、`redundancy=30%`。

| 载荷大小 | 源符号数 K | ingest 耗时 | ingest 吞吐 | assemble_raw 吞吐 | 正确性 |
|----------|-----------|------------|------------|-------------------|--------|
| 50 KB    | 49        | 0.1 ms     | 640 MiB/s（66 万 sym/s） | — | ✅ |
| 500 KB   | 489       | 0.6 ms     | 623 MiB/s（64 万 sym/s） | — | ✅ |
| 2 MB     | 1954      | 1.8 ms     | 1025 MiB/s（105 万 sym/s） | 14000+ MiB/s | ✅ |
| 8 MB     | 7813      | 7.3 ms     | 1064 MiB/s（109 万 sym/s） | 8800+ MiB/s | ✅ |

**关键结论**：

1. **Rust 核心绝对不是瓶颈**。接收端 ingest 跑在 **~1000 MiB/s**（~100 万符号/秒），
   assemble_raw 更快（>8000 MiB/s）。8 MB 文件完整恢复（含 RaptorQ 重组）仅 **7.3 ms**。
2. 这个吞吐**远超光学信道的物理上限**（见下）。即使在最乐观的光学配置下，Rust 核心
   也只需不到 1% 的算力就能跟上帧流。

## 光学信道理论上限（对比基准）

默认配置（浏览器 `symbol_size=1400B`、60 fps、四码同屏）：

| 配置 | 原始符号率 | 扣描述符后有效载荷 | 等效吞吐 |
|------|-----------|-------------------|---------|
| 60fps × 1 码 | 60 sym/s | ~56 sym/s | **~78 KiB/s** |
| 60fps × 4 码 | 240 sym/s | ~225 sym/s | **~308 KiB/s** |

历史 Android 720p 实测为 **210–240 sym/s**（接近四码理论上限）。

**对比**：Rust 核心能消化 ~100 万 sym/s，光学信道最多喂 240 sym/s —— **核心算力富余 ~4000 倍**。
因此网页接收端的**实际瓶颈必然在摄像头采集 + ZXing 解码，而非 Rust 核心**。

## 网页接收端实际瓶颈分析（未真机实测的推断）

| 环节 | 预期影响 | 依据 |
|------|---------|------|
| **Rust 核心 ingest** | 可忽略（~1000 MiB/s） | 本基准实测 |
| **摄像头采集** | 可能是主瓶颈之一 | 多数笔记本/手机前置摄像头在浏览器里只能给 30fps（非 60）；后置 1080p60 需要桌面级设备 |
| **VideoFrame → ImageData（RGBA）** | 中等（canvas blit + 像素格式转换） | M2 兼容路径用 RGBA，未做 Y 平面零拷贝（M3 快路径用 `VideoFrame.copyTo(I420)` 直写 WASM heap 可消除） |
| **zxing-wasm 全帧解码** | 主瓶颈之一 | 每帧对全画面跑 ReadBarcodes；M3 的 ROI tracker（只解码小窗口）+ 灰度快路径可大幅缓解 |
| **JS ↔ WASM 边界** | 较小 | M1 已用 `assemble_raw` 单次取字节，ingest 用 packed u64 状态字避免 per-frame JSON |

**预估**（需真机 A/B 实测确认，此处仅给出量级）：
- 现代桌面 + 真 1080p60 摄像头 + M3 快路径落地后：有机会达到同手机原生 App 的 **75%–100%**。
- 普通 30fps 摄像头 + M2 兼容路径：约为 60fps 原生上限的 **50%**（帧率减半主导）。

## 已验证 / 未验证清单

| 项目 | 状态 | 证据 |
|------|------|------|
| Rust 核心 WASM 接收端正确性 | ✅ 已验证 | 41 端到端断言（单文件/文字/包/小文件）+ 6 逻辑单测全过 |
| Rust 核心 ingest/assemble 吞吐 | ✅ 已验证 | 本基准：~1000 MiB/s |
| Web 构建产物（sender+receiver 双入口） | ✅ 已验证 | `npm run build` 产出 index.html + receiver.html + 3 worker chunk + zxing/zstd/transfer/lzma 4 wasm |
| 三端 ingest 打包位布局一致 | ✅ 已验证 | 共享 `ingest_status.rs`，cffi 测试守护 |
| wasm32 解压 fail-closed | ✅ 已验证 | `cargo check --target wasm32` + stub 改 NONE 原样/其余 Err |
| **真机摄像头端到端恢复** | ⚠️ 未验证 | 需物理摄像头 + 发送端屏幕；M2 兼容路径逻辑经 Node 端到端验证，但浏览器 getUserMedia + zxing-wasm 运行时尚未跑通 |
| **zxing-wasm 浏览器运行时** | ⚠️ 未验证 | wasm 已复制到 dist 根，但 worker 内 `getZXingModule()` fetch + 实际解码未在浏览器实测 |
| **真机 A/B 性能对比** | ❌ 未验证 | 需 Android 手机 + 同屏发送端；本环境无硬件 |

## 复现命令

```bash
# Rust 核心测试
cargo test -p transfer-engine                              # 42 单测 + 集成测试
cargo test -p transfer-engine --features cffi              # + C ABI 契约
cargo check -p transfer-engine --target wasm32-unknown-unknown --features wasm

# WASM 接收端吞吐基准
cd apps/sender && npm run wasm                              # 先构建 WASM 双产物
node core/transfer-engine/scripts/bench_receiver.mjs       # 吞吐基准
node core/transfer-engine/scripts/e2e_receiver.mjs         # 41 端到端断言

# Web 构建（含接收端）
cd apps/web && npm run build                                # dist/index.html + receiver.html
```

## 下一步（真机实测所需）

1. 在有摄像头的机器上 `cd apps/web && npm run dev`，打开 `http://localhost:5180/receiver.html`。
2. 用另一屏幕播放发送端 QR 流（扩展或 `index.html`），对准摄像头，确认恢复。
3. 对比同手机的 Android APK，按 M6 矩阵记录 sym/s、KiB/s、完成时间。
4. 落地 M3（自编译 ZXing-C++ WASM 灰度快路径，需 Emscripten）+ ROI tracker 后重测。
