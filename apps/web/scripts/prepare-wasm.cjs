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
 *  2. `public/wasm-zstd.wasm` — the zstd compressor WASM, fetched at runtime by
 *     the compress worker's fallback path (`new URL("wasm-zstd.wasm",
 *     self.location.href)`). In the extension this file lives next to the page;
 *     on the web it must be served from the site root, so we copy it into
 *     `public/` where Vite serves static assets.
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

// (2) Copy wasm-zstd.wasm into public/ for the worker's runtime fetch.
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

console.log("[prepare-wasm] ready")
