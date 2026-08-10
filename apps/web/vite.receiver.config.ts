/**
 * Vite config for the AirFerry web receiver (standalone deployable entry).
 *
 * The sender's main vite.config.ts now builds ONLY index.html (sender) into
 * dist/. This config builds ONLY receiver.html into dist-receiver/ so the two
 * ship as independent, self-contained zips:
 *   - dist/            → airferry-sender-web-v{VER}.zip
 *   - dist-receiver/   → airferry-receiver-web-v{VER}.zip
 *
 * The receiver reuses sender source via the same `@/` alias (ReceivePage +
 * QR/receive workers). It additionally needs zxing-wasm (QR decode) and
 * lzma-wasm (xz decompress), which live in web's node_modules, so those
 * aliases are mirrored here.
 *
 * Runtime resources in public/:
 *   - wasm-zstd.wasm   (zstd decompress — the receiver ingests zstd-compressed
 *                       payloads too)
 *   - zxing_reader.wasm (QR decode worker's Emscripten locateFile fetch)
 * Both are copied into the output root by Vite (publicDir) and fetched at
 * runtime.
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
      { find: "@/", replacement: path.resolve(__dirname, "../sender/src/") + "/" },
      { find: "@airferry-wasm/", replacement: path.resolve(__dirname, "wasm-pkg/") + "/" },
      // zxing-wasm + lzma-wasm live in web's node_modules; the QR/receive
      // workers are compiled from sender source. Pin to the exact dist entry.
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
  optimizeDeps: {
    exclude: ["lzma-wasm", "@foxglove/wasm-zstd"],
  },
  // QR/receive workers use dynamic imports (lzma-wasm, zxing-wasm/reader),
  // which produce code-split chunks — requires the "es" worker format.
  worker: {
    format: "es",
  },
  server: {
    port: 5181,
    strictPort: false,
  },
  build: {
    outDir: "dist-receiver",
    emptyOutDir: true,
    target: "esnext",
    rollupOptions: {
      input: {
        receiver: path.resolve(__dirname, "receiver.html"),
      },
    },
  },
  // Relative asset base so the receiver works under any sub-path.
  base: "./",
})
