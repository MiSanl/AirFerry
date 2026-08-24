# GitHub Actions 全量发布

`.github/workflows/release-all.yml` 是所有可分发端的统一构建和发布入口。推送与版本号一致的 `v<VER>` tag，或在 Actions 页面手动选择已有 tag，即会重建并上传以下 GitHub Release 资产：

- Chrome/Edge MV2、MV3 固定扩展 ID 的 CRX 与解压加载包（zip），以及 Firefox MV2、MV3 扩展包（xpi）
- 网页发送端 zip、网页接收端 zip、可双击的单文件网页发送端 html
- Android arm64-v8a 签名 APK
- Windows WPF 接收端 zip
- Windows x64 发送端 portable EXE

网页 Pages 部署仍由 `.github/workflows/pages.yml` 单独负责。若仓库未在 Settings → Pages 中选择 GitHub Actions，Pages 部署会失败，但不影响 `release-all.yml` 的 Release 资产构建或上传。

## 必需 Secrets

Android Release APK 使用固定签名，工作流会 fail-closed：缺少任何一项时不会发布 debug 或未签名 APK。将以下 Secrets 配置在仓库 Settings → Secrets and variables → Actions：

| Secret | 内容 |
|---|---|
| `ANDROID_KEYSTORE_BASE64` | 固定 release keystore 的 base64 内容 |
| `ANDROID_STORE_PASSWORD` | keystore 密码 |
| `ANDROID_KEY_ALIAS` | 签名 key alias |
| `ANDROID_KEY_PASSWORD` | 签名 key 密码 |
| `CHROME_EXTENSION_PEM_BASE64` | 固定 Chrome 扩展 PEM 私钥的 base64 内容 |

工作流会核对 Chrome PEM 派生的公钥 SHA-256 与项目固定值是否一致；不一致或缺失会失败，避免发布改变扩展 ID 的 CRX。私钥只在 runner 临时文件中使用，不会上传到 artifacts 或 Release。

## 发布步骤

1. 同步各端版本号、发行说明和 lockfile。
2. 提交并推送 `main`。
3. 创建并推送 `v<VER>` tag。
4. 等待 `release-all` 全部 job 完成，在 Release 页面确认每个资产均已上传。

已存在的 tag 不会自动获得后来新增的工作流；工作流合入后，可在 Actions → `release-all` → Run workflow 中输入该 tag 手动重建。Secrets 未配置时，Android 或 CRX job 会按设计失败，不会发布未签名替代品。
