# Windows 端构建指南

> Windows 扫码接收端（C# WPF + Rust 引擎 DLL + ZXing-C++ DLL），支持**设备选择**（摄像头或采集卡）。相机解码镜像 Android v1.1.3 模式。

---

## 1. 技术栈

| 层 | 技术 | 说明 |
|----|------|------|
| UI | WPF (.NET 8, C#) | 对标 Android Compose UI |
| 相机/采集卡 | OpenCvSharp4 (DirectShow 后端) | 单句柄读取；Gray 送解码、池化 BGR24 快照送预览 |
| 设备枚举 | DirectShowLib (DsDevice) | `FilterCategory.VideoInputDevice` 同时覆盖摄像头+采集卡 |
| QR 解码 | ZXing-C++（全帧/ROI 均 TryHarder + TryInvert） | `core/zxing-decoder/` + Windows 薄 C ABI，选项与 Android v1.1.3 相同 |
| 核心引擎 | Rust `transfer-engine` (C ABI, `--features cffi`) | 编解码逻辑与 Android/WASM 共享，编译为 `transfer_engine.dll` |
| MVVM | CommunityToolkit.Mvvm | ObservableObject / RelayCommand 源生成器 |

**关键不变量**：RaptorQ/帧协议仍由三端共享 Rust 核心实现。Android 锁定 v1.1.3 JNI 解码实现；Windows 通过 `core/zxing-decoder/` 和 `QrDecodePool.cs` 镜像相同解码选项与调度状态机。

---

## 2. 环境要求

| 工具 | 版本 | 说明 |
|------|------|------|
| Windows | 10 (10.0.17763+) / 11 | DirectShow/Media Foundation 仅桌面版 Windows 有 |
| .NET SDK | 8.0+ | WPF 需要 `net8.0-windows` TFM |
| Rust | 1.75+ (stable) | 默认 `x86_64-pc-windows-msvc` target（`rustup` 默认安装） |
| CMake | 3.22+ | 配置/构建 `airferry_zxing.dll`，首次会获取固定 commit 的 zxing-cpp |
| Visual Studio | 2022，Desktop development with C++ | MSVC x64 编译器和 Windows SDK |

验证：
```powershell
dotnet --version          # ≥ 8.0
rustc --version           # ≥ 1.75
rustup target list --installed   # 应含 stable-x86_64-pc-windows-msvc（默认即有）
cmake --version           # ≥ 3.22
```

---

## 3. 一键构建（PowerShell，首选）

```powershell
# 构建（Debug/Release 配置）
.\scripts\build-windows.ps1

# 构建 + 打包到 dist/（发布用 zip）
.\scripts\build-windows.ps1 -Pack
```

脚本流程：
1. `cargo build -p transfer-engine --features cffi --release` → `target/release/transfer_engine.dll`
2. 拷贝 DLL 到 `apps/windows/AirFerry.Windows/runtime/transfer_engine.dll`
3. CMake 配置/编译共享 ZXing-C++ → CTest → 拷贝 `airferry_zxing.dll` 到同一 `runtime/`
4. `dotnet restore` + `dotnet build -c Release`（或 `-Pack` 时 `dotnet publish`）
5. （`-Pack` 时）压缩发布目录到 `dist/airferry-windows-x64-v{VER}.zip`

> 也可以用 bash 入口（Git Bash/WSL 下）：`./scripts/build-all.sh windows`。逻辑等价，但 PowerShell 是 Windows 上的首选。

---

## 4. 手动分步构建

### 4.1 编译 Rust C ABI DLL

```powershell
cargo build -p transfer-engine --features cffi --release
# 产物: target/release/transfer_engine.dll
```

> **必须先于 C# 构建**：csproj 会把两个 `runtime/*.dll` 扁平复制到 build/publish 的 exe 同目录，并明确排除单文件内嵌；发布脚本还会显式复制并核验一次，防止 SDK item glob 变化造成漏包。若 DLL 缺失，运行时第一个 P/Invoke 会抛 `DllNotFoundException`。

### 4.2 编译共享 ZXing-C++ DLL

```powershell
cmake -S apps/windows/native -B apps/windows/native/build `
  -G "Visual Studio 17 2022" -A x64
cmake --build apps/windows/native/build --config Release --parallel
ctest --test-dir apps/windows/native/build -C Release --output-on-failure

$dll = Get-ChildItem apps/windows/native/build -Recurse -Filter airferry_zxing.dll |
  Sort-Object LastWriteTime -Descending | Select-Object -First 1
Copy-Item $dll.FullName apps/windows/AirFerry.Windows/runtime/airferry_zxing.dll -Force
```

> CMake 固定 zxing-cpp v3.0.2 对应 commit。Windows 算法位于 `core/zxing-decoder/`，C ABI 负责传参、异常边界和结果内存所有权；C#/C ABI 行为镜像 Android v1.1.3 JNI 模式。

### 4.3 构建 C# WPF

```powershell
cd apps\windows
dotnet restore
dotnet build -c Release
# 产物: AirFerry.Windows\bin\x64\Release\net8.0-windows\win-x64\AirFerry.exe
```

### 4.4 运行

```powershell
dotnet run --project AirFerry.Windows -c Release
# 或直接双击 AirFerry.exe
```

---

## 5. 关键依赖顺序（坑）

1. **两个 native DLL 必须先于 C# 构建**：见 §4.1/§4.2。走 `build-windows.ps1` 会自动跑 cargo、CMake 与 CTest。
2. **WPF 只能在 Windows 上构建**：`net8.0-windows` TFM 依赖 Windows SDK，无法在 macOS/Linux 上编译 C# 主项目。**协议层单元测试**（`AirFerry.Windows.Tests`）用纯 `net8.0` TFM，可在任何 OS 上跑（不依赖 P/Invoke，只测 IngestStatus 位域、FrameHeader 解析、BundleParser 等纯逻辑）。
3. **版本号同步**：改版本时同时改 `apps/sender/package.json`（→ 文件名）+ `apps/scanner/app/build.gradle.kts` versionName（→ APK 内嵌）+ `Cargo.toml`（→ 核心库）+ `apps/windows/AirFerry.Windows/AirFerry.Windows.csproj` `<Version>`（→ exe 内嵌）+ **`.github/workflows/windows.yml` `env.VER`**（→ CI zip 名与 release tag）。详见 [AGENTS.md](../AGENTS.md) §2.8 / §2.9。

---

## 6. GitHub Actions 发版（推荐，非 Windows 本机）

macOS/Linux 无法编 WPF。正式 Windows 产物用 [`.github/workflows/windows.yml`](../.github/workflows/windows.yml)：

```text
push/PR（core/** 或 apps/windows/**）
  → rust-cffi (ubuntu) + csharp-tests (ubuntu) + windows-build (windows-2022)

workflow_dispatch（手动）且上述三 job 成功
  → windows-pack：
       cargo build --features cffi --release
       拷贝 transfer_engine.dll → apps/windows/AirFerry.Windows/runtime/
       CMake/MSVC 构建共享 ZXing-C++ + CTest
       拷贝 airferry_zxing.dll → apps/windows/AirFerry.Windows/runtime/
       dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true --self-contained false
       Compress-Archive → airferry-windows-x64-v${VER}.zip
       gh release upload v${VER} … --clobber
       （若 tag/release 不存在则 gh release create --draft）
```

操作：Actions → **windows** → **Run workflow**。`VER` 在 workflow 文件顶部 `env.VER`，发版前与四处版本号一并改。

本地 Windows 仍可用 `.\scripts\build-windows.ps1 -Pack`（产物进 `dist/`）。

---

## 7. 设备选择（摄像头 / 采集卡）

Windows 端的核心新增功能。启动后进入**设备选择页**：

- 自动枚举所有 DirectShow 视频输入设备（`FilterCategory.VideoInputDevice`）
- 摄像头（USB 摄像头、内置摄像头）与采集卡（USB HDMI 采集卡、专业 SDI 采集卡）在 DirectShow API 下是**同类设备**，统一列出
- 通过设备名启发式标注（含 "capture"/"采集"/"HDMI"/"Magewell"/"Elgato" 等关键字 → 标为「采集卡」，仅显示用，行为无差别）
- 下拉选择 + 刷新按钮，确认后点「开始扫码」进入扫码页

### 7.1 单采集源与停止生命周期

- 解码和预览共用一个 DirectShow 句柄；生产线程每次读取只向池化 Gray 缓冲复制一次，并按最高 15fps 发布 BGR24 预览快照，避免独占型驱动因双开设备导致黑屏。
- 2–6 个 worker 调用 Windows 的 ZXing-C++ 核心，队列容量为 `worker+2`，每个 worker 最多累积 4 个符号后进入串行摄入；全帧/ROI 与 miss 计数状态机镜像 Android v1.1.3。
- 预览快照使用 `ArrayPool<byte>`，UI 只把托管像素写入 `WriteableBitmap`，不在 Dispatcher 上调用阻塞式 `VideoCapture.Read()`。
- 扫描页只保留最新预览帧；UI 忙时自动覆盖旧帧，不堆积 Dispatcher 任务或大图像缓冲。
- 停止会先作废旧会话的排队 UI 回调，并启动唯一的有序清理任务：等待生产者、完成后的组装/落盘任务及全部解码 worker 真正结束后，才释放 native handle/camera。前台最多等待 2 秒；慢摄像头超时后资源由后台任务继续持有并安全释放，期间禁止重启扫描，因此既不冻结 WPF Dispatcher，也不会并发 free/Dispose。
- 状态卡以约 7Hz 一致快照显示 3 秒窗口解码速率、有效吞吐、采集/丢帧/已解码计数、源文件/传输大小，以及每个码位 active/paused 状态，流程与 Android 扫描页一致。

### 7.2 技术栈取舍

当前阶段保留 WPF，不把一次稳定性改造与 UI 框架迁移绑在一起。WPF 本身只支持 Windows，因此这里的“跨平台”边界是 Rust 协议核心、内容模型和可测试的纯 C# 协议层；WPF 仅作为 Windows 外壳。

若后续确实需要桌面端同时覆盖 macOS/Linux，建议先把扫描编排、文件库和接收结果抽为不依赖 WPF 的 .NET 类库，再用 Avalonia 替换视图层。不要在现有 WPF 上继续叠 MAUI/Electron：这会保留 OpenCV、ZXing、Rust FFI 的全部复杂度，同时再增加一套运行时和打包链。

---

## 8. 产物

| 产物 | 路径 | 说明 |
|------|------|------|
| 可执行文件 | `apps/windows/AirFerry.Windows/bin/x64/Release/net8.0-windows/win-x64/AirFerry.exe` | 依赖同目录下的 `transfer_engine.dll`、`airferry_zxing.dll` + OpenCV native DLLs |
| Windows 接收端 zip（本地） | `dist/airferry-windows-x64-v{VER}.zip` | `build-windows.ps1 -Pack` |
| 发布 zip（CI） | GitHub Release asset 同名 | `windows.yml` → `windows-pack` job |

> 所有本地产物均 git-ignored。分发走 GitHub Release；**默认 Windows 发版路径是 workflow**（§6）。

---

## 9. 测试

```powershell
cd apps\windows
dotnet test
```

测试覆盖纯托管逻辑（不实际加载 P/Invoke DLL，跨平台可跑）：
- `IngestStatusTests`：packed 位域解析（对标 Rust `cffi::tests`）
- `FrameHeaderTests`：60B 大端帧头解析
- `BundleParserTests`：ETBUNDL1 多文件包解包
- `FileNameUtilTests`：文件名 sanitize + Windows 保留名处理
- `ProgressSnapshotTests`：进度 JSON 解析
- `PreviewFrameTests`：池化预览缓冲的所有权与幂等释放
- `ZxingDecoderTests`：共享 native packed 结果的长度、bbox、畸形输入及尾部字节拒绝

共享 C++ 核心另有原生 CTest（几何校验与 packed 布局）：

```powershell
ctest --test-dir apps/windows/native/build -C Release --output-on-failure
```

> Rust 侧的 C ABI 端到端测试：`cargo test -p transfer-engine --features cffi --test cffi_e2e`（用真实 sender 帧喂 cffi receiver，验证完整恢复）。

---

## 10. 与 Android 端的对照

| 维度 | Android | Windows |
|------|---------|---------|
| UI | Compose | WPF XAML |
| 相机 | CameraX (Y plane) | OpenCvSharp VideoCapture (BGR→Gray) |
| 设备枚举 | CameraX 自动 | DirectShow DsDevice（★新增设备选择） |
| QR 解码 | ZXing-C++ v1.1.3 路径（JNI） | 等价 ZXing-C++ 模式（C ABI/P/Invoke） |
| 核心引擎 | Rust `jni.rs` (JNI) | Rust `cffi.rs` (C ABI) |
| 并行解码 | 2–6 workers + v1.1.3 调度/4 符号批摄入 | 同 worker/队列/批量/miss 状态机 + ingestLock |
| 落盘 | ContentStore blob + `index.json` | `%USERPROFILE%\Documents\AirFerry\store\blobs\<hh>\<sha256>` + `index.json` |
| 多文件包 | BundleParser.kt | BundleParser.cs |
| 签名 | keystore.properties | （暂无 Authenticode 签名） |
