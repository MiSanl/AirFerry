using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirFerry.Windows.Native;
using AirFerry.Windows.Scan;

namespace AirFerry.Windows.Bundle;

/// <summary>
/// Durable, disk-backed assembler for one descriptor-v5 root transfer.
/// A logical transfer is compressed **once** into a single compressed stream,
/// then split into fixed <see cref="SegmentRawBytes"/> (~32 MiB) segments. Each
/// completed segment's **compressed** bytes are written at their canonical
/// offset within the compressed stream, and the receipt bitmap is atomically
/// published only after the bytes reach disk. When every segment has arrived,
/// the concatenated compressed stream is decompressed exactly once to recover
/// the original payload (which may be a file, a multi-file bundle, or text).
/// </summary>
public sealed class SegmentAssembler
{
    public const long SegmentRawBytes = (32L * 1024 * 1024) - 65_528L;
    public const int MaxSegmentCount = 131_072;
    private const long MinFreeReserveBytes = 64L * 1024 * 1024;
    private const long MaxDecompressedBytes = 256L * 1024 * 1024;

    private readonly ulong _rootLo;
    private readonly ulong _rootHi;
    private readonly int _segmentCount;
    /// <summary>Whole **compressed** stream size (== sum of every segment's compressed bytes).</summary>
    private readonly long _compressedSize;
    /// <summary>Whole **decompressed** original size.</summary>
    private readonly long _decompressedSize;
    /// <summary>Compression-algorithm tag of the whole stream (0=None,1=Zstd,2=Xz).</summary>
    private readonly byte _compression;
    /// <summary>CRC32 over the whole decompressed original (0 if unknown).</summary>
    private readonly uint _crc32;
    private readonly bool _crc32Known;
    private readonly string _rootSha256;
    private readonly string _fileName;
    private readonly string _dir;
    private readonly bool[] _received;
    private readonly string?[] _hashes;
    private int _receivedCount;
    private long _updatedAt;

    private string RootSessionIdHex => $"{_rootHi:x16}{_rootLo:x16}";
    private string PartialPath => Path.Combine(_dir, "transfer.partial");
    private string BitmapPath => Path.Combine(_dir, "bitmap.json");

    public static string SegmentAssemblerRoot => Path.Combine(ContentStore.RootDir, "seg");

    public sealed record TaskInfo(
        ulong RootLo,
        ulong RootHi,
        string FileName,
        long RootOriginalSize,
        string RootSha256,
        int SegmentCount,
        int ReceivedCount,
        IReadOnlyList<int> ReceivedIndices,
        long UpdatedAt)
    {
        public string RootSessionIdHex => $"{RootHi:x16}{RootLo:x16}";
    }

    private SegmentAssembler(
        ulong rootLo, ulong rootHi, int segmentCount, long compressedSize,
        long decompressedSize, byte compression, uint crc32, bool crc32Known,
        byte[] rootSha256, string fileName)
    {
        _rootLo = rootLo;
        _rootHi = rootHi;
        _segmentCount = segmentCount;
        _compressedSize = compressedSize;
        _decompressedSize = decompressedSize;
        _compression = compression;
        _crc32 = crc32;
        _crc32Known = crc32Known;
        _rootSha256 = Convert.ToHexString(rootSha256).ToLowerInvariant();
        _fileName = fileName;
        _dir = Path.Combine(SegmentAssemblerRoot, RootSessionIdHex);
        _received = new bool[segmentCount];
        _hashes = new string?[segmentCount];
        _updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public static SegmentAssembler Open(
        ulong rootLo, ulong rootHi, int segmentCount, long compressedSize,
        long decompressedSize, byte compression, uint crc32, bool crc32Known,
        byte[] rootSha256, string fileName)
    {
        if (segmentCount is <= 0 or > MaxSegmentCount)
            throw new InvalidDataException("segment count out of range");
        if (compressedSize <= 0)
            throw new InvalidDataException("compressed stream size must be positive");
        long expected = checked((compressedSize - 1) / SegmentRawBytes + 1);
        if (expected != segmentCount)
            throw new InvalidDataException("segment count inconsistent with compressed stream size");
        if (rootSha256.Length != 32)
            throw new InvalidDataException("root SHA-256 must be 32 bytes");

        var asm = new SegmentAssembler(
            rootLo, rootHi, segmentCount, compressedSize, decompressedSize,
            compression, crc32, crc32Known, rootSha256,
            string.IsNullOrWhiteSpace(fileName) ? "received_file" : fileName);
        if (File.Exists(asm.BitmapPath) && IsLegacyLedger(asm.BitmapPath))
        {
            // Tasks from an older model (per-segment decompressed bytes) are
            // incompatible with the compressed-stream segment layout. Reset them.
            Directory.Delete(asm._dir, recursive: true);
        }
        if (!File.Exists(asm.BitmapPath))
        {
            long available = AvailableBytes(ContentStore.RootDir);
            if (available < compressedSize + MinFreeReserveBytes)
                throw new IOException(
                    $"存储空间不足：大文件任务需要约 {compressedSize / 1024 / 1024} MiB 可用空间");
        }
        asm.Resume();
        return asm;
    }

    private void Resume()
    {
        if (!File.Exists(BitmapPath)) return;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(BitmapPath));
            JsonElement root = doc.RootElement;
            bool same = root.GetProperty("rootLo").GetString() == _rootLo.ToString()
                        && root.GetProperty("rootHi").GetString() == _rootHi.ToString()
                        && root.GetProperty("count").GetInt32() == _segmentCount
                        && root.GetProperty("compressedSize").GetString() == _compressedSize.ToString()
                        && root.GetProperty("decompressedSize").GetString() == _decompressedSize.ToString()
                        && root.GetProperty("compression").GetInt32() == _compression
                        && root.GetProperty("crc32").GetString() == _crc32.ToString()
                        && root.GetProperty("crc32Known").GetBoolean() == _crc32Known
                        && root.GetProperty("rootSha256").GetString() == _rootSha256
                        && root.GetProperty("name").GetString() == _fileName;
            if (!same) throw new InvalidDataException("同一根任务的分段元数据冲突");

            _updatedAt = root.TryGetProperty("updatedAt", out JsonElement updated)
                ? updated.GetInt64()
                : new DateTimeOffset(File.GetLastWriteTimeUtc(BitmapPath))
                    .ToUnixTimeMilliseconds();
            JsonElement recv = root.GetProperty("received");
            bool hasHashes = root.TryGetProperty("hashes", out JsonElement hashes)
                             && hashes.ValueKind == JsonValueKind.Array;
            for (int i = 0; i < _segmentCount && i < recv.GetArrayLength(); i++)
            {
                bool on = recv[i].GetBoolean();
                string? hash = hasHashes && i < hashes.GetArrayLength()
                    && hashes[i].ValueKind == JsonValueKind.String
                    ? hashes[i].GetString()
                    : null;
                bool valid = on && hash is { Length: 64 } && VerifyPersistedSegment(i, hash);
                _received[i] = valid;
                _hashes[i] = valid ? hash : null;
                if (valid) _receivedCount++;
            }
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("分段恢复账本已损坏，原文件已保留", ex);
        }
    }

    public bool StoreSegment(int index, byte[] bytes, byte[] expectedSha256)
    {
        lock (this)
        {
            if (index < 0 || index >= _segmentCount)
                throw new InvalidDataException("segment index out of range");
            if (expectedSha256.Length != 32)
                throw new InvalidDataException("segment SHA-256 must be 32 bytes");
            long expectedLength = CanonicalLength(index);
            if (bytes.LongLength != expectedLength)
                throw new InvalidDataException(
                    $"segment {index} length {bytes.LongLength} != {expectedLength}");

            byte[] actualSha = SHA256.HashData(bytes);
            if (!CryptographicOperations.FixedTimeEquals(actualSha, expectedSha256))
                throw new InvalidDataException($"segment {index} SHA-256 mismatch");
            string hashHex = Convert.ToHexString(actualSha).ToLowerInvariant();
            if (_received[index])
            {
                if (!string.Equals(_hashes[index], hashHex, StringComparison.Ordinal))
                    throw new InvalidDataException("duplicate segment metadata conflicts");
                return false;
            }

            Directory.CreateDirectory(_dir);
            if (AvailableBytes(_dir) < bytes.LongLength + MinFreeReserveBytes)
                throw new IOException(
                    $"存储空间不足（至少需要保留 {MinFreeReserveBytes / 1024 / 1024} MiB）");
            using (var fs = new FileStream(PartialPath, FileMode.OpenOrCreate, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                if (fs.Length < _compressedSize) fs.SetLength(_compressedSize);
                fs.Position = checked((long)index * SegmentRawBytes);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }

            _received[index] = true;
            _hashes[index] = hashHex;
            _receivedCount++;
            _updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            PersistBitmap();
            return true;
        }
    }

    public int ReceivedCount() { lock (this) return _receivedCount; }
    public int SegmentCount() { lock (this) return _segmentCount; }
    public bool IsComplete() { lock (this) return _receivedCount == _segmentCount; }
    public uint Crc32() { lock (this) return _crc32; }
    public bool Crc32Known() { lock (this) return _crc32Known; }
    public string RootSha256Hex { get { lock (this) return _rootSha256; } }
    public long DecompressedSize() { lock (this) return _decompressedSize; }

    public bool Matches(
        ulong rootLo, ulong rootHi, int segmentCount, long compressedSize,
        byte[] rootSha256, string fileName)
    {
        string normalizedName = string.IsNullOrWhiteSpace(fileName) ? "received_file" : fileName;
        lock (this)
        {
            return _rootLo == rootLo && _rootHi == rootHi
                   && _segmentCount == segmentCount
                   && _compressedSize == compressedSize
                   && rootSha256.Length == 32
                   && string.Equals(
                       _rootSha256,
                       Convert.ToHexString(rootSha256).ToLowerInvariant(),
                       StringComparison.Ordinal)
                   && string.Equals(_fileName, normalizedName, StringComparison.Ordinal);
        }
    }
    public bool HasSegment(int index)
    {
        lock (this) return index >= 0 && index < _segmentCount && _received[index];
    }

    /// <summary>
    /// Finish the transfer: stream the concatenated compressed stream (already
    /// in <c>transfer.partial</c>) to <c>transfer.decompressed</c>, decompressing
    /// **once** via native Rust while computing CRC32 + SHA-256 incrementally.
    /// The native call verifies the decompressed size, CRC32 (when known) and
    /// root SHA-256 (over the decompressed bytes) before returning success, and
    /// removes the partial output on any mismatch — so neither the compressed
    /// stream nor the original is ever held wholly in memory (very large files
    /// are recoverable). Returns the decompressed file path, or null if
    /// segments are still missing or verification failed.
    /// </summary>
    public string? Finish()
    {
        lock (this)
        {
            if (!IsComplete()) return null;
            if (!File.Exists(PartialPath)) return null;
            if (new FileInfo(PartialPath).Length != _compressedSize) return null;

            string outPath = Path.Combine(_dir, "transfer.decompressed");
            if (File.Exists(outPath)) File.Delete(outPath);
            // Re-check free space against the DECOMPRESSED size right before the
            // final streaming decompress. The initial Open() check only verified
            // compressedSize (the segment payload budget); the decompressed result
            // can be far larger (legitimate highly-compressible file, or a hostile
            // compressed stream that expands to the declared decompressedSize).
            // Trusting only the descriptor's decompressedSize here can fill the disk.
            long available = AvailableBytes(_dir);
            // Guard against long overflow: a hostile descriptor can declare a
            // decompressedSize near long.MaxValue, and the naive
            // `decompressedSize + reserve` would wrap to a negative number,
            // bypassing the space check. Compare without adding: if
            // decompressedSize alone already exceeds what the volume can hold
            // (minus the reserve), reject. Use saturating math.
            long needed = _decompressedSize > long.MaxValue - MinFreeReserveBytes
                ? long.MaxValue
                : _decompressedSize + MinFreeReserveBytes;
            if (available < needed)
            {
                return null;
            }
            // Paths cross the P/Invoke boundary as NUL-terminated UTF-8 byte[]:
            // the store root (<MyDocuments>\AirFerry\store\...) can be non-ASCII
            // on a localized Windows (文档 / non-ASCII username), and Rust reads
            // them as UTF-8. Encoding as UTF-8 here matches the Rust contract.
            // _rootSha256 is lowercase ASCII hex, but encode it the same way for
            // signature consistency.
            int ok = NativeBridge.DecompressStreamToFile(
                Encoding.UTF8.GetBytes(PartialPath + "\0"),
                Encoding.UTF8.GetBytes(outPath + "\0"),
                _compression,
                (ulong)_decompressedSize, // hard output cap (decompression-bomb guard)
                (ulong)_decompressedSize, // expected decompressed size
                _crc32,
                _crc32Known,
                Encoding.UTF8.GetBytes(_rootSha256 + "\0"));
            if (ok != 1)
            {
                if (File.Exists(outPath)) File.Delete(outPath);
                return null;
            }
            return outPath;
        }
    }

    public void CommitArchived()
    {
        lock (this)
        {
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
            catch { /* final content is already indexed; cleanup is retryable */ }
        }
    }

    public static IReadOnlyList<TaskInfo> ListTasks()
    {
        if (!Directory.Exists(SegmentAssemblerRoot)) return [];
        var result = new List<TaskInfo>();
        foreach (string dir in Directory.EnumerateDirectories(SegmentAssemblerRoot))
        {
            string bitmap = Path.Combine(dir, "bitmap.json");
            if (!File.Exists(bitmap)) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(bitmap));
                JsonElement root = doc.RootElement;
                int count = root.GetProperty("count").GetInt32();
                long size = long.Parse(root.GetProperty("compressedSize").GetString()!);
                if (count is <= 0 or > MaxSegmentCount || size <= 0) continue;
                string rootSha256 = root.GetProperty("rootSha256").GetString() ?? "";
                if (rootSha256.Length != 64 || rootSha256.Any(c => !Uri.IsHexDigit(c)))
                    continue;
                JsonElement recv = root.GetProperty("received");
                var receivedIndices = new List<int>();
                for (int i = 0; i < count && i < recv.GetArrayLength(); i++)
                    if (recv[i].GetBoolean()) receivedIndices.Add(i);
                long displaySize = root.TryGetProperty("decompressedSize", out JsonElement ds)
                    ? long.Parse(ds.GetString()!)
                    : size;
                result.Add(new TaskInfo(
                    ulong.Parse(root.GetProperty("rootLo").GetString()!),
                    ulong.Parse(root.GetProperty("rootHi").GetString()!),
                    root.TryGetProperty("name", out JsonElement name)
                        ? name.GetString() ?? "received_file"
                        : "received_file",
                    displaySize,
                    rootSha256,
                    count,
                    receivedIndices.Count,
                    receivedIndices,
                    root.TryGetProperty("updatedAt", out JsonElement updated)
                        ? updated.GetInt64()
                        : new DateTimeOffset(File.GetLastWriteTimeUtc(bitmap))
                            .ToUnixTimeMilliseconds()));
            }
            catch
            {
                // Preserve an unreadable task for forensic/manual recovery; do
                // not let one corrupt ledger hide healthy tasks.
            }
        }
        return result.OrderByDescending(t => t.UpdatedAt).ToList();
    }

    public static void Discard(ulong lo, ulong hi)
    {
        string dir = Path.Combine(SegmentAssemblerRoot, $"{hi:x16}{lo:x16}");
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private void PersistBitmap()
    {
        var payload = new
        {
            rootLo = _rootLo.ToString(),
            rootHi = _rootHi.ToString(),
            count = _segmentCount,
            compressedSize = _compressedSize.ToString(),
            decompressedSize = _decompressedSize.ToString(),
            compression = (int)_compression,
            crc32 = _crc32.ToString(),
            crc32Known = _crc32Known,
            rootSha256 = _rootSha256,
            name = _fileName,
            updatedAt = _updatedAt,
            received = _received,
            hashes = _hashes,
        };
        Directory.CreateDirectory(_dir);
        string temp = Path.Combine(_dir, $"bitmap.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                fs.Write(json);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, BitmapPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private long CanonicalLength(int index)
    {
        long remaining = _compressedSize - checked((long)index * SegmentRawBytes);
        return Math.Min(SegmentRawBytes, remaining);
    }

    private bool VerifyPersistedSegment(int index, string expectedHash)
    {
        if (!File.Exists(PartialPath) || new FileInfo(PartialPath).Length != _compressedSize) return false;
        try
        {
            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var fs = new FileStream(PartialPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Position = checked((long)index * SegmentRawBytes);
            long left = CanonicalLength(index);
            byte[] buffer = new byte[64 * 1024];
            while (left > 0)
            {
                int n = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                if (n <= 0) return false;
                incremental.AppendData(buffer, 0, n);
                left -= n;
            }
            string actual = Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(actual, expectedHash, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static string Sha256HexFile(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    private static long AvailableBytes(string path)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full)
            ?? throw new IOException("cannot resolve storage volume");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    private static bool IsLegacyLedger(string bitmapPath)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(bitmapPath));
            return !doc.RootElement.TryGetProperty("compressedSize", out JsonElement cs)
                   || cs.ValueKind != JsonValueKind.String;
        }
        catch
        {
            // Preserve unreadable current ledgers for forensic/manual recovery.
            return false;
        }
    }
}
