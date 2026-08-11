/** Durable browser-side ledger for descriptor-v5 segmented receives. */

const DB_NAME = "airferry-receive-v1"
const DB_VERSION = 3
const TASKS = "tasks"
const SEGMENTS = "segments"
// Compressed-stream segment size (mirrors Rust `SEGMENT_RAW_BYTES`).
// `MAX_OBJECT_BYTES - MAX_SYMBOL_SIZE` so a full segment, after RaptorQ symbol
// padding, stays within the 32 MiB wire ceiling for any legal symbol size.
const MAX_OBJECT_BYTES = 32 * 1024 * 1024
const MAX_SYMBOL_SIZE = 65_528
const SEGMENT_RAW_BYTES = MAX_OBJECT_BYTES - MAX_SYMBOL_SIZE
const MAX_SEGMENT_COUNT = 131_072
const MIN_FREE_RESERVE_BYTES = 64 * 1024 * 1024

export interface StoredSegmentTask {
  rootId: string
  rootLo: string
  rootHi: string
  fileName: string
  /** Whole decompressed size of the original payload (display + verify). */
  rootOriginalSize: number
  /** Whole compressed stream size (== sum of every segment's compressed bytes). */
  compressedSize: number
  /** Compression algorithm of the whole stream (shared by every segment). */
  compression: number
  /** CRC32 over the whole decompressed original payload. */
  crc32: number
  crc32Known: boolean
  segmentCount: number
  /** Complete-file digest (SHA-256 of the whole decompressed original). */
  rootSha256: string
  received: number[]
  hashes: (string | null)[]
  state: "receiving" | "complete"
  createdAt: number
  updatedAt: number
}

interface SegmentRecord {
  rootId: string
  index: number
  bytes: ArrayBuffer
}

export interface StoreSegmentInput {
  rootLo: bigint
  rootHi: bigint
  fileName: string
  /** Whole decompressed size of the original payload. */
  originalSize: number
  /** Whole compressed stream size. */
  compressedSize: number
  /** Compression algorithm of the whole stream. */
  compression: number
  /** CRC32 over the whole decompressed original. */
  crc32: number
  crc32Known: boolean
  segmentCount: number
  index: number
  sha256Hex: string
  rootSha256Hex: string
  bytes: Uint8Array
}

function request<T>(req: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error ?? new Error("IndexedDB request failed"))
  })
}

function transactionDone(tx: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    tx.oncomplete = () => resolve()
    tx.onabort = () => reject(tx.error ?? new Error("IndexedDB transaction aborted"))
    tx.onerror = () => reject(tx.error ?? new Error("IndexedDB transaction failed"))
  })
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION)
    req.onupgradeneeded = (event) => {
      const db = req.result
      if (!db.objectStoreNames.contains(TASKS)) {
        db.createObjectStore(TASKS, { keyPath: "rootId" })
      }
      if (!db.objectStoreNames.contains(SEGMENTS)) {
        db.createObjectStore(SEGMENTS, { keyPath: ["rootId", "index"] })
      }
      // v1.1.6 descriptor-v4 tasks did not bind their segments to a
      // complete-file digest and are unsafe to resume under revision 2.
      if ((event as IDBVersionChangeEvent).oldVersion > 0 &&
          (event as IDBVersionChangeEvent).oldVersion < 2) {
        req.transaction?.objectStore(TASKS).clear()
        req.transaction?.objectStore(SEGMENTS).clear()
      }
      // Revision 3 switched stored segment blobs from *decompressed* bytes to
      // *compressed* bytes (compressed-stream segmentation). Any task stored
      // under an older revision holds incompatible segment bytes, so drop them.
      if ((event as IDBVersionChangeEvent).oldVersion > 0 &&
          (event as IDBVersionChangeEvent).oldVersion < 3) {
        req.transaction?.objectStore(TASKS).clear()
        req.transaction?.objectStore(SEGMENTS).clear()
      }
    }
    req.onsuccess = () => resolve(req.result)
    req.onerror = () => reject(req.error ?? new Error("无法打开接收任务数据库"))
    req.onblocked = () => reject(new Error("接收任务数据库正在被其他页面占用"))
  })
}

export function rootIdHex(lo: bigint, hi: bigint): string {
  return `${hi.toString(16).padStart(16, "0")}${lo.toString(16).padStart(16, "0")}`
}

/** Atomically publish one verified segment and the updated completion ledger. */
export async function storeVerifiedSegment(
  input: StoreSegmentInput
): Promise<{ task: StoredSegmentTask; newlyStored: boolean }> {
  // Segment coordinates are over the **compressed** stream.
  const expectedCount = Math.ceil(input.compressedSize / SEGMENT_RAW_BYTES)
  const expectedOffset = input.index * SEGMENT_RAW_BYTES
  const expectedLength = Math.min(
    SEGMENT_RAW_BYTES,
    input.compressedSize - expectedOffset
  )
  if (
    !Number.isSafeInteger(input.compressedSize) ||
    input.compressedSize <= 0 ||
    !Number.isSafeInteger(input.originalSize) ||
    input.originalSize <= 0 ||
    !Number.isInteger(input.segmentCount) ||
    input.segmentCount <= 0 ||
    input.segmentCount > MAX_SEGMENT_COUNT ||
    input.segmentCount !== expectedCount ||
    !Number.isInteger(input.index) ||
    input.index < 0 ||
    input.index >= input.segmentCount ||
    input.bytes.byteLength !== expectedLength ||
    !/^[0-9a-f]{64}$/.test(input.sha256Hex) ||
    !/^[0-9a-f]{64}$/.test(input.rootSha256Hex)
  ) {
    throw new Error("拒绝写入非法的分段任务或分段长度")
  }

  const estimate = await navigator.storage?.estimate?.()
  if (
    estimate?.quota !== undefined &&
    estimate.usage !== undefined &&
    estimate.quota - estimate.usage < input.bytes.byteLength + MIN_FREE_RESERVE_BYTES
  ) {
    throw new Error("浏览器存储空间不足，无法安全持久化下一分段")
  }

  const rootId = rootIdHex(input.rootLo, input.rootHi)
  const db = await openDb()
  try {
    // Read and publish under one readwrite transaction. IndexedDB serializes
    // this transaction across tabs, so two receiver tabs cannot overwrite one
    // another's newly-received bitmap entry.
    const tx = db.transaction([TASKS, SEGMENTS], "readwrite")
    const done = transactionDone(tx)
    const previous = (await request(
      tx.objectStore(TASKS).get(rootId)
    )) as StoredSegmentTask | undefined

    const now = Date.now()
    const task: StoredSegmentTask = previous
      ? {
          ...previous,
          received: [...previous.received],
          hashes: [...previous.hashes],
        }
      : {
          rootId,
          rootLo: input.rootLo.toString(),
          rootHi: input.rootHi.toString(),
          fileName: input.fileName,
          rootOriginalSize: input.originalSize,
          compressedSize: input.compressedSize,
          compression: input.compression,
          crc32: input.crc32,
          crc32Known: input.crc32Known,
          segmentCount: input.segmentCount,
          rootSha256: input.rootSha256Hex,
          received: [],
          hashes: new Array(input.segmentCount).fill(null),
          state: "receiving",
          createdAt: now,
          updatedAt: now,
        }

    if (
      task.rootLo !== input.rootLo.toString() ||
      task.rootHi !== input.rootHi.toString() ||
      task.fileName !== input.fileName ||
      task.rootOriginalSize !== input.originalSize ||
      task.compressedSize !== input.compressedSize ||
      task.compression !== input.compression ||
      task.crc32 !== input.crc32 ||
      task.crc32Known !== input.crc32Known ||
      task.segmentCount !== input.segmentCount ||
      task.rootSha256 !== input.rootSha256Hex ||
      task.hashes.length !== input.segmentCount ||
      task.received.some(
        (index) => !Number.isInteger(index) || index < 0 || index >= input.segmentCount
      ) ||
      new Set(task.received).size !== task.received.length
    ) {
      throw new Error("同一根任务的不可变元数据发生冲突")
    }

    const segmentStore = tx.objectStore(SEGMENTS)
    const alreadyStored = task.received.includes(input.index)
    if (alreadyStored && task.hashes[input.index] !== input.sha256Hex) {
      throw new Error("重复分段的 SHA-256 与已存账本冲突")
    }
    if (alreadyStored) {
      // The bitmap and blob must agree. A browser/storage crash can leave a
      // checked ledger entry whose segment record is missing or damaged. The
      // newly scanned bytes have already passed descriptor CRC + SHA checks, so
      // use them to heal the record instead of making the task permanently
      // impossible to complete.
      const stored = (await request(
        segmentStore.get([rootId, input.index])
      )) as SegmentRecord | undefined
      const sameBytes =
        stored?.bytes.byteLength === input.bytes.byteLength &&
        new Uint8Array(stored.bytes).every((byte, i) => byte === input.bytes[i])
      if (!sameBytes) {
        const repaired = input.bytes.buffer.slice(
          input.bytes.byteOffset,
          input.bytes.byteOffset + input.bytes.byteLength
        ) as ArrayBuffer
        segmentStore.put({ rootId, index: input.index, bytes: repaired } satisfies SegmentRecord)
        task.updatedAt = now
        tx.objectStore(TASKS).put(task)
      }
      await done
      return { task, newlyStored: false }
    }

    task.received.push(input.index)
    task.received.sort((a, b) => a - b)
    task.hashes[input.index] = input.sha256Hex
    task.updatedAt = now
    task.state = task.received.length === task.segmentCount ? "complete" : "receiving"

    const exact = input.bytes.buffer.slice(
      input.bytes.byteOffset,
      input.bytes.byteOffset + input.bytes.byteLength
    ) as ArrayBuffer
    segmentStore.put({ rootId, index: input.index, bytes: exact } satisfies SegmentRecord)
    tx.objectStore(TASKS).put(task)
    await done
    return { task, newlyStored: true }
  } finally {
    db.close()
  }
}

/**
 * SHA-256 (lowercase hex) of an ArrayBuffer. Uses the Web Crypto API, which is
 * available in the web receiver's secure context (HTTPS / localhost). Returns
 * `null` if crypto is unavailable — the caller treats that as "not verified" so
 * it falls through to the normal store path's self-healing instead of trusting
 * a possibly-corrupt record.
 */
async function sha256Hex(bytes: ArrayBuffer): Promise<string | null> {
  try {
    if (!globalThis.crypto?.subtle) return null
    const digest = await globalThis.crypto.subtle.digest("SHA-256", bytes)
    return Array.from(new Uint8Array(digest), (b) =>
      b.toString(16).padStart(2, "0")
    ).join("")
  } catch {
    return null
  }
}

/**
 * Whether a specific segment of a root transfer is already stored AND its Blob
 * is actually present and intact (byte-length and SHA-256 match the ledger).
 *
 * Used for **early duplicate detection**: the moment a descriptor-v5 segment's
 * meta is confirmed, the receiver checks here — if the segment was already
 * received, the UI is told immediately (instead of scanning the whole segment
 * again and only de-duplicating after it fully plays out).
 *
 * This checks BOTH the ledger bitmap (task.received) AND the actual segment
 * Blob in the SEGMENTS store, including a full SHA-256 over the stored bytes
 * compared against the per-segment hash the ledger committed. A browser/storage
 * crash can leave a checked ledger entry whose Blob is missing, truncated, or
 * corrupted in place; a length-only check can be fooled by same-length corrupt
 * bytes. Only returning true when the segment re-hashes correctly guarantees
 * the caller may safely skip it. Any mismatch (or a ledger without a per-segment
 * hash, i.e. a legacy record) makes the caller rescan, which then hits the
 * store path's heal-on-duplicate logic and repairs the record.
 */
export async function hasStoredSegment(
  rootLo: bigint,
  rootHi: bigint,
  index: number
): Promise<boolean> {
  const rootId = rootIdHex(rootLo, rootHi)
  const db = await openDb()
  try {
    const tx = db.transaction([TASKS, SEGMENTS], "readonly")
    const done = transactionDone(tx)
    const task = (await request(
      tx.objectStore(TASKS).get(rootId)
    )) as StoredSegmentTask | undefined
    if (!task || !task.received.includes(index)) {
      await done
      return false
    }
    // Ledger claims the segment was received — verify the Blob is actually
    // present, matches the canonical segment length, AND hashes to the SHA-256
    // recorded in the ledger when that segment was first stored. A length-only
    // check can be fooled by same-length-but-corrupt bytes (a crash mid-write,
    // or IndexedDB backing-store corruption); the stored segment's SHA must
    // match what the ledger committed. If the ledger has no hash for this
    // index (legacy record), or the SHA/bytes are missing, return false so the
    // caller rescans and the store path's heal-on-duplicate repairs the record.
    const expectedLength = Math.min(
      SEGMENT_RAW_BYTES,
      task.compressedSize - index * SEGMENT_RAW_BYTES
    )
    const stored = (await request(
      tx.objectStore(SEGMENTS).get([rootId, index])
    )) as SegmentRecord | undefined
    if (!stored || stored.bytes.byteLength !== expectedLength) {
      await done
      return false
    }
    const expectedSha = task.hashes[index]
    if (!expectedSha || !/^[0-9a-f]{64}$/.test(expectedSha)) {
      await done
      return false
    }
    const actualSha = await sha256Hex(stored.bytes)
    if (actualSha === null || actualSha !== expectedSha) {
      await done
      return false
    }
    await done
    return true
  } finally {
    db.close()
  }
}

export async function listStoredTasks(): Promise<StoredSegmentTask[]> {
  const db = await openDb()
  try {
    const tx = db.transaction(TASKS, "readonly")
    const done = transactionDone(tx)
    const all = (await request(tx.objectStore(TASKS).getAll())) as StoredSegmentTask[]
    await done
    return all.sort((a, b) => b.updatedAt - a.updatedAt)
  } finally {
    db.close()
  }
}

export async function readStoredSegment(rootId: string, index: number): Promise<Uint8Array> {
  const db = await openDb()
  try {
    const tx = db.transaction(SEGMENTS, "readonly")
    const done = transactionDone(tx)
    const record = (await request(
      tx.objectStore(SEGMENTS).get([rootId, index])
    )) as SegmentRecord | undefined
    await done
    if (!record) throw new Error(`缺少已持久化分段 ${index + 1}`)
    return new Uint8Array(record.bytes)
  } finally {
    db.close()
  }
}

export async function deleteStoredTask(rootId: string): Promise<void> {
  const db = await openDb()
  try {
    const tx = db.transaction([TASKS, SEGMENTS], "readwrite")
    tx.objectStore(TASKS).delete(rootId)
    const store = tx.objectStore(SEGMENTS)
    const range = IDBKeyRange.bound([rootId, 0], [rootId, Number.MAX_SAFE_INTEGER])
    const cursorReq = store.openCursor(range)
    cursorReq.onsuccess = () => {
      const cursor = cursorReq.result
      if (!cursor) return
      cursor.delete()
      cursor.continue()
    }
    await transactionDone(tx)
  } finally {
    db.close()
  }
}
