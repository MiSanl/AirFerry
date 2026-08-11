/**
 * Large-transfer segmentation for the browser sender.
 *
 * A logical file larger than a single RaptorQ object's 32 MiB wire budget is
 * split into fixed `SEGMENT_RAW_BYTES` (8 MiB) *raw* segments. Each segment is
 * independently compressed and independently RaptorQ-encoded as a descriptor-v4
 * child session (child session id = `deriveSegment(root, index)`), so the
 * receiver recovers one segment at a time and assembles the full file.
 *
 * This module only handles the *splitting bookkeeping*: slicing the raw file,
 * computing each segment's SHA-256 (the descriptor's `raw_sha256`), and
 * deriving the deterministic child session id. Compression + RaptorQ encoding
 * stay in the existing `compress` / `SenderSessionWasm.new_segment` paths.
 *
 * Protocol constants mirror `core/transfer-engine/src/segment.rs`:
 *   - `SEGMENT_RAW_BYTES` = 8 MiB
 *   - child id = FNV-1a 128 over b"AirFerry.segment.v1" || root BE || index BE
 */

import { deriveSessionId } from "./session"

/** Fixed uncompressed segment size (mirrors Rust `SEGMENT_RAW_BYTES`). */
export const SEGMENT_RAW_BYTES = 8 * 1024 * 1024
/** Resource ceiling mirrored from Rust `MAX_SEGMENT_COUNT`. */
export const MAX_SEGMENT_COUNT = 131_072

/** Session-id domain tag for segment child ids (mirrors Rust). */
const SEGMENT_DOMAIN = "AirFerry.segment.v1"

const FNV128_OFFSET_BIAS = 0x6c62272e07bb01426b82175983ad0b58n
const FNV128_PRIME = 0x0000000001000000000000000000013bn
const U128_MASK = (1n << 128n) - 1n
const MASK64 = (1n << 64n) - 1n

/** Canonical count of segments for a root file of `rootSize` bytes. */
export function segmentCountFor(rootSize: number): number {
  if (!Number.isSafeInteger(rootSize) || rootSize < 0) {
    throw new Error(`invalid root file size: ${rootSize}`)
  }
  if (rootSize <= 0) return 1
  const count = Math.max(1, Math.ceil(rootSize / SEGMENT_RAW_BYTES))
  if (count > MAX_SEGMENT_COUNT) {
    throw new Error(`segment count ${count} exceeds limit ${MAX_SEGMENT_COUNT}`)
  }
  return count
}

/** Canonical raw length of `segmentIndex` within a root file of `rootSize`. */
export function segmentRawLen(rootSize: number, segmentIndex: number): number {
  const off = segmentIndex * SEGMENT_RAW_BYTES
  return Math.min(SEGMENT_RAW_BYTES, Math.max(0, rootSize - off))
}

/** True when a root of `rootSize` bytes must be segmented (> 1 segment). */
export function needsSegmentation(rootSize: number): boolean {
  return segmentCountFor(rootSize) > 1
}

/** Canonical offset of `segmentIndex` within the root file. */
export function segmentOffset(segmentIndex: number): number {
  return segmentIndex * SEGMENT_RAW_BYTES
}

/** SHA-256 of `bytes` via the WebCrypto subtle API. Returns raw 32-byte digest. */
export async function sha256(bytes: Uint8Array): Promise<Uint8Array> {
  const digest = await crypto.subtle.digest("SHA-256", bytes.slice().buffer as ArrayBuffer)
  return new Uint8Array(digest)
}

/**
 * Derive the deterministic child session id for a segment.
 * Mirrors Rust `SessionId::derive_segment` (FNV-1a 128 over the domain tag +
 * root BE + index BE). Returns `{ lo, hi }`.
 */
export function deriveSegmentId(
  rootSessionId: { lo: bigint; hi: bigint },
  segmentIndex: number
): { lo: bigint; hi: bigint } {
  let h = FNV128_OFFSET_BIAS
  const feed = (bytes: ArrayLike<number>) => {
    for (let i = 0; i < bytes.length; i++) {
      h ^= BigInt(bytes[i])
      h = (h * FNV128_PRIME) & U128_MASK
    }
  }
  // Domain tag bytes + root as big-endian + index as big-endian, matching Rust.
  feed(new TextEncoder().encode(SEGMENT_DOMAIN))
  feed(beBytes128(rootSessionId))
  feed(beBytes32(segmentIndex))
  return { lo: h & MASK64, hi: (h >> 64n) & MASK64 }
}

/**
 * Slice `file` into its canonical raw segments. Returns one `Uint8Array` per
 * segment (its raw, uncompressed bytes). For a file ≤ 8 MiB this returns a
 * single segment equal to the whole file.
 *
 * Reads the whole file into memory; for very large files callers should stream
 * `File.slice` per segment instead (see `sliceSegment`).
 */
export async function sliceSegments(file: File): Promise<Uint8Array[]> {
  const count = segmentCountFor(file.size)
  const out: Uint8Array[] = []
  for (let i = 0; i < count; i++) {
    out.push(await sliceSegment(file, i))
  }
  return out
}

/** Read exactly one canonical segment's raw bytes (streams via File.slice). */
export async function sliceSegment(file: File, segmentIndex: number): Promise<Uint8Array> {
  const start = segmentOffset(segmentIndex)
  const end = Math.min(file.size, start + SEGMENT_RAW_BYTES)
  if (start >= end) {
    throw new Error(`segment ${segmentIndex} out of range`)
  }
  const blob = file.slice(start, end)
  return new Uint8Array(await blob.arrayBuffer())
}

/**
 * Build the descriptor-v4 metadata for a single segment of a root file.
 * `raw` is that segment's uncompressed bytes (used for the SHA-256).
 */
export async function buildSegmentMeta(
  rootSessionId: { lo: bigint; hi: bigint },
  segmentIndex: number,
  segmentCount: number,
  rootOriginalSize: number,
  raw: Uint8Array
): Promise<SegmentMetaWasm> {
  const sha = await sha256(raw)
  return {
    rootSessionIdLo: rootSessionId.lo & 0xffffffffffffffffn,
    rootSessionIdHi: rootSessionId.hi & 0xffffffffffffffffn,
    rootSessionId,
    segmentIndex,
    segmentCount,
    originalOffset: segmentOffset(segmentIndex),
    rootOriginalSize,
    rawSha256: sha
  }
}

/**
 * Descriptor-v4 segment metadata in the form consumed by
 * `SenderSessionWasm.new_segment` (lo/hi split for the wasm-bindgen u64 args).
 */
export interface SegmentMetaWasm {
  /** Decimal-safe BigInt form of the root session id. */
  rootSessionId: { lo: bigint; hi: bigint }
  /** lo 64 bits of the root session id (wasm arg). */
  rootSessionIdLo: bigint
  /** hi 64 bits of the root session id (wasm arg). */
  rootSessionIdHi: bigint
  segmentIndex: number
  segmentCount: number
  originalOffset: number
  rootOriginalSize: number
  /** SHA-256 of this segment's uncompressed bytes (32 bytes). */
  rawSha256: Uint8Array
}

/** Big-endian 128-bit bytes of a `{lo,hi}` session id (mirrors Rust to_be_bytes). */
function beBytes128(id: { lo: bigint; hi: bigint }): Uint8Array {
  let full = ((id.hi & MASK64) << 64n) | (id.lo & MASK64)
  const out = new Uint8Array(16)
  for (let i = 15; i >= 0; i--) {
    out[i] = Number(full & 0xffn)
    full >>= 8n
  }
  return out
}

/** Big-endian 32-bit bytes of `v`. */
function beBytes32(v: number): Uint8Array {
  const out = new Uint8Array(4)
  out[0] = (v >>> 24) & 0xff
  out[1] = (v >>> 16) & 0xff
  out[2] = (v >>> 8) & 0xff
  out[3] = v & 0xff
  return out
}

// Re-export for parity with the root session-id derivation used elsewhere.
export { deriveSessionId }
