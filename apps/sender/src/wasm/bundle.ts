/**
 * Multi-file bundle container (AirFerry Bundle v1).
 *
 * The whole transfer is a single RaptorQ object: one compressed payload → one
 * QR video stream. To send several files "in one go" we pack them into one
 * byte blob *before* compression, so the entire bundle benefits from the
 * three-algorithm compressor and travels as a single transfer. After RaptorQ
 * recovery + decompression, the receiver detects the bundle by its magic
 * prefix and unpacks it back into the individual files.
 *
 * ## When is a bundle used?
 *
 *  - 1 file  → NOT bundled. Sent as a raw single file (current behaviour). The
 *              receiver saves it under the descriptor filename. Fully backward
 *              compatible.
 *  - ≥2 files → bundled. The receiver detects the magic and unpacks every file.
 *
 * ## Wire format (all integers big-endian)
 *
 *   offset  size   field
 *   0       8      magic: ASCII "ETBUNDL1" (0x45 54 42 55 4E 44 4C 31)
 *   8       2      version: u16 = 1
 *   10      2      file_count: u16  (number of files, 1..65535)
 *   12      …      file entries (file_count ×):
 *                    2   name_len: u16  (UTF-8 byte length, 0..65535)
 *                    N   name: name_len bytes, UTF-8
 *                    8   size: u64     (file content length in bytes)
 *                    S   content: size bytes
 *
 * No per-file CRC: the whole bundle is integrity-protected by the
 * transfer-level CRC32 (descriptor) + RaptorQ + the two frame CRCs. The magic
 * is 8 bytes to make an accidental collision with an ordinary single file's
 * first bytes effectively impossible.
 *
 * The Android receiver mirrors this exact layout in `BundleParser.kt`.
 */

export const BUNDLE_MAGIC = "ETBUNDL1"
const BUNDLE_VERSION = 1
/**
 * Wire (compressed) transfer ceiling, mirrored from
 * `raptorq_core::MAX_OBJECT_BYTES`. Bounds the RaptorQ object that is actually
 * transmitted symbol-by-symbol over the QR stream — not the post-decompression
 * size. 32 MiB is already a very long QR playout.
 */
export const MAX_TRANSFER_BYTES = 32 * 1024 * 1024
export const MAX_TRANSFER_MIB = MAX_TRANSFER_BYTES / (1024 * 1024)
/**
 * Receiver budget for the original (post-decompression) size, mirrored from
 * `raptorq_core::MAX_ORIGINAL_BYTES` / the JS receiver's
 * `MAX_DECOMPRESSED_BYTES`. This is what the sender's select page checks against
 * when warning about an oversized selection: a highly compressible object can
 * be under the wire ceiling yet far above 32 MiB once expanded.
 */
export const MAX_ORIGINAL_BYTES = 256 * 1024 * 1024
export const MAX_ORIGINAL_MIB = MAX_ORIGINAL_BYTES / (1024 * 1024)
export const MAX_BUNDLE_NAME_BYTES = 0xffff
/** Product cap that keeps bundle indexes and receiver history operations bounded. */
export const MAX_BUNDLE_FILES = 4096

/** One file inside a bundle. `data` is the raw file content. */
export interface BundleEntry {
  name: string
  data: Uint8Array
}

export interface BundleManifestEntry {
  name: string
  size: number
}

export interface BuiltBundle {
  bytes: Uint8Array
  entries: BundleManifestEntry[]
}

/** True if `bytes` begins with the bundle magic (8 bytes). */
export function isBundle(bytes: Uint8Array): boolean {
  if (bytes.length < 12) return false
  for (let i = 0; i < 8; i++) {
    if (bytes[i] !== BUNDLE_MAGIC.charCodeAt(i)) return false
  }
  return true
}

/** Encode a big-endian u16 into a 2-byte array. */
function u16be(v: number): [number, number] {
  return [(v >>> 8) & 0xff, v & 0xff]
}

/** Encode a non-negative safe-integer as a big-endian u64 (8 bytes). */
function u64be(v: number): number[] {
  // Split via BigInt to stay correct above 2^53 (theoretical only; files this
  // large are impractical over QR, but the format is defined as u64).
  const big = BigInt(v)
  const out: number[] = []
  for (let i = 7; i >= 0; i--) {
    out.push(Number((big >> BigInt(i * 8)) & 0xffn))
  }
  return out
}

/**
 * Pack `files` into a single bundle byte array. Files are read fully into
 * memory (the single-file path already does the same).
 */
export async function buildBundle(files: File[]): Promise<BuiltBundle> {
  if (files.length === 0) {
    throw new Error("buildBundle: no files")
  }
  if (files.length > MAX_BUNDLE_FILES) {
    throw new Error(`一次最多发送 ${MAX_BUNDLE_FILES} 个文件，请分批发送`)
  }

  // Validate the complete allocation before reading any file into memory.
  const nameBytes: Uint8Array[] = files.map((f) => new TextEncoder().encode(f.name))
  let total = 8 + 2 + 2
  for (let i = 0; i < files.length; i++) {
    if (nameBytes[i].length > MAX_BUNDLE_NAME_BYTES) {
      throw new Error(`文件名 UTF-8 编码超过 ${MAX_BUNDLE_NAME_BYTES} 字节: ${files[i].name}`)
    }
    total += 2 + nameBytes[i].length + 8 + files[i].size
    if (!Number.isSafeInteger(total)) {
      throw new Error("打包后内容大小溢出")
    }
    if (total > MAX_ORIGINAL_BYTES) {
      throw new Error(`多文件包原始大小超过 ${MAX_ORIGINAL_MIB} MiB 接收上限，请分批发送`)
    }
  }

  // Allocate the final container once, then read each file directly into it.
  // The old implementation retained every per-file ArrayBuffer while also
  // allocating the complete bundle, doubling peak memory for large batches.
  const out = new Uint8Array(total)
  const dv = new DataView(out.buffer)
  let o = 0

  // magic
  for (let i = 0; i < 8; i++) out[o++] = BUNDLE_MAGIC.charCodeAt(i)
  // version (u16 BE)
  dv.setUint16(o, BUNDLE_VERSION)
  o += 2
  // file_count (u16 BE)
  dv.setUint16(o, files.length)
  o += 2

  const manifest: BundleManifestEntry[] = []
  for (let i = 0; i < files.length; i++) {
    const name = nameBytes[i]
    dv.setUint16(o, name.length)
    o += 2
    out.set(name, o)
    o += name.length
    // size as u64 BE
    const data = new Uint8Array(await files[i].arrayBuffer())
    // Defensive: under memory pressure `arrayBuffer()` can silently return a
    // SHORT buffer. Without this check the manifest/CRC would be computed
    // over the truncated bytes — internally consistent, so every
    // receiver-side check passes and the user silently gets a corrupt file.
    // Same guard as the single-file path in compress.worker's processFiles.
    if (data.length !== files[i].size) {
      throw new Error(
        `文件「${files[i].name}」读取不完整（期望 ${files[i].size} 字节，实际 ${data.length} 字节），` +
          `请重试或拆分文件后发送。`
      )
    }
    const sizeBytes = u64be(data.length)
    for (const b of sizeBytes) out[o++] = b
    // content
    out.set(data, o)
    o += data.length
    manifest.push({ name: files[i].name, size: data.length })
  }

  return { bytes: out, entries: manifest }
}
