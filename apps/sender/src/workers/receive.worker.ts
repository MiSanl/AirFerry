/**
 * Receive worker — owns the single Rust `ReceiverSessionWasm` and serializes
 * all ingest calls (the receiver handle is NOT thread-safe, exactly like the
 * Android `ingestLock`). QR decode runs in a separate worker pool; this worker
 * only ingests the already-decoded frame byte arrays.
 *
 * ## Lifecycle
 *  0. main posts `{type:"wasm-init", zstd:ArrayBuffer|null}` (BEFORE `init`;
 *     worker messages are FIFO so it is processed first) → install the
 *     preloaded zstd bytes via `initZstdFromBytes`, so decompression never
 *     runtime-fetches `wasm-zstd.wasm` against this worker's own assets/ URL
 *     (that resolves to `assets/wasm-zstd.wasm` → 404; the file is deployed
 *     at the site root). Null means the main-thread preload failed/timed out
 *     — zstd is then fetched lazily at use time (correctly resolved).
 *  1. main posts `{type:"init", jobId}` → load transfer_engine WASM, mark ready
 *  2. main posts `{type:"frames", frames:Uint8Array[], jobId}` → ingest batch
 *  3. when complete bit set, main posts `{type:"assemble", jobId}` → run
 *     assemble_raw → JS decompress → verify CRC → parse text/bundle/file
 *
 * ## Epoch guard
 * `activeJobId` supersedes stale results: a new session (different jobId)
 * drops the old `ReceiverSessionWasm` and ignores late frames from the old one.
 *
 * Standalone build: transfer_engine WASM is inlined as base64 on
 * `globalThis.__WASM_TRANSFER_ENGINE__` (handled inside loader.ts).
 */

/// <reference lib="webworker" />
import { ensureWasm, ReceiverSessionWasm } from "@/wasm/loader"
import { initZstdFromBytes } from "@/wasm/compress"
import { decompressAndVerify, VerifyResult } from "@/receive/decompress"
import {
  parseRecovered,
  Recovered,
  ParseError,
} from "@/receive/parse"
import {
  hasStoredSegment,
  storeVerifiedSegment,
  type StoredSegmentTask,
} from "@/receive/taskStore"

/** ingest packed-status bit layout (mirror ingest_status.rs). */
const STATUS_COMPLETE = 0x1n
const STATUS_INGEST_ERROR = 0xffffffff00000000n

interface MetaInfo {
  fileName: string
  originalSize: number
  compressedSize: number
  compressedSizeKnown: boolean
  compression: number
  crc32: number
  crc32Known: boolean
  metaConfirmed: boolean
  segmented: boolean
  /** Canonical 128-bit root id (hex), present for descriptor-v5 segments. */
  rootId?: string
}

/** Live session progress snapshot (mirrors progress_json fields). */
interface ProgressSnapshot {
  totalSymbols: number
  decodedSymbols: number
  receivedSymbols: number
  decodedBlocks: number
  totalBlocks: number
  decodedFraction: number
  framesSeen: number
  framesDuplicate: number
  framesCorrupt: number
  metaConfirmed: boolean
  symbolSize: number
  complete: boolean
}

interface IngestBatchResult {
  complete: boolean
  /** number of frames in this batch that were accepted (contributed a symbol). */
  acceptedCount: number
  receivedSymbols: number
  mismatchStreak: number
  /** Optional richer snapshot when a session exists. */
  snapshot: ProgressSnapshot | null
}

let session: ReceiverSessionWasm | null = null
let activeJobId = 0
let ready = false
let lastMetaSent = false

/**
 * wasm-bindgen `free()` is not idempotent: a second call can dereference an
 * already-released native pointer (and this object's `__wbg_ptr` becomes 0, so
 * any later method call throws "null pointer passed to rust"). Guard every
 * release so a session is never freed twice, no matter how many code paths
 * (reset / assemble / segment-complete / error) reach it.
 */
const freedSessions = new WeakSet<ReceiverSessionWasm>()
function freeSession(s: ReceiverSessionWasm | null | undefined): void {
  if (!s || freedSessions.has(s)) return
  freedSessions.add(s)
  try {
    s.free()
  } catch {
    /* best-effort */
  }
}

function post(msg: unknown, transfer: Transferable[] = []): void {
  ;(postMessage as (m: unknown, transfer?: Transferable[]) => void)(msg, transfer)
}

function recoveredTransferables(recovered: Recovered): Transferable[] {
  const buffers = new Set<ArrayBuffer>()
  const add = (bytes: Uint8Array) => {
    if (bytes.buffer instanceof ArrayBuffer) buffers.add(bytes.buffer)
  }
  if (recovered.kind === "file") add(recovered.data)
  if (recovered.kind === "bundle") {
    for (const entry of recovered.entries) add(entry.data)
  }
  return [...buffers]
}

/** Drop the current session (if any); called on a new job / reset. */
function dropSession(): void {
  freeSession(session)
  session = null
  lastMetaSent = false
}

function readMeta(s: ReceiverSessionWasm): MetaInfo {
  const segmented = s.is_segmented()
  const rootId = segmented
    ? `${s.root_session_id_hi().toString(16).padStart(16, "0")}${s
        .root_session_id_lo()
        .toString(16)
        .padStart(16, "0")}`
    : undefined
  return {
    fileName: s.file_name(),
    originalSize: Number(s.original_size()),
    compressedSize: Number(s.compressed_size()),
    compressedSizeKnown: s.compressed_size_known(),
    compression: s.compression(),
    crc32: s.crc32() >>> 0,
    crc32Known: s.crc32_known(),
    metaConfirmed: s.meta_confirmed(),
    segmented,
    rootId,
  }
}

/** Read an 8-byte big-endian u64 half of the 16-byte session_id in a frame. */
function readSidHalf(f: Uint8Array, offset: number): bigint {
  let v = 0n
  for (let i = 0; i < 8; i++) v = (v << 8n) | BigInt(f[offset + i])
  return v
}

/** Read a big-endian u32 at `offset` from a frame (header field). */
function readU32BE(f: Uint8Array, offset: number): number {
  return ((f[offset] << 24) | (f[offset + 1] << 16) | (f[offset + 2] << 8) | f[offset + 3]) >>> 0
}

/**
 * Estimate the total source symbols K from the first data/descriptor frame's
 * header (`total_symbols` at offset 32). Used only before meta is confirmed to
 * show approximate progress (mirrors Android's `estimatedTotalSymbols`), so the
 * first codes don't look like a stuck 0%.
 */
function estimateTotalSymbols(frames: Uint8Array[]): number {
  for (const f of frames) {
    if (f.length >= 40) {
      const v = readU32BE(f, 32)
      if (v > 0) return v
    }
  }
  return 0
}

/** Parse the session's progress_json into a flat ProgressSnapshot. */
function parseProgress(s: ReceiverSessionWasm): ProgressSnapshot {
  const j = JSON.parse(s.progress_json()) as Record<string, unknown>
  const num = (v: unknown, d = 0): number =>
    typeof v === "number" ? v : typeof v === "string" ? Number(v) || d : d
  const boo = (v: unknown): boolean => v === true || v === 1
  return {
    totalSymbols: num(j.total_symbols),
    decodedSymbols: num(j.decoded_symbols),
    receivedSymbols: num(j.received_symbols),
    decodedBlocks: num(j.decoded_blocks),
    totalBlocks: num(j.total_blocks),
    decodedFraction: num(j.decoded_fraction),
    framesSeen: num(j.frames_seen),
    framesDuplicate: num(j.frames_duplicate),
    framesCorrupt: num(j.frames_corrupt),
    metaConfirmed: boo(j.meta_confirmed),
    symbolSize: num(j.symbol_size),
    complete: boo(j.complete),
  }
}

/** Ingest a batch of frame byte arrays into the session (lazy-create it). */
function ingestBatch(frames: Uint8Array[], jobId: number): IngestBatchResult {
  let result: IngestBatchResult = {
    complete: false,
    acceptedCount: 0,
    receivedSymbols: 0,
    mismatchStreak: 0,
    snapshot: null,
  }
  if (!session) {
    // Bootstrap. Preferred: the FIRST descriptor in this batch (validates the
    // whole OTI end-to-end). But we must NOT require one — in 4-code mode the
    // descriptor only lives in the top-left code, so requiring it forces the
    // user to hit exactly that code to start a transfer. Cache-only bootstrap:
    // if there's no descriptor, read the session id from ANY data frame's
    // header and create a `new_pending` session — Rust buffers pre-descriptor
    // data frames and confirms OTI as soon as a descriptor arrives. So scanning
    // any code can begin buffering; the transfer establishes on the next
    // descriptor tick.
    const descIdx = frames.findIndex((f) => f.length >= 64 && (f[3] & 0x01) !== 0)
    if (descIdx >= 0) {
      try {
        session = ReceiverSessionWasm.from_descriptor(frames[descIdx])
      } catch (e) {
        // Bad descriptor (CRC / hostile payload). Surface + skip; the next batch
        // may carry a valid one.
        post({
          type: "warn",
          message: `描述符无效，等待下一个: ${String(e)}`,
          jobId,
        })
        return result
      }
      // Send meta once the descriptor is confirmed.
      maybePostMeta(jobId)
      // Ingest the frames AFTER the descriptor (the descriptor itself is already
      // consumed by from_descriptor).
      frames = frames.slice(descIdx + 1)
    } else {
      // Cache-only bootstrap from any valid data frame's session id.
      const probe = frames.find((f) => f.length >= 64 && (f[3] & 0x01) === 0)
      if (!probe) return result
      try {
        // Frame session_id is a big-endian 128-bit at header[4..20]:
        //   [4..12] = HIGH 64 bits, [12..20] = LOW 64 bits.
        // `new(lo, hi)` expects (lo << hi<<64); passing them reversed made the
        // cache-bootstrap session's id differ from the descriptor's, so a
        // descriptor fed after bootstrapping was rejected → transfer never
        // established ("看到解码了但建立不了文件流").
        const sidLo = readSidHalf(probe, 12)
        const sidHi = readSidHalf(probe, 4)
        session = new ReceiverSessionWasm(sidLo, sidHi)
      } catch {
        return result
      }
    }
  }

  let received = 0
  for (const f of frames) {
    const status = session.ingest(f)
    if (status === STATUS_INGEST_ERROR) {
      continue // frame rejected (bad CRC / length)
    }
    received = Number((status >> 32n) & 0xffffffffn)
    if (status & 0x2n) result.acceptedCount++
    result.mismatchStreak = Number((status >> 8n) & 0xffffn)
    if (status & STATUS_COMPLETE) {
      result.complete = true
      result.receivedSymbols = received
      break
    }
  }
  if (received > 0) result.receivedSymbols = received
  // Refresh meta post-confirm if not yet sent.
  maybePostMeta(jobId)
  // Rich progress snapshot for the UI (rates, decoded/total symbols, sizes).
  try {
    result.snapshot = parseProgress(session)
    // Before meta is confirmed, progress_json reports total_symbols = 0; estimate
    // it from the frame header so the first codes show moving progress instead of
    // a stuck 0% (mirrors Android's estimatedTotalSymbols).
    if (result.snapshot && result.snapshot.totalSymbols === 0) {
      const est = estimateTotalSymbols(frames)
      if (est > 0) result.snapshot.totalSymbols = est
    }
  } catch {
    /* progress_json parse failure is non-fatal */
  }
  return result
}

function maybePostMeta(jobId: number): void {
  if (!session || lastMetaSent) return
  if (!session.meta_confirmed()) return
  const meta = readMeta(session)
  post({ type: "meta", meta, jobId })
  lastMetaSent = true
  // Early duplicate detection: the moment a segmented descriptor is confirmed,
  // check whether this segment was already stored. If so, tell the UI to skip it
  // immediately instead of scanning the whole segment again and only
  // de-duplicating after it fully plays out.
  if (meta.segmented && meta.rootId) {
    const rootLo = session.root_session_id_lo()
    const rootHi = session.root_session_id_hi()
    const index = session.segment_index()
    // Capture the job id at spawn time; this IIFE awaits IndexedDB and so runs
    // outside the serialized messageQueue. While it awaits, the user may reset
    // / switch transfers (bumping activeJobId and bootstrapping a brand-new
    // session). Re-check activeJobId before dropSession() so a stale duplicate
    // detection for transfer A cannot free the now-live session for transfer B
    // (the `segment-duplicate` message above is already guarded by jobId on the
    // UI side, but dropSession() is unconditional without this check).
    const spawnJobId = jobId
    void (async () => {
      try {
        if (await hasStoredSegment(rootLo, rootHi, index)) {
          if (spawnJobId !== activeJobId) return
          post({
            type: "segment-duplicate",
            index,
            jobId: spawnJobId,
          })
          dropSession()
        }
      } catch {
        // Store unavailable — fall through to normal scanning / deferred dedupe.
      }
    })()
  }
}

/** Assemble + decompress + verify + parse. Throws on hard failure. */
async function assembleAndRecover(jobId: number): Promise<{
  verify: VerifyResult
  recovered: Recovered
}> {
  if (!session) throw new Error("assemble 前没有活动的接收会话")
  const raw = session.assemble_raw()
  if (raw.length === 0) {
    throw new Error("恢复尚未完成或组装失败")
  }
  const meta = readMeta(session)
  const verify = await decompressAndVerify(
    raw,
    meta.compression,
    meta.originalSize,
    meta.crc32,
    meta.crc32Known
  )
  const recovered = parseRecovered(verify.bytes, meta.fileName)
  return { verify, recovered }
}

/**
 * Handle a completed descriptor-v5 segment: verify it, then atomically publish
 * the bytes + receipt ledger to IndexedDB. No root-sized Uint8Array is created;
 * the UI streams completed tasks to a user-selected file when supported.
 *
 * Returns `null` while the transfer is still awaiting more segments.
 */
async function handleSegmentComplete(jobId: number): Promise<StoredSegmentTask | null> {
  if (!session || !session.is_segmented()) return null
  const seg = {
    rootLo: session.root_session_id_lo(),
    rootHi: session.root_session_id_hi(),
    index: session.segment_index(),
    count: session.segment_count(),
    offset: Number(session.original_offset()),
    rootOriginalSize: Number(session.root_original_size()),
  }
  const meta = readMeta(session)

  // Segments carry **compressed** bytes in the compressed-stream model. Each
  // recovered segment is a slice of the single root compressed stream, so it is
  // NOT independently decompressible; it is stored as-is (compressed) and the
  // receiver concatenates all segments in order and decompresses exactly once
  // at save time. Per-segment CRC32 does not apply — CRC32 is over the whole
  // original payload.
  const compressed = session.assemble_raw()
  if (compressed.length === 0) {
    throw new Error("分段恢复尚未完成或组装失败")
  }

  // Descriptor v5 requires a SHA-256 over this segment's **compressed** bytes.
  const expectedSha = session.raw_sha256()
  const expectedRootSha = session.root_sha256()
  if (expectedSha.length !== 32 || expectedRootSha.length !== 32) {
    throw new Error("分段描述符缺少有效的分段或整文件 SHA-256")
  }
  const actualSha = new Uint8Array(
    await crypto.subtle.digest(
      "SHA-256",
      compressed.slice().buffer as ArrayBuffer
    )
  )
  if (!actualSha.every((v, i) => v === expectedSha[i])) {
    throw new Error(`分段 ${seg.index + 1}/${seg.count} SHA-256 校验失败，已拒绝写入`)
  }

  // Range consistency against the canonical compressed-stream coordinates.
  const SEGMENT_BYTES = 32 * 1024 * 1024 - 65_528 // mirrors Rust SEGMENT_RAW_BYTES
  const expectedCount = Math.ceil(seg.rootOriginalSize / SEGMENT_BYTES)
  const expectedOffset = seg.index * SEGMENT_BYTES
  if (
    !Number.isSafeInteger(seg.rootOriginalSize) ||
    seg.rootOriginalSize <= 0 ||
    !Number.isInteger(seg.count) ||
    seg.count <= 0 ||
    seg.count > 131_072 ||
    expectedCount !== seg.count ||
    !Number.isInteger(seg.index) ||
    seg.index < 0 ||
    seg.index >= seg.count ||
    !Number.isSafeInteger(seg.offset) ||
    seg.offset !== expectedOffset ||
    compressed.length > Math.min(SEGMENT_BYTES, seg.rootOriginalSize - expectedOffset)
  ) {
    throw new Error("分段描述符的段数、偏移或长度不一致")
  }

  const sha256Hex = Array.from(actualSha, (b) => b.toString(16).padStart(2, "0")).join("")
  const rootSha256Hex = Array.from(
    expectedRootSha,
    (b) => b.toString(16).padStart(2, "0")
  ).join("")
  const { task } = await storeVerifiedSegment({
    rootLo: seg.rootLo,
    rootHi: seg.rootHi,
    fileName: meta.fileName,
    originalSize: meta.originalSize,
    compressedSize: seg.rootOriginalSize,
    compression: meta.compression,
    crc32: meta.crc32,
    crc32Known: meta.crc32Known,
    segmentCount: seg.count,
    index: seg.index,
    sha256Hex,
    rootSha256Hex,
    bytes: compressed,
  })
  post({
    type: "segment",
    index: seg.index,
    count: task.segmentCount,
    received: task.received.length,
    jobId,
  })
  dropSession()
  return task.state === "complete" ? task : null
}

async function onMessage(e: MessageEvent): Promise<void> {
  const data = e.data
  if (!data || typeof data !== "object") return

  // ── init ──
  if (data.type === "init") {
    const jobId = typeof data.jobId === "number" ? data.jobId : ++activeJobId
    activeJobId = jobId
    try {
      await ensureWasm()
      ready = true
      post({ type: "ready", jobId })
    } catch (err) {
      post({ type: "error", message: `WASM 加载失败: ${String(err)}`, jobId })
    }
    return
  }

  // ── wasm-init (zstd bytes preloaded on the main thread) ──
  if (data.type === "wasm-init") {
    const zstd = (data as { type: "wasm-init"; zstd?: ArrayBuffer | null }).zstd
    if (zstd instanceof ArrayBuffer && zstd.byteLength > 0) {
      // Install the bytes as the zstd module source so ensureZstd() /
      // zstdDecompress() does not need a second network request. Null bytes
      // mean the main-thread preload failed or timed out; the zstd branch then
      // lazily fetches via the context-aware worker URL (`../wasm-zstd.wasm`).
      initZstdFromBytes(zstd)
    }
    return
  }

  // ── reset (new session / user navigated back) ──
  if (data.type === "reset") {
    const jobId = typeof data.jobId === "number" ? data.jobId : ++activeJobId
    activeJobId = jobId
    dropSession()
    post({ type: "reset-ack", jobId })
    return
  }

  if (!ready) {
    // Ignore frames until ready; main re-sends on "ready".
    return
  }

  const jobId = typeof data.jobId === "number" ? data.jobId : activeJobId
  if (jobId !== activeJobId) {
    // Stale batch from a superseded session — drop.
    return
  }

  // ── frames batch ──
  if (data.type === "frames") {
    const frames = data.frames as Uint8Array[]
    if (!Array.isArray(frames)) return
    // If a new jobId arrived, reset.
    if (data.jobId !== undefined && data.jobId !== activeJobId) return
    const res = ingestBatch(frames, activeJobId)
    post({
      type: "status",
      complete: res.complete,
      acceptedCount: res.acceptedCount,
      receivedSymbols: res.receivedSymbols,
      mismatchStreak: res.mismatchStreak,
      snapshot: res.snapshot,
      nowMs: Date.now(),
      jobId: activeJobId,
    })
    return
  }

  // ── assemble + recover ──
  if (data.type === "assemble") {
    if (!session) return
    const segmented = session.is_segmented()
    try {
      if (segmented) {
        // Large transfer: store this completed segment and, once all segments
        // are in, merge and deliver the full root file.
        const task = await handleSegmentComplete(activeJobId)
        if (task) {
          post({
            type: "stored-result",
            task,
            jobId: activeJobId,
          })
        }
        // else: awaiting more segments — main keeps scanning (status "segment").
        return
      }
      const { verify, recovered } = await assembleAndRecover(activeJobId)
      const transfer = recoveredTransferables(recovered)
      dropSession()
      post({
        type: "result",
        recovered,
        crcOk: verify.crcOk,
        crcKnown: verify.crcKnown,
        jobId: activeJobId,
      }, transfer)
    } catch (err) {
      post({
        type: segmented ? "segment-error" : "error",
        message:
          err instanceof ParseError || err instanceof Error
            ? err.message
            : String(err),
        jobId: activeJobId,
      })
      dropSession()
    }
    return
  }
}

// Serialize the async handler itself. `assemble` awaits decompression and hash
// verification; without this queue, a later frame/reset message can mutate or
// free the WASM session while that work is still in flight.
let messageQueue = Promise.resolve()
self.addEventListener("message", (e) => {
  messageQueue = messageQueue
    .then(() => onMessage(e))
    .catch((err) => {
      post({
        type: "error",
        message: `receive worker 内部错误: ${String(err)}`,
        jobId: activeJobId,
      })
    })
})
