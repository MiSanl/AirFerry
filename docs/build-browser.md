# 浏览器扩展构建说明 (Browser Extension Build)

## 前置条件

- Node.js ≥ 18
- npm（或 pnpm）
- Rust + wasm-pack（见 [开发环境搭建](dev-setup.md)）
- 打包发布产物（可选）：macOS 上安装的 Google Chrome，用于签名 `.crx`

## 构建 WASM 核心（双产物）

```bash
cd apps/sender
npm run wasm
```

此命令（`scripts/build-wasm.cjs`）编译 `core/transfer-engine` 为 WebAssembly **两份产物**：

| 产物目录 | wasm-bindgen 版本 | 特性 | 供哪个目标 |
|---------|------------------|------|-----------|
| `wasm-pkg-legacy/` | `=0.2.92`（默认锁定） | 标量、无 externref（Chrome 87+ 可加载） | MV2（`chrome-mv2` / `firefox-mv2`） |
| `wasm-pkg-simd/` | `=0.2.125`（隔离副本升级） | 标量 + externref（Chrome 96+ / FF 116+；`simd` 为历史名） | MV3（`chrome-mv3` / `firefox-mv3`） |

> **工作树隔离**：脚本把 workspace 复制到临时目录，只在副本中升级 wasm-bindgen 并重算 lockfile；源码 `Cargo.toml` / `Cargo.lock` 从不写入。`.wasm-build.lock` 还会阻止并发构建互相覆盖发布目录。详见 `apps/sender/scripts/build-wasm.cjs`。

> **关于 SIMD 的实测结论**：旧 `+simd128` 构建对当前 wasm32 热路径**无性能收益**（实测 0.95×，反而因 wasm 变大略慢），并在部分加固/虚拟化 Chromium 中无法加载，因此已移除。双产物都是标量；差别仅是 MV2 的旧 wasm-bindgen 与 MV3/Web 的新 wasm-bindgen/externref。`simd` 目录和命令名为兼容现有构建链保留。

> `npm run build` 已内嵌此步骤，通常无需单独跑 `npm run wasm`。

> **Plasmo/Parcel dev-server 安全补丁**：Plasmo 0.90.5 固定 Parcel 2.9.3；单独升级 Parcel 会破坏其私有 core adapter。`postinstall` 因此运行 `patch-parcel-dev-server.cjs`，把有风险的通配 CORS 回移为仅允许 `chrome-extension://` / `moz-extension://` 来源。`npm audit` 只按上游版本元数据判断，仍会列出该已回移修复的 4 条 moderate 链式记录；生产依赖审计为 0。升级 Plasmo/Parcel 时脚本会因版本不匹配硬失败，要求人工复核并删除补丁。

## 构建扩展

### 全部目标（推荐）

```bash
cd apps/sender
npm run build
```

一次性构建全部 4 个目标，产物在 `apps/sender/build/`：

| 目标目录 | 支持浏览器 |
|---------|-----------|
| `chrome-mv3-prod` | Chrome / Edge（MV3，Chrome 96+ / Edge 96+） |
| `chrome-mv2-prod` | Chrome / Edge（MV2 遗留，旧版浏览器） |
| `firefox-mv3-prod` | Firefox（MV3，Firefox 116+） |
| `firefox-mv2-prod` | Firefox（MV2 遗留，Firefox 91+） |

### 单独构建某个目标

```bash
npm run build:chrome-mv3    # Chrome / Edge MV3
npm run build:chrome-mv2    # Chrome / Edge MV2
npm run build:firefox-mv3   # Firefox MV3
npm run build:firefox-mv2   # Firefox MV2
```

单目标脚本不重新编译 WASM；先运行一次 `npm run wasm`。随后脚本会明确选择对应的 `wasm-pkg-simd`（MV3 现代标量版，历史名）或 `wasm-pkg-legacy`（MV2 标量版），再执行 Plasmo、manifest 修正和 zstd 资源复制，不会复用上一次目标遗留的错误变体。WASM 构建、扩展目标切换和 web 快照复制共用 `.wasm-build.lock`；锁覆盖完整 Plasmo 构建窗口，防止并发任务切换正在读取的 glue/WASM。

### 构建后处理

`scripts/fix-manifest.cjs` 会自动执行以下修正：

- **图标处理**：将 `assets/icon{16,32,48,64,128}.png` 真实 RGBA 图标复制进产物目录，覆盖 Plasmo 生成的 1-bit 占位图，并重写 `icons` / `default_icon` 指向新文件
- MV2：移除无效的 `action` 字段，保留 `browser_action`，并补全 `default_title`
- MV3：补全 `action.default_title`
- MV2：CSP 改为 `wasm-eval`（MV3 的 `wasm-unsafe-eval` 在 MV2 中不支持）
- Firefox：添加 `browser_specific_settings.gecko.id`（`airferry@airferry.app`）
- MV3：Chrome/Edge 写入最低版本 96；Firefox 写入最低版本 116（现代 wasm-bindgen 的 externref 下限）
- 修补 HTML `<title>` 标签为「AirFerry · 无网文件传输」

## 打包发布产物

构建 + 打包由根目录的一键脚本完成，版本号取自 `apps/sender/package.json`：

```bash
# 构建 + 打包到 dist/（含 crx/xpi 签名）
./scripts/build-all.sh release

# 或仅打包（apps/sender/build/ 与 APK 已构建好）
./scripts/build-all.sh dist
```

产物（`dist/`，均 git-ignored，通过 GitHub Release 分发）：

| 产物 | 说明 |
|------|------|
| `airferry-sender-chrome-mv3-v<VER>.crx` | Chrome/Edge MV3（已签名 Cr24） |
| `airferry-sender-chrome-mv3-v<VER>.zip` | Chrome/Edge MV3（解压加载回退） |
| `airferry-sender-chrome-mv2-v<VER>.crx` | Chrome/Edge MV2（已签名 Cr24） |
| `airferry-sender-chrome-mv2-v<VER>.zip` | Chrome/Edge MV2（解压加载回退） |
| `airferry-sender-firefox-mv3-v<VER>.xpi` | Firefox MV3（zip→xpi） |
| `airferry-sender-firefox-mv2-v<VER>.xpi` | Firefox MV2（zip→xpi） |
| `airferry-extension.pem` | Chrome 固定签名私钥（须预先配置，git-ignored；脚本核对公钥指纹，绝不自动换钥） |

### Chrome crx 签名机制

脚本调用 Chrome 的 `--pack-extension` 生成 Cr24 签名：

- **首次**（无 `dist/airferry-extension.pem`）：Chrome 新生成私钥，脚本把它挪到 `dist/airferry-extension.pem`
- **后续**：用 `--pack-extension-key` 复用同一私钥 → MV2/MV3 得到**相同的扩展 ID**，便于升级替换
- **找不到 Chrome**（如 Linux/CI）：warn 并跳过 crx，仅保留 `.zip`（此时用「加载已解压的扩展程序」安装）

> 私钥决定扩展 ID，**务必妥善保管 `dist/airferry-extension.pem`**；丢失后无法再为同一扩展 ID 签名。

### Firefox xpi 说明

`.xpi` 本质是 zip（固定扩展名），脚本直接 zip 打包后改名。注意发布的 `.xpi` **未经 Mozilla 签名**（Mozilla 不支持纯本地签名，需经 AMO 服务签名），因此普通 Firefox 正式版会拒绝安装。可行方案见 [README → Firefox 扩展](../README.md#firefox-扩展)（Developer/Nightly 关闭签名校验、临时载入、或上传 AMO 签名后分发）。

## 开发模式

```bash
npm run dev
```

Plasmo 启动 HMR 开发服务器，自动重载扩展。

## 加载到浏览器

### Chrome / Edge

1. 打开 `chrome://extensions`（或 `edge://extensions`）
2. 开启「开发者模式」
3. 二选一：
   - 拖入已签名的 `.crx`
   - 解压 `.zip` 后点「加载已解压的扩展程序」并选择 `chrome-mv3-prod/`（或 mv2）目录；也可直接用 `apps/sender/build/chrome-mv3-prod/`

### Firefox

1. 打开 `about:debugging#/runtime/this-firefox`
2. 点击「临时载入附加组件」
3. 选择 `build/firefox-mv3-prod/manifest.json`（或 MV2），或拖入 `.xpi`

> 因未签名，正式版 Firefox 拒绝安装 `.xpi`；需 Developer/Nightly 关闭 `xpinstall.signatures.required` 或走临时载入（详见 README）。

### 使用

1. 点击工具栏 AirFerry 图标 → **直接在新标签页打开完整应用**（无 popup；`background` 监听 `onClicked`）
2. **添加文件**（文件或文件夹可拖到网页任意位置，也可点选；追加到列表）和/或 **添加文字**（弹窗命名为 `.txt`）
3. 点 **「发送」** 才压缩并进入参数页 → 选速度预设 / 调参数 → 播放二维码视频流  
   - 恰好 1 条文字、0 文件 → `ETTEXTv1`  
   - ≥2 项（文件和/或文字）→ `ETBUNDL1` 打包

## 项目结构

```
apps/sender/
├── package.json                # 依赖 + manifest + 各目标 build 脚本
├── tsconfig.json
├── pnpm-workspace.yaml         # 允许构建的原生依赖白名单
├── scripts/
│   ├── build-all.cjs           # 全量构建脚本（4 目标）
│   ├── build-wasm.cjs          # 双 wasm 产物（legacy + simd）
│   ├── fix-manifest.cjs        # MV2/Firefox manifest + 图标修正 + 删 default_popup
│   ├── prepare-plasmo-icon.cjs # clean build 用 icon.png
│   ├── patch-parcel-dev-server.cjs # 2.9.3 dev-server 限制 CORS 的安全回移
│   └── extract-lzma-wasm.cjs   # lzma-wasm base64 → .wasm 提取
├── src/
│   ├── background/index.ts     # 图标 onClicked → 打开 options
│   ├── options.tsx             # 完整应用（4 页面路由 + Worker 调度）
│   ├── types.ts                # PendingItem + 速度预设(6档) + DEFAULT_CONFIG
│   ├── pages/                  # 4 个页面组件
│   │   ├── FileSelectPage.tsx  # 统一列表：添加文件/文字 → 发送
│   │   ├── ParamsPage.tsx
│   │   ├── PlayPage.tsx
│   │   └── StatsPage.tsx
│   ├── components/
│   │   ├── QrStream.tsx        # QR 视频流（scratch view + putImageData）
│   │   └── CompressProgress.tsx# 压缩阶段进度遮罩
│   ├── workers/
│   │   └── compress.worker.ts  # processFiles / processText
│   ├── wasm/
│   │   ├── loader.ts           # WASM 加载
│   │   ├── bundle.ts           # 多文件打包（ETBUNDL1）
│   │   ├── text.ts             # ETTEXTv1
│   │   ├── compress.ts         # 三算法选优压缩
│   │   ├── crc32.ts
│   │   ├── session.ts
│   │   └── base64.ts           # 单文件版 WASM 解码
│   ├── storage/
│   │   └── textDrafts.ts       # 文件名规范化等（草稿库遗留，UI 主路径不依赖）
│   └── assets/
│       └── app.css
├── wasm-pkg-legacy/            # MV2：标量 + wasm-bindgen 0.2.92
├── wasm-pkg-simd/              # MV3：标量 + 0.2.125（历史名）
└── assets/                     # 发送端 icon{16,32,48,64,128,512}.png + 接收端 receiver-icon*.png
```
