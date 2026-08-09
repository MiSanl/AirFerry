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
import { buildBundle, MAX_TRANSFER_BYTES, MAX_TRANSFER_MIB } from "@/wasm/bundle"
import { buildTextPayload, TEXT_DISPLAY_NAME } from "@/wasm/text"
import { normalizeDraftFilename } from "@/storage/textDrafts"

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
  /** Compressed payload, transferred back (detached from this thread). */
  compressed: ArrayBuffer
  algorithm: number
  originalSize: number
  compressedSize: number
  preCrc32: number
  sessionId: SessionIdDto
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
    const selectedBytes = files.reduce((sum, file) => sum + file.size, 0)
    if (!Number.isSafeInteger(selectedBytes) || selectedBytes > MAX_TRANSFER_BYTES) {
      throw new Error(`所选内容超过 ${MAX_TRANSFER_MIB} MiB 接收上限`)
    }
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
      raw = new Uint8Array(await files[0].arrayBuffer())
      if (!isCurrent(jobId)) return
      displayName = files[0].name
      const fp = computeFingerprint(raw)
      const f = files[0]
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
    if (raw.length > MAX_TRANSFER_BYTES) {
      throw new Error(`文字内容超过 ${MAX_TRANSFER_MIB} MiB 接收上限`)
    }
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
 * Shared finalize tail for both file and text paths: compress → CRC32 +
 * fingerprint already done by caller → package the transferable result and
 * post `done`. Runs the heavy synchronous-WASM compress here in the worker so
 * the main thread keeps painting the progress overlay.
 */
async function finalizeAndPost(
  raw: Uint8Array,
  displayName: string,
  sessionId: { lo: bigint; hi: bigint },
  jobId: number
) {
  if (!isCurrent(jobId)) return
  // --- Compress (zstd always; xz if compressible) ---
  // preparePayload drives the stage callback so we can post zstd/xz phase
  // boundaries up to the UI. The compress itself is synchronous WASM but
  // runs here in the worker, so the main thread keeps painting meanwhile.
  const { payload: compressed, algorithm, compressedSize } = await preparePayload(
    raw,
    (phase) => {
      if (isCurrent(jobId)) post({ phase, jobId })
    }
  )
  if (!isCurrent(jobId)) return
  console.log(
    `Compression: ${raw.length} → ${compressedSize} bytes ` +
      `(${raw.length > 0 ? ((compressedSize / raw.length) * 100).toFixed(1) : "0"}%)`
  )

  // --- CRC32 (on the pre-compress bytes) ---
  // 这一段（CRC32 over the whole payload）没有任何阶段回调，是 done 前的"盲区"。
  // 大文件 CRC 可达数百毫秒，补一个 finalizing 阶段让 UI 步骤清单能显示它，
  // 而非从"压缩"直接跳到"完成"。
  post({ phase: "finalizing", jobId })
  const crc = crc32(raw)
  if (!isCurrent(jobId)) return

  // Transfer the compressed buffer back (zero-copy). Ensure it owns a
  // dedicated ArrayBuffer at offset 0 so the transfer detaches cleanly. The
  // compress output is always backed by a plain ArrayBuffer (zstd .slice()
  // or the original File bytes), never a SharedArrayBuffer, so the assert is
  // safe.
  const ownsBuffer =
    compressed.byteOffset === 0 && compressed.byteLength === compressed.buffer.byteLength
  const outBuf = (ownsBuffer ? compressed.buffer : compressed.slice().buffer) as ArrayBuffer

  const result: CompressResult = {
    phase: "done",
    jobId,
    compressed: outBuf,
    algorithm,
    originalSize: raw.length,
    compressedSize,
    preCrc32: crc,
    sessionId: {
      lo: sessionId.lo.toString(),
      hi: sessionId.hi.toString(),
    },
    displayName,
  }
  // Detach the ArrayBuffer via the transfer list.
  ;(self as unknown as Worker).postMessage(result, [outBuf])
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
