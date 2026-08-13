/**
 * Prepare WASM assets for the web sender before dev/build.
 *
 * Two things must be in place:
 *
 *  1. `apps/sender/wasm-pkg-simd/` — the modern Rust WASM produced by
 *     `wasm-pack`. We validate both glue and binary, then atomically copy it
 *     into web's private `wasm-pkg/` import path. This prevents extension builds
 *     from switching the package while Vite is reading it.
 *
 *  2. `public/wasm-zstd.wasm` — the zstd codec WASM. The MAIN THREAD fetches
 *     it via `document.baseURI` (`wasm/zstdPreload.ts`) and posts the bytes to
 *     the compress/receive workers (`wasm-init`); the worker-side fallback
 *     fetch steps up from its assets/ script URL (`../wasm-zstd.wasm`). Both
 *     resolve to the site root, so we copy it into `public/` where Vite
 *     serves static assets.
 *
 * Run via `predev`/`prebuild`. Idempotent.
 */
const fs = require("fs")
const path = require("path")
const { acquireWasmLock } = require("../../sender/scripts/wasm-lock.cjs")

const webRoot = path.resolve(__dirname, "..")
const senderRoot = path.resolve(webRoot, "..", "sender")
const wasmPkgDir = path.join(webRoot, "wasm-pkg")
const modernPkgDir = path.join(senderRoot, "wasm-pkg-simd")
const wasmPkgGlue = path.join(modernPkgDir, "transfer_engine.js")
const wasmPkgBinary = path.join(modernPkgDir, "transfer_engine_bg.wasm")

// Web owns its selected package directory. Copy it while holding the sender's
// WASM lock, then release: extension MV2/MV3 builds may freely switch their own
// shared package without changing files Vite is currently bundling.
const releaseLock = acquireWasmLock(senderRoot)
try {
  // Verify only after acquiring the publisher lock; otherwise we could inspect
  // the tiny remove/rename window of an in-progress publish and fail spuriously.
  if (!fs.existsSync(wasmPkgGlue) || !fs.existsSync(wasmPkgBinary)) {
    throw new Error(
      "apps/sender/wasm-pkg-simd/ is incomplete. Build it first with: " +
        "cd apps/sender && npm install && npm run wasm"
    )
  }
  const stagedPkg = path.join(webRoot, `.wasm-pkg.web-staged-${process.pid}`)
  fs.rmSync(stagedPkg, { recursive: true, force: true })
  fs.cpSync(modernPkgDir, stagedPkg, { recursive: true })
  fs.rmSync(wasmPkgDir, { recursive: true, force: true })
  fs.renameSync(stagedPkg, wasmPkgDir)
} finally {
  releaseLock()
}
console.log("[prepare-wasm] copied wasm-pkg-simd into web-owned wasm-pkg")

// (2) Copy wasm-zstd.wasm into public/ for the main-thread preload (and the
// worker-side fallback fetch) — both resolve to the site root.
const zstdSrc = path.join(webRoot, "node_modules", "@foxglove", "wasm-zstd", "dist", "wasm-zstd.wasm")
const publicDir = path.join(webRoot, "public")
const zstdDst = path.join(publicDir, "wasm-zstd.wasm")

if (!fs.existsSync(zstdSrc)) {
  console.error(
    "\n✖ @foxglove/wasm-zstd not installed. Run `npm install` in apps/web first.\n"
  )
  process.exit(1)
}

fs.mkdirSync(publicDir, { recursive: true })

// Skip copy if already up to date.
const needCopy =
  !fs.existsSync(zstdDst) ||
  fs.statSync(zstdDst).size !== fs.statSync(zstdSrc).size ||
  fs.statSync(zstdDst).mtimeMs < fs.statSync(zstdSrc).mtimeMs

if (needCopy) {
  fs.copyFileSync(zstdSrc, zstdDst)
  console.log(`[prepare-wasm] copied wasm-zstd.wasm → ${path.relative(webRoot, zstdDst)}`)
} else {
  console.log("[prepare-wasm] wasm-zstd.wasm up to date")
}

// (3) Copy zxing_reader.wasm into public/ for the QR decode worker's runtime
// fetch. zxing-wasm's Emscripten `locateFile` resolves it relative to the
// worker location, so it must sit at the build output root (alongside
// wasm-zstd.wasm).
const zxingSrc = path.join(
  webRoot,
  "node_modules",
  "zxing-wasm",
  "dist",
  "reader",
  "zxing_reader.wasm"
)
const zxingDst = path.join(publicDir, "zxing_reader.wasm")
if (fs.existsSync(zxingSrc)) {
  const zNeed =
    !fs.existsSync(zxingDst) ||
    fs.statSync(zxingDst).size !== fs.statSync(zxingSrc).size ||
    fs.statSync(zxingDst).mtimeMs < fs.statSync(zxingSrc).mtimeMs
  if (zNeed) {
    fs.copyFileSync(zxingSrc, zxingDst)
    console.log(
      `[prepare-wasm] copied zxing_reader.wasm → ${path.relative(webRoot, zxingDst)}`
    )
  } else {
    console.log("[prepare-wasm] zxing_reader.wasm up to date")
  }
} else {
  // zxing-wasm is only needed by the receiver; warn (not fail) if absent so
  // sender-only builds still work.
  console.warn("[prepare-wasm] zxing-wasm not installed — receiver QR decode will be unavailable")
}

// (4) Copy the self-compiled FAST ZXing-C++ backend (airferry_zxing.js + .wasm)
// into public/ for the QR decode worker's fast path. Produced by
// scripts/build-fastzxing.sh (Emscripten; git-ignored). The worker loads it via
// `new URL("../airferry_zxing.js", self.location.href)` — same public-root
// mechanism as zxing_reader.wasm. Warn (not fail) if absent: the worker falls
// back to the zxing-wasm compat backend, so local builds without Emscripten and
// sender-only builds still work; only the production receiver (built via CI with
// build-fastzxing.sh) gets the ~2× fast path.
const fastzxingDir = path.join(senderRoot, "src", "fastzxing")
const fastFiles = ["airferry_zxing.js", "airferry_zxing.wasm"]
let fastCopied = false
for (const f of fastFiles) {
  const src = path.join(fastzxingDir, f)
  const dst = path.join(publicDir, f)
  if (fs.existsSync(src)) {
    const need =
      !fs.existsSync(dst) ||
      fs.statSync(dst).size !== fs.statSync(src).size ||
      fs.statSync(dst).mtimeMs < fs.statSync(src).mtimeMs
    if (need) {
      fs.copyFileSync(src, dst)
      fastCopied = true
    }
  } else {
    console.warn(
      `[prepare-wasm] ${f} not found — FAST ZXing backend unavailable, receiver will use zxing-wasm compat path. Run scripts/build-fastzxing.sh to enable.`
    )
  }
}
if (fastCopied) {
  console.log("[prepare-wasm] copied airferry_zxing.js/.wasm → public/")
}

console.log("[prepare-wasm] ready")
