/**
 * AirFerry sender app — single full-tab page with internal routing across
 * the four required screens: select → params → play → stats.
 *
 * The select page holds a unified pending list (real files + text items that
 * keep their string content). Nothing is compressed until the user clicks
 * 「发送」. Staging rules:
 *  - exactly one text item, no files → ETTEXTv1 (`processText`) so the
 *    receiver opens the copy/share text page (and can still save as .txt)
 *  - otherwise materialise text as named .txt Files and `processFiles`
 *    (1 item = single-file path; ≥2 = ETBUNDL1 bundle)
 */
import { useState, useCallback, useEffect, useRef } from "react"
import "@/assets/app.css"
import iconUrl from "../assets/icon128.png"
import { ensureWasm, SenderSessionWasm } from "@/wasm/loader"
import { FileSelectPage } from "@/pages/FileSelectPage"
import { ParamsPage } from "@/pages/ParamsPage"
import { PlayPage } from "@/pages/PlayPage"
import { StatsPage } from "@/pages/StatsPage"
import { CompressProgress, type CompressPhase } from "@/components/CompressProgress"
import {
  loadConfig,
  saveConfig,
  type Page,
  type PendingItem,
  type TransferConfig,
} from "@/types"
import { base64ToBuffer } from "@/wasm/base64"

/**
 * The compress worker. Built by Parcel 2 into a separate bundle per target
 * (chrome-mv2/mv3, firefox-mv2/mv3). It runs the heavy, synchronous-WASM file
 * prep (bundle → compress → crc → fingerprint → session id) off the main thread
 * so the UI stays responsive — without this the compressors freeze the page.
 *
 * Standalone (single-file) build: under `file://`, `new Worker(url)` cannot
 * load a separate script file, so the standalone build inlines the worker
 * source as a string on `globalThis.__WORKER_CODE__`. When present we wrap it in
 * a Blob URL and spawn the worker from that (modern browsers permit blob:-
 * origin workers under file://). The worker source itself uses the same base64
 * WASM constants the main thread does (see zstd pre-load below).
 */
const standaloneGlobals = globalThis as {
  __AIRFERRY_STANDALONE__?: boolean
  __WORKER_CODE__?: string
  __WASM_ZSTD__?: string
}

function createCompressWorker(): Worker {
  if (standaloneGlobals.__AIRFERRY_STANDALONE__ && standaloneGlobals.__WORKER_CODE__) {
    const blobUrl = URL.createObjectURL(
      new Blob([standaloneGlobals.__WORKER_CODE__], { type: "text/javascript" })
    )
    try {
      return new Worker(blobUrl)
    } finally {
      URL.revokeObjectURL(blobUrl)
    }
  }
  return new Worker(
    new URL("./workers/compress.worker.ts", import.meta.url),
    { type: "module" }
  )
}

/**
 * Pre-load zstd WASM bytes on the main thread and transfer them to the worker.
 * Inside a Web Worker, chrome.runtime.getURL() + fetch() may fail silently,
 * causing compression to always fall back to raw (100% ratio). By loading the
 * bytes here and passing them as a transferable, we guarantee the worker always
 * has the WASM binary available regardless of its execution context.
 *
 * Three environments, three ways to get the bytes:
 *  - **Standalone (single-file) build**: the wasm is inlined as base64 on
 *    `globalThis.__WASM_ZSTD__` (file:// can't fetch it). Decode directly.
 *  - **Browser extension**: `chrome.runtime.getURL` resolves the packed asset.
 *  - **Plain web page**: resolve relative to the document.
 *
 * Always posts `wasm-init` (with bytes or null) so the worker never parks
 * forever waiting for zstd — preparePayload already falls back to raw.
 */
async function initializeCompressWorker(worker: Worker): Promise<void> {
  let bytes: ArrayBuffer | null = null
  try {
    if (standaloneGlobals.__AIRFERRY_STANDALONE__ && standaloneGlobals.__WASM_ZSTD__) {
      // Standalone build: decode the inlined base64 (file:// can't fetch).
      // base64ToBuffer returns ArrayBuffer | undefined; coerce to null so the
      // unified `bytes` slot (ArrayBuffer | null) stays consistent below.
      const decoded = base64ToBuffer(standaloneGlobals.__WASM_ZSTD__)
      if (decoded) bytes = decoded
    } else {
      const wasmUrl =
        typeof chrome !== "undefined" && chrome.runtime?.getURL
          ? chrome.runtime.getURL("wasm-zstd.wasm")
          : new URL("wasm-zstd.wasm", document.baseURI).href
      const resp = await fetch(wasmUrl, { credentials: "same-origin" })
      if (resp.ok) bytes = await resp.arrayBuffer()
      else console.warn("Failed to pre-load wasm-zstd.wasm:", resp.status)
    }
  } catch (e) {
    console.warn("Failed to pre-load wasm-zstd.wasm:", e)
  }
  if (bytes) {
    worker.postMessage({ type: "wasm-init", zstd: bytes }, [bytes])
  } else {
    // Still unlock the worker queue; compress may use raw-only.
    worker.postMessage({ type: "wasm-init", zstd: null })
  }
}

export type { Page, PendingItem, TransferConfig }

/** A "send unit": either one real file, a text message, or a bundle of several. */
interface PreparedPayload {
  /** Final bytes fed to the RaptorQ encoder (already compressed). */
  compressed: Uint8Array
  /** Compression algorithm applied to `compressed` (mirrors compress.rs tags). */
  compressionAlgorithm: number
  /** CRC32 of the *pre-compression* bytes (single file, or whole bundle). */
  preCrc32: number
  /** Display name for the descriptor. Single file → its name; bundle → "N files". */
  displayName: string
  /** Total original (uncompressed) byte count for the transfer. */
  originalSize: number
  /** Session id derived from the transfer identity. */
  sessionId: { lo: bigint; hi: bigint }
  /** True when staged as a pure ETTEXTv1 text transfer (receiver copy UI). */
  isText: boolean
}

export interface AppState {
  page: Page
  /**
   * Pending send list on the select page (files + text items with content).
   * Only staged into the compress worker when the user clicks「发送」.
   */
  items: PendingItem[]
  /** The prepared transfer unit (after compress worker). Null until ready. */
  prepared: PreparedPayload | null
  session: SenderSessionWasm | null
  config: TransferConfig
  /** Loading state while WASM encoder is being initialized (can be slow for large files). */
  initializing: boolean
  /**
   * Compress-worker phase. While set (not null), a full-screen progress
   * overlay is shown — the prep runs in the worker so this spinner keeps
   * animating even during the slow synchronous-WASM compress.
   */
  compressPhase: CompressPhase | null
  /** Error message if WASM session creation or compression fails. */
  error: string | null
}

// wasm-bindgen `free()` is not idempotent: a second call can dereference an
// already-released native pointer. Keep ownership release idempotent across
// async epoch exits, React cleanup, and session replacement.
const freedSessions = new WeakSet<SenderSessionWasm>()
function freeSenderSession(session: SenderSessionWasm | null | undefined): void {
  if (!session || freedSessions.has(session)) return
  freedSessions.add(session)
  session.free()
}

/** Materialise pending items as File[] for the file/bundle worker path. */
function itemsToFiles(items: PendingItem[]): File[] {
  return items.map((it) => {
    if (it.kind === "file") return it.file
    const blob = new Blob([it.content], { type: "text/plain;charset=utf-8" })
    return new File([blob], it.name, {
      type: "text/plain",
      lastModified: Date.now(),
    })
  })
}

function itemByteSize(it: PendingItem): number {
  return it.kind === "file"
    ? it.file.size
    : new TextEncoder().encode(it.content).length
}

export default function App() {
  useEffect(() => {
    document.title = "AirFerry · 无网文件传输"
  }, [])

  // Initialize config from localStorage (so the user's last-used transfer
  // params — redundancy, fps, symbol size, brightness, multi-QR — carry over to
  // every subsequent transfer instead of resetting to defaults each time).
  const [state, setState] = useState<AppState>({
    page: "select",
    items: [],
    prepared: null,
    session: null,
    config: loadConfig(),
    initializing: false,
    compressPhase: null,
    error: null
  })

  const ownedSessionRef = useRef<SenderSessionWasm | null>(null)
  const mountedRef = useRef(false)
  const releaseOwnedSession = useCallback(() => {
    const session = ownedSessionRef.current
    ownedSessionRef.current = null
    freeSenderSession(session)
  }, [])

  // Track the actual owner independently of React effect dependencies. A
  // dependency cleanup can run twice under StrictMode and must never free a
  // still-live session. This mount cleanup only releases the current owner.
  useEffect(() => {
    mountedRef.current = true
    return () => {
      mountedRef.current = false
      releaseOwnedSession()
    }
  }, [releaseOwnedSession])

  /**
   * Epoch for in-flight compress. Bumped when the pending list changes so a
   * late worker `done` cannot apply after the user edited the selection.
   * Sent to the worker as `jobId`; worker suppresses posts for stale ids.
   * Main thread also double-checks `issuedEpoch === epoch` before applying.
   */
  const epoch = useRef(0)
  const issuedEpoch = useRef(-1)
  const workerRef = useRef<Worker | null>(null)
  const restartWorkerRef = useRef<() => void>(() => undefined)
  /** Items snapshot at compress start (for prepared.isText). */
  const compressItemsRef = useRef<PendingItem[]>([])

  const go = useCallback((page: Page) => {
    // Navigating back to select while compressing must cancel the in-flight
    // worker result (same as editing the list).
    if (page === "select") {
      epoch.current += 1
      if (issuedEpoch.current >= 0) restartWorkerRef.current()
      issuedEpoch.current = -1
      setState((s) => ({
        ...s,
        page,
        compressPhase: null,
      }))
      return
    }
    setState((s) => ({ ...s, page }))
  }, [])

  useEffect(() => {
    let worker: Worker | null = null
    let disposed = false
    const handler = (e: MessageEvent) => {
      const msg = e.data
      if (!msg || typeof msg.phase !== "string") return
      // Stale: list was edited (or a newer send replaced this one).
      // Prefer jobId from worker when present; fall back to issued epoch.
      if (typeof msg.jobId === "number") {
        if (msg.jobId !== epoch.current || issuedEpoch.current !== epoch.current) return
      } else if (issuedEpoch.current !== epoch.current) {
        return
      }

      if (msg.phase === "done") {
        issuedEpoch.current = -1
        const itemsSnap = compressItemsRef.current
        const pureText =
          itemsSnap.length === 1 && itemsSnap[0].kind === "text"
        const compressed = new Uint8Array(msg.compressed as ArrayBuffer)
        setState((s) => ({
          ...s,
          prepared: {
            compressed,
            compressionAlgorithm: msg.algorithm,
            preCrc32: msg.preCrc32,
            displayName: msg.displayName,
            originalSize: msg.originalSize,
            sessionId: {
              lo: BigInt(msg.sessionId.lo),
              hi: BigInt(msg.sessionId.hi),
            },
            isText: pureText,
          },
          compressPhase: null,
          page: "params",
          error: null,
        }))
      } else if (msg.phase === "error") {
        issuedEpoch.current = -1
        setState((s) => ({
          ...s,
          compressPhase: null,
          error: `文件处理失败: ${msg.message}`,
        }))
      } else {
        setState((s) =>
          s.compressPhase != null
            ? { ...s, compressPhase: msg.phase as CompressPhase }
            : s
        )
      }
    }

    const failWorker = (message: string) => {
      if (disposed) return
      epoch.current += 1
      issuedEpoch.current = -1
      setState((s) => ({
        ...s,
        compressPhase: null,
        error: `文件处理线程失败: ${message}`,
      }))
      startWorker()
    }
    const errorHandler = (e: ErrorEvent) => {
      e.preventDefault()
      failWorker(e.message || "worker crashed")
    }
    const messageErrorHandler = () => failWorker("无法解析 worker 消息")
    const startWorker = () => {
      worker?.removeEventListener("message", handler)
      worker?.removeEventListener("error", errorHandler)
      worker?.removeEventListener("messageerror", messageErrorHandler)
      worker?.terminate()
      try {
        worker = createCompressWorker()
        workerRef.current = worker
        worker.addEventListener("message", handler)
        worker.addEventListener("error", errorHandler)
        worker.addEventListener("messageerror", messageErrorHandler)
        void initializeCompressWorker(worker).catch((e) =>
          failWorker(e instanceof Error ? e.message : String(e))
        )
      } catch (e) {
        worker = null
        workerRef.current = null
        setState((s) => ({
          ...s,
          compressPhase: null,
          error: `无法启动文件处理线程: ${e instanceof Error ? e.message : String(e)}`,
        }))
      }
    }
    restartWorkerRef.current = startWorker
    startWorker()
    return () => {
      disposed = true
      restartWorkerRef.current = () => undefined
      worker?.terminate()
      workerRef.current = null
    }
  }, [])

  /**
   * Select page: only update the pending list. Does NOT compress or advance —
   * that happens in `onSend` when the user explicitly confirms.
   * Changing the list invalidates prepared/session and cancels in-flight compress.
   */
  const onItemsChange = useCallback((items: PendingItem[]) => {
    releaseOwnedSession()
    epoch.current += 1
    if (issuedEpoch.current >= 0) restartWorkerRef.current()
    issuedEpoch.current = -1
    compressItemsRef.current = []
    setState((s) => ({
      ...s,
      items,
      prepared: null,
      session: null,
      compressPhase: null,
      error: null,
      page: "select",
    }))
  }, [releaseOwnedSession])

  /**
   * Explicit send:
   *  - one text item alone → worker `{ jobId, text, name }` → ETTEXTv1
   *  - otherwise → materialise text as .txt Files → worker `{ jobId, files }`
   * Re-entry while compressPhase != null is ignored.
   * `jobId` is the current epoch so the worker can drop superseded jobs.
   */
  const onSend = useCallback(() => {
    const items = state.items
    if (items.length === 0) return
    if (state.compressPhase != null) return
    const worker = workerRef.current
    if (!worker) {
      setState((s) => ({ ...s, error: "文件处理线程尚未就绪，请重试" }))
      return
    }
    // Bump epoch so any prior in-flight job (if re-entry races) is superseded.
    epoch.current += 1
    const e = epoch.current
    issuedEpoch.current = e
    compressItemsRef.current = items
    releaseOwnedSession()
    setState((s) => ({
      ...s,
      session: null,
      compressPhase: "reading",
      error: null,
    }))
    if (items.length === 1 && items[0].kind === "text") {
      // Carry the user-chosen filename into the descriptor (worker defaults to
      // "文字消息.txt" only when name is empty).
      worker.postMessage({
        jobId: e,
        text: items[0].content,
        name: items[0].name,
      })
    } else {
      worker.postMessage({ jobId: e, files: itemsToFiles(items) })
    }
  }, [state.items, state.compressPhase, releaseOwnedSession])

  /** Params confirmed → build the WASM sender session, go to play. */
  const onStart = useCallback(async () => {
    if (!state.prepared) return
    const startEpoch = epoch.current
    const cfg = state.config
    const p = state.prepared
    setState((s) => ({ ...s, initializing: true, error: null }))
    try {
      await ensureWasm()
      if (!mountedRef.current || epoch.current !== startEpoch) {
        if (mountedRef.current) {
          setState((s) => ({ ...s, initializing: false }))
        }
        return
      }
      const session = new SenderSessionWasm(
        p.compressed,
        p.sessionId.lo,
        p.sessionId.hi,
        cfg.redundancyPct,
        cfg.symbolSize,
        p.displayName,
        BigInt(p.originalSize),
        p.preCrc32,
        p.compressionAlgorithm
      )
      if (!mountedRef.current || epoch.current !== startEpoch) {
        freeSenderSession(session)
        releaseOwnedSession()
        if (mountedRef.current) {
          setState((s) => ({ ...s, session: null, initializing: false }))
        }
        return
      }
      releaseOwnedSession()
      ownedSessionRef.current = session
      setState((s) => ({ ...s, session, page: "play", initializing: false }))
    } catch (e: any) {
      console.error("WASM session creation failed:", e)
      setState((s) => ({
        ...s,
        initializing: false,
        error: `编码器初始化失败: ${e?.message || e}`
      }))
    }
  }, [state.prepared, state.config, releaseOwnedSession])

  const updateConfig = useCallback(
    (patch: Partial<TransferConfig>) =>
      setState((s) => {
        const next = { ...s.config, ...patch }
        // Persist every change so the chosen params survive a page reload / next
        // transfer. saveConfig swallows storage errors, so this never throws.
        saveConfig(next)
        return { ...s, config: next }
      }),
    []
  )

  return (
    <div className="app">
      <header className="app-header">
        <div className="app-logo"><img src={iconUrl} alt="AirFerry" /></div>
        <h1>AirFerry · 无网文件传输</h1>
      </header>
      <div className="steps">
        <div className={`step ${state.page === "select" ? "active" : state.prepared ? "done" : ""}`} onClick={() => go("select")}>
          <span className="step-dot">1</span> 选择文件
        </div>
        <div className="step-line" />
        <div className={`step ${state.page === "params" ? "active" : state.session ? "done" : ""}`} onClick={() => state.prepared && go("params")}>
          <span className="step-dot">2</span> 传输参数
        </div>
        <div className="step-line" />
        <div className={`step ${state.page === "play" ? "active" : ""}`} onClick={() => state.session && go("play")}>
          <span className="step-dot">3</span> 播放传输
        </div>
        <div className="step-line" />
        <div className={`step ${state.page === "stats" ? "active" : ""}`} onClick={() => state.session && go("stats")}>
          <span className="step-dot">4</span> 统计
        </div>
      </div>
      <main className="app-main">
        {state.error && (
          <div className="error-banner" style={{ padding: "12px 16px", marginBottom: 16, background: "#fef2f2", border: "1px solid #fecaca", borderRadius: 8, color: "#991b1b", fontSize: 14 }}>
            {state.error}
          </div>
        )}
        {state.page === "select" && (
          <FileSelectPage
            items={state.items}
            onItemsChange={onItemsChange}
            onSend={onSend}
          />
        )}
        {state.page === "params" && state.prepared && (
          <ParamsPage
            items={state.items}
            displayName={state.prepared.displayName}
            originalSize={state.prepared.originalSize}
            compressedSize={state.prepared.compressed.length}
            isBundle={state.items.length > 1}
            isText={state.prepared.isText}
            config={state.config}
            onChange={updateConfig}
            onStart={onStart}
            initializing={state.initializing}
          />
        )}
        {state.page === "play" && state.session && state.prepared && (
          <PlayPage
            session={state.session}
            config={state.config}
            sessionId={state.prepared.sessionId}
            totalBytes={state.prepared.compressed.length}
          />
        )}
        {state.page === "stats" && state.session && state.prepared && (
          <StatsPage
            session={state.session}
            fileSize={state.prepared.originalSize}
          />
        )}
      </main>
      <footer className="app-footer">
        <a
          className="app-footer-link"
          href="https://github.com/UR-SillyB/AirFerry/releases/latest"
          target="_blank"
          rel="noopener noreferrer"
        >
          下载扫码端
        </a>
        <span className="app-footer-sep">·</span>
        <a
          className="app-footer-link"
          href="https://github.com/UR-SillyB/AirFerry"
          target="_blank"
          rel="noopener noreferrer"
        >
          项目地址
        </a>
      </footer>
      {/* Compress progress overlay — shown while the worker prepares the file.
          The worker keeps the main thread free, so this spinner animates even
          during the slow xz pass. */}
      <CompressProgress
        phase={state.compressPhase}
        isBundle={state.items.length > 1}
        displayName={
          state.items.length === 0
            ? undefined
            : state.items.length > 1
              ? `${state.items.length}项`
              : state.items[0].kind === "file"
                ? state.items[0].file.name
                : state.items[0].name
        }
        originalSize={
          state.items.reduce((sum, it) => sum + itemByteSize(it), 0) || undefined
        }
      />
    </div>
  )
}
