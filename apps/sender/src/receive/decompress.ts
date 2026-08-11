/**
 * Receiver-side decompression + integrity verification.
 *
 * The WASM receiver core (`ReceiverSessionWasm.assemble_raw`) returns the
 * transmitted payload WITHOUT decompressing — the wasm32 build cannot link the
 * native zstd/xz C libraries, and `decompress_with_limit` is fail-closed for
 * compressed tags (see `core/qr-protocol/src/compress.rs`). This module does
 * the JS-side decompression that Android/Windows do natively, using the same
 * standard zstd / xz streams the sender worker produces.
 *
 * After decompression we verify the length matches the descriptor's
 * `original_size` and (when `crc32_known`) the CRC32. A mismatch never returns
 * partial bytes — it throws so the caller can surface a clear error.
 */

import { crc32 } from "@/wasm/crc32"
import { ensureZstdLoaded, zstdDecompress } from "@/wasm/compress"

/** Compression-algorithm tags (mirror `qr_protocol::compress`). */
export const COMPRESSION_NONE = 0
export const COMPRESSION_ZSTD = 1
export const COMPRESSION_XZ = 2

/**
 * Hard output cap for the **browser** receiver's in-memory decompression. The
 * native receivers (Android/Windows) stream decompression to disk and are not
 * bounded by this — but the web receiver has no disk-streaming codec, so it
 * holds the decompressed original in JS memory. Unlike the native receiver
 * (which streams to disk via `decompress_stream_to_file` and stays memory-
 * bounded), the browser has no streaming disk decompressor: the recovery path
 * (`recoverStoredTask`) builds the whole compressed stream AND the whole
 * decompressed result in JS memory simultaneously (plus a copy for SHA-256),
 * so the peak is roughly `compressedSize + ~2× decompressedSize`.
 *
 * The earlier 2 GiB figure was a theoretical JS-array ceiling, not a realistic
 * recoverable size — a 1 GiB file would already need ~2-3 GiB resident and OOM
 * a typical tab. 256 MiB keeps peak memory under ~1 GiB on a default tab,
 * matching the native single-object ceiling; larger files should use the
 * Android/Windows receiver (streaming disk decompression, unbounded file size).
 */
export const MAX_DECOMPRESSED_BYTES = 256 * 1024 * 1024

/** 128 MiB memory ceiling for the XZ decoder (mirrors the native budget). */
const XZ_MEM_LIMIT = 128 * 1024 * 1024

export interface VerifyResult {
  /** The decompressed original bytes. */
  bytes: Uint8Array
  /** True if the CRC32 was known and matched. */
  crcOk: boolean
  /** True if the descriptor advertised a CRC32 at all. */
  crcKnown: boolean
}

export class DecompressError extends Error {}

/** Initialize the zstd WASM module (idempotent). Delegates to compress.ts so
 * the same Emscripten instance is shared — no second wasm fetch. */
export async function ensureZstd(): Promise<void> {
  await ensureZstdLoaded()
}

/**
 * Decompress + verify a recovered payload.
 *
 * @param raw the bytes from `assemble_raw` (already trimmed to compressed_size)
 * @param compression algorithm tag
 * @param originalSize expected decompressed length (from descriptor)
 * @param crc32 expected CRC32 (from descriptor)
 * @param crc32Known whether the descriptor supplied a real CRC32
 */
export async function decompressAndVerify(
  raw: Uint8Array,
  compression: number,
  originalSize: number,
  expectedCrc32: number,
  crc32Known: boolean
): Promise<VerifyResult> {
  if (originalSize > MAX_DECOMPRESSED_BYTES) {
    throw new DecompressError(
      `原始大小 ${originalSize} 超过接收上限 ${MAX_DECOMPRESSED_BYTES}`
    )
  }

  let out: Uint8Array
  if (compression === COMPRESSION_NONE) {
    out = raw
  } else if (compression === COMPRESSION_ZSTD) {
    // Reuse the sender's zstd Emscripten module (shared singleton) instead of
    // importing @foxglove/wasm-zstd's JS wrapper, whose inner `require(.wasm)`
    // trips Vite's ESM-wasm proposal guard during worker bundling.
    await ensureZstd()
    out = await zstdDecompress(raw, originalSize)
  } else if (compression === COMPRESSION_XZ) {
    // lzma-wasm requires initWasm() before any compress/decompress call.
    const lzma = await import("lzma-wasm")
    await lzma.initWasm()
    out = lzma.decompress(raw, { memLimit: XZ_MEM_LIMIT })
  } else {
    throw new DecompressError(`未知的压缩算法标签: ${compression}`)
  }

  if (out.length !== originalSize) {
    throw new DecompressError(
      `解压后大小 ${out.length} 与描述符声明的 ${originalSize} 不一致`
    )
  }

  let crcOk = false
  if (crc32Known) {
    const actual = crc32(out)
    crcOk = actual === (expectedCrc32 >>> 0)
    if (!crcOk) {
      // Don't throw — the caller may offer a "save anyway" option. Surface via
      // the result instead so the UI can warn.
    }
  }

  return { bytes: out, crcOk, crcKnown: crc32Known }
}
