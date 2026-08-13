/**
 * Main-thread preloader for the zstd WASM binary.
 *
 * Why this exists: inside a **bundled** Web Worker (Vite emits workers to
 * `assets/*.js`), a runtime `fetch` of `"wasm-zstd.wasm"` resolved against
 * `self.location.href` points at `assets/wasm-zstd.wasm` — but the file is
 * deployed at the site root, so it 404s. The main thread can resolve the
 * correct root URL (`document.baseURI`, correct for the dev server and
 * subpath deployments alike) or, for the extension, `chrome.runtime.getURL`.
 * It therefore fetches the bytes itself and transfers them to the worker via
 * postMessage (`wasm-init`), where `initZstdFromBytes()` installs them and the
 * worker never fetches at runtime. Used by both the compress worker (sender)
 * and the receive worker (web receiver decompresses zstd at completion).
 *
 * Three environments, three ways to get the bytes:
 *  - **Standalone (single-file) build**: the wasm is inlined as base64 on
 *    `globalThis.__WASM_ZSTD__` (file:// can't fetch). Decode directly.
 *  - **Browser extension**: `chrome.runtime.getURL` resolves the packed asset.
 *  - **Plain web page**: resolve relative to the document base.
 *
 * Returns `null` when the bytes could not be loaded — callers should still
 * post `wasm-init` with `null` so the worker never parks waiting for zstd
 * (the compress path falls back to raw; the receive path lazily re-fetches at
 * use time via the context-aware fallback URL, and only errors if that also
 * fails).
 */
import { base64ToBuffer } from "@/wasm/base64"

/**
 * Bound the preload fetch. Callers hard-await this before posting `init` to
 * their worker, so a server connection that *hangs* (instead of fast-failing
 * with 404) would otherwise stall page/worker startup indefinitely — before
 * the worker ready-barrier timeout even starts counting. On timeout the
 * fetch aborts, the catch below returns null, and zstd is fetched lazily at
 * use time (correctly URL-resolved; only reached for actual zstd payloads).
 */
const PRELOAD_FETCH_TIMEOUT_MS = 5000

/**
 * fetch() with a timeout via AbortController + setTimeout. (`AbortSignal.timeout()`
 * is deliberately avoided — not available in older browsers.)
 */
function fetchWithTimeout(url: string): Promise<Response> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), PRELOAD_FETCH_TIMEOUT_MS)
  return fetch(url, {
    credentials: "same-origin",
    signal: controller.signal,
  }).finally(() => clearTimeout(timer))
}

export async function preloadZstdBytes(): Promise<ArrayBuffer | null> {
  const g = globalThis as {
    __AIRFERRY_STANDALONE__?: boolean
    __WASM_ZSTD__?: string
  }
  try {
    if (g.__AIRFERRY_STANDALONE__ && g.__WASM_ZSTD__) {
      // Standalone build: decode the inlined base64 (file:// can't fetch).
      const decoded = base64ToBuffer(g.__WASM_ZSTD__)
      if (decoded) return decoded
    } else {
      const wasmUrl =
        typeof chrome !== "undefined" && chrome.runtime?.getURL
          ? chrome.runtime.getURL("wasm-zstd.wasm")
          : new URL("wasm-zstd.wasm", document.baseURI).href
      const resp = await fetchWithTimeout(wasmUrl)
      if (resp.ok) return await resp.arrayBuffer()
      console.warn("Failed to pre-load wasm-zstd.wasm:", resp.status)
    }
  } catch (e) {
    if (e instanceof DOMException && e.name === "AbortError") {
      console.warn(
        `Pre-loading wasm-zstd.wasm timed out after ${PRELOAD_FETCH_TIMEOUT_MS} ms; ` +
          `zstd will be fetched lazily at use time`
      )
    } else {
      console.warn("Failed to pre-load wasm-zstd.wasm:", e)
    }
  }
  return null
}
