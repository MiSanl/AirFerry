using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AirFerry.Windows.Bundle;

/// <summary>
/// Durable, disk-backed assembler for one descriptor-v4 root transfer.
/// Verified raw segments are written at canonical 8 MiB offsets and the receipt
/// bitmap is atomically published only after the segment bytes reach disk.
/// </summary>
public sealed class SegmentAssembler
{
    public const long SegmentRawBytes = 8L * 1024 * 1024;
    public const int MaxSegmentCount = 131_072;
    private const long MinFreeReserveBytes = 64L * 1024 * 1024;

    private readonly ulong _rootLo;
    private readonly ulong _rootHi;
    private readonly int _segmentCount;
    private readonly long _rootOriginalSize;
    private readonly string _rootSha256;
    private readonly string _fileName;
    private readonly string _dir;
    private readonly bool[] _received;
    private readonly string?[] _hashes;
    private int _receivedCount;
    private long _updatedAt;

    private string RootSessionIdHex => $"{_rootHi:x16}{_rootLo:x16}";
    private string PartialPath => Path.Combine(_dir, "transfer.partial");
    private string CompletePath => Path.Combine(_dir, "transfer.complete");
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
        ulong rootLo, ulong rootHi, int segmentCount, long rootOriginalSize,
        byte[] rootSha256, string fileName)
    {
        _rootLo = rootLo;
        _rootHi = rootHi;
        _segmentCount = segmentCount;
        _rootOriginalSize = rootOriginalSize;
        _rootSha256 = Convert.ToHexString(rootSha256).ToLowerInvariant();
        _fileName = fileName;
        _dir = Path.Combine(SegmentAssemblerRoot, RootSessionIdHex);
        _received = new bool[segmentCount];
        _hashes = new string?[segmentCount];
        _updatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public static SegmentAssembler Open(
        ulong rootLo, ulong rootHi, int segmentCount, long rootOriginalSize,
        byte[] rootSha256, string fileName)
    {
        if (segmentCount is <= 0 or > MaxSegmentCount)
            throw new InvalidDataException("segment count out of range");
        if (rootOriginalSize <= 0)
            throw new InvalidDataException("root size must be positive");
        long expected = checked((rootOriginalSize - 1) / SegmentRawBytes + 1);
        if (expected != segmentCount)
            throw new InvalidDataException("segment count inconsistent with root size");
        if (rootSha256.Length != 32)
            throw new InvalidDataException("root SHA-256 must be 32 bytes");

        var asm = new SegmentAssembler(
            rootLo, rootHi, segmentCount, rootOriginalSize, rootSha256,
            string.IsNullOrWhiteSpace(fileName) ? "received_file" : fileName);
        if (File.Exists(asm.BitmapPath) && IsLegacyLedger(asm.BitmapPath))
        {
            // v1.1.6 tasks lack a complete-file digest and cannot be resumed
            // safely. Start them over under the revised descriptor-v4 contract.
            Directory.Delete(asm._dir, recursive: true);
        }
        if (!File.Exists(asm.BitmapPath))
        {
            long available = AvailableBytes(ContentStore.RootDir);
            if (available < rootOriginalSize + MinFreeReserveBytes)
                throw new IOException(
                    $"存储空间不足：大文件任务需要约 {rootOriginalSize / 1024 / 1024} MiB 可用空间");
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
                        && root.GetProperty("rootSize").GetString() == _rootOriginalSize.ToString()
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
                if (fs.Length < _rootOriginalSize) fs.SetLength(_rootOriginalSize);
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
    public bool Matches(
        ulong rootLo, ulong rootHi, int segmentCount, long rootOriginalSize,
        byte[] rootSha256, string fileName)
    {
        string normalizedName = string.IsNullOrWhiteSpace(fileName) ? "received_file" : fileName;
        lock (this)
        {
            return _rootLo == rootLo && _rootHi == rootHi
                   && _segmentCount == segmentCount
                   && _rootOriginalSize == rootOriginalSize
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
    /// Promote the complete partial to a task-owned complete file. The bitmap is
    /// deliberately retained until <see cref="CommitArchived"/>.
    /// </summary>
    public string? Finish()
    {
        lock (this)
        {
            if (!IsComplete()) return null;
            if (File.Exists(CompletePath))
            {
                if (new FileInfo(CompletePath).Length != _rootOriginalSize ||
                    !string.Equals(Sha256HexFile(CompletePath), _rootSha256,
                        StringComparison.Ordinal))
                    throw new InvalidDataException("整文件 SHA-256 校验失败");
                return CompletePath;
            }
            if (!File.Exists(PartialPath)) return CompletePath;
            if (new FileInfo(PartialPath).Length != _rootOriginalSize) return null;
            if (!string.Equals(Sha256HexFile(PartialPath), _rootSha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException("整文件 SHA-256 校验失败");
            File.Move(PartialPath, CompletePath, overwrite: false);
            return CompletePath;
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
                long size = long.Parse(root.GetProperty("rootSize").GetString()!);
                if (count is <= 0 or > MaxSegmentCount || size <= 0) continue;
                string rootSha256 = root.GetProperty("rootSha256").GetString() ?? "";
                if (rootSha256.Length != 64 || rootSha256.Any(c => !Uri.IsHexDigit(c)))
                    continue;
                JsonElement recv = root.GetProperty("received");
                var receivedIndices = new List<int>();
                for (int i = 0; i < count && i < recv.GetArrayLength(); i++)
                    if (recv[i].GetBoolean()) receivedIndices.Add(i);
                result.Add(new TaskInfo(
                    ulong.Parse(root.GetProperty("rootLo").GetString()!),
                    ulong.Parse(root.GetProperty("rootHi").GetString()!),
                    root.TryGetProperty("name", out JsonElement name)
                        ? name.GetString() ?? "received_file"
                        : "received_file",
                    size,
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
            rootSize = _rootOriginalSize.ToString(),
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
        long remaining = _rootOriginalSize - checked((long)index * SegmentRawBytes);
        return Math.Min(SegmentRawBytes, remaining);
    }

    private bool VerifyPersistedSegment(int index, string expectedHash)
    {
        string source = File.Exists(PartialPath) ? PartialPath : CompletePath;
        if (!File.Exists(source) || new FileInfo(source).Length != _rootOriginalSize) return false;
        try
        {
            using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var fs = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
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

    public string RootSha256Hex => _rootSha256;

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
            return !doc.RootElement.TryGetProperty("rootSha256", out JsonElement hash)
                   || hash.ValueKind != JsonValueKind.String
                   || hash.GetString() is not { Length: 64 };
        }
        catch
        {
            // Preserve unreadable current ledgers for forensic/manual recovery.
            return false;
        }
    }
}
