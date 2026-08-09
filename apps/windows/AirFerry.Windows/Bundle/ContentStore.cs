using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AirFerry.Windows.ViewModels;

namespace AirFerry.Windows.Bundle;

/// <summary>
/// Content-addressed store + logical entry index (mirrors Android ContentStore).
/// Layout under Documents/AirFerry/store/:
///   blobs/hh/sha256
///   index.json
/// </summary>
public static class ContentStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public sealed record Entry(
        string Id,
        string Name,
        string Hash,
        long Size,
        string CrcHex,
        bool CrcUnknown,
        string Kind,
        long CreatedAt,
        string? BundleId,
        string? BundleTitle);

    public sealed record PutResult(Entry Entry, string Path, bool Deduped);

    public static string RootDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "AirFerry", "store");

    private static string IndexPath => Path.Combine(RootDir, "index.json");

    public static string BlobPath(string hash)
    {
        string h = hash.ToLowerInvariant();
        if (h.Length != 64 || h.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new ArgumentException("Invalid SHA-256 hash", nameof(hash));
        }
        string dir = Path.Combine(RootDir, "blobs", h[..2]);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, h);
    }

    public static string Sha256Hex(byte[] bytes)
    {
        byte[] d = SHA256.HashData(bytes);
        return Convert.ToHexString(d).ToLowerInvariant();
    }

    public static PutResult PutBytes(
        string displayName,
        byte[] bytes,
        string crcHex = "unknown",
        bool crcUnknown = true,
        string kind = "file",
        string? bundleId = null,
        string? bundleTitle = null)
    {
        lock (Gate)
        {
            // Fail closed before writing a blob. Treating a corrupt index as an
            // empty history would make the next receive overwrite every logical
            // entry and orphan otherwise-valid content-addressed blobs.
            var all = LoadIndex();
            Directory.CreateDirectory(RootDir);
            string hash = Sha256Hex(bytes);
            string path = BlobPath(hash);
            bool deduped = FileMatchesHash(path, hash, bytes.LongLength);
            if (!deduped)
            {
                WriteAllBytesAtomic(path, bytes);
            }
            var entry = new Entry(
                Id: Guid.NewGuid().ToString("N"),
                Name: FileNameUtil.Sanitize(displayName),
                Hash: hash,
                Size: bytes.LongLength,
                CrcHex: crcHex,
                CrcUnknown: crcUnknown,
                Kind: kind,
                CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                BundleId: bundleId,
                BundleTitle: bundleTitle);
            all.Add(entry);
            SaveIndex(all);
            return new PutResult(entry, path, deduped);
        }
    }

    public static IReadOnlyList<Entry> ListEntries()
    {
        lock (Gate) return LoadIndex();
    }

    public static bool DeleteEntry(string id)
    {
        lock (Gate)
        {
            var all = LoadIndex();
            int idx = all.FindIndex(e => e.Id == id);
            if (idx < 0) return false;
            Entry removed = all[idx];
            all.RemoveAt(idx);
            SaveIndex(all);
            if (all.TrueForAll(e => e.Hash != removed.Hash))
            {
                string p = BlobPath(removed.Hash);
                if (File.Exists(p)) File.Delete(p);
            }
            return true;
        }
    }

    public static void ClearAll()
    {
        lock (Gate)
        {
            SaveIndex([]);
            string blobs = Path.Combine(RootDir, "blobs");
            if (Directory.Exists(blobs)) Directory.Delete(blobs, recursive: true);
        }
    }

    /// <summary>Import legacy Documents/AirFerry/received once if store is empty.</summary>
    public static void MigrateLegacyReceivedIfNeeded()
    {
        lock (Gate)
        {
            if (LoadIndex().Count > 0) return;
            string legacy = ScanViewModel.ReceivedDir;
            if (!Directory.Exists(legacy)) return;
            foreach (string f in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    byte[] bytes = File.ReadAllBytes(f);
                    string name = Path.GetFileName(f);
                    PutBytes(name, bytes);
                }
                catch
                {
                    // skip
                }
            }
            try
            {
                string bak = legacy + ".bak." + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Directory.Move(legacy, bak);
            }
            catch
            {
                // leave legacy in place if rename fails
            }
        }
    }

    private static List<Entry> LoadIndex()
    {
        if (!File.Exists(IndexPath)) return [];
        try
        {
            string json = File.ReadAllText(IndexPath, Encoding.UTF8);
            List<Entry> entries = JsonSerializer.Deserialize<List<Entry>>(json, JsonOpts)
                ?? throw new InvalidDataException("ContentStore index is null");
            if (entries.Any(e => e is null || e.Size < 0 || e.Hash is null ||
                                 e.Hash.Length != 64 || !e.Hash.All(Uri.IsHexDigit)))
            {
                throw new InvalidDataException("ContentStore index contains an invalid entry");
            }
            return entries;
        }
        catch (Exception ex)
        {
            string backup = Path.Combine(
                RootDir,
                $"index.corrupt.{File.GetLastWriteTimeUtc(IndexPath).Ticks}.json");
            try
            {
                if (!File.Exists(backup)) File.Copy(IndexPath, backup, overwrite: false);
            }
            catch
            {
                // Preserve the original index in place even if the backup copy
                // cannot be created (disk full/permissions).
            }
            throw new InvalidDataException(
                $"接收历史索引已损坏，已停止写入以保护现有数据。备份: {backup}", ex);
        }
    }

    private static void SaveIndex(List<Entry> entries)
    {
        Directory.CreateDirectory(RootDir);
        string json = JsonSerializer.Serialize(entries, JsonOpts);
        string temp = Path.Combine(RootDir, $"index.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] encoded = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(json);
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(encoded);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, IndexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static bool FileMatchesHash(string path, string expectedHash, long expectedSize)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != expectedSize) return false;
        try
        {
            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(expectedHash));
        }
        catch
        {
            return false;
        }
    }

    private static void WriteAllBytesAtomic(string path, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is null) throw new IOException("Blob path has no directory");
        Directory.CreateDirectory(dir);
        string temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
