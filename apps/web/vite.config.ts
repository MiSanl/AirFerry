/**
 * Vite config for the AirFerry web sender.
 *
 * This project reuses the sender extension's source verbatim via cross-project
 * imports. Two cross-project concerns are handled here:
 *
 *  1. `@/` alias — the sender's source (e.g. types.ts, components/, wasm/)
 *     imports each other through `@/*` which resolves to its own `src/`. We
 *     map `@/` to `../sender/src/` so every such import lands on the real file.
 *
 *  2. WASM packages — `@airferry-wasm/` points to the web-owned snapshot made
 *     by `prepare-wasm.cjs`. This avoids reading sender's target-switching
 *     package during a concurrent extension build. wasm-pack emits standard
 *     ESM that Vite bundles natively.
 *
 * The compress worker is spawned via the standard
 *   `new Worker(new URL("./workers/compress.worker.ts", import.meta.url), {type:"module"})`
 * form in options.tsx; Vite handles it as a separate entry and applies the same
 * `@/` alias inside the worker bundle.
 */
import { defineConfig } from "vite"
import react from "@vitejs/plugin-react"
import path from "node:path"
import { fileURLToPath } from "node:url"

const __dirname = path.dirname(fileURLToPath(import.meta.url))

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: [
      // Mirror the sender's tsconfig `@/*` -> `./src/*`, pointing at the real
      // sender source so cross-project imports resolve identically. Using the
      // { find, replacement } form with a trailing-slash replacement is the
      // reliable way to alias a path prefix under Vite/Rollup.
      { find: "@/", replacement: path.resolve(__dirname, "../sender/src/") + "/" },
      { find: "@airferry-wasm/", replacement: path.resolve(__dirname, "wasm-pkg/") + "/" },
      // zxing-wasm + lzma-wasm live in web's node_modules; the QR/receive
      // workers are compiled from sender source, so without these aliases
      // Rollup searches sender/node_modules (which lacks them) first. Pin to
      // the exact dist entry each subpath resolves to via package "exports".
      {
        find: "zxing-wasm/reader",
        replacement: path.resolve(
          __dirname,
          "node_modules/zxing-wasm/dist/es/reader/index.js"
        ),
      },
      {
        find: /^zxing-wasm$/,
        replacement: path.resolve(
          __dirname,
          "node_modules/zxing-wasm/dist/es/full/index.js"
        ),
      },
      {
        find: "lzma-wasm",
        replacement: path.resolve(__dirname, "node_modules/lzma-wasm"),
      },
    ],
  },
  // The transfer_engine wasm-pkg and the lzma/zstd loaders are only needed at
  // runtime; exclude them from Vite's dep pre-bundling to avoid mismatches.
  // @foxglove/wasm-zstd ships an Emscripten CJS module that resolves its .wasm
  // via require()/fetch() at runtime — pre-bundling makes Vite try to parse the
  // inner `require("./wasm-zstd.wasm")` and fail on the ESM-wasm proposal.
  optimizeDeps: {
    exclude: ["lzma-wasm", "@foxglove/wasm-zstd"],
  },
  // Receive/QR workers use dynamic imports (lzma-wasm, zxing-wasm/reader),
  // which produce code-split chunks — that requires the "es" worker format
  // (the default "iife" can't express splits).
  worker: {
    format: "es",
  },
  server: {
    // QR scanning requires a clean screen; a stable port makes local testing
    // predictable across reloads.
    port: 5180,
    strictPort: false,
  },
  build: {
    // Emit a static site under dist/ that can be hosted anywhere (GitHub Pages,
    // Netlify, any static server). Relative base so it works under sub-paths
    // (e.g. username.github.io/repo/), not just site root.
    // Single entry: sender (index.html). The receiver is built separately by
    // vite.receiver.config.ts into dist-receiver/ so the two ship as independent
    // self-contained zips (airferry-sender-web / airferry-receiver-web).
    outDir: "dist",
    emptyOutDir: true,
    target: "esnext",
    rollupOptions: {
      input: {
        index: path.resolve(__dirname, "index.html"),
      },
    },
  },
  // Relative asset base: index.html emits `./assets/...` so the site works
  // under any sub-path without rewriting URLs. wasm-zstd.wasm (site root,
  // from public/) is fetched on the MAIN THREAD via `document.baseURI`
  // (preloadZstdBytes) and posted to the workers (`wasm-init`), so it stays
  // correct in sub-paths too; the worker-side fallback fetch steps up one
  // level from its assets/ script URL (`../wasm-zstd.wasm`).
  base: "./",
})
