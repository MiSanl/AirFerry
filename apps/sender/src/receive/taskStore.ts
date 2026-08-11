/** Durable browser-side ledger for descriptor-v4 segmented receives. */

const DB_NAME = "airferry-receive-v1"
const DB_VERSION = 2
const TASKS = "tasks"
const SEGMENTS = "segments"
const SEGMENT_RAW_BYTES = 8 * 1024 * 1024
const MAX_SEGMENT_COUNT = 131_072
const MIN_FREE_RESERVE_BYTES = 64 * 1024 * 1024

export interface StoredSegmentTask {
  rootId: string
  rootLo: string
  rootHi: string
  fileName: string
  rootOriginalSize: number
  segmentCount: number
  /** Complete-file digest shared by every descriptor-v4 segment. */
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
  rootOriginalSize: number
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
  const expectedCount = Math.ceil(input.rootOriginalSize / SEGMENT_RAW_BYTES)
  const expectedOffset = input.index * SEGMENT_RAW_BYTES
  const expectedLength = Math.min(
    SEGMENT_RAW_BYTES,
    input.rootOriginalSize - expectedOffset
  )
  if (
    !Number.isSafeInteger(input.rootOriginalSize) ||
    input.rootOriginalSize <= 0 ||
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
          rootOriginalSize: input.rootOriginalSize,
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
      task.rootOriginalSize !== input.rootOriginalSize ||
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
