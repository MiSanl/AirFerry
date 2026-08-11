# AGENTS.md — AI 代理操作手册

> 本文是给 AI 代理（以及人类开发者）的**导航 + 操作索引**：怎么构建、怎么定位代码、怎么排错、哪些坑别踩。
> 本文**不重复** `docs/*.md` 的细节，每条都指向权威来源。跨端不变量的位级规格见 [`docs/SPEC.md`](docs/SPEC.md)。
> **遇到文档与代码冲突时，一律以代码为准**（见下方「文档与代码偏差清单」）。
> **当本文与 `docs/SPEC.md` 互相矛盾时**（如默认值、常量、行号），先去读对应权威源代码裁决——两份文档都可能滞后，切勿仅凭其中一份下结论。
> **每次改动代码后，必须同步更新相关文档**：改动若涉及常量、默认值、行号、签名、帧格式、文件路径、构建步骤等任一被文档引用的事实，**同一提交内**回写 AGENTS.md / docs/*.md 的对应位置，勿留滞后（本次审计即是文档滞后于代码的教训）。

## 项目一句话

**AirFerry**：完全离线的光学文件传输。发送端（浏览器扩展）把文件编成二维码视频流在屏幕上连续播放；接收端（Android App）用摄像头实时扫描恢复文件。两端共享同一套 Rust 核心库（分别编译为 WASM 与 Android Native `.so`），编解码逻辑数学上一致。**零网络依赖、单向信道、无握手**。

---

## 1. 仓库布局

```
AirFerry/
├── core/                       # Rust 核心库（三 crate workspace）
│   ├── raptorq-core/           #   RFC 6330 RaptorQ 编解码封装（纯逻辑）
│   ├── qr-protocol/            #   帧格式 / 分块 / 压缩 / CRC / QR 矩阵 / 会话 ID
│   ├── transfer-engine/        #   编排 / 状态机 / 进度 / 断点 / 大文件分段组装 + WASM&JNI&C-ABI 绑定
│   └── zxing-decoder/          #   Windows ZXing-C++ 解码核心（非 Cargo crate）
├── apps/
│   ├── sender/                 # 浏览器扩展（Plasmo + React + TS + WASM）
│   │   ├── src/                #   TS 源码（页面 / WASM 桥 / 压缩 worker / background 图标点击直跳）
│   │   ├── wasm-pkg/           #   当前扩展目标的临时 WASM 快照（generated, git-ignored）
│   │   ├── assets/             #   扩展图标
│   │   └── scripts/            #   构建 / manifest 修正 / lzma-wasm 提取
│   ├── web/                    # 网页端（Vite + React + TS）— 直接复用 sender/src 源码，无代码重复
│   │   ├── src/main.tsx        #   薄入口：mount sender 的 App（options.tsx）
│   │   ├── scripts/            #   prepare-wasm（锁定并复制 sender simd 包 + 拷 zstd）/ lzma 提取
│   │   └── public/             #   wasm-zstd.wasm（worker 运行时 fetch，构建时拷入）
│   ├── scanner/                # Android App（Kotlin + CameraX + ZXing-C++）
│   │   └── app/src/main/
│   │       ├── java/com/airferry/app/   # Kotlin
│   │       ├── cpp/                     # ZXing-C++ JNI 桥（CMake）
│   │       └── jniLibs/arm64-v8a/       # Rust .so（cargo-ndk 产物, git-ignored）
│   └── windows/                # Windows App（C# WPF + OpenCvSharp + ZXing-C++）
│       ├── native/                     #   Windows ZXing C ABI 包装器 + CMake
│       ├── AirFerry.Windows/            #   主项目（Views/ViewModels/Scan/Bundle/Native）
│       │   └── runtime/                 #   transfer_engine.dll + airferry_zxing.dll（git-ignored）
│       └── AirFerry.Windows.Tests/      #   协议层单元测试（net8.0，跨平台可跑）
├── docs/                       # 协议 / 架构 / API / 构建说明（中文）
├── scripts/build-all.sh        # 一键构建脚本（含 windows 子命令）
├── scripts/build-windows.ps1   # Windows 端原生 PowerShell 构建脚本（首选）
├── Cargo.toml                  # Rust workspace 根配置
└── dist/                       # 发布产物 + 签名密钥（git-ignored）
```

---

## 2. 快速构建命令

### 2.1 核心库（Rust）

```bash
# 全部单元 + 集成测试（根目录）
cargo test

# 含 diag_* 基准测试（默认 #[ignore]）
cargo test -- --ignored

# 仅构建
cargo build            # 或 cargo build --release
```

> 集成测试位于 `core/transfer-engine/tests/`：`e2e.rs`（端到端恢复 + 丢帧/乱序/重复）、`compress_pipeline.rs`（压缩往返）、`wasm_interop.rs`（跨语言，需先跑 `wasm_dump_frames.mjs` 生成 `frames.bin`/`payload.bin`）、`filemeta_check.rs`（元数据往返）；`diag_*.rs` 为性能/复现基准。

### 2.2 浏览器发送端（apps/sender）

```bash
cd apps/sender
npm install            # 首次（含 postinstall: 提取 lzma-wasm）

# ① 先构建 WASM 双产物（必须是扩展构建的前置）
npm run wasm
#   等价: node scripts/build-wasm.cjs，构建两份 wasm：
#     • wasm-pkg-legacy/ — wasm-bindgen =0.2.92（默认锁定）、标量、无 SIMD、无 externref
#       → Chrome 87+/FF 91+ 可加载，供 MV2 目标使用
#     • wasm-pkg-simd/   — wasm-bindgen =0.2.125（隔离副本升级）、+simd128、含 externref
#       → Chrome 91+/FF 89+ 支持 SIMD、Chrome 96+/FF 116+ 支持 externref，供 MV3 目标使用
#   ⚠️ 工作树隔离：build-wasm.cjs 把 workspace 复制到临时目录，仅在副本中把
#       wasm-bindgen/js-sys/web-sys 升到现代版本并重算 lockfile。源码 Cargo.toml /
#       Cargo.lock 从不写入；脚本还用 `.wasm-build.lock` 阻止两个 WASM 构建互相覆盖。
#   说明: `wasm` feature 已隐含 `serde`（见 core/transfer-engine/Cargo.toml）。
#         SIMD（+simd128）是 RUSTFLAGS target-feature，与 wasm-bindgen 版本正交——
#         0.2.92 也能开 SIMD。MV3 同时开新版 wasm-bindgen（externref）+ SIMD。
#   ⚠️ 实测结论（见 §5 第6条）：当前 raptorq crate 为纯标量 Rust，无 SIMD
#       intrinsics，+simd128 对其无收益。QR 编码已改用 fast_qr（~7-9× 快于
#       旧 qrcode crate，Reed-Solomon 路径大幅提速，见 §5 第6条），但 fast_qr 同样
#       无 wasm32 SIMD intrinsics，+simd128 对它也无作用（wasm 反而大 ~8KB）。
#       新版 wasm-bindgen 的 externref 对 JS↔WASM 交互也无显著提升。双产物机制
#       的真实价值是「MV2 兼容老 Chrome + MV3 用新工具链」各得其所，并为未来
#       引入 SIMD 化的库（如 RaptorQ 用 GF(256) SIMD）铺路。

# ② 构建扩展（全部 4 个目标，会自动先跑 extract-lzma-wasm + build-wasm.cjs）
npm run build
#   等价: extract-lzma-wasm.cjs && build-wasm.cjs（双 wasm 产物） && build-all.cjs
#   build-all.cjs 按 MV2/MV3 把对应 wasm-pkg-* 目录复制到 wasm-pkg/ 后再 plasmo build：
#     chrome-mv3 / firefox-mv3 → wasm-pkg-simd/   复制为 wasm-pkg/
#     chrome-mv2 / firefox-mv2 → wasm-pkg-legacy/ 复制为 wasm-pkg/
#   loader.ts 通过 `@airferry-wasm` alias 指向 `wasm-pkg/`，靠 swap 目录名切换。
#   产物: apps/sender/build/{chrome,firefox}-{mv2,mv3}-prod/

# 单独构建某个目标（不自动跑 wasm 双产物，需先 `npm run wasm`）
npm run build:chrome-mv3     # 或 :chrome-mv2 / :firefox-mv3 / :firefox-mv2

npm run dev                  # Plasmo HMR 开发模式
```

| 目标目录 | 支持浏览器 |
|---------|-----------|
| `chrome-mv3-prod` | Chrome / Edge（MV3） |
| `chrome-mv2-prod` | Chrome / Edge（MV2 遗留） |
| `firefox-mv3-prod` | Firefox 116+ |
| `firefox-mv2-prod` | Firefox 91+ |

### 2.3 Android 扫码端（apps/scanner）

```bash
# ① 先构建 Rust JNI 库（cargo-ndk，必须是 APK 构建的前置）
cargo ndk -t arm64-v8a \
  -o apps/scanner/app/src/main/jniLibs \
  build -p transfer-engine --features jni --release
#   产物: apps/scanner/app/src/main/jniLibs/arm64-v8a/libtransfer_engine.so

# ② 构建 APK
cd apps/scanner
./gradlew :app:assembleDebug      # 调试 APK
./gradlew :app:assembleRelease    # 发布 APK（必须配置 keystore.properties；缺失即失败）
#   产物: app/build/outputs/apk/{debug,release}/app-*.apk

adb install app/build/outputs/apk/release/app-release.apk
```

> ZXing-C++（`libairferry_zxing.so`）由 Gradle 的 CMake 任务在首次 APK 构建时从 GitHub 拉取 v3.0.2 自动编译（需网络；缓存后离线可用）。
> **16 KiB 页对齐**：Android 15+ 的 16 KiB 页设备会拒绝 `dlopen` 仅 4 KiB 对齐的 `.so`（表现：所有 QR 解码静默失败）。`cpp/CMakeLists.txt` 用 `-Wl,-z,max-page-size=16384` 强制对齐；Rust `.so` 由 cargo-ndk 默认对齐。验证：`llvm-readelf -l lib*.so | grep LOAD`（Align 列应为 `0x4000`）。

### 2.4 Windows 扫码端（apps/windows）

```powershell
# 首选：PowerShell 原生脚本（须 Windows + .NET 8 SDK + CMake/VS C++）
.\scripts\build-windows.ps1           # 构建（Rust DLL + ZXing-C++ DLL + WPF）
.\scripts\build-windows.ps1 -Pack     # 构建 + 打包到 dist/

# 或 Git Bash/WSL 下用 build-all.sh 的 windows 子命令（逻辑等价）
./scripts/build-all.sh windows
```

> **WPF 只能在 Windows 上构建**（`net8.0-windows` TFM 依赖 Windows SDK）。
> **托管层单元测试**（`AirFerry.Windows.Tests`）用纯 `net8.0`，可在任何 OS 上跑（协议、文件导出、池化缓冲及 native packed 结果解析；不实际加载 P/Invoke DLL）：
> ```bash
> cd apps/windows && dotnet test    # 任意 OS
> ```
> 详见 [`docs/build-windows.md`](docs/build-windows.md)。

### 2.5 网页端（apps/web）

```bash
cd apps/web
npm install            # 首次（含 postinstall: 提取 lzma-wasm）

npm run dev            # Vite HMR 开发（http://localhost:5180）
npm run build          # 产出**发送端**静态站点 dist/（index.html 单入口，可部署任意静态托管）
npm run build:receiver # 产出**接收端**静态站点 dist-receiver/（receiver.html 单入口，独立 zip）
npm run build:standalone  # 产出发送端自包含单文件 dist-standalone/index.html（双击即用，file:// 可运行）
npm run preview        # 本地预览构建产物
```

> **复用 sender 源码，零代码重复**：web 端是薄入口（`src/main.tsx` 只 mount sender 的 `App`），通过 Vite alias `@/ → ../sender/src/` 直接跨工程 import `apps/sender/src/` 的全部页面/组件/worker/wasm 模块。改 sender 业务代码，web 端自动同步。
>
> **唯一前置依赖**：web 构建复用 `apps/sender/wasm-pkg-simd/`（Rust WASM 现代产物）。首次构建前需在 sender 下先跑一次 `npm run wasm`。`predev`/`prebuild` 会持构建锁校验并复制到 web 自有 `apps/web/wasm-pkg/`，缺失时报清晰错误；web 与扩展的并发构建不会相互切换依赖目录。
>
> **环境自适应**：sender 的 `options.tsx` 用 `typeof chrome` 判断 —— 扩展走 `chrome.runtime.getURL`，网页走 `new URL(..., document.baseURI)`。`compress.ts` 的 worker 内 zstd 加载同样有网页 fallback。两端共用同一份 `options.tsx`，扩展行为不变。
>
> **部署**：`dist/` 是纯静态文件，资源用相对路径（`base: "./"`），可放 GitHub Pages / Netlify / 任意静态服务器的任意子路径。`wasm-zstd.wasm` 在产物根目录供 worker 运行时 fetch。核心传输**不需要** COOP/COEP 头（不依赖 SharedArrayBuffer）。详见 [`docs/build-web.md`](docs/build-web.md)。
>
> **单文件版（`npm run build:standalone`）**：产出**单个 `dist-standalone/index.html`**（约 2MB），所有 JS/CSS/Worker/WASM 内联（WASM 转 base64），**双击即可在 `file://` 下运行**，无需服务器。原理：① Vite IIFE bundle（去 ES module 标记，绕过 file:// 的 module 限制）；② worker 源码字符串化后用 Blob URL 加载（绕过 file:// 的 Worker 限制）；③ 三个 WASM base64 内联 + 复用现成 buffer 接口（`init(buffer)` / `initZstdFromBytes` / lzma 自带 base64，绕过 file:// 的 fetch 限制）。`build-standalone.cjs` 后处理脚本完成内联，并处理 `</script>` 转义、`import.meta.url` 替换、`process` polyfill 三个细节。sender 源码（`options.tsx`/`loader.ts`）通过 `globalThis.__AIRFERRY_STANDALONE__` 标志做环境自适应，扩展/web 普通版不受影响。
>
> **网页接收端（v1.1.6 新增，`receiver.html`）**：v1.1.6 起发送端与接收端**分开构建、独立 zip**——`npm run build` 产出发送端 `dist/`（index.html 单入口），`npm run build:receiver` 产出接收端 `dist-receiver/`（receiver.html 单入口），打包为 `airferry-sender-web-v{VER}.zip` + `airferry-receiver-web-v{VER}.zip` 两个独立可部署产物（发送端 zip 排除 `zxing_reader.wasm`）。接收端用浏览器 `getUserMedia` 拿摄像头（**三级 fallback**：后置高分辨高帧率 → 后置无约束 → 默认摄像头 `true`，缓解「Starting videoinput failed」——某些摄像头/驱动对严格 constraint 集或 facingMode 不兼容；**`frameRate:{ideal:60,max:60}`** 钉住上限确保 60fps 摄像头真给 60，否则 ideal 软约束常被降 30fps → Web 卡 120 码/s），`requestVideoFrameCallback` 取帧（**1080 全分辨率，不 downscale**），经 **QR decode worker 池**（`zxing-wasm/reader` 兼容路径，**`QR_WORKER_POOL=4` 个 worker 并行解码**跨核分摊帧率，镜像 Android 线程池；**整帧全图解码**，zxing `maxNumberOfSymbols:4` 在多码任意位置都能检出——不用固定 ROI，真实手机拍摄码偏移/倾斜时 ROI 会把码切半导致难扫）→ **receive worker**（单例，`ReceiverSessionWasm` **串行** ingest + `assemble_raw` + JS 侧 zstd/xz 解压 + CRC 校验 + 文字/包/单文件分流）。源码在 `apps/sender/src/{pages/ReceivePage.tsx, receive/{decompress,parse}.ts, workers/{receive,qr-decode}.worker.ts}`，web 入口 `apps/web/src/receiver.tsx` + `apps/web/receiver.html`。Vite 多页面（`rollupOptions.input`）+ `worker.format:"es"`（worker 含 dynamic import 需 ES 格式）。`zxing-wasm` 与 `lzma-wasm` 装在 web 的 node_modules，用 alias 指向其 dist 入口（sender/node_modules 无此包）。`prepare-wasm.cjs` 额外拷 `zxing_reader.wasm` 到 `public/` 供 worker 运行时 fetch。发送端与接收端 web **拆为两个独立 zip**（`airferry-sender-web-v{VER}.zip` / `airferry-receiver-web-v{VER}.zip`），各自自包含可独立部署。**接收端只有普通多文件版（`receiver.html`），不再保留单文件版**（v1.1.6 曾用 `build:receiver:standalone`，已移除）。**⚠️ 网页接收端三限制**：① 不能双击 `receiver.html` 运行——是多文件静态站点，需先部署（静态服务器/GitHub Pages/Netlify）或 `serve-https.mjs` 起 HTTPS；② 必须 HTTPS 或 localhost——`getUserMedia` 摄像头只在安全上下文可用，`file://` 直开/普通 http（非 localhost）无法访问摄像头（因此**无法**像发送端单文件版那样双击即用，这是浏览器硬限制）；③ 速度低于原生——JS/WASM 解码 + 浏览器摄像头管道，结构性慢于 Android/Windows 原生（C++ 多线程 + SIMD），追求满速扫码建议用原生接收端。局域网一键启动：`cd apps/web && node scripts/serve-https.mjs dist-receiver .cert/selfsigned.crt .cert/selfsigned.key 8765`。
>
> **接收端 UI 复用发送端设计系统**：`ReceivePage.tsx` 样式从 `app.css` 抽离为独立 `apps/sender/src/assets/receive.css`（发送端 `app.css` 不再含 `.receive-*`）。接收端 JSX 复用发送端骨架（`.app` / `.app-header` / `.app-logo` / `.app-main` / `.app-footer`），色值一律走 `app.css` 设计 token（`--color-primary`/`--color-card`/`--color-border`/`--color-success`/`--color-error` 等），无硬编码色；进度条/参数卡/结果卡/按钮与发送端同源观感。接收端专属结构（`.receive-header`/`.camera-area`/`.fps-badge`/`.progress-*`/`.result-area`/`.bundle-*` 等）定义在 `receive.css`。`.receive-page` 复用 `.app` 的 720px 居中布局但设 `justify-content:flex-start`（相机+进度卡可能超一屏，`center` 会裁剪顶部）。图标直接 `import iconUrl from "../../assets/icon128.png"`。

### 2.6 一键脚本 `scripts/build-all.sh`

| 子命令 | 动作 | 是否自动跑 cargo 前置 |
|--------|------|---------------------|
| `all`（默认） | `build_sender` + `build_web` + `build_scanner` | ✅（scanner） |
| `sender` | 双 wasm 产物 + 扩展 4 目标 | — |
| `web` | `npm run build`（apps/web，Vite 静态站点；自动跑 `prebuild`→prepare-wasm 校验 sender `wasm-pkg-simd/`、复制私有快照 + 拷 `wasm-zstd.wasm`） | — |
| `scanner` | `cargo ndk` 编译 `.so` → `./gradlew assembleRelease` | ✅ |
| `windows` | Rust C ABI DLL + 共享 ZXing-C++ DLL → `dotnet build`（须 Windows） | ✅ |
| `wasm` | 仅 `npm run wasm`（= build-wasm.cjs，产 legacy + simd 两份） | — |
| `dist` | **仅打包**：把已构建的 `build/` + APK + Windows zip + web zip 复制/签名到 `dist/`（不重新构建） | — |
| `release` | `build_sender` → `build_web` → `build_scanner` → `pack_dist`（全量构建 + 打包） | ✅（scanner） |

```bash
./scripts/build-all.sh              # 构建 sender + web + scanner（不打包）
./scripts/build-all.sh release      # 全量构建 + 打包到 dist/（最常用）
./scripts/build-all.sh dist         # 仅打包（假设已构建好，不重新编译）
./scripts/build-all.sh sender       # 仅浏览器端（含 WASM）
./scripts/build-all.sh web          # 仅网页端（须先有 apps/sender/wasm-pkg-simd/）
./scripts/build-all.sh scanner      # 仅 APK
./scripts/build-all.sh windows      # 仅 Windows 端（须 Windows + .NET 8 SDK + CMake/VS C++；首选 build-windows.ps1）
./scripts/build-all.sh wasm         # 仅 WASM
```

**脚本行为要点**（权威源 `scripts/build-all.sh`）：
- **版本号**：从 `apps/sender/package.json` 的 `version` 读取（`read_version()`），与扩展 manifest 同源。改版本改这一处即可被脚本读取，但 APK/扩展本身的版本号仍需手动同步（见 §2.7）。
- **`build_scanner` 自动跑 cargo-ndk**：在 `./gradlew assembleRelease` **之前**先用 `cargo ndk -t arm64-v8a ... build -p transfer-engine --features jni --release` 编译 `libtransfer_engine.so` 到 `jniLibs/`。这是为了避免打进过期 `.so`（AirFerry 重命名后旧符号 `com.easytransfer.*` 与 Kotlin 新包名对不上会 `UnsatisfiedLinkError` 闪退）。因此 `scanner`/`all`/`release` 子命令都自带这步，无需手动前置。
- **`build_windows` 自动构建两个 native 前置**：在 `dotnet build` **之前**先用 cargo 编译 `transfer_engine.dll`，再用 CMake/VS C++ 编译并测试 `airferry_zxing.dll`；二者都复制到 `runtime/`。Android 直接保留 v1.1.3 的 `scan_jni.cpp`，Windows 通过 `core/zxing-decoder/` 镜像同一解码选项与全帧/ROI 调度模式。**Windows 端只能在 Windows + .NET 8 SDK + CMake/VS C++ 下完整构建**，首选 `scripts/build-windows.ps1`。
- **`build_web` 不编译 Rust**：`cd apps/web && npm run build`（Vite 静态站点），`prebuild` 会跑 `prepare-wasm.cjs` 校验 `apps/sender/wasm-pkg-simd/{transfer_engine.js,transfer_engine_bg.wasm}`，持共享构建锁原子复制到 web 自有 `apps/web/wasm-pkg/`，再拷 `wasm-zstd.wasm` 到 `public/`。web 明确复用现代 WASM，**首次构建前须先 `cd apps/sender && npm run wasm`**。`pack_dist` 用 warn（非 error）模式打包 web zip——产物缺失时跳过而非中断，因为用户可能只发扩展+APK 不发网页端。
- **Chrome crx 签名**：调用 macOS Chrome 的 `--pack-extension` + `--pack-extension-key`。私钥固定在 `dist/airferry-extension.pem`——**首次运行自动生成并挪到此处，之后 MV2/MV3 复用同一私钥**，保证扩展 ID 稳定。找不到 Chrome 二进制时跳过 crx、仅留 zip。
- **`pack_dist` 会清旧产物**：删 `dist/airferry-{receiver-android-*.apk,receiver-windows-*.zip,receiver-web-*.zip,sender-chrome-*.crx,sender-chrome-*.zip,sender-firefox-*.xpi,sender-web-*.zip}`，但**不动** `*.pem` 和 `*.keystore`。

### 2.7 构建目录布局

三层产物目录，**全部 git-ignored**（见 `.gitignore`）：

```
源码 ──构建──► 中间产物目录 ──打包──► dist/（发布）
```

| 目录 | 内容 | 来源 | git-ignored |
|------|------|------|-------------|
| `apps/sender/wasm-pkg-legacy/` | WASM 编译产物（wasm-bindgen 0.2.92 / 标量 / 无 externref，Chrome87-safe）；供 MV2 目标使用。内含 `.gitignore` 为 `*`（全忽略） | `npm run wasm:legacy`（由 build-wasm.cjs 调度） | ✅ |
| `apps/sender/wasm-pkg-simd/` | WASM 编译产物（wasm-bindgen 0.2.125 / +simd128 / 含 externref）；供 MV3 目标使用。内含 `.gitignore` 为 `*`（全忽略） | `npm run wasm:simd`（由 build-wasm.cjs 调度） | ✅ |
| `apps/sender/wasm-pkg/` | **临时**目录：build-all.cjs 在每次 plasmo build 前按目标把 `wasm-pkg-legacy/` 或 `wasm-pkg-simd/` 复制到这里（`@airferry-wasm` alias 指向它）。不长期存在 | `build-all.cjs` 的 `useWasmPkg()` | ✅ |
| `apps/web/wasm-pkg/` | **web 私有快照**：prepare-wasm 持锁从 sender 的现代产物原子复制；避免与扩展目标切换竞态 | `predev`/`prebuild`/`prebuild:standalone` | ✅ |
| `apps/sender/build/` | Plasmo 扩展构建产物：`{chrome,firefox}-{mv2,mv3}-prod/` 四个目录 + 构建期生成的 `.crx`/`.xpi`/`.zip` | `npm run build` | ✅ |
| `apps/web/public/wasm-zstd.wasm` | zstd WASM，构建时由 `prepare-wasm.cjs` 从 `@foxglove/wasm-zstd/dist/` 拷入，供 worker 运行时 fetch | `predev`/`prebuild`（prepare-wasm.cjs） | ✅ |
| `apps/web/dist/` | Vite 网页构建产物：`index.html` + `assets/`（JS/CSS/wasm/worker）+ 根目录 `wasm-zstd.wasm`。相对路径 `base:"./"`，可部署到任意子路径 | `npm run build` | ✅ |
| `apps/scanner/app/build/` | Gradle/APK 构建产物：`outputs/apk/{debug,release}/app-*.apk` + native-debug-symbols + baselineProfiles | `./gradlew` | ✅ |
| `apps/scanner/app/src/main/jniLibs/arm64-v8a/` | Rust 编译的 `libtransfer_engine.so` + 遗留的 `libet_code.so`（**当前代码无任何 `System.loadLibrary("et_code")` 引用，未使用，建议清理**）。ZXing 的 `libairferry_zxing.so` 不在此处——由 CMake 在 APK 构建时直接编译进 APK | `cargo ndk ... build` | ✅ |
| `apps/windows/AirFerry.Windows/bin/` `obj/` | C# WPF 构建产物：`bin/x64/Release/net8.0-windows/win-x64/` + OpenCV/native DLLs | `dotnet build` / `dotnet publish` | ✅ |
| `apps/windows/AirFerry.Windows/runtime/transfer_engine.dll` | Rust 编译的 C ABI DLL（`--features cffi`）；csproj 显式纳入 build/publish 并扁平复制到 exe 同目录，打包脚本再显式复制+核验 | `cargo build`（由 build-windows.ps1 拷入） | ✅ |
| `apps/windows/AirFerry.Windows/runtime/airferry_zxing.dll` | Windows 对 Android v1.1.3 模式的 C ABI 等价实现；作为独立 native DLL 放在 exe 同目录，打包时显式复制+核验 | `cmake --build apps/windows/native/build`（由 build-windows.ps1 拷入） | ✅ |
| `apps/windows/native/build/` | Windows ZXing-C++ 配置、依赖与 CTest 产物 | CMake（首次配置按固定 commit 获取 zxing-cpp） | ✅ |
| `dist/` | 发布归档 + 签名材料（`*.pem` Chrome 私钥、`airferry-release.keystore`） | `pack_dist` | ✅ |
| `target/` | Rust 编译缓存（workspace 共享） | `cargo` | ✅ |

> 构建产物**绝不提交 git**。分发走 GitHub Release，产物放 `dist/`。

### 2.8 产物格式与命名规范

发布归档统一格式 `airferry-{角色}-{平台及变体}-v{版本}.{扩展}`，**角色前缀进文件名**：

- `sender` = **发送端**（浏览器扩展 / 网页）：把文件编成二维码视频流在屏幕上播放
- `receiver` = **接收端**（Android / Windows App）：用摄像头 / 采集卡扫码恢复文件

> **asset 不设 label**（Release 资产的「下载说明」字段留空，GitHub 页面直接显示文件名）。各端的用途、系统要求、架构、变体差异等**写在 Release notes 的产物表**（见各版 `docs/releases/v{VER}.md` 与 README 下载表）和文件名本身里，不重复进 label——文件名已含角色+平台+变体+版本，足够区分。

| 产物 | 命名格式 | 格式说明 |
|------|---------|---------|
| Android 接收端 APK | `airferry-receiver-android-arm64-v{VER}.apk` | **Android 扫码端**。arm64-v8a 单 ABI；Android 10+（minSdk 29）；安装后对准屏幕二维码即可接收。必须用 `apps/scanner/keystore.properties` 指向的 release keystore 签名；缺失时 release 构建失败，打包还会用 `apksigner` 拒绝 debug/无效签名 |
| Windows 接收端 zip | `airferry-receiver-windows-x64-v{VER}.zip` | **Windows 扫码端**。x64 单架构；Windows 10+，需安装 .NET 8 Desktop Runtime；支持摄像头 + USB/HDMI/SDI 采集卡作为视频源。`dotnet publish` 单文件 + 框架依赖；内含 `AirFerry.exe` + `transfer_engine.dll` + `airferry_zxing.dll` + OpenCV native DLLs。**只能在 Windows 上构建**（WPF TFM） |
| Chrome MV3 | `airferry-sender-chrome-mv3-v{VER}.crx` + `.zip` | `.crx` = Cr24 签名格式（Chrome 96+/Edge 96+，现代 WASM externref）；`.zip` = 解压目录打包回退（`.crx` 被商店外安装拦截时用） |
| Chrome MV2 | `airferry-sender-chrome-mv2-v{VER}.crx` + `.zip` | 同上，旧版浏览器兼容 |
| Firefox MV3 | `airferry-sender-firefox-mv3-v{VER}.xpi` | Firefox 116+；`.xpi` 本质是 zip 改名 |
| Firefox MV2 | `airferry-sender-firefox-mv2-v{VER}.xpi` | Firefox 91+ |
| 网页发送端 | `airferry-sender-web-v{VER}.zip` | 纯静态站点（`index.html` + `assets/` + 根目录 `wasm-zstd.wasm`）；Vite `base:"./"` 相对路径，可部署到任意静态托管的任意子路径。v1.1.6 起**仅含发送端**（`build_web` 产 `apps/web/dist/`，打包排除 `zxing_reader.wasm`）。`pack_dist` 自动打包（须先跑 `build-all.sh web`，缺失时 warn 跳过） |
| 网页接收端 | `airferry-receiver-web-v{VER}.zip` | **接收端独立 zip**（`receiver.html` + assets + `wasm-zstd.wasm` + `zxing_reader.wasm`）。v1.1.6 起与发送端 web 拆分，可独立部署；`build:receiver` 产 `apps/web/dist-receiver/`。⚠️ **不能双击运行**，需部署到 HTTPS / localhost（`getUserMedia` 摄像头仅安全上下文可用）。`pack_dist` 自动打包（缺失时 warn 跳过） |
| 网页发送端单文件 | `airferry-sender-web-standalone-v{VER}.html` | **单个自包含 HTML**（约 2MB），所有 JS/CSS/Worker/WASM 内联（WASM 转 base64），**双击在 `file://` 下即用**，无需服务器。由 `cd apps/web && npm run build:standalone` 产出（`dist-standalone/index.html` → 改名）。不进 `pack_dist`（与静态站点 zip 不同，单文件是 `.html` 无需 zip），手动上传 Release |

#### 发布流程（GitHub Release 怎么来）

1. **本地构建打包**：`./scripts/build-all.sh release`（macOS/Linux）→ 产出 `dist/` 下除 Windows 外的全部产物（扩展 4 目标 crx/zip/xpi + Android APK + web zip）。Windows zip 单独在 Windows 上用 `.\scripts\build-windows.ps1 -Pack` 或走 `.github/workflows/windows.yml` 的 `windows-pack` job（见 §2.9）。
2. **创建/更新 Release**：`build-all.sh release` 内部 `pack_dist` 只把产物放进 `dist/`，**不自动上传 GitHub**。发版时手动：
   ```bash
   gh release create v{VER} -R UR-SillyB/AirFerry \
     --target <commit-sha> --title v{VER} \
     --notes-file docs/releases/v{VER}.md        # 创建（首次）
   gh release upload v{VER} -R UR-SillyB/AirFerry dist/* --clobber   # 上传/覆盖 asset
   ```
   Windows asset 由 `windows.yml` 的 `windows-pack` job 自动 `gh release upload --clobber`。
3. **重新发布同一 tag（需改 notes / 重排 asset / 清 label 时）**：保留 tag 以维持下载链接稳定，只重建 release 记录：
   ```bash
   gh release delete v{VER} -R UR-SillyB/AirFerry --yes --cleanup-tag=false   # 删 release，保留 tag
   gh release create v{VER} -R UR-SillyB/AirFerry --target <tag-commit> --title v{VER} --notes-file docs/releases/v{VER}.md
   gh release upload  v{VER} -R UR-SillyB/AirFerry dist/* --clobber            # 重新上传
   ```
   `gh release upload` **不写 label**（label 留空，按本节规范）。历史版本（v1.0.0–v1.1.3）文件名不含角色前缀，属历史命名，不回改。

**扩展产物内部结构**（每个 `*-prod/` 目录）：
- `manifest.json`——由 `scripts/fix-manifest.cjs` 后处理：复制真实 RGBA 图标覆盖 Plasmo 占位图、MV2 删 `action` 留 `browser_action` 并把 CSP 改为 `wasm-eval`、Firefox 补 `browser_specific_settings.gecko.id = airferry@airferry.app`、修补 HTML `<title>`
- `transfer_engine_bg.wasm` + `wasm-zstd.wasm` + `lzma-wasm.wasm`——运行时加载的 WASM 模块
- 图标 `icon{16,32,48,64,128}.png`

> MV2 与 MV3 用同一 Chrome 签名私钥打包会得到**相同的扩展 ID**（`nboajkjpabbekenmadidokmefholfmfk`），便于升级替换。

### ⚠️ 关键依赖顺序（最容易踩的坑）

1. **WASM 双产物必须先于扩展构建**：`npm run build` 已内嵌 `build-wasm.cjs`（产 `wasm-pkg-legacy/` + `wasm-pkg-simd/`）再 `build-all.cjs`，故一条命令搞定。但**单独跑 `npm run build:chrome-mv3` 等单目标脚本不自动跑 wasm**——需先 `npm run wasm` 产出双产物，否则 `build-all.cjs` 的 `useWasmPkg()` 会因 `wasm-pkg-*/` 缺失报错退出。（`build-all.sh sender/release` 走 `npm run build`，不会踩坑。）
2. **JNI `.so` 必须先于 APK 构建**：`cargo ndk ... build` 产出 `libtransfer_engine.so` 到 `jniLibs/` 后，`./gradlew` 才能打包进 APK。**`build-all.sh` 的 `scanner`/`all`/`release` 子命令会自动先跑 cargo-ndk**（见 §2.5），所以走脚本不会踩坑；但**若单独手动跑 `./gradlew`**（不经脚本），必须自己先跑 cargo-ndk，否则 APK 会带过期/缺失的 `.so`、扫码端运行时 `UnsatisfiedLinkError` 闪退。
3. **两个 native DLL 都必须先于 C# 构建**：cargo 产出 `transfer_engine.dll`，CMake 产出 `airferry_zxing.dll`，二者放入 `runtime/` 后 `dotnet build` 才会打包到 exe 同目录。**`build-windows.ps1`/`build-all.sh windows` 会自动构建、测试并复制二者**（见 §2.5）；若只手动跑 `dotnet build`，运行时会在引擎或二维码解码的首个 P/Invoke 处抛 `DllNotFoundException`。
4. **`dist` 子命令不重新构建**：它假设 `apps/sender/build/` 与 APK 已就绪（Windows zip 可选），只做复制/签名/打包。缺 sender/scanner 产物会 `error` 退出；缺 Windows 产物则 `warn` 跳过（因为 Windows 端只能在 Windows 上构建）。
5. **版本号同步（发版必查）**：`build-all.sh` 的发布文件名版本取自 `apps/sender/package.json`；下列**全部**须同一版本号：
   - `apps/sender/package.json` `version` + `manifest.version`（→ 扩展/打包文件名）
   - `apps/scanner/app/build.gradle.kts` `versionName`（+ 通常 `versionCode++`）（→ APK 内嵌）
   - `Cargo.toml` `[workspace.package] version`（→ 核心库）
   - `apps/windows/AirFerry.Windows/AirFerry.Windows.csproj` `<Version>`（→ exe 内嵌）
   - `.github/workflows/windows.yml` `jobs.windows-pack.env.VER`（→ CI 产出的 Windows zip 名与 `gh release upload` 的 tag）
   - 建议同步 `apps/web/package.json` `version`（展示用，不进 `build-all.sh` 文件名）

### 2.9 Windows 发版（GitHub Actions workflow）

> macOS/Linux **不能**本地编 WPF。正式 Windows 产物走 CI：`.github/workflows/windows.yml`。

| Job | Runner | 触发 | 作用 |
|-----|--------|------|------|
| `rust-cffi` | ubuntu | push/PR（core/windows 路径）+ `workflow_dispatch` | `cargo test/build --features cffi` |
| `csharp-tests` | ubuntu | 同上 | `dotnet test` 协议层及 packed QR 结果解析（不加载 native DLL） |
| `windows-build` | windows-2022 | 同上 | cargo + CMake/CTest 生成两个 native DLL → `dotnet build` WPF；固定 runner 以匹配 VS 2022 CMake generator |
| `windows-pack` | windows-2022 | **仅** `workflow_dispatch`（且前三 job 通过） | 重建两个 native DLL → `dotnet publish` 单文件 → zip → Release |

**发版步骤（Windows 端）**：

1. 把上表版本（含 `windows.yml` 的 `VER`）改到目标版本（如 `1.1.1`），提交并推 `main`。
2. 本地（或其它 CI）先打好 sender/APK/web 并创建/上传 GitHub Release tag `v{VER}`（若尚无 tag，workflow 会 **draft** 创建，之后可补 notes/其它 asset）。
3. GitHub → Actions → **windows** → **Run workflow**（`workflow_dispatch`）。
4. 跑完后 Release 上应有 `airferry-receiver-windows-x64-v{VER}.zip`（`--clobber` 可覆盖同名 asset；asset 不设 label，见 §2.8）。

本地 Windows 机仍可用 `.\scripts\build-windows.ps1 -Pack` 等价打包到 `dist/`，但**默认发布路径是 workflow**。

---

## 3. 代码导航地图（file:line 索引）

### 3.1 核心库热路径

| 关注点 | 位置 | 说明 |
|--------|------|------|
| 编码器（源符号 O(1) + 按需修复） | `core/raptorq-core/src/encoder.rs:67,77` | `source_symbol` / `repair_symbols` |
| 解码器（任意顺序符号 + 块满即解） | `core/raptorq-core/src/decoder.rs`（`add_symbol`） | 含 ESI/载荷越界守卫 |
| **安全闸门** OTI 校验 | `core/raptorq-core/src/meta.rs:107` | `ObjectMeta::validate`——接收前必跑，防 panic-on-abort |
| **帧解析热点** | `core/qr-protocol/src/frame.rs`（`from_bytes`） | magic/version/双层 CRC 校验 |
| 帧封装 | `core/qr-protocol/src/frame.rs`（`build` / `to_bytes`） | |
| 会话 ID 派生（FNV-1a 128） | `core/qr-protocol/src/session.rs:23` | `derive`——必须与 TS 端位一致 |
| 压缩分发 + 解压炸弹防护 | `core/qr-protocol/src/compress.rs:76,107` | `compress_with` / `decompress_with_limit` |
| QR 矩阵（动态最小版本） | `core/qr-protocol/src/qr_render.rs`（`encode` / `min_version_for`） | fast_qr；1464B(T=1400)→V27，1088B(T=1024)→V23，576B(T=512)→V16 |
| **发送端帧流入口** | `core/transfer-engine/src/sender.rs`（`next_frame`） | 每16帧插描述符，首帧即描述符 |
| 持续新鲜修复符号 | `core/transfer-engine/src/sender.rs`（`next_symbol_id`） | 源一遍→不重复修复；ESI 达 2²⁴ 时明确停止 |
| **接收端摄入入口** | `core/transfer-engine/src/receiver.rs`（`pub fn ingest`） | 缓存引导→描述符确认 OTI→喂解码器；预描述符 `symbol_cache` 上限 `PRE_META_SYMBOL_CACHE_MAX=12000` |
| 描述符载荷解析 | `core/transfer-engine/src/descriptor.rs`（`parse_payload`） | v1/v2/v3 + v2/v3 消歧；v4 分段尾段（`build_segment_payload`/`parse_segment_payload`） |
| 大文件分段（descriptor v4） | `core/transfer-engine/src/segment.rs` + `assembler.rs` | 固定 `SEGMENT_RAW_BYTES=8 MiB`、`MAX_SEGMENT_COUNT=131072`；`SegmentMeta::validate` 强制 child id / 根大小 / 段数 / 规范偏移 / 精确段长。`TransferAssembler` 仅是 ≤256 MiB 的便利内存实现；Web 用 IndexedDB 段 Blob，Android/Windows 用磁盘 `.partial` |
| 进度快照 | `core/transfer-engine/src/progress.rs` | `Progress` / `Stats` |
| 断点状态序列化 | `core/transfer-engine/src/resume.rs` + `receiver.rs` | `ResumeState`（serde-gated JSON）；JSON 封顶 128 MiB，恢复前校验 OTI/坐标/预算；**只有带载荷的符号才可回放并计入 received**。descriptor 后为控内存不保存全部 decoder 输入，普通当前对象重启需重扫；大文件跨重启靠宿主“已完成段”账本 |
| **JNI 绑定（Android）** | `core/transfer-engine/src/jni.rs:67` | `receiverIngest` 返回**packed jlong**（非 JSON，见 SPEC §7）；打包逻辑三端共享 `ingest_status.rs`；`receiverLastAssembleError` 暴露组装/解压失败原因；descriptor-v4 段 getter：`receiverIsSegmented/SegmentIndex/SegmentCount/RootOriginalSize/OriginalOffset/RootSessionIdLo/Hi/RawSha256`（读 `session.segment_meta()`，WASM/C-ABI 同构） |
| **C-ABI 绑定（Windows）** | `core/transfer-engine/src/cffi.rs` | `airferry_receiver_is_segmented/segment_index/segment_count/root_original_size/original_offset/root_session_id_lo/hi/raw_sha256`；Windows `NativeBridge.cs` + `ReceiverSession.cs` 对应转发 |
| **C ABI 绑定（Windows/.NET P/Invoke）** | `core/transfer-engine/src/cffi.rs:106` | `airferry_receiver_ingest` 返回**packed u64**（位布局三端共享 `ingest_status.rs`）；assemble 用「Rust 分配+free」单次调用 |
| **WASM 绑定（浏览器）** | `core/transfer-engine/src/wasm.rs` | **发送端** `SenderSessionWasm`：`next_qr` / `next_qr_multi` 为兼容 API；热路径用 `next_qr_scratch` 写入会话内固定缓冲，再用 `qr_scratch_view` 取得当帧 WASM 视图。**接收端** `ReceiverSessionWasm`（v1.1.6 新增，网页接收端用）：`from_descriptor`（校验完整帧 CRC+描述符 flag→锁定 session id→摄入使 meta confirmed）/ `new(sid_lo,sid_hi)` 缓存引导 / `ingest` 返回**packed u64**（位布局三端共享，见 §SPEC ingest 状态字）/ 元数据 getters（`file_name`/`original_size`/`compressed_size`/`compression`/`crc32`/`*_known`/`meta_confirmed`）/ `assemble_raw`（**只重组不解压**，JS 侧用 zstd/xz WASM 解压 + 校验 CRC32）/ `progress_json`。**不暴露 `assemble_result`**（wasm32 解压 fail-closed，见 §5 第10条） |
| **Windows 相机 QR 解码核心** | `core/zxing-decoder/` | Windows C ABI 对 Android v1.1.3 ZXing 选项、全帧/多 ROI 与结果打包模式的等价实现；不是 Cargo crate |

### 3.2 浏览器发送端

| 关注点 | 位置 | 说明 |
|--------|------|------|
| 主应用（4 页路由） | `apps/sender/src/options.tsx`（`export default function App`） | select→params→play→stats；WASM session 用独立 owner ref + 幂等释放，覆盖 epoch 失效/替换/StrictMode 卸载，禁止双重 `free()` |
| **图标点击直跳（MV2/MV3 兼容）** | `apps/sender/src/background/index.ts` | `chrome.action.onClicked`/`browserAction.onClicked` → 新标签页打开 options。**无 `default_popup`**（popup.tsx 已删），listener 才会触发；有 popup 时 onClicked 永不触发 |
| popup（仅启动器） | ~~`apps/sender/src/popup.tsx`~~ | **已删除**——图标点击改为直接跳转，不再弹小窗 |
| **QR 渲染循环** | `apps/sender/src/components/QrStream.tsx`（`QrStream` / rAF loop） | Canvas2D rAF 每次可见刷新调用 `next_qr_scratch` / `qr_scratch_view`；`fps=0` 也只推进一次可见帧 |
| 单次 putImageData 绘制 | `apps/sender/src/components/QrStream.tsx`（`drawMatrix`） | 模块展开为像素后每码一次 putImageData |
| **文字传输魔数（ETTEXTv1）** | `apps/sender/src/wasm/text.ts` | `buildTextPayload`/`isTextPayload`；与 `bundle.ts` 的 ETBUNDL1 并列。**单条纯文字**走 ETTEXTv1（收端 `ReceiveText` 可复制/分享/存 .txt）；描述符 filename = 选择页用户命名（默认 `文字消息.txt`）；**混发**时文字以命名 `.txt` 进 ETBUNDL1 |
| **三算法选优压缩** | `apps/sender/src/wasm/compress.ts`（`preparePayload`） | raw/zstd/xz，早期退出阈值 **70%** |
| 压缩 worker（离主线程） | `apps/sender/src/workers/compress.worker.ts` | 读→压缩→CRC32→会话 ID。`wasm-init`（可无 zstd）始终解锁队列；发送带 `jobId`/epoch，过期任务不 post。`1×text` → `processText({ jobId, text, name })`（name 规范化进描述符）；否则 `processFiles`。**descriptor-v4 大文件分段**：单文件原始 >32 MiB 走 `processSegmentedFile`；根指纹只读头/尾各 1 KiB；首次仅切片/压缩第 0 段，随后 `prepare-segment` 按需重读目标 8 MiB 段，worker 与 React 均只保留当前段。每段独立 CRC + SHA-256 + `deriveSegmentId(root,i)`；`PlayPage` 支持上/下一段及输入段号直达 |
| WASM 加载器 | `apps/sender/src/wasm/loader.ts:22` | `ensureWasm` 一次性初始化 |
| 会话 ID（TS 端，镜像 Rust） | `apps/sender/src/wasm/session.ts:17` | `deriveSessionId` |
| 多文件容器 | `apps/sender/src/wasm/bundle.ts` | 打包格式（ETBUNDL1） |
| 4 个页面 | `apps/sender/src/pages/*.tsx` | FileSelect / Params / Play / Stats |
| **选择页（统一列表）** | `apps/sender/src/pages/FileSelectPage.tsx` + `options.tsx` + `types.PendingItem` | 无 Tab：上方按钮——**添加文件夹**（`showDirectoryPicker`/`<input webkitdirectory>`，递归加入）+ **添加文字**（弹窗，**保留 content**，文件名文案「收端展示/落盘名」）；**添加文件**由下方大拖放区（全页拖放/点选，追加）承担；全页拖放仅拦截文件，文件夹会递归读取；**仅**点「发送」才 stage。改列表/回步骤 1 会作废在途压缩（epoch/`jobId`）。`1×text`→ETTEXTv1；否则→`processFiles`/ETBUNDL1 |
| 文字文件名规范化 | `apps/sender/src/storage/textDrafts.ts` | 仅 `normalizeDraftFilename`（IndexedDB 草稿已移除） |
| **文本类可复制** | Android `TextLike.kt` / Windows `FileNameUtil.IsTextLikeName` | 扩展名启发式（txt/md/json/csv/源码…）：单文件、打包条目、历史列表均可进 `ReceiveText` 复制/分享/存盘。ETTEXTv1 仍优先 |

| manifest 前/后处理 | `prepare-plasmo-icon.cjs` + `fix-manifest.cjs` | 前者从 icon128 生成 git-ignored 的 `assets/icon.png`（clean build 必需）；后者写入各尺寸图标/MV2 CSP/Firefox id，并兜底删 `default_popup` |
| **★网页接收端（v1.1.6）** | `apps/sender/src/pages/ReceivePage.tsx` | 接收端主页面：相机 `getUserMedia`→`requestVideoFrameCallback` 取帧（1080 全分辨率）→QR worker→receive worker。进度对齐 Android，使用 3 秒窗口解码/有效吞吐；`initWorkers` 重建时 terminate 旧 worker，`scanningActiveRef` 阻止残留捕获循环。**大文件 UX**：历史卡展示已收/总段数与缺失范围；继续时锁定 `targetRootRef`，遇其他 root/non-segmented 会 reset 并继续扫描；完成任务优先逐段校验后流式写 File System Access，兼容浏览器回退 Blob。web 入口 `apps/web/src/receiver.tsx` + `apps/web/receiver.html` |
| **接收端解压 + 解析** | `apps/sender/src/receive/{decompress,parse}.ts` | `decompressAndVerify`：NONE 原样/ZSTD 复用 sender 的 zstd Emscripten 单例（`zstdDecompress`）/XZ 用 `lzma-wasm`（**必须先 `await lzma.initWasm()` 再 `decompress`**，否则抛 `Please call initWasm()...`，曾漏掉），原始内容上限 `MAX_DECOMPRESSED_BYTES`=256 MiB（与 Rust `MAX_ORIGINAL_BYTES` 同步；wire 上限仍为 `MAX_OBJECT_BYTES`=32 MiB）+ CRC32 校验。`parseRecovered`：ETTEXTv1→text / ETBUNDL1→bundle / 否则→单文件（字节级镜像 Android/Windows） |
| **receive worker** | `apps/sender/src/workers/receive.worker.ts` | 持 `ReceiverSessionWasm` 单例，`messageQueue` 串行整个 async handler；jobId/epoch 防过期；batch 含 descriptor 用 `from_descriptor`，否则从任意数据帧低/高 64 位建立 pending session。**descriptor-v4**：`assemble_raw`→JS 解压并强制 CRC32→`raw_sha256`→规范段数/偏移/长度；随后 `receive/taskStore.ts` 在同一 IndexedDB readwrite transaction 提交 Blob+账本。未齐发 `segment` 并 `dropSession()`；全齐发 `stored-result`，不在 worker 合并根数组。坏段发可重试 `segment-error`，已有段不删；重复段可修补丢失/损坏的 IDB Blob |
| **QR decode worker（池）** | `apps/sender/src/workers/qr-decode.worker.ts` + `ReceivePage` 池管理 | **双后端**：① **FAST（M3 快路径，默认）**：自编译 ZXing-C++→WASM（`fastzxing/airferry_zxing.js/.wasm`，Emscripten 3.1.64 + `-O3 -msimd128`），吃 **Y 灰度平面**（主线程 `extractYPlane` = **canvas drawImage + getImageData + RGBA→Y**，stride 严格 = width）。**⚠️ 不要用 `VideoFrame.copyTo(I420)` 取 Y**：I420 的 Y 平面按 coded-stride(≥width)/codedWidth(≥displayWidth) 布局，`subarray(0,w*h)` + `rowStride=width` 会行错位、WASM 解不出（曾因此「扫不出来」）。canvas RGBA→Y 布局确定，虽然多一次 RGBA 转换，但正确。worker `decodeFastY` 调 `airferry_wasm_decode_multi_y`（复用 `core/zxing-decoder/airferry_zxing_core.cpp` 纯 C++，与 Windows/Android 同源）。实测合成四码 598px **10.4ms**（vs zxing-wasm ~22ms，~2×+）。**⚠️ 编译必须 `-fexceptions` + `-s STACK_SIZE=1MiB` + 固定 64MiB 内存（`ALLOW_MEMORY_GROWTH=0`）**：ZXing-C++ 在 1080p 大帧上会抛 C++ 异常，emscripten 默认 `-fno-exceptions` 会让它 trap 成 JS 数字异常（`解码失败: 638680`）→ fast 后端崩 → 「扫不出来」。开异常支持 + 大栈让 `catch(...)` 优雅跳过坏帧。② **COMPAT（回退）**：`zxing-wasm/reader` 的 `readBarcodesFromImageData`（RGBA）。**自动探测**：worker `init` 先 `loadFastBackend()`，成功 `ready{fast:true}`（main 喂 Y），失败回退 zxing-wasm `ready{fast:false}`（main 喂 RGBA）。**默认全帧解码**（`maxNumberOfSymbols:4`，码任意位置可检出）。**算法提速**：① `tryInvert:false`；② 两级 tryHarder。decode 可选 `roiGrid`（`{cols:2,rows:2}`）保留代码但**默认不启用**——固定 ROI 把真实拍摄偏移/倾斜的码切半导致难扫，故主链路走全图；`cropRgba` 仅在显式 `roiGrid` 时用。**⚠️ 无自适应 ROI 跟踪**：曾参考 Android `decodeYRegionTracked` 实现 ROI 跟踪，但 Android 实测 ROI 不可靠已回退，Web 同理走全图最稳。**ReceivePage 持 `QR_WORKER_POOL=4` 个 qr worker 池**：captureLoop 每帧派发给第一个空闲 worker（`qrBusyRef`），无空闲才丢帧——多帧跨核并行解码，镜像 Android 线程池；每个 worker 独立 zxing WASM 实例，`decoded` 后标记空闲并把 payloads 喂给单例 receive worker（ingest 仍串行）。**⚠️ 捕获循环用 `scanningActiveRef` 守卫**：`teardown`/`reset` 置 false，`startScanning` 置 true，captureLoop 开头检查——否则上次会话残留的 RVFC/rAF 回调不停止（`requestVideoFrameCallback` 的 handle 无法用 `cancelAnimationFrame` 取消），再点「开始接收」会**双循环重叠**，表现为「第二次很难扫出来」。快路径（自编译 ZXing-C++ WASM 灰度，M3）待落地 |

### 3.3 Android 扫码端

| 关注点 | 位置 | 说明 |
|--------|------|------|
| Activity / 管线编排 | `app/.../ui/ScanActivity.kt:64` | owns 管线、相机绑定、摄入线程；`onCreate` 设 `FLAG_KEEP_SCREEN_ON` 防长传息屏 |
| CameraX 生产者（非阻塞） | `app/.../scan/QrStreamAnalyzer.kt:16` | 拷贝 Y 平面入队后立即 close |
| **并行解码 + 串行摄入** | `app/.../scan/QrDecodePool.kt:27` | N worker(2-6) + `ingestLock` 串行化 |
| **v1.1.3 解码调度** | `app/.../scan/QrDecodePool.kt`（`decodeMultiTracked`） | 1.1.4 重新发布版按用户实测回退并锁定为 v1.1.3 的 worker/批摄入/全帧与 ROI 状态机；Windows C# 镜像相同模式。**全帧/ROI 重锁语义**：`multiMiss % MULTI_FULL_DECODE_EVERY == 0`——因 `multiMiss` 初值为 0 且成功后归 0，`0 % 3 == 0` 恒真，**效果是稳定成功时每帧都走 1080p 全帧 `decodeMultiY`，ROI 热路径几乎不可达**。这在理论上是"bug"（注释意图是仅 miss 时全帧），但 **release 实测表明全帧每帧的解码成功率优于 ROI 优先**（曾尝试加 `> 0` 守卫让稳定态走 ROI，A/B 对比实测更难扫），因此**有意保留现状，不要修改此判定条件**。详见 §5 第 9 条 |
| **Rust JNI 桥（Kotlin）** | `app/.../nativelib/NativeBridge.kt` | `receiverIngest` 返回 Long |
| **接收会话管理器** | `app/.../scan/ReceiverSessionManager.kt:17` | 仅从描述符初始化；`IngestStatus.unpack`(line 68) |
| 帧头解析（Kotlin 侧） | `app/.../scan/ReceiverSessionManager.kt:99` | `parseHeader`：60B 大端 |
| ZXing JNI 桥（Kotlin） | `app/.../scan/ZxingDecoder.kt` | 单码/多码/ROI 解码 |
| **ZXing-C++ JNI（native）** | `app/src/main/cpp/scan_jni.cpp` | 完整保留 v1.1.3 解码实现（TryHarder/TryInvert、全帧/ROI/多码打包）；CMake 仍把 ZXing v3.0.2 固定到不可变 commit |
| 多文件包解包 | `app/.../scan/BundleParser.kt` | 恢复后拆包（ETBUNDL1） |
| **文字载荷解析** | `app/.../scan/TextParser.kt` | `isText`/`parse`（ETTEXTv1 → UTF-8）；字节级镜像 TS `text.ts` 与 C# `TextParser.cs` |
| **文本类启发式** | `app/.../scan/TextLike.kt` | 扩展名 + `decodeUtf8Strict`；与 Windows `FileNameUtil.IsTextLikeName` 对齐 |
| **内容库（映射去重）** | `app/.../scan/ContentStore.kt` | `files/store/blobs/<hh>/<sha256>` + `index.json`；同内容一份；索引损坏会备份并停止写入。**大文件**：`putFile` 流式哈希后原子复制/发布 blob，只有索引成功才删除任务源；`SegmentAssembler.kt` 按规范偏移写 `.partial`、fsync 后原子位图、恢复时逐段重验 SHA。Windows 的 `Bundle/ContentStore.cs` / `SegmentAssembler.cs` 同构 |
| 文件导出名 / MIME | `app/.../scan/FileTransfer.kt` + `AirFerryFileProvider.kt` | ContentStore 实体是无扩展名 SHA-256；四参数 URI 把逻辑名写入 `DISPLAY_NAME`，自定义 provider 覆盖 `getType`/`getTypeAnonymous` 返回逻辑 MIME；SAF 同样按扩展名选择 MIME，避免目标应用落成 `.bin` |
| UI | `app/.../ui/{ReceiveDetail,ReceiveText,ReceiveBundle,FileList,Settings}Activity.kt` | 详情/文字/列表/设置。`recoverAndStage` 写入 ContentStore；Android 分享直出 blob，但 URI 携带逻辑文件名和 MIME |
| **文字接收页（可复制/分享/存 .txt）** | `app/.../ui/ReceiveTextActivity.kt` | ETTEXTv1 或文本类；历史列表按 entry.kind / TextLike 打开 |

### 3.4 Windows 扫码端

| 关注点 | 位置 | 说明 |
|--------|------|------|
| **Rust C ABI 桥（P/Invoke）** | `apps/windows/AirFerry.Windows/Native/NativeBridge.cs` | 11 个 `[DllImport]` 声明，对标 Android `NativeBridge.kt`。⚠️ 每个**必须钉死 `EntryPoint = "airferry_*"`**：Rust `cffi.rs` 导出 snake_case 符号，而 P/Invoke 默认按 C# 方法名（PascalCase）查找——漏写会让首个 native 调用抛 `EntryPointNotFoundException`，且 CI 协议层单测（纯 C# 逻辑，不触达 DLL）无法发现。对比 JNI 由 JVM 自动解析 `Java_<class>_<method>` 名，Windows 走纯 C ABI 无此机制 |
| **接收会话管理器** | `apps/windows/AirFerry.Windows/Scan/ReceiverSession.cs` | lazy init from descriptor + mismatch re-init（镜像 Kotlin） |
| 帧头解析 | `apps/windows/AirFerry.Windows/Scan/FrameHeader.cs` | 60B 大端，magic/version/session_id hi+lo |
| IngestStatus 位域解析 | `apps/windows/AirFerry.Windows/Scan/IngestStatus.cs` | `.Unpack(u64)`，位布局与 Rust/Kotlin 一致 |
| **v1.1.3 模式 + 串行摄入** | `apps/windows/AirFerry.Windows/Scan/QrDecodePool.cs` | 镜像 Android v1.1.3：N worker（2–6）、容量 `worker+2`、4 符号批摄入、相同全帧/ROI 状态机（`% 3 == 0` 使稳定成功时每帧全帧，有意保留——实测优于 ROI 优先，见 §5 第 9 条）；额外保留池化 Gray 与安全停止 |
| ★**设备枚举（摄像头+采集卡）** | `apps/windows/AirFerry.Windows/Scan/DeviceEnumerator.cs` | DirectShow `DsDevice`，两类设备统一枚举 |
| 视频采集 + 预览 | `apps/windows/AirFerry.Windows/Scan/VideoCapture.cs` + `PreviewFrame.cs` | 单个 OpenCvSharp DirectShow 句柄：生产线程一次读取后 BGR→Gray 送解码池，并以 15fps 将池化 BGR24 快照交给 WPF；UI 线程不读设备，快照由消费端 `Dispose` 归还 `ArrayPool` |
| **Windows ZXing-C++ C ABI** | `apps/windows/native/` + `core/zxing-decoder/` | Windows 薄导出层；全帧与 ROI 都使用 Android v1.1.3 相同的 TryHarder/TryInvert，固定 ABI 版本和 packed 结果布局 |
| QR 结果解析 | `apps/windows/AirFerry.Windows/Scan/ZxingDecoder.cs` + `Native/NativeZxingBridge.cs` | P/Invoke 调用 `airferry_zxing.dll`，校验长度/上限并在 finally 释放 native buffer |
| 多文件包解包 | `apps/windows/AirFerry.Windows/Bundle/BundleParser.cs` | ETBUNDL1（字节级镜像 Kotlin BundleParser.kt） |
| 分享导出 | `apps/windows/AirFerry.Windows/Bundle/ShareExport.cs` | 不暴露无扩展名 hash blob；生成带逻辑名的临时副本、写 MOTW，并在启动/下次分享时清理超过 24 小时的受控 GUID 目录 |
| **文字载荷解析** | `apps/windows/AirFerry.Windows/Bundle/TextParser.cs` | `IsText`/`Parse`（ETTEXTv1 → UTF-8）；字节级镜像 TS `text.ts` 与 Kotlin `TextParser.kt`。有跨平台单测 `TextParserTests.cs` |
| 文件名 sanitize | `apps/windows/AirFerry.Windows/Bundle/FileNameUtil.cs` | + Windows 保留名（CON/PRN/COM1-9）处理 |
| 主状态机 | `apps/windows/AirFerry.Windows/ViewModels/ScanViewModel.cs` | 编排单源采集→解码池/预览→会话→恢复→落盘；7Hz UI 快照展示 3 秒窗口解码/有效吞吐和源/传输大小。停止以 session epoch 作废旧 UI 回调；前台最多等 2 秒，超时则保留资源并由单一后台任务按 producer→recovery→workers→native/camera 顺序安全释放，完成前禁止重启；`RecoverAndStage` 按 text→bundle→单文件顺序分流。**descriptor-v4 分段**：`RecoverAndStageCore` 开头检测 `session.IsSegmented()` → `HandleSegmentedTransfer` 存入 `Bundle/SegmentAssembler`，未到齐返回 null（保持扫描，显示「k/N 段已收」），全部到齐 `Finish()` 后 `ContentStore.PutFile` |
| UI | `apps/windows/AirFerry.Windows/Views/*.xaml` | Scan/DeviceSelect/ReceiveDetail/ReceiveText/ReceiveBundle/FileList/Settings；历史任务显示缺失段范围，“继续恢复”把 root id 经 DeviceSelect/ScanView 传入 ScanViewModel，非目标传输会被忽略；Scan 的安全停止等待移出 Dispatcher |
| **文字接收页（可复制/保存 .txt）** | `apps/windows/AirFerry.Windows/Views/ReceiveTextView.xaml` | `Clipboard.SetText` + SaveFileDialog UTF-8；RecoveryResult 新增 `Text`/`IsText` |

### 3.5 网页发送端

> **功能与浏览器扩展（§3.2）完全一致**，**直接复用 `apps/sender/src/` 全部源码，无代码重复**。下表只列 web 端特有的接入点；业务逻辑（页面/组件/worker/wasm）见 §3.2。

| 关注点 | 位置 | 说明 |
|--------|------|------|
| 薄入口（mount sender App） | `apps/web/src/main.tsx` | `import App from "@/options"`（alias → `../sender/src/options.tsx`）+ `createRoot().render(<App/>)`。仅此一个源文件是 web 专有 |
| Vite 配置（跨工程 alias） | `apps/web/vite.config.ts` | `resolve.alias: { "@/": "../sender/src/" }` —— sender 源码内部所有 `@/` 引用全部重定向到真实文件；worker 的 `new URL("./workers/...", import.meta.url)` 跨工程解析 |
| WASM 前置校验 + zstd 拷贝 | `apps/web/scripts/prepare-wasm.cjs` | `predev`/`prebuild`/`prebuild:standalone` 跑：①持锁校验 sender `wasm-pkg-simd` 并原子复制到 web 私有 `wasm-pkg/`；②拷 zstd WASM 到 `public/` |
| lzma-wasm 提取 | `apps/web/scripts/extract-lzma-wasm.cjs` | 同 sender 版（base64 → 物理 .wasm，解 Rollup 静态分析）。`postinstall` 跑 |
| **环境自适应接入点** | `apps/sender/src/options.tsx` | 三环境共用同一份源码，按运行环境分流：① 扩展（`chrome.runtime.getURL`）② 网页普通版（`document.baseURI` fetch）③ **单文件版**（`globalThis.__AIRFERRY_STANDALONE__` 标志 → Blob URL worker + base64 WASM）。worker 初始化、zstd 预加载两处都做三路判断，扩展/普通版行为不变 |
| **base64 工具（单文件专用）** | `apps/sender/src/wasm/base64.ts` | `base64ToBuffer(b64)`：`atob` + Uint8Array，主线程/worker 共用。单文件版运行时把内联 base64 WASM 解码成 buffer，喂给 `init(buffer)`/`initZstdFromBytes` |
| 相对路径部署 | `apps/web/vite.config.ts` `base:"./"` | 产物 `dist/` 资源用 `./assets/...`，可部署到任意子路径（GitHub Pages 的 `user.github.io/repo/` 等） |
| **单文件构建配置** | `apps/web/vite.standalone.config.ts` + `apps/web/src/standalone.tsx` | IIFE bundle + worker 单独 ES chunk。`standalone.tsx` mount 前设 `__AIRFERRY_STANDALONE__=true` 触发单文件路径 |
| **单文件后处理** | `apps/web/scripts/build-standalone.cjs` | 把 Vite 多文件产物内联成单个 `dist-standalone/index.html`：注入 worker 源码（`__WORKER_CODE__`）+ zstd base64（`__WASM_ZSTD__`）+ process polyfill；处理 `</script>` 转义 + `import.meta.url` 替换 |

---

## 4. 调试速查表（症状 → 首查位置）

| 症状 | 首查 | 可能原因 |
|------|------|---------|
| 点扩展图标仍弹小窗（而非直跳 options） | `apps/sender/src/background/index.ts` + `fix-manifest.cjs` | manifest 残留 `default_popup`（popup 优先级高于 `onClicked`）；或 `src/popup.tsx` 误被恢复（Plasmo 据此重新注入 popup）。删 popup.tsx + `fix-manifest.cjs` 兜底删 `default_popup` 是触发 `onClicked` 的前提 |
| 文字传输收端当文件保存 | `TextParser` + `TextLike`/`IsTextLikeName` + `options.onSend` | 仅「列表里只有 1 条 text」才 ETTEXTv1。混发文字是 `.txt` 条目——点开仍可进 `ReceiveText`（扩展名启发式）。非法 UTF-8 的「文本扩展名」回退文件页。若单条文字也落成文件，查是否误走了 `itemsToFiles` |
| 发送文字时自定义文件名不生效 | `options.onSend` + `compress.worker processText` + Android/Windows ETTEXTv1 落盘 | 纯文字须 `postMessage({ text, name })`，worker 用 name 作 `displayName`/session；收端 ETTEXTv1 用描述符 `fileName()`，勿写死 `文字消息.txt` |
| 拖文件就跳进参数页 | `FileSelectPage` + `options.onSend` | 选择只改 pending `items`；必须点「发送」才 stage。若又变成选完即跳，检查是否误把 `onItemsChange` 接回了立即压缩 |
| 收端卡「恢复中 0%」 | `descriptor::parse_payload` + `ReceiverSession::ingest` | OTI 未确认（`meta_confirmed=false`）；描述符解析失败 |
| 收端崩溃（扫码即崩） | `ObjectMeta::validate` + receiver 摄入守卫 | 恶意/越界坐标未在入解码器前拦截（panic=abort 下致命） |
| 进度越往后越慢 | `sender::next_symbol_id` | 必须是「源一遍→持续新鲜修复」，不能循环有限计划；ESI 达协议上限时返回错误 |
| 帧被静默丢弃 | `Frame::from_bytes` | magic/version/双层 CRC 校验失败 |
| 恢复出空文件 | `descriptor` v2/v3 消歧 + `compressed_size_known` | v3 尾部被误读为 v2 补零 |
| 解压 OOM / 崩溃 | `compress.rs:107 decompress_with_limit` | 炸弹上限未封顶到 `original_size` |
| JNI 摄入竞态/UAF | `QrDecodePool.ingestLock` + `ScanActivity` `ingestStopped` | 句柄非线程安全，未串行化 |
| 摄像头读不出码 | `qr_render::encode`（版本选择）+ `scan_jni` TryHarder | 版本过高致模块过密；或 16KiB 页未对齐致 .so 加载失败 |
| 长时间扫码后息屏/锁屏 | `ScanActivity.onCreate` `FLAG_KEEP_SCREEN_ON` | 扫码页必须 `window.addFlags(FLAG_KEEP_SCREEN_ON)`；仅窗口可见时生效，离开 Activity 自动恢复系统超时 |
| 解码速率/用时速度不“即时” | `ScanActivity` `rateSamples` / `RATE_WINDOW_MS` | 应用 ~3s 滑动窗口 `Δ符号/Δt`（及 `×symbolSize`），勿再用全程 `wireBytes/elapsed` 平均；完成时清零 |
| 压缩总是走 raw（100%） | `compress.ts` `initZstdFromBytes` | worker 内 zstd WASM 未加载成功（主线程应仍 post `wasm-init`；缺 zstd 时允许 raw，不应卡死） |
| 点发送后永久「正在准备」 | `compress.worker` `ready` + `options` `wasm-init` | 主线程未 post `wasm-init`（含失败时 `zstd: null`）；或 worker 仍硬等 zstd 成功才 ready |
| **网页端压缩走 raw（100%）** | `apps/web/public/wasm-zstd.wasm` + `prepare-wasm.cjs` | `public/wasm-zstd.wasm` 缺失（未跑 `prebuild`），worker fetch 404 → 回退 raw。重新跑 `npm run build`（会触发 `prebuild`→prepare-wasm） |
| **网页端启动报 transfer_engine.js 找不到** | `apps/sender/wasm-pkg-simd/` | web 从 sender 现代产物复制自有快照；首次构建前需 `cd apps/sender && npm run wasm`。`prepare-wasm.cjs` 会校验并报清晰错误 |
| **`release`/`dist` 产物缺 web zip** | `apps/web/dist/` | `pack_dist` 用 warn 模式（非中断）：`apps/web/dist/` 缺失时跳过 web zip。先跑 `./scripts/build-all.sh web` 再 `dist`/`release` |
| 扩展构建缺 WASM | `apps/sender/wasm-pkg-legacy/` 或 `wasm-pkg-simd/` | 单独跑 `npm run build:chrome-mv3` 等单目标脚本前忘了先 `npm run wasm`（双产物缺失） |
| APK 缺 native 库 | `jniLibs/arm64-v8a/libtransfer_engine.so` | 手动跑 `./gradlew` 而未经 `build-all.sh`（后者已自动先跑 cargo-ndk） |
| Windows 端 DllNotFoundException | `apps/windows/AirFerry.Windows/runtime/{transfer_engine,airferry_zxing}.dll` | 手动跑 `dotnet build` 而未经 `build-windows.ps1`；后者会自动构建、测试并复制 Rust 引擎与 ZXing-C++ 两个 DLL |
| Windows 端 EntryPointNotFoundException | `apps/windows/AirFerry.Windows/Native/NativeBridge.cs` | `[DllImport]` 缺 `EntryPoint`：P/Invoke 默认按 PascalCase C# 方法名查找，但 Rust `cffi.rs` 导出的是 snake_case `airferry_*`。**首个 native 调用即抛**（热路径 `ReceiverIngest` → 扫第一个码就崩），CI 协议层单测测不到。修复：每个声明钉死 `EntryPoint = "airferry_receiver_ingest"` 等 |
| Windows 端设备打不开 | `DeviceEnumerator.cs` + `VideoCapture.cs` | 设备被其他程序独占；或 DirectShow 驱动问题（换 MSMF 后端或换设备） |
| Windows 端扫码即崩（Rust panic） | `cffi.rs` + `ReceiverSession.cs` | 同 Android：恶意/越界输入应在 Rust `Frame::from_bytes` 拦截；检查 `panic=abort` 下 DLL 是否正确编译 |

---

## 5. 文档与代码偏差清单（AI 高风险，务必注意）

> 调研发现多处文档滞后于代码。**修改/引用时一律以代码为准**。下面是已确认的偏差：

1. **JNI 签名已在本轮审计同步**：`docs/api.md`、`docs/SPEC.md` 与 `NativeBridge.kt` 现统一为 `receiverIngest(...): Long`、`receiverProgressJson(...): ByteArray?`、`receiverAssembleBytes(...): ByteArray?`。后续修改 JNI 时须三处同步；位布局见 [`docs/SPEC.md`](docs/SPEC.md) §JNI ingest 状态字。

2. **压缩参数（两端不同，勿混为一谈）**：
   - **浏览器发送端**（`apps/sender/src/wasm/compress.ts:55-64`）：Zstd **level 1**、Xz **level 9**、early-exit 阈值 **70%**（`e10079d`/`c2ae4a2` 提交已调；三算法选优 raw/zstd/xz）。
   - **Rust 核心库**（`core/qr-protocol/src/compress.rs:24,53`）：Zstd **level 22**（`DEFAULT_LEVEL`）、Xz **level 6 + EXTREME**（`XZ_PRESET`）。
   - 两套编码默认值不同是**有意的**：浏览器发送端追求启动快（Zstd Lv1），Rust 原生压缩 API 追求压缩率（Zstd Lv22；XZ Lv6+EXTREME，见 `compress.rs:41-49`）。接收端按标准流解压，不依赖编码级别。引用压缩参数时**必须分清 TS 与 Rust 默认值**，不要合并描述。

3. **版本号/Release 混用（历史教训）**：README/dist/workflow 曾出现 v1.0.0/v1.0.1/v1.1.0 漂移（含 `windows.yml` 的 `VER` 滞后）。**当前权威版本 `1.1.6`**（versionCode=11）。改版本时按 §2.8 第 5 条 + §2.9 全表同步，勿只改 `package.json`。

4. **`derive_meta_from_totals` 已废弃**：`receiver.rs` 内仍保留 JNI/ABI 兼容符号，**新代码勿调用**（其 OTI 构建在大文件上会 assert）。现代路径：从描述符帧拿权威 OTI。

5. **wasm-bindgen 双轨制（MV2=0.2.92 旧版 / MV3=0.2.125 新版）**：`core/transfer-engine/Cargo.toml` **默认仍钉死 `=0.2.92`**（js-sys/web-sys `=0.3.69`），**不要**改回宽松的 `"0.2"` / `"0.3"`。`build-wasm.cjs` 把 workspace 复制进独立临时目录，只在副本中升级到 0.2.125/0.3.102、重算副本 lockfile 并编译；源码 Cargo 文件从不写入。`.wasm-build.lock` 串行化发布目录切换。
   - **为什么是双轨**：wasm-bindgen 0.2.93+ 默认开 reference-types proposal（`externref` 值类型），仅 Chrome 96+ 支持；Chrome 87/88 会在 `WebAssembly.instantiateStreaming()` 报 `CompileError: invalid value type 'externref'`。Chrome 87 是 MV2 的兼容目标。MV3 同时启用新版 externref（JS↔WASM 引用传递）+ SIMD（`+simd128` target-feature）。SIMD 本身与 wasm-bindgen 版本正交——0.2.92 也能开——但新版 externref 必须升 wasm-bindgen，MV3 默认把两者一起给。
   - **⚠️ 实测性能结论（不要据此期待提速）**：对 RaptorQ + Frame + QR RS + 矩阵全链路的 Node benchmark（T=1400、5000 帧）：
     - **SIMD vs 标量**：约 0.95×（**轻微变慢**）。`raptorq` / `fast_qr` 均无 wasm32 SIMD intrinsics；`+simd128` 主要增大 wasm。
     - **新版 wasm-bindgen externref**：对 `&mut [u8]` 传递无明显收益。
     - **原真实瓶颈 QR 编码已大幅缓解**：旧 `qrcode` 单帧占主导，现 `fast_qr` ~7-9×。剩余方向：降 symbol size、并行编码（需 SAB+COOP/COEP）、或 RaptorQ GF(256) SIMD。
   - **那为什么还保留双产物 + SIMD**：① MV2 兼容老 Chrome + MV3 新工具链；② 为未来 SIMD 库铺路。
   - **产物分流**：`wasm-pkg-legacy/`（MV2）+ `wasm-pkg-simd/`（MV3）；`build-all.cjs` 持锁按目标 swap 到 sender 的 `wasm-pkg/`。web 在同一锁下复制 simd 到自有目录，避免并发读写竞态。
   - **升级现代版**：改 `build-wasm.cjs` 顶部 `MODERN` 三元组，跑 `npm run wasm`，确认两个变体及 web/扩展构建。
   - **症状定位**：旧 Chrome 报 `invalid value type 'externref'` → MV2 误用了新版 wasm-bindgen。

6. **WASM 零拷贝 API**：热路径用 `next_qr_scratch` 写入会话内预留缓冲，随即用 `qr_scratch_view` 借用 WASM 内存；`drawMatrix` 必须在下一次可变 WASM 调用前完成。这样避免 JS→WASM copy-in、WASM→JS copy-out 和逐帧分配。

7. **网页端（apps/web）零代码复用 sender**：Vite alias `@/ → ../sender/src/` 跨工程 import；环境自适应见 `options.tsx`（chrome / document.baseURI / standalone）与 worker 内 zstd fallback。**不要在 web 里 fork 业务代码**。

8. **Parcel dev-server 安全回移**：Plasmo 0.90.5 依赖 Parcel 2.9.3，直接 override 到 2.16.x 会触发私有 core adapter / BundleGraph 不兼容。`apps/sender/scripts/patch-parcel-dev-server.cjs` 在 postinstall 中把通配 CORS 改为只允许 Chrome/Firefox 扩展 origin，并钉 `@parcel/core=2.9.3` 防 npm 混装两套 core。上游版本升级时补丁会 fail closed，必须重审；不要为追求 `npm audit` 数字而删除回移或强升 Parcel。

9. **ROI 调度状态机：全帧每帧是有意行为，不要修改（实测教训）**：Android `QrDecodePool.kt` + Windows `QrDecodePool.cs` 的全帧重锁判定为 `multiMiss % 3 == 0`——因 `multiMiss` 初始为 0 且每次成功后重置为 0，`0 % 3 == 0` 恒真，**效果是稳定成功时每帧都走 1080p 全帧 `decodeMultiY`/`ReadBarcodes`，ROI 热路径（`decodeMultiYTracked`）几乎不可达**。这在代码分析层面看似 bug（注释意图是仅 miss 时全帧；单码路径用 `incrementAndGet() % N` 正确实现了该意图），但**两版 release APK A/B 实测结论：全帧每帧的解码成功率明显优于 ROI 优先**。推测原因：手持场景下帧间抖动 + 多码布局变化超出 35% margin 的 ROI 窗口容忍度，导致 ROI 路径频繁 miss→全帧→miss→全帧的振荡反而不如直接每帧全帧稳定。**结论：有意保留 `multiMiss % 3 == 0` 现状，不要加 `> 0` 守卫或任何等价修改。** 若未来要重新尝试 ROI 优先，必须同时解决：① 增大 `TRACK_MARGIN`（当前 0.35f 可能不够）或改用自适应窗口；② 多码模式下部分码 miss 时的降级策略；③ **必须 release 实测对比，不能仅凭代码分析下结论**。

10. **WASM 接收端导出 + ingest 打包三端共享 + 解压 stub fail-closed（v1.1.6）**：为网页接收端铺路的底层改动，三点：
    - **新增 `ReceiverSessionWasm`**（`core/transfer-engine/src/wasm.rs`）：此前 WASM 只导出发送端（`SenderSessionWasm` + `encode_qr`）。现补齐接收端：`from_descriptor`（校验完整帧 CRC + 描述符 flag → 锁 session id → 摄入使 meta confirmed）/ `new(sid_lo,sid_hi)` 缓存引导 / `ingest` 返回 packed u64 / 元数据 getters / `assemble_raw`（**只重组不解压**）/ `progress_json`。**不暴露 `assemble_result`**——wasm32 下解压 fail-closed（见下），网页接收端走 `assemble_raw` + JS 侧 zstd/xz WASM 自解压 + 校验 CRC32。逻辑级单测在 `core/transfer-engine/tests/wasm_receiver.rs`（6 个，覆盖描述符引导/坏帧拒绝/packed golden/乱序恢复/截断）。
    - **ingest 打包抽共享模块**：原 `jni.rs` 与 `cffi.rs` 各有一份逐字相同的 `pack_ingest_status` + `INGEST_ERROR`（靠注释 + 测试维系一致）。现抽到 `core/transfer-engine/src/ingest_status.rs`，JNI/C ABI/WASM 三端统一引用，杜绝漂移。位布局不变（bit0 complete / bit1 accepted / bits8..23 streak / bits32..63 received_symbols / `received_symbols==u32::MAX` 为错误哨兵）。`docs/SPEC.md` §7 已改为「三端共享」权威描述。
    - **wasm32 解压 stub 改 fail-closed**（`core/qr-protocol/src/compress.rs`）：原 stub 对所有 compression 都 identity 返回（注释「receiver never runs in the browser」）——网页接收端落地后这会静默把压缩字节当原文件。现 `COMPRESSION_NONE` 原样返回（正确），`COMPRESSION_ZSTD`/`COMPRESSION_XZ` 返回 `Err`（fail-closed）。`qr-protocol::Error::Compress` 变体原先 `#[cfg(not(target_arch="wasm32"))]`，已去掉 cfg gate 让 wasm32 也能构造该错误。网页接收端不经此路径（用 `assemble_raw`），native/Android 解压不受影响。

11. **网页接收端 M2 兼容路径落地（v1.1.6）**：在 M1（WASM 接收端导出）基础上，落地端到端可用的网页接收端（除真机摄像头 A/B 外）：
    - **接收端源码组织**：`apps/sender/src/receive/{decompress,parse}.ts`（解压+解析，被 web 复用）、`apps/sender/src/workers/{receive,qr-decode}.worker.ts`（串行 ingest / ZXing 解码 worker）、`apps/sender/src/pages/ReceivePage.tsx`（相机+取帧+worker 编排+结果分流 UI）。web 入口 `apps/web/src/receiver.tsx` + `apps/web/receiver.html`。
    - **解压路径不走 `@foxglove/wasm-zstd` JS 包**：该包内部 `require("./wasm-zstd.wasm")` 会触发 Vite「ESM integration proposal for Wasm」报错（worker 打包时静态分析到）。改为复用 sender `compress.ts` 已有的 Emscripten 手动实例化单例——给它补绑 `_decompress`（wasm 的 `j` 导出，原本只绑 compress 相关），导出 `zstdDecompress` + `ensureZstdLoaded`，receive worker 直接用。同一个 zstd wasm 文件、同一个 Emscripten 实例，不重复加载。
    - **Vite 多页面 + worker ES 格式**：`vite.config.ts` 加 `rollupOptions.input:{index,receiver}` 双入口、`worker.format:"es"`（worker 含 `import("zxing-wasm/reader")`/`import("lzma-wasm")` dynamic import 产生 code-split，默认 iife 不支持）。`zxing-wasm`/`lzma-wasm` 装在 web node_modules，用 alias 指向其 dist 入口（sender/node_modules 无此包，否则 worker rollup 解析失败）。`optimizeDeps.exclude` 加 `@foxglove/wasm-zstd`（虽不再直接 import，保留排除以防误预打包）。
    - **zxing-wasm 兼容路径**：M2 用 `zxing-wasm/reader` 的 `readBarcodesFromImageData`（RGBA 全帧，无手动 ROI tracker）。`zxing_reader.wasm` 由 `prepare-wasm.cjs` 复制到 `public/` 供 worker 运行时 fetch。**⚠️ 默认 locateFile 走 jsDelivr CDN**：zxing-wasm 的 `share.ts` 默认 `locateFile` 把 `*_*.wasm` 解析到 `https://fastly.jsdelivr.net/npm/zxing-wasm@...`，离线/CDN 被墙时 `getZXingModule` 加载失败（表现为 `[qr] WORKER ERROR: @undefined:undefined` 或 init 超时）。`qr-decode.worker.ts` 的 `ensureReady` 传 `locateFile: (f) => new URL("../" + f, self.location.href)` 覆盖为**本地**加载（worker 在 `assets/`，wasm 在站点根 `../zxing_reader.wasm`，相对路径兼容子路径部署）。standalone 版不受影响（走 `Module.wasmBinary` 注入，跳过 fetch/locateFile）。**M3 自编译 ZXing-C++ WASM 快路径已落地**：`core/zxing-decoder/{zxing_wasm.cpp,CMakeLists.txt,link-wasm.sh}` + `scripts/build-fastzxing.sh`（Emscripten 3.1.64 编译 ZXing-C++ v3.0.2 固定 commit `8dd1cf5...`，`-O3 -msimd128`，产物 `apps/sender/src/fastzxing/airferry_zxing.js/.wasm`，约 700 KB / gzip 331 KB）。构建需 Emscripten（`source ~/emsdk/emsdk_env.sh`）；复用 Android 缓存 zxing-src 用 `./scripts/build-fastzxing.sh --use-cache`，否则 FetchContent 下载。
    - **测试**：`core/transfer-engine/scripts/e2e_receiver.mjs` Node 端到端验证（41 断言，覆盖单文件/ETTEXTv1/ETBUNDL1/小文件全链路：帧生成→ingest→assemble_raw→解析）。真机摄像头 A/B（M6）需硬件，未覆盖。
    - **zstd decompress 导出扩展**：`compress.ts` 的 `ZstdWasmModule` 接口加 `_decompress`，`instantiateZstd` 绑定 `instance.exports.j`。**注意**：这是 `@foxglove/wasm-zstd` 的 `wasm-zstd.wasm`（含 decompress 导出），不是 sender 压缩路径新引入的——该 wasm 本就有 decompress，只是 compress-only 路径没绑。

---

## 6. 架构关键不变量（速览，详见 [`docs/SPEC.md`](docs/SPEC.md)）

- **同一份 Rust 源码** → 三个 FFI 目标：浏览器 `wasm32-unknown-unknown`（wasm-bindgen）、Android `aarch64-linux-android`（`#[no_mangle] extern "system"` JNI）、Windows `x86_64-pc-windows-msvc`（`#[no_mangle] extern "C"` C ABI，供 .NET P/Invoke）。编解码数学一致。
  - **浏览器扩展（apps/sender）与网页端（apps/web）共用 TS 源码和构建出的 WASM 变体**。MV2 选 legacy，MV3 与 web 选 simd/modern；sender 的 `wasm-pkg/` 是当前扩展目标快照，web 有独立 `apps/web/wasm-pkg/` 快照。两者由共享锁协调发布。web 通过 Vite alias 跨工程 import，**不单独编译 Rust、不复制业务代码**。
- **帧格式**：`[Header 60B][Payload T][Footer 4B]`，大端，magic `0x4554`，version 1，双层 CRC32；T=symbol_size（浏览器默认 1400——`DEFAULT_CONFIG.symbolSize`，核心库默认 1024——`Config::default()`）。
- **会话 ID**：FNV-1a 128-bit（name/size/mtime/指纹），确定性 → 断点恢复。Rust 与 TS 实现必须位一致。
- **喷泉码发射**：源符号跨块轮询发一遍 → 持续新鲜修复符号（ESI 单调递增、不重复）；每块到 24 位 ESI 上限时明确停止，绝不回绕。进度近似线性，无 coupon-collector 拖尾。
- **接收端安全生命线**：`panic = "abort"` 构建，任何 panic = 进程崩溃。`ObjectMeta::validate` + `decompress_with_limit` 是把恶意/越界输入挡在解码器前的防线。
- **线程模型**：原生 receiver 句柄**非线程安全**。Android 用一把 `ingestLock` 串行化所有摄入；ZXing 解码在 N 个 worker 上并行。

---

## 7. 约定

- **语言**：文档、提交信息均用中文；代码注释中英混合（Rust 偏英文 doc-comment，TS 偏英文）。
- **构建 profile**：`release` 用 `opt-level="z"` + LTO + `panic="abort"`（求小体积）；但热路径 crate（raptorq-core / qr-protocol / raptorq / fast_qr / crc32fast / transfer-engine）单独提升到 `opt-level=3`（见 `Cargo.toml`）。
- **不提交产物**：`target/`、`wasm-pkg/`、`build/`、`dist/`、`*.so`、`*.apk`、`*.pem`、`*.keystore` 均在 `.gitignore`。
- **修改帧/协议字段**：两端（Rust + TS + Kotlin）必须同步；更新 [`docs/SPEC.md`](docs/SPEC.md) 的位级规格。
- **改码即改文档（AI 硬性收尾）**：每次改动代码后，凡是影响文档中引用过的**事实**——常量值、默认值、`file:line` 行号、函数签名、帧/字段布局、文件路径、目录结构、构建命令、版本号——都必须在**同一个提交**里回写对应文档（AGENTS.md 的 §3 导航 / §4 调试表 / §5 偏差清单，或 `docs/SPEC.md` 的权威源/速查表，或具体 `docs/*.md`）。改了哪一端，就核对该端在文档里的所有引用点。提交前自检：「本次改的符号/常量/路径，在文档里被引用过吗？被引用的地方还成立吗？」**不更新文档的代码改动视为未完成**。这是防止文档再次漂移的唯一手段，也是上一轮 SPEC.md/AGENTS.md 互相矛盾（浏览器默认 512 vs 1400）的根本成因——代码改了，文档没跟上。

## 8. 权威文档索引

| 主题 | 文档 |
|------|------|
| 跨端契约（位级不变量） | [docs/SPEC.md](docs/SPEC.md) |
| 协议规范 | [docs/protocol.md](docs/protocol.md) |
| 帧格式 | [docs/qr-frame-format.md](docs/qr-frame-format.md) |
| RaptorQ 参数 | [docs/raptorq-params.md](docs/raptorq-params.md) |
| 系统架构 | [docs/architecture.md](docs/architecture.md) |
| 数据流 | [docs/data-flow.md](docs/data-flow.md) |
| API 参考 | [docs/api.md](docs/api.md) |
| 构建指南 - 浏览器 | [docs/build-browser.md](docs/build-browser.md) |
| 构建指南 - 网页端 | [docs/build-web.md](docs/build-web.md) |
| 构建指南 - Android | [docs/build-android.md](docs/build-android.md) |
| 构建指南 - Windows | [docs/build-windows.md](docs/build-windows.md) |
| 开发环境搭建 | [docs/dev-setup.md](docs/dev-setup.md) |
