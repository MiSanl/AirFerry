package com.airferry.app.scan

import android.util.AtomicFile
import com.airferry.app.nativelib.NativeBridge
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.nio.file.Files
import java.nio.file.StandardCopyOption

/**
 * Disk-backed assembler for a descriptor-v4 large transfer.
 *
 * A logical transfer is compressed **once** into a single compressed stream,
 * then split into fixed `SEGMENT_RAW_BYTES` (~32 MiB) segments. Each segment is
 * recovered by an independent `ReceiverSession`. `SegmentAssembler` stores each
 * completed segment's **compressed** bytes at its canonical offset within the
 * compressed stream in a single `.partial` file (random-access write), persists
 * a completion bitmap atomically, and once every segment has arrived
 * concatenates the compressed stream (already in `.partial`) and decompresses
 * it **exactly once** to recover the original payload, verifying the whole-file
 * CRC32 and root SHA-256 (over the decompressed bytes).
 *
 * Memory stays bounded to one segment at a time while scanning; only the final
 * decompression materializes the whole original (bounded by
 * [NativeBridge.decompressBytes]'s output cap). Unlike the legacy per-segment
 * raw model, a segment is NOT independently decompressible — a single zstd/xz
 * stream cannot be sliced into decodable pieces — so completion is required
 * before the file can be recovered.
 *
 * Layout under the cache root:
 *   `<root>/seg/<rootSessionIdHex>/transfer.partial`  — the growing compressed stream
 *   `<root>/seg/<rootSessionIdHex>/bitmap.json`       — atomic completion bitmap
 */
class SegmentAssembler private constructor(
    private val rootSessionIdLo: Long,
    private val rootSessionIdHi: Long,
    private val segmentCount: Int,
    /** Whole **compressed** stream size (== sum of every segment's compressed bytes). */
    private val compressedSize: Long,
    /** Whole **decompressed** original size. */
    private val decompressedSize: Long,
    /** Compression-algorithm tag of the whole stream (0=None,1=Zstd,2=Xz). */
    private val compression: Int,
    /** CRC32 over the whole decompressed original (0 if unknown). */
    private val crc32: Long,
    private val crc32Known: Boolean,
    /** SHA-256 of the whole **decompressed** original. */
    private val rootSha256: String,
    private val fileName: String,
    private val dir: File,
) {
    /** Preallocated final length of the `.partial` file (the compressed stream). */
    private val partialFile: File = File(dir, "transfer.partial")

    // ── bitmap (in-memory + persisted) ──
    private val received = BooleanArray(segmentCount)
    private val hashes = arrayOfNulls<String>(segmentCount)
    private var receivedCount = 0
    private var updatedAt = System.currentTimeMillis()

    private fun rootSessionIdHex(): String {
        val lo = java.lang.Long.toUnsignedString(rootSessionIdLo, 16).padStart(16, '0')
        val hi = java.lang.Long.toUnsignedString(rootSessionIdHi, 16).padStart(16, '0')
        return "$hi$lo"
    }

    /** Index of this segment in the root (also its slot in [received]). */
    private fun canonicalOffset(index: Int): Long = index.toLong() * SEGMENT_RAW_BYTES

    /**
     * Write one completed segment's **compressed** bytes at its canonical offset
     * within the compressed stream. Returns `true` if newly stored, `false` if
     * a duplicate.
     */
    @Synchronized
    fun storeSegment(index: Int, bytes: ByteArray, expectedSha256: ByteArray): Boolean {
        if (index < 0 || index >= segmentCount) return false
        require(expectedSha256.size == 32) { "segment SHA-256 must be 32 bytes" }
        val expectedLength = canonicalLength(index)
        require(bytes.size.toLong() == expectedLength) {
            "segment $index length ${bytes.size} != $expectedLength"
        }
        val actualSha256 = MessageDigest.getInstance("SHA-256").digest(bytes)
        require(MessageDigest.isEqual(actualSha256, expectedSha256)) {
            "segment $index SHA-256 mismatch"
        }
        val hashHex = actualSha256.toHex()
        if (received[index]) {
            require(hashes[index] == hashHex) { "duplicate segment metadata conflicts" }
            return false
        }
        if (!dir.exists() && !dir.mkdirs()) return false
        require(dir.usableSpace >= bytes.size.toLong() + MIN_FREE_RESERVE_BYTES) {
            "存储空间不足（至少需要保留 ${MIN_FREE_RESERVE_BYTES / 1024 / 1024} MiB）"
        }

        // Grow/truncate the partial file to the compressed stream size once, then
        // write this segment's compressed bytes at its compressed-stream offset.
        RandomAccessFile(partialFile, "rw").use { raf ->
            if (raf.length() < compressedSize) raf.setLength(compressedSize)
            val off = canonicalOffset(index)
            if (off + bytes.size > compressedSize) {
                throw IllegalArgumentException(
                    "segment $index overruns compressed stream: ${off + bytes.size} > $compressedSize"
                )
            }
            raf.seek(off)
            raf.write(bytes)
            raf.fd.sync()
        }

        received[index] = true
        hashes[index] = hashHex
        receivedCount++
        updatedAt = System.currentTimeMillis()
        persistBitmap()
        return true
    }

    /** Number of segments stored so far. */
    @Synchronized
    fun receivedCount(): Int = receivedCount

    fun segmentCount(): Int = segmentCount

    fun rootSessionIdLo(): Long = rootSessionIdLo

    fun rootSessionIdHi(): Long = rootSessionIdHi

    fun rootSha256Hex(): String = rootSha256

    fun decompressedSize(): Long = decompressedSize

    fun crc32(): Long = crc32

    fun crc32Known(): Boolean = crc32Known

    fun matches(
        lo: Long,
        hi: Long,
        count: Int,
        size: Long,
        rootHash: ByteArray,
        name: String,
    ): Boolean =
        rootSessionIdLo == lo && rootSessionIdHi == hi && segmentCount == count &&
            compressedSize == size && rootSha256 == rootHash.toHex() && fileName == name

    @Synchronized
    fun isComplete(): Boolean = receivedCount >= segmentCount

    /** Whether the segment at `index` has already been stored. */
    @Synchronized
    fun hasSegment(index: Int): Boolean =
        index in 0 until segmentCount && received[index]

    /**
     * Finish the transfer: stream the concatenated compressed stream (already in
     * `.partial`) to `transfer.decompressed`, decompressing **once** via native
     * Rust while computing CRC32 + SHA-256 incrementally. The native call
     * verifies the decompressed size, CRC32 (when known) and root SHA-256
     * (over the decompressed bytes) before returning success, and removes the
     * partial output on any mismatch — so neither the compressed stream nor the
     * original is ever held wholly in memory (very large files are recoverable).
     * Returns the decompressed file, or null if segments are still missing or
     * verification failed.
     */
    @Synchronized
    fun finish(): File? {
        if (!isComplete()) return null
        if (!partialFile.exists()) return null
        if (partialFile.length() != compressedSize) return null
        val outFile = File(dir, "transfer.decompressed")
        if (outFile.exists()) outFile.delete()
        val ok = NativeBridge.decompressStreamToFile(
            partialFile.absolutePath,
            outFile.absolutePath,
            compression,
            decompressedSize, // hard output cap (decompression-bomb guard)
            decompressedSize, // expected decompressed size
            crc32,
            crc32Known,
            rootSha256,
        )
        if (!ok) {
            if (outFile.exists()) outFile.delete()
            return null
        }
        return outFile
    }

    /** Remove the durable task only after ContentStore has published its index. */
    @Synchronized
    fun commitArchived() {
        File(dir, "bitmap.json").delete()
        partialFile.delete()
        File(dir, "transfer.complete").delete()
        dir.delete()
    }

    /** Persist the bitmap atomically. */
    private fun persistBitmap() {
        val obj = JSONObject()
        obj.put("rootLo", rootSessionIdLo.toString())
        obj.put("rootHi", rootSessionIdHi.toString())
        obj.put("count", segmentCount)
        obj.put("compressedSize", compressedSize.toString())
        obj.put("decompressedSize", decompressedSize.toString())
        obj.put("compression", compression)
        obj.put("crc32", crc32.toString())
        obj.put("crc32Known", crc32Known)
        obj.put("rootSha256", rootSha256)
        obj.put("name", fileName)
        obj.put("updatedAt", updatedAt)
        val arr = org.json.JSONArray()
        for (i in 0 until segmentCount) arr.put(received[i])
        obj.put("received", arr)
        val hashArr = org.json.JSONArray()
        for (i in 0 until segmentCount) hashArr.put(hashes[i] ?: JSONObject.NULL)
        obj.put("hashes", hashArr)
        val af = AtomicFile(File(dir, "bitmap.json"))
        var out: FileOutputStream? = null
        try {
            out = af.startWrite()
            out.write(obj.toString().toByteArray(Charsets.UTF_8))
            out.fd.sync()
            af.finishWrite(out)
        } catch (e: Exception) {
            if (out != null) af.failWrite(out)
            throw e
        }
    }

    data class Task(
        val rootSessionIdLo: Long,
        val rootSessionIdHi: Long,
        val fileName: String,
        val rootOriginalSize: Long,
        val rootSha256: String,
        val segmentCount: Int,
        val receivedCount: Int,
        val receivedIndices: List<Int>,
        val updatedAt: Long,
    ) {
        val rootSessionIdHex: String
            get() {
                val lo = java.lang.Long.toUnsignedString(rootSessionIdLo, 16).padStart(16, '0')
                val hi = java.lang.Long.toUnsignedString(rootSessionIdHi, 16).padStart(16, '0')
                return "$hi$lo"
            }

        /** Compact one-based missing ranges, e.g. `2、5–7、11`. */
        fun missingSegmentsText(maxRanges: Int = 4): String {
            val have = receivedIndices.toHashSet()
            val ranges = ArrayList<String>()
            var omitted = false
            var i = 0
            while (i < segmentCount) {
                if (i in have) {
                    i++
                    continue
                }
                val start = i
                while (i + 1 < segmentCount && i + 1 !in have) i++
                val end = i
                if (ranges.size < maxRanges) {
                    ranges += if (start == end) "${start + 1}" else "${start + 1}–${end + 1}"
                } else {
                    omitted = true
                }
                i++
            }
            return if (ranges.isEmpty()) "无" else ranges.joinToString("、") +
                if (omitted) " 等" else ""
        }
    }

    companion object {
        /** Fixed compressed-stream segment size (mirrors core `SEGMENT_RAW_BYTES`). */
        const val SEGMENT_RAW_BYTES = (32L * 1024 * 1024) - 65_528L
        const val MAX_SEGMENT_COUNT = 131_072
        private const val MIN_FREE_RESERVE_BYTES = 64L * 1024 * 1024
        private const val MAX_DECOMPRESSED_BYTES = 256L * 1024 * 1024

        /**
         * Open (or resume) an assembler for a root transfer. `segmentCount` /
         * `compressedSize` / `decompressedSize` / `compression` / `crc32` /
         * `fileName` are taken from the first segment's descriptor and must be
         * consistent across segments.
         */
        fun open(
            root: File,
            rootSessionIdLo: Long,
            rootSessionIdHi: Long,
            segmentCount: Int,
            compressedSize: Long,
            decompressedSize: Long,
            compression: Int,
            crc32: Long,
            crc32Known: Boolean,
            rootSha256: ByteArray,
            fileName: String,
        ): SegmentAssembler {
            require(segmentCount in 1..MAX_SEGMENT_COUNT) { "segment count out of range" }
            require(compressedSize > 0) { "compressed stream size must be positive" }
            val expectedCount = ((compressedSize - 1) / SEGMENT_RAW_BYTES + 1)
            require(expectedCount == segmentCount.toLong()) {
                "segment count inconsistent with compressed stream size"
            }
            require(rootSha256.size == 32) { "root SHA-256 must be 32 bytes" }
            val hex = rootSessionIdHex(rootSessionIdLo, rootSessionIdHi)
            val dir = File(root, "seg/$hex")
            val bm = File(dir, "bitmap.json")
            // Legacy ledgers did not bind segments to a compressed-stream digest /
            // whole-file metadata. They are unsafe to resume under this model.
            if (bm.exists()) {
                try {
                    val savedRootHash = JSONObject(bm.readText()).optString("rootSha256")
                    if (savedRootHash.length != 64) dir.deleteRecursively()
                } catch (_: Exception) {
                    // A corrupt current-format ledger is handled below.
                }
            }
            if (!bm.exists()) {
                require(root.usableSpace >= compressedSize + MIN_FREE_RESERVE_BYTES) {
                    "存储空间不足：大文件任务需要约 ${compressedSize / 1024 / 1024} MiB 可用空间"
                }
            }
            val rootHashHex = rootSha256.joinToString("") { "%02x".format(it) }
            val asm = SegmentAssembler(
                rootSessionIdLo, rootSessionIdHi,
                segmentCount, compressedSize, decompressedSize, compression,
                crc32, crc32Known, rootHashHex, fileName, dir
            )
            // Resume from a persisted bitmap if present.
            if (bm.exists()) {
                try {
                    val obj = JSONObject(bm.readText())
                    val count = obj.optInt("count")
                    val sameTask = count == segmentCount &&
                        obj.optString("rootLo") == rootSessionIdLo.toString() &&
                        obj.optString("rootHi") == rootSessionIdHi.toString() &&
                        obj.optString("compressedSize") == compressedSize.toString() &&
                        obj.optString("decompressedSize") == decompressedSize.toString() &&
                        obj.optInt("compression", -1) == compression &&
                        obj.optString("crc32") == crc32.toString() &&
                        obj.optBoolean("crc32Known", false) == crc32Known &&
                        obj.optString("rootSha256") == rootHashHex &&
                        obj.optString("name") == fileName
                    if (!sameTask) {
                        throw IllegalArgumentException("同一根任务的分段元数据冲突")
                    }
                    if (sameTask) {
                        val recv = obj.optJSONArray("received")
                        val savedHashes = obj.optJSONArray("hashes")
                        asm.updatedAt = obj.optLong("updatedAt", bm.lastModified())
                        if (recv != null) {
                            for (i in 0 until segmentCount) {
                                val on = recv.optBoolean(i)
                                val hash = if (savedHashes == null || savedHashes.isNull(i)) {
                                    null
                                } else {
                                    savedHashes.optString(i).takeIf { it.length == 64 }
                                }
                                val valid = on && hash != null &&
                                    asm.verifyPersistedSegment(i, hash)
                                asm.received[i] = valid
                                asm.hashes[i] = if (valid) hash else null
                                if (valid) asm.receivedCount++
                            }
                        }
                    }
                } catch (e: IllegalArgumentException) {
                    throw e
                } catch (_: Exception) {
                    // Corrupt bitmap → start fresh (overwrite below).
                }
            }
            return asm
        }

        /**
         * Read-only check whether a segment is already durably received for a
         * given root transfer. Unlike [open] this needs no full metadata and
         * never mutates any state — used to reject re-scanning an already
         * completed segment (the scan-side duplicate-segment fast path).
         */
        fun hasStoredSegment(root: File, rootSessionIdLo: Long, rootSessionIdHi: Long, index: Int): Boolean {
            if (index < 0) return false
            val hex = rootSessionIdHex(rootSessionIdLo, rootSessionIdHi)
            val bm = File(File(root, "seg/$hex"), "bitmap.json")
            if (!bm.isFile) return false
            return try {
                val recv = JSONObject(bm.readText()).optJSONArray("received")
                recv != null && index < recv.length() && recv.optBoolean(index)
            } catch (_: Exception) {
                false
            }
        }

        /** List durable incomplete/completed-but-not-yet-archived tasks. */
        fun listTasks(root: File): List<Task> {
            val segRoot = File(root, "seg")
            if (!segRoot.isDirectory) return emptyList()
            return segRoot.listFiles()?.mapNotNull { dir ->
                val bm = File(dir, "bitmap.json")
                if (!bm.isFile) return@mapNotNull null
                try {
                    val obj = JSONObject(bm.readText())
                    val count = obj.getInt("count")
                    val compressedSize = obj.getString("compressedSize").toLong()
                    if (count !in 1..MAX_SEGMENT_COUNT || compressedSize <= 0) return@mapNotNull null
                    val rootSha256 = obj.optString("rootSha256")
                    if (rootSha256.length != 64 ||
                        rootSha256.any { it !in '0'..'9' && it !in 'a'..'f' }
                    ) return@mapNotNull null
                    val recv = obj.optJSONArray("received")
                    val receivedIndices = (0 until count)
                        .filter { recv?.optBoolean(it) == true }
                    Task(
                        rootSessionIdLo = obj.getString("rootLo").toLong(),
                        rootSessionIdHi = obj.getString("rootHi").toLong(),
                        fileName = obj.optString("name", "received_file"),
                        rootOriginalSize = obj.optString("decompressedSize").toLongOrNull()
                            ?: compressedSize,
                        rootSha256 = rootSha256,
                        segmentCount = count,
                        receivedCount = receivedIndices.size,
                        receivedIndices = receivedIndices,
                        updatedAt = obj.optLong("updatedAt", bm.lastModified()),
                    )
                } catch (_: Exception) {
                    null
                }
            }?.sortedByDescending { it.updatedAt } ?: emptyList()
        }

        private fun rootSessionIdHex(lo: Long, hi: Long): String {
            val l = java.lang.Long.toUnsignedString(lo, 16).padStart(16, '0')
            val h = java.lang.Long.toUnsignedString(hi, 16).padStart(16, '0')
            return "$h$l"
        }

        /** Clean up leftover `.partial` / bitmap state (e.g. on discard). */
        fun discard(root: File, lo: Long, hi: Long) {
            val hex = rootSessionIdHex(lo, hi)
            val dir = File(root, "seg/$hex")
            if (dir.exists()) dir.deleteRecursively()
        }
    }

    private fun canonicalLength(index: Int): Long {
        val remaining = compressedSize - canonicalOffset(index)
        return remaining.coerceAtMost(SEGMENT_RAW_BYTES)
    }

    private fun verifyPersistedSegment(index: Int, expectedHash: String): Boolean {
        if (!partialFile.isFile) return false
        val length = canonicalLength(index)
        if (length <= 0 || partialFile.length() != compressedSize) return false
        return try {
            val md = MessageDigest.getInstance("SHA-256")
            RandomAccessFile(partialFile, "r").use { raf ->
                raf.seek(canonicalOffset(index))
                val buf = ByteArray(64 * 1024)
                var left = length
                while (left > 0) {
                    val n = raf.read(buf, 0, minOf(buf.size.toLong(), left).toInt())
                    if (n <= 0) return false
                    md.update(buf, 0, n)
                    left -= n
                }
            }
            md.digest().toHex() == expectedHash
        } catch (_: Exception) {
            false
        }
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}
