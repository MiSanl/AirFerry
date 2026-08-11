/**
 * File-preparation worker.
 *
 * Moves the heavy, synchronous-WASM file-prep pipeline (bundle build →
 * three-algorithm compression → CRC32 → content fingerprint → session-id
 * derivation) OFF the main thread, so the UI stays responsive while the sender
 * processes the chosen file(s). Without this, the zstd/lzma WASM compressors
 * block the main thread for seconds (level-1 zstd / level-9 xz on a few MB),
 * freezing all rendering — including any "compressing…" spinner — so the page
 * looks frozen. In a dedicated worker the main thread is free to paint a
 * progress overlay throughout.
 *
 * Bundled by Parcel 2 via `new Worker(new URL("./compress.worker.ts", import.meta.url),`
 * `{ type: "module" })` in options.tsx — one worker bundle is emitted per build
 * target (chrome-mv2/mv3, firefox-mv2/mv3). The CSP already permits worker
 * scripts + WASM (`'self'` + `wasm-unsafe-eval`/`wasm-eval`), and
 * `chrome.runtime.getURL` + `fetch` are available inside a dedicated worker
 * spawned by an extension page, so the existing zstd/lzma loaders work
 * unchanged.
 *
 * ## Message protocol
 *
 * In (from main):
 *   { type: "wasm-init", zstd?: ArrayBuffer | null }
 *       — optional zstd bytes. Always marks the worker ready; missing/null zstd
 *         still allows compress (preparePayload falls back to raw).
 *   { jobId: number, files: File[] }
 *       — chosen files (File is structurally cloneable).
 *   { jobId: number, text: string, name?: string }
 *       — pure text item. Wrapped in ETTEXTv1; optional `name` becomes the
 *         descriptor filename (default "文字消息.txt", normalized to *.txt).
 *
 * A logical transfer (file, bundle, or text) is compressed **once**; if the
 * compressed stream fits a single RaptorQ object it is sent directly, otherwise
 * the compressed stream is split into `SEGMENT_RAW_BYTES` segments and all
 * segments are delivered in the `done` message (each transferable).
 *
 * `jobId` is the main-thread compress epoch. Stale jobs are ignored so list
 * edits / "back to select" do not apply late results (CPU may still finish the
 * in-flight WASM compress; only the post is suppressed).
 *
 * Out — progress (stage-based; the compressors are synchronous WASM so no
 * mid-stage percentage is possible, only phase boundaries):
 *   { phase: "reading" | "bundling" | "zstd" | "xz" | "finalizing", jobId? }
 *
 * Out — final result:
 *   { phase: "done", jobId,
 *     compressed: ArrayBuffer,          — transferable (zero-copy back to main)
 *     algorithm, originalSize, compressedSize,
 *     preCrc32,
 *     sessionId: { lo: string, hi: string },
 *     displayName }
 *
 * Out — error:
 *   { phase: "error", message: string, jobId? }
 */

/// <reference lib="webworker" />

import { preparePayload, initZstdFromBytes } from "@/wasm/compress"
import { crc32 } from "@/wasm/crc32"
import { contentFingerprint, deriveSessionId } from "@/wasm/session"
import {
  deriveSegmentId,
  segmentCountFor,
  segmentOffset,
  sliceSegment
} from "@/wasm/segment"
import { buildBundle, MAX_ORIGINAL_BYTES } from "@/wasm/bundle"
import { buildTextPayload, TEXT_DISPLAY_NAME } from "@/wasm/text"
import { normalizeDraftFilename } from "@/storage/textDrafts"

/**
 * Single-file read ceiling for the browser sender.
 *
 * `file.arrayBuffer()` reads the whole file into one contiguous buffer. On a
 * memory-constrained tab this either OOMs or silently returns a *shorter* buffer
 * than the file (observed: a 5 GB file came back as ~1193 MB), which then ships
 * a corrupt / truncated payload. Cap single-file reads here and reject oversized
 * files up front with a clear error instead of encoding broken data.
 * Segmentation happens *after* compression, so a single oversized input still
 * has to fit in memory all at once.
 */
const MAX_SINGLE_READ_BYTES = 1 * 1024 * 1024 * 1024 // 1 GiB

/** Human-readable byte count (mirrors ParamsPage.formatBytes). */
function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}

/** Decimal-string form of the 128-bit session id, clone-safe across threads. */
export interface SessionIdDto {
  lo: string
  hi: string
}

/** Result message payload (phase = "done"). */
export interface CompressResult {
  phase: "done"
  /** Main-thread compress epoch that issued this job. */
  jobId: number
  /** True when the payload was split into descriptor-v5 segments. */
  needsSegmentation: boolean
  /**
   * Single-object transfer (non-segmented). Present when
   * `needsSegmentation` is false.
   */
  compressed: ArrayBuffer
  algorithm: number
  originalSize: number
  compressedSize: number
  preCrc32: number
  sessionId: SessionIdDto
  displayName: string
  /**
   * Large-transfer segment list (only when `needsSegmentation`). Every segment
   * of the compressed stream is delivered in this `done` message, each backed
   * by its own transferable buffer.
   */
  rootSessionId: SessionIdDto
  segmentCount: number
  /** Whole decompressed size of the root transfer. */
  rootOriginalSize: number
  rootDisplayName: string
  segments: SegmentSpec[]
}

/** One descriptor-v5 segment of the compressed root stream. */
export interface SegmentSpec {
  /** This segment's slice of the compressed stream (transferable). */
  compressed: ArrayBuffer
  /** Compression algorithm of the whole stream (shared by every segment). */
  algorithm: number
  /** Whole decompressed size of the root transfer (`file_meta.original_size`). */
  originalSize: number
  /** This segment's compressed byte count (`file_meta.compressed_size`). */
  compressedSize: number
  /** CRC32 over the whole pre-compression payload (`file_meta.crc32`). */
  preCrc32: number
  segmentIndex: number
  segmentCount: number
  /** Offset of this segment within the compressed stream. */
  originalOffset: number
  /** Whole compressed stream size (`SegmentMeta.root_original_size`). */
  rootOriginalSize: number
  rootSessionId: SessionIdDto
  childSessionId: SessionIdDto
  /** SHA-256 (raw 32 bytes) of this segment's compressed bytes. */
  rawSha256: ArrayBuffer
  /** SHA-256 of the complete decompressed root payload. */
  rootSha256: ArrayBuffer
  displayName: string
}

export type CompressPhase = "reading" | "bundling" | "zstd" | "xz" | "finalizing"

/** All messages the worker posts back to the main thread. */
export type WorkerMessage =
  | { phase: CompressPhase; jobId?: number }
  | CompressResult
  | { phase: "error"; message: string; jobId?: number }

type PendingJob =
  | { kind: "files"; jobId: number; files: File[] }
  | { kind: "text"; jobId: number; text: string; name?: string }

/** Latest pending request while the worker is still waiting for first init. */
let pendingJob: PendingJob | null = null
/**
 * Ready to accept compress jobs. Set true by `wasm-init` even when zstd bytes
 * are missing — preparePayload already falls back to raw without zstd.
 * Also set true on the first job if the main thread never sent wasm-init
 * (defensive: never hang the UI forever).
 */
let ready = false
/** Latest accepted job id; posts for older ids are suppressed. */
let activeJobId = -1
/** True while processFiles/processText is running (single-flight). */
let busy = false

self.onmessage = async (
  e: MessageEvent<
    | { jobId?: number; files: File[] }
    | { jobId?: number; text: string; name?: string }
    | { type: "wasm-init"; zstd?: ArrayBuffer | null }
  >
) => {
  const data = e.data

  // Handle WASM pre-load message (sent from main thread before compression).
  if ("type" in data && data.type === "wasm-init") {
    const zstd = (data as { type: "wasm-init"; zstd?: ArrayBuffer | null }).zstd
    if (zstd && zstd.byteLength > 0) {
      try {
        initZstdFromBytes(zstd)
      } catch (err) {
        console.warn("initZstdFromBytes failed; compress will fall back to raw:", err)
      }
    } else {
      // Explicit null/empty: still ready — raw-only path is fine.
      console.warn("wasm-init without zstd bytes; compress will fall back to raw when zstd unavailable")
    }
    ready = true
    drainPending()
    return
  }

  const jobId =
    typeof (data as { jobId?: number }).jobId === "number"
      ? (data as { jobId: number }).jobId
      : 0

  if ("text" in data && typeof (data as { text?: unknown }).text === "string") {
    const text = (data as { text: string; name?: string }).text
    const name = (data as { text: string; name?: string }).name
    if (text.length === 0) {
      post({ phase: "error", message: "empty text", jobId })
      return
    }
    enqueueOrRun({ kind: "text", jobId, text, name })
    return
  }

  const files = (data as { files?: File[] }).files
  if (!files || files.length === 0) {
    post({ phase: "error", message: "no files", jobId })
    return
  }
  enqueueOrRun({ kind: "files", jobId, files })
}

function enqueueOrRun(job: PendingJob): void {
  // Newer job always supersedes any queued request.
  activeJobId = job.jobId
  if (!ready) {
    // Main always posts wasm-init, including an explicit null when preloading
    // failed. Queueing here preserves the preloaded-byte fast path when the
    // user clicks Send before that asynchronous fetch completes.
    pendingJob = job
    return
  }
  if (busy) {
    // Replace pending; the in-flight job will finish but its posts are
    // suppressed via activeJobId checks.
    pendingJob = job
    return
  }
  void runJob(job)
}

function drainPending(): void {
  if (!ready || busy || pendingJob === null) return
  const job = pendingJob
  pendingJob = null
  void runJob(job)
}

async function runJob(job: PendingJob): Promise<void> {
  busy = true
  activeJobId = job.jobId
  try {
    if (job.kind === "files") {
      await processFiles(job.files, job.jobId)
    } else {
      await processText(job.text, job.name, job.jobId)
    }
  } finally {
    busy = false
    // If a newer job was queued while we were busy, run it now
    // (enqueueOrRun already set activeJobId to that pending job).
    if (pendingJob !== null) {
      const next = pendingJob
      pendingJob = null
      void runJob(next)
    }
  }
}

function isCurrent(jobId: number): boolean {
  return jobId === activeJobId
}

async function processFiles(files: File[], jobId: number) {
  try {
    if (files.length === 1 && files[0].size === 0) {
      throw new Error("暂不支持发送空文件（0 B）")
    }
    if (!isCurrent(jobId)) return
    post({ phase: "reading", jobId })
    const isBundle = files.length > 1
    let raw: Uint8Array
    let displayName: string
    let sessionId: { lo: bigint; hi: bigint }
    if (isBundle) {
      if (!isCurrent(jobId)) return
      post({ phase: "bundling", jobId })
      const built = await buildBundle(files)
      if (!isCurrent(jobId)) return
      raw = built.bytes
      displayName = `${files.length}个文件打包`
      console.log(`Bundle: ${files.length} files, ${raw.length} bytes pre-compress`)
      const fp = computeFingerprint(raw)
      const mtimeMax = files.reduce(
        (m, f) => (f.lastModified > m ? f.lastModified : m),
        0
      )
      const namesJoined = files.map((f) => f.name).join("\u0001")
      sessionId = deriveSessionId(namesJoined, BigInt(raw.length), BigInt(mtimeMax), fp)
    } else {
      const f = files[0]
      // Reject oversized single files before reading: `file.arrayBuffer()`
      // loads the whole file into one buffer and can OOM / silently truncate on
      // very large inputs. Check size up front and give a clear error instead of
      // shipping a corrupt payload.
      if (f.size > MAX_SINGLE_READ_BYTES) {
        throw new Error(
          `文件过大（${formatBytes(f.size)}），当前发送端单文件上限为 ` +
            `${MAX_SINGLE_READ_BYTES / (1024 * 1024)} MiB。请将文件拆分后分别发送。`
        )
      }
      raw = new Uint8Array(await f.arrayBuffer())
      if (!isCurrent(jobId)) return
      // Defensive: the browser may still return a short buffer even under the
      // cap. Never encode a truncated payload.
      if (raw.length !== f.size) {
        throw new Error(
          `文件读取不完整（期望 ${formatBytes(f.size)}，实际 ${formatBytes(raw.length)}），` +
            `请重试或拆分文件后发送。`
        )
      }
      displayName = f.name
      const fp = computeFingerprint(raw)
      sessionId = deriveSessionId(f.name, BigInt(f.size), BigInt(f.lastModified), fp)
    }

    await finalizeAndPost(raw, displayName, sessionId, jobId)
  } catch (err) {
    if (!isCurrent(jobId)) return
    post({ phase: "error", message: (err as Error)?.message || String(err), jobId })
  }
}

/**
 * Process a text transfer: wrap the text in the ETTEXTv1 magic, then feed the
 * bytes through the SAME compress → CRC → finalize path as a file.
 *
 * `name` (optional) is the user-chosen filename from the select page; empty /
 * missing falls back to {@link TEXT_DISPLAY_NAME}. Normalized to a safe `*.txt`
 * so the descriptor never carries path separators.
 */
async function processText(text: string, name: string | undefined, jobId: number) {
  try {
    if (!isCurrent(jobId)) return
    post({ phase: "reading", jobId })
    const raw = buildTextPayload(text)
    const fp = computeFingerprint(raw)
    const displayName =
      normalizeDraftFilename(typeof name === "string" ? name : "") || TEXT_DISPLAY_NAME
    // mtime substitute: Date.now() at send time. Deterministic enough for
    // resume within the same moment; differs across distinct sends.
    const sessionId = deriveSessionId(
      displayName,
      BigInt(raw.length),
      BigInt(Date.now()),
      fp
    )
    await finalizeAndPost(raw, displayName, sessionId, jobId)
  } catch (err) {
    if (!isCurrent(jobId)) return
    post({ phase: "error", message: (err as Error)?.message || String(err), jobId })
  }
}

/**
 * SHA-256 (raw 32 bytes) of `bytes` via WebCrypto.
 *
 * Avoids copying the whole input when it is already a whole-buffer view (the
 * common case for a single file read via `arrayBuffer()`): `subtle.digest`
 * reads `byteLength` bytes starting at `byteOffset`, so a zero-offset,
 * full-length view can be hashed in place. Only a partial/subarray view falls
 * back to `.slice()` (those are per-segment, ≤ SEGMENT_RAW_BYTES, so cheap).
 */
async function sha256Bytes(bytes: Uint8Array): Promise<ArrayBuffer> {
  const ownsWholeBuffer =
    bytes.byteOffset === 0 && bytes.byteLength === bytes.buffer.byteLength
  // Hash the backing buffer directly. A `Uint8Array` is typed
  // `Uint8Array<ArrayBufferLike>` (it can wrap a SharedArrayBuffer), which the
  // Web Crypto `BufferSource` overload rejects, so we pin the backing store to a
  // concrete `ArrayBuffer`. For a subarray view, slice() first so we only hash
  // this segment's own bytes (slice() always yields a fresh ArrayBuffer).
  const buf: ArrayBuffer = ownsWholeBuffer
    ? (bytes.buffer as ArrayBuffer)
    : (bytes.slice().buffer as ArrayBuffer)
  return crypto.subtle.digest("SHA-256", buf)
}

/** Own a transferable ArrayBuffer for `compressed` (detach-safe). */
function ownBuffer(compressed: Uint8Array): ArrayBuffer {
  const buf = compressed.buffer as ArrayBuffer
  const owns = compressed.byteOffset === 0 && compressed.byteLength === buf.byteLength
  if (owns) return buf
  const copy = new Uint8Array(compressed.byteLength)
  copy.set(compressed)
  return copy.buffer as ArrayBuffer
}

/**
 * Shared finalize tail for file, bundle, and text paths: compress once → CRC32
 * → if the compressed stream fits a single object, post a plain `done`; else
 * split the compressed stream into fixed `SEGMENT_RAW_BYTES` segments and post
 * a segmented `done` carrying every segment (each transferable). The receiver
 * concatenates the compressed segments in order and decompresses exactly once.
 *
 * Runs the heavy synchronous-WASM compress here in the worker so the main
 * thread keeps painting the progress overlay.
 */
async function finalizeAndPost(
  raw: Uint8Array,
  displayName: string,
  sessionId: { lo: bigint; hi: bigint },
  jobId: number
) {
  if (!isCurrent(jobId)) return
  // Capture every value derived from `raw` up front so the original buffer can
  // be released to GC as soon as hashing/crc are done (see below), instead of
  // holding `raw` (whole original) alongside the whole compressed stream.
  const originalSize = raw.length
  // --- Compress (zstd always; xz if compressible) ---
  const { payload: compressed, algorithm, compressedSize } = await preparePayload(
    raw,
    (phase) => {
      if (isCurrent(jobId)) post({ phase, jobId })
    }
  )
  if (!isCurrent(jobId)) return
  console.log(
    `Compression: ${originalSize} → ${compressedSize} bytes ` +
      `(${originalSize > 0 ? ((compressedSize / originalSize) * 100).toFixed(1) : "0"}%)`
  )

  // --- Segment-after-compression gate ---
  // A logical transfer is compressed **once**. If the compressed stream fits a
  // single RaptorQ object (≤ SEGMENT_RAW_BYTES after symbol padding) it is
  // sent directly. Only when the compressed stream would exceed that budget do
  // we split it into segments — this is the "压缩后分段" model. A stream larger
  // than a whole segment is always splittable (segments are slices of the same
  // compressed stream), so unlike the old raw-segment model there is no hard
  // "too big to send" failure: every size is representable.
  post({ phase: "finalizing", jobId })
  const crc = crc32(raw)
  if (!isCurrent(jobId)) return

  const rootCompressedSize = compressedSize
  // Gate also on the ORIGINAL size, not just the compressed-stream size. The
  // receiver rejects a *non-segmented* object whose `original_size` exceeds
  // MAX_ORIGINAL_BYTES (256 MiB) — see `file_meta_invalid` in receiver.rs.
  // A highly compressible file (e.g. 300 MiB → 20 MiB) would otherwise pass the
  // compressed-size gate and ship as a single descriptor-v3 object that the
  // receiver then refuses outright. When the original exceeds the single-object
  // ceiling, force the descriptor-v5 segmented path even if the compressed
  // stream fits one segment — v5 carries the whole-file `root_original_size`
  // which the receiver accepts unbounded for segmented transfers (the host
  // streams decompression to disk). The "one segment" case is still valid: the
  // v5 tail just wraps a single slice of the compressed stream.
  const originalExceedsSingleObject = originalSize > MAX_ORIGINAL_BYTES
  if (segmentCountFor(rootCompressedSize) <= 1 && !originalExceedsSingleObject) {
    // Single-object transfer.
    const outBuf = ownBuffer(compressed)
    const result: CompressResult = {
      phase: "done",
      jobId,
      needsSegmentation: false,
      compressed: outBuf,
      algorithm,
      originalSize,
      compressedSize,
      preCrc32: crc,
      sessionId: {
        lo: sessionId.lo.toString(),
        hi: sessionId.hi.toString(),
      },
      displayName,
      rootSessionId: {
        lo: sessionId.lo.toString(),
        hi: sessionId.hi.toString(),
      },
      segmentCount: 1,
      rootOriginalSize: originalSize,
      rootDisplayName: displayName,
      segments: [],
    }
    ;(self as unknown as Worker).postMessage(result, [outBuf])
    return
  }

  // --- Segmented transfer (compressed byte-stream) ---
  // Every segment shares the same `file_meta` identity: whole-file decompressed
  // size, compression algorithm, and CRC32 over the original bytes. Only the
  // per-segment compressed length, offset, and SHA-256 vary.
  const count = segmentCountFor(rootCompressedSize)
  const rootSha256 = await sha256Bytes(raw)
  // The whole original buffer is no longer needed (size, crc, root sha are
  // captured). Release it so GC can reclaim it while we slice the compressed
  // stream, instead of holding `raw` (≈ original) + `compressed` (≈ stream)
  // simultaneously for the rest of the segmentation loop.
  raw = new Uint8Array(0)
  if (!isCurrent(jobId)) return

  const segments: SegmentSpec[] = []
  const transfers: ArrayBuffer[] = []
  for (let i = 0; i < count; i++) {
    const seg = sliceSegment(compressed, i)
    if (!isCurrent(jobId)) return
    const childSessionId = deriveSegmentId(sessionId, i)
    const segSha = await sha256Bytes(seg)
    if (!isCurrent(jobId)) return
    const outBuf = ownBuffer(seg)
    segments.push({
      compressed: outBuf,
      algorithm,
      originalSize, // whole decompressed size (file_meta.original_size)
      compressedSize: seg.length, // this segment's compressed length
      preCrc32: crc, // whole-file CRC32 (file_meta.crc32)
      segmentIndex: i,
      segmentCount: count,
      originalOffset: segmentOffset(i), // offset in the compressed stream
      rootOriginalSize: rootCompressedSize, // whole compressed stream size
      rootSessionId: {
        lo: sessionId.lo.toString(),
        hi: sessionId.hi.toString(),
      },
      childSessionId: {
        lo: childSessionId.lo.toString(),
        hi: childSessionId.hi.toString(),
      },
      rawSha256: segSha, // SHA-256 over this segment's compressed bytes
      rootSha256: rootSha256.slice() as ArrayBuffer, // SHA-256 over whole original
      displayName,
    })
    transfers.push(outBuf)
  }

  const result: CompressResult = {
    phase: "done",
    jobId,
    needsSegmentation: true,
    compressed: new ArrayBuffer(0),
    algorithm,
    originalSize,
    compressedSize: rootCompressedSize,
    preCrc32: crc,
    sessionId: {
      lo: sessionId.lo.toString(),
      hi: sessionId.hi.toString(),
    },
    displayName,
    rootSessionId: {
      lo: sessionId.lo.toString(),
      hi: sessionId.hi.toString(),
    },
    segmentCount: count,
    rootOriginalSize: originalSize, // whole decompressed size
    rootDisplayName: displayName,
    segments,
  }
  ;(self as unknown as Worker).postMessage(result, transfers)
}

/**
 * Content fingerprint over the head + tail of the pre-compress bytes. Mirrors
 * the Rust `SessionId::content_fingerprint` so both ends derive the same
 * session id. Wrapped in a helper so the file and text paths stay in sync.
 */
function computeFingerprint(raw: Uint8Array): Uint8Array {
  const head = raw.slice(0, 1024)
  const tail = raw.slice(Math.max(0, raw.length - 1024))
  return contentFingerprint(head, tail)
}

/** Post a non-transferable message. */
function post(msg: WorkerMessage): void {
  ;(self as unknown as Worker).postMessage(msg)
}
